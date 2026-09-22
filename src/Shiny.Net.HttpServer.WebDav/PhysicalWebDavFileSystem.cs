using Shiny.Net.HttpServer.StaticFiles;

namespace Shiny.Net.HttpServer.WebDav;

/// <summary>
/// A directory on disk, served as a WebDAV mount - what <see cref="WebDavOptions.RootPath"/>
/// builds when no <see cref="WebDavOptions.FileSystem"/> is given.
/// <para>
/// Everything is resolved inside the root and nothing outside it is reachable, links included: a
/// path whose any existing segment is a link leading out of the root does not resolve, which is
/// what stops a symlink inside a shared folder from turning into a way out of it.
/// </para>
/// </summary>
public sealed class PhysicalWebDavFileSystem : IWebDavFileSystem
{
    readonly string root;

    /// <exception cref="DirectoryNotFoundException">The directory does not exist.</exception>
    public PhysicalWebDavFileSystem(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        this.root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));

        if (!Directory.Exists(this.root))
            throw new DirectoryNotFoundException($"The WebDAV root '{this.root}' does not exist.");
    }

    /// <summary>The absolute path being served.</summary>
    public string RootPath => this.root;

    public WebDavEntry? GetEntry(string path)
    {
        if (!this.TryResolve(path, out var full))
            return null;

        if (Directory.Exists(full))
            return Describe(new DirectoryInfo(full));

        if (File.Exists(full))
            return Describe(new FileInfo(full));

        return null;
    }

    public IEnumerable<WebDavEntry> GetChildren(string path)
    {
        if (!this.TryResolve(path, out var full) || !Directory.Exists(full))
            yield break;

        foreach (var child in new DirectoryInfo(full).EnumerateFileSystemInfos())
            yield return Describe(child);
    }

    public ValueTask<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
    {
        Stream stream = new FileStream(
            this.Resolve(path),
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            }
        );

        return new ValueTask<Stream>(stream);
    }

    public async ValueTask WriteAsync(string path, Stream content, CancellationToken cancellationToken)
    {
        var full = this.Resolve(path);

        // Written to a temporary file and moved into place, so a failed or abandoned upload cannot
        // leave a half-written file where a whole one used to be.
        var staging = full + ".webdav-" + Guid.NewGuid().ToString("n")[..8];

        try
        {
            var file = new FileStream(
                staging,
                new FileStreamOptions
                {
                    Mode = FileMode.Create,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                }
            );

            await using (file.ConfigureAwait(false))
                await content.CopyToAsync(file, 64 * 1024, cancellationToken).ConfigureAwait(false);

            File.Move(staging, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(staging))
                File.Delete(staging);
        }
    }

    public ValueTask CreateDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(this.Resolve(path));
        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteAsync(string path, CancellationToken cancellationToken)
    {
        var full = this.Resolve(path);

        if (Directory.Exists(full))
            Directory.Delete(full, recursive: true);
        else
            File.Delete(full);

        return ValueTask.CompletedTask;
    }

    public ValueTask MoveAsync(string source, string destination, CancellationToken cancellationToken)
    {
        var from = this.Resolve(source);
        var to = this.Resolve(destination);

        if (Directory.Exists(from))
            Directory.Move(from, to);
        else
            File.Move(from, to);

        return ValueTask.CompletedTask;
    }

    public ValueTask CopyAsync(string source, string destination, bool recursive, CancellationToken cancellationToken)
    {
        var from = this.Resolve(source);
        var to = this.Resolve(destination);

        if (Directory.Exists(from))
            CopyTree(from, to, recursive);
        else
            File.Copy(from, to, overwrite: false);

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The volume the root is on - not the size of the subtree, which is what RFC 4331's wording
    /// suggests and what walking it would cost. Clients ask for this to draw a "space free" figure,
    /// and the volume's is both the number they mean and the one that is true.
    /// </summary>
    public WebDavQuota? GetQuota(string path)
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(this.root) ?? this.root);

            return new WebDavQuota(drive.AvailableFreeSpace, drive.TotalSize - drive.AvailableFreeSpace);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // A volume that will not answer is not a failed request. The properties are simply
            // absent, and a client that wanted a number falls back to not showing one.
            return null;
        }
    }

    static WebDavEntry Describe(FileSystemInfo info)
        => new(
            info.Name,
            info is DirectoryInfo,
            info is FileInfo file ? file.Length : 0,
            new DateTimeOffset(info.CreationTimeUtc, TimeSpan.Zero),
            new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)
        )
        {
            IsHidden = info.Attributes.HasFlag(FileAttributes.Hidden),
            IsLink = info.Attributes.HasFlag(FileAttributes.ReparsePoint)
        };

    static void CopyTree(string source, string destination, bool recursive)
    {
        Directory.CreateDirectory(destination);

        if (!recursive)
            return;

        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            // Not descending into a link keeps a cycle inside the root from turning a copy into an
            // unbounded one.
            if (new DirectoryInfo(directory).Attributes.HasFlag(FileAttributes.ReparsePoint))
                continue;

            CopyTree(directory, Path.Combine(destination, Path.GetFileName(directory)), recursive: true);
        }
    }

    string Resolve(string path)
        => this.TryResolve(path, out var full)
            ? full
            : throw new UnauthorizedAccessException($"'{path}' is outside the WebDAV root.");

    /// <summary>
    /// Maps a mount path onto the disk, refusing anything that leaves the root.
    /// <para>
    /// Every existing segment is checked for a link, not only the last one. A new file under a
    /// linked folder does not exist yet, so a check of the final path alone finds nothing to
    /// follow - and the write then lands wherever the folder's link points.
    /// </para>
    /// </summary>
    bool TryResolve(string path, out string full)
    {
        full = this.root;

        if (path.Length == 0)
            return true;

        var candidate = Path.GetFullPath(Path.Combine(this.root, path.Replace('/', Path.DirectorySeparatorChar)));

        if (!this.IsInsideRoot(candidate))
            return false;

        var walked = this.root;

        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            walked = Path.Combine(walked, segment);

            FileSystemInfo info = Directory.Exists(walked) ? new DirectoryInfo(walked) : new FileInfo(walked);

            // Nothing below a segment that is not there can be a link either.
            if (!info.Exists)
                break;

            if (info.LinkTarget is not null &&
                info.ResolveLinkTarget(returnFinalTarget: true) is { } target &&
                !this.IsInsideRoot(target.FullName))
                return false;
        }

        full = candidate;
        return true;
    }

    bool IsInsideRoot(string fullPath)
        => fullPath.StartsWith(this.root + Path.DirectorySeparatorChar, StaticFilePath.PathComparison)
            || string.Equals(fullPath, this.root, StaticFilePath.PathComparison);
}
