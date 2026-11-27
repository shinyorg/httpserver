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

    // The registry serialises writes per package, not per scope, so a publish of @scope/b can run
    // while the last package of @scope/a is being removed. Creating a package directory and removing
    // an emptied scope directory are each a check followed by an act on the shared parent, and they
    // hold this so neither acts on a check the other has made stale.
    readonly Lock directories = new();

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
        FileStream file;

        // Opened straight away rather than checked first: an unpublish between a File.Exists and the
        // open turned what should be a 404 into a 500.
        try
        {
            file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, useAsync: true);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }

        await using (file.ConfigureAwait(false))
            return await JsonSerializer.DeserializeAsync(file, NpmStoreJson.Default.NpmPackageDocument, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<NpmPackageDocument>> GetAllAsync(CancellationToken cancellationToken)
    {
        var all = new List<NpmPackageDocument>();

        foreach (var top in Directory.EnumerateDirectories(this.root))
        {
            var name = Path.GetFileName(top);
            var candidates = name.StartsWith('@')
                ? ScopeMembers(top).Select(d => name + "/" + Path.GetFileName(d))
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

        var directory = this.CreatePackageDirectory(document.Name);
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

        if (!string.Equals(parent, this.root, StringComparison.Ordinal))
        {
            lock (this.directories)
            {
                if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
                {
                    this.BeforeScopeRemoved?.Invoke();
                    Directory.Delete(parent);
                }
            }
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask SaveTarballAsync(string name, string file, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        var directory = this.CreatePackageDirectory(name);
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

    /// <summary>Test hook: runs with the directory lock held, just before an emptied scope directory is removed.</summary>
    internal Action? BeforeScopeRemoved { get; set; }

    /// <summary>
    /// Creates a package's directory, and its scope's. Once the package directory exists the scope
    /// directory is no longer empty, so a removal checking it afterwards leaves it alone.
    /// </summary>
    string CreatePackageDirectory(string name)
    {
        var path = this.PackageDirectory(name);

        lock (this.directories)
            return Directory.CreateDirectory(path).FullName;
    }

    /// <summary>The packages in a scope directory, or none when it was removed while being read.</summary>
    static List<string> ScopeMembers(string scope)
    {
        try
        {
            return [.. Directory.EnumerateDirectories(scope)];
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
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
