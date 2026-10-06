using System.Security.Cryptography;
using NuGet.Versioning;
using Shiny.Net.HttpServer.NuGet.Internal;

namespace Shiny.Net.HttpServer.NuGet;

/// <summary>
/// Keeps packages in a directory, in the layout NuGet itself uses for a hierarchical folder feed and
/// for the global packages folder:
/// <code>
/// {root}/{id}/{version}/{id}.{version}.nupkg
/// {root}/{id}/{version}/{id}.{version}.nupkg.sha512
/// {root}/{id}/{version}/{id}.nuspec
/// </code>
/// with the id and version lower-cased. So the same directory also works as a plain local package
/// source (<c>dotnet nuget add source /path/to/root</c>), can be seeded by copying a folder in, and
/// can be backed up with anything that copies files.
/// <para>
/// An unlisted version carries an empty <c>.unlisted</c> marker beside its package. The directory is
/// read once, on first use, and kept in memory after that; a version dropped into the directory by
/// hand while the server is running is picked up by <see cref="Reload"/>.
/// </para>
/// </summary>
public sealed class DiskNuGetPackageStore : INuGetPackageStore
{
    const string UnlistedMarker = ".unlisted";

    readonly string root;
    readonly SemaphoreSlim gate = new(1, 1);
    Dictionary<string, Dictionary<NuGetVersion, NuGetPackage>>? index;

    public DiskNuGetPackageStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        this.root = Path.GetFullPath(root);
        Directory.CreateDirectory(this.root);
    }

    /// <summary>The directory the packages are kept in.</summary>
    public string RootPath => this.root;

    /// <summary>Forgets what was read from disk, so the next request reads it again.</summary>
    public void Reload() => Volatile.Write(ref this.index, null);

    public async ValueTask<IReadOnlyList<NuGetPackage>> GetVersionsAsync(string id, CancellationToken cancellationToken)
    {
        var index = await this.GetIndexAsync(cancellationToken).ConfigureAwait(false);

        lock (index)
            return index.TryGetValue(id, out var versions) ? [.. versions.Values] : [];
    }

    public async ValueTask<IReadOnlyList<NuGetPackage>> GetAllAsync(CancellationToken cancellationToken)
    {
        var index = await this.GetIndexAsync(cancellationToken).ConfigureAwait(false);

        lock (index)
            return [.. index.Values.SelectMany(v => v.Values)];
    }

    public async ValueTask<bool> AddAsync(NuGetPackage package, Stream nupkg, ReadOnlyMemory<byte> manifest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(nupkg);

        var index = await this.GetIndexAsync(cancellationToken).ConfigureAwait(false);
        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            lock (index)
            {
                if (index.TryGetValue(package.Id, out var existing) && existing.ContainsKey(package.Version))
                    return false;
            }

            var (id, version) = Names(package.Id, package.Version);
            var directory = Path.Combine(this.root, id, version);
            var packagePath = Path.Combine(directory, $"{id}.{version}.nupkg");

            Directory.CreateDirectory(directory);

            // The .nupkg is what marks a version as present - when the directory is read back, a
            // folder without one is ignored - so it is moved into place last and in one step. A
            // crash part way leaves a folder that is invisible, not a package that is half there.
            var temp = Path.Combine(directory, "." + Guid.NewGuid().ToString("n") + ".tmp");
            long size;
            string sha512;

            try
            {
                await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    await nupkg.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
                    size = file.Length;
                }

                sha512 = package.Sha512 ?? await HashAsync(temp, cancellationToken).ConfigureAwait(false);

                await File.WriteAllBytesAsync(Path.Combine(directory, $"{id}.nuspec"), manifest.ToArray(), cancellationToken).ConfigureAwait(false);
                await File.WriteAllTextAsync(packagePath + ".sha512", sha512, cancellationToken).ConfigureAwait(false);

                if (!package.Listed)
                    await File.WriteAllBytesAsync(Path.Combine(directory, UnlistedMarker), [], cancellationToken).ConfigureAwait(false);

                File.Move(temp, packagePath);
            }
            finally
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }

            // The push time is the file's time, so it survives a restart without a database.
            if (package.Published != default)
                File.SetLastWriteTimeUtc(packagePath, package.Published.UtcDateTime);

            var stored = package with
            {
                Size = size,
                Sha512 = sha512,
                Published = package.Published != default ? package.Published : File.GetLastWriteTimeUtc(packagePath)
            };

            lock (index)
            {
                if (!index.TryGetValue(stored.Id, out var versions))
                    index[stored.Id] = versions = [];

                versions[stored.Version] = stored;
            }

            return true;
        }
        finally
        {
            this.gate.Release();
        }
    }

    public ValueTask<Stream?> OpenPackageAsync(string id, NuGetVersion version, CancellationToken cancellationToken)
    {
        var (lowerId, lowerVersion) = Names(id, version);
        return Open(Path.Combine(this.root, lowerId, lowerVersion, $"{lowerId}.{lowerVersion}.nupkg"));
    }

    public ValueTask<Stream?> OpenManifestAsync(string id, NuGetVersion version, CancellationToken cancellationToken)
    {
        var (lowerId, lowerVersion) = Names(id, version);
        var directory = Path.Combine(this.root, lowerId, lowerVersion);

        // Only a version that is really there: the nuspec is written before the package is moved
        // into place, so it alone does not mean the push finished.
        return File.Exists(Path.Combine(directory, $"{lowerId}.{lowerVersion}.nupkg"))
            ? Open(Path.Combine(directory, $"{lowerId}.nuspec"))
            : ValueTask.FromResult<Stream?>(null);
    }

    public async ValueTask<bool> SetListedAsync(string id, NuGetVersion version, bool listed, CancellationToken cancellationToken)
    {
        var index = await this.GetIndexAsync(cancellationToken).ConfigureAwait(false);
        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            NuGetPackage? current;

            lock (index)
                current = index.TryGetValue(id, out var versions) ? versions.GetValueOrDefault(version) : null;

            if (current is null)
                return false;

            var (lowerId, lowerVersion) = Names(id, version);
            var marker = Path.Combine(this.root, lowerId, lowerVersion, UnlistedMarker);

            if (listed)
                File.Delete(marker);
            else if (!File.Exists(marker))
                await File.WriteAllBytesAsync(marker, [], cancellationToken).ConfigureAwait(false);

            lock (index)
                index[current.Id][current.Version] = current with { Listed = listed };

            return true;
        }
        finally
        {
            this.gate.Release();
        }
    }

    public async ValueTask<bool> DeleteAsync(string id, NuGetVersion version, CancellationToken cancellationToken)
    {
        var index = await this.GetIndexAsync(cancellationToken).ConfigureAwait(false);
        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            NuGetPackage? current;

            lock (index)
                current = index.TryGetValue(id, out var versions) ? versions.GetValueOrDefault(version) : null;

            if (current is null)
                return false;

            var (lowerId, lowerVersion) = Names(id, version);
            var idDirectory = Path.Combine(this.root, lowerId);
            var directory = Path.Combine(idDirectory, lowerVersion);

            // The package first, so if the rest fails the version is already gone rather than
            // half-deleted and still advertised on the next read.
            File.Delete(Path.Combine(directory, $"{lowerId}.{lowerVersion}.nupkg"));

            try
            {
                Directory.Delete(directory, recursive: true);

                if (!Directory.EnumerateFileSystemEntries(idDirectory).Any())
                    Directory.Delete(idDirectory);
            }
            catch (IOException)
            {
            }

            lock (index)
            {
                var versions = index[current.Id];
                versions.Remove(current.Version);

                if (versions.Count == 0)
                    index.Remove(current.Id);
            }

            return true;
        }
        finally
        {
            this.gate.Release();
        }
    }

    async ValueTask<Dictionary<string, Dictionary<NuGetVersion, NuGetPackage>>> GetIndexAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref this.index) is { } loaded)
            return loaded;

        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (this.index is { } raced)
                return raced;

            var built = new Dictionary<string, Dictionary<NuGetVersion, NuGetPackage>>(StringComparer.OrdinalIgnoreCase);

            foreach (var idDirectory in Directory.EnumerateDirectories(this.root))
            {
                foreach (var versionDirectory in Directory.EnumerateDirectories(idDirectory))
                {
                    if (await ReadAsync(versionDirectory, cancellationToken).ConfigureAwait(false) is not { } package)
                        continue;

                    if (!built.TryGetValue(package.Id, out var versions))
                        built[package.Id] = versions = [];

                    versions[package.Version] = package;
                }
            }

            Volatile.Write(ref this.index, built);
            return built;
        }
        finally
        {
            this.gate.Release();
        }
    }

    /// <summary>
    /// One version folder, or null when it does not hold a readable package - a folder left by a
    /// push that never finished, or one that was copied in incomplete.
    /// </summary>
    static async ValueTask<NuGetPackage?> ReadAsync(string directory, CancellationToken cancellationToken)
    {
        var packagePath = Directory.EnumerateFiles(directory, "*.nupkg").FirstOrDefault();

        if (packagePath is null)
            return null;

        try
        {
            var manifestPath = Directory.EnumerateFiles(directory, "*.nuspec").FirstOrDefault();
            byte[] manifest;

            if (manifestPath is not null)
            {
                manifest = await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await using var file = File.OpenRead(packagePath);
                manifest = NuspecReader.ExtractManifest(file);
            }

            var package = NuspecReader.Parse(manifest);
            var hashPath = packagePath + ".sha512";
            var sha512 = File.Exists(hashPath)
                ? (await File.ReadAllTextAsync(hashPath, cancellationToken).ConfigureAwait(false)).Trim()
                : await HashAsync(packagePath, cancellationToken).ConfigureAwait(false);

            return package with
            {
                Listed = !File.Exists(Path.Combine(directory, UnlistedMarker)),
                Published = File.GetLastWriteTimeUtc(packagePath),
                Size = new FileInfo(packagePath).Length,
                Sha512 = sha512
            };
        }
        catch (Exception ex) when (ex is NuGetFeedException or IOException or InvalidDataException)
        {
            return null;
        }
    }

    static async ValueTask<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var file = File.OpenRead(path);
        return Convert.ToBase64String(await SHA512.HashDataAsync(file, cancellationToken).ConfigureAwait(false));
    }

    static ValueTask<Stream?> Open(string path)
    {
        try
        {
            return ValueTask.FromResult<Stream?>(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, useAsync: true));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return ValueTask.FromResult<Stream?>(null);
        }
    }

    static (string Id, string Version) Names(string id, NuGetVersion version)
        => (id.ToLowerInvariant(), version.ToNormalizedString().ToLowerInvariant());
}
