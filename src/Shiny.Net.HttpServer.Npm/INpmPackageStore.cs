using System.Text.Json;
using System.Text.Json.Serialization;
using Shiny.Net.HttpServer.Npm.Internal;

namespace Shiny.Net.HttpServer.Npm;

/// <summary>
/// Where a registry keeps packages. <see cref="DiskNpmPackageStore"/> is the one most apps want;
/// implement this to keep them in blob storage or a database.
/// <para>
/// The registry serialises every write to one package before it calls in here, and checks the
/// revision itself, so a store only has to read and write what it is given.
/// </para>
/// </summary>
public interface INpmPackageStore
{
    /// <summary>The package, or null when there is none.</summary>
    ValueTask<NpmPackageDocument?> GetAsync(string name, CancellationToken cancellationToken);

    /// <summary>Every package - what search runs over.</summary>
    ValueTask<IReadOnlyList<NpmPackageDocument>> GetAllAsync(CancellationToken cancellationToken);

    /// <summary>Creates or replaces a package's document.</summary>
    ValueTask SaveAsync(NpmPackageDocument document, CancellationToken cancellationToken);

    /// <summary>Removes a package's document and every tarball it has.</summary>
    ValueTask DeleteAsync(string name, CancellationToken cancellationToken);

    ValueTask SaveTarballAsync(string name, string file, ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    /// <summary>The tarball, or null when there is no such file.</summary>
    ValueTask<Stream?> OpenTarballAsync(string name, string file, CancellationToken cancellationToken);

    ValueTask DeleteTarballAsync(string name, string file, CancellationToken cancellationToken);
}

/// <summary>
/// Keeps each package in its own directory - <c>{root}/{name}/package.json</c> beside its tarballs,
/// and <c>{root}/@scope/{name}/</c> for a scoped one - so a package can be backed up, moved or
/// removed by hand. Documents are written to a temporary file and moved into place, so a crash
/// leaves the old one rather than half of a new one.
/// </summary>
public sealed class DiskNpmPackageStore : INpmPackageStore
{
    const string DocumentFile = "package.json";

    readonly string root;

    public DiskNpmPackageStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        this.root = Path.GetFullPath(root);
        Directory.CreateDirectory(this.root);
    }

    /// <summary>The directory packages are kept in.</summary>
    public string RootPath => this.root;

    public async ValueTask<NpmPackageDocument?> GetAsync(string name, CancellationToken cancellationToken)
    {
        var path = Path.Combine(this.PackageDirectory(name), DocumentFile);

        if (!File.Exists(path))
            return null;

        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, useAsync: true);
        return await JsonSerializer.DeserializeAsync(file, NpmStoreJson.Default.NpmPackageDocument, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<NpmPackageDocument>> GetAllAsync(CancellationToken cancellationToken)
    {
        var all = new List<NpmPackageDocument>();

        foreach (var top in Directory.EnumerateDirectories(this.root))
        {
            var name = Path.GetFileName(top);
            var candidates = name.StartsWith('@')
                ? Directory.EnumerateDirectories(top).Select(d => name + "/" + Path.GetFileName(d))
                : [name];

            foreach (var candidate in candidates)
            {
                if (NpmRules.IsValidName(candidate) && await this.GetAsync(candidate, cancellationToken).ConfigureAwait(false) is { } document)
                    all.Add(document);
            }
        }

        return all;
    }

    public async ValueTask SaveAsync(NpmPackageDocument document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);

        var directory = Directory.CreateDirectory(this.PackageDirectory(document.Name)).FullName;
        var path = Path.Combine(directory, DocumentFile);
        var temp = path + "." + Guid.NewGuid().ToString("n") + ".tmp";

        await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
        {
            await JsonSerializer.SerializeAsync(file, document, NpmStoreJson.Default.NpmPackageDocument, cancellationToken).ConfigureAwait(false);
            file.Flush(flushToDisk: true);
        }

        File.Move(temp, path, overwrite: true);
    }

    public ValueTask DeleteAsync(string name, CancellationToken cancellationToken)
    {
        var directory = this.PackageDirectory(name);

        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);

        // An emptied scope directory goes too.
        var parent = Path.GetDirectoryName(directory)!;

        if (!string.Equals(parent, this.root, StringComparison.Ordinal) && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
            Directory.Delete(parent);

        return ValueTask.CompletedTask;
    }

    public async ValueTask SaveTarballAsync(string name, string file, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        var directory = Directory.CreateDirectory(this.PackageDirectory(name)).FullName;
        var path = this.TarballPath(name, file);
        var temp = Path.Combine(directory, "." + Guid.NewGuid().ToString("n") + ".tmp");

        await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, path, overwrite: true);
    }

    public ValueTask<Stream?> OpenTarballAsync(string name, string file, CancellationToken cancellationToken)
    {
        try
        {
            return ValueTask.FromResult<Stream?>(new FileStream(this.TarballPath(name, file), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, useAsync: true));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return ValueTask.FromResult<Stream?>(null);
        }
    }

    public ValueTask DeleteTarballAsync(string name, string file, CancellationToken cancellationToken)
    {
        File.Delete(this.TarballPath(name, file));
        return ValueTask.CompletedTask;
    }

    string PackageDirectory(string name)
    {
        // The name becomes a path, so nothing but a valid one gets this far.
        if (!NpmRules.IsValidName(name))
            throw new ArgumentException($"'{name}' is not a valid package name.", nameof(name));

        return Path.Combine(this.root, name.Replace('/', Path.DirectorySeparatorChar));
    }

    string TarballPath(string name, string file)
    {
        if (file.Contains('/') || file.Contains('\\') || file.StartsWith('.') || !file.EndsWith(".tgz", StringComparison.Ordinal) || file == DocumentFile)
            throw new ArgumentException($"'{file}' is not a tarball name.", nameof(file));

        return Path.Combine(this.PackageDirectory(name), file);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(NpmPackageDocument))]
sealed partial class NpmStoreJson : JsonSerializerContext;
