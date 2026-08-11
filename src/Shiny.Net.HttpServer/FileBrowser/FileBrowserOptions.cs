using System.Text.Json.Serialization;

namespace Shiny.Net.HttpServer.FileBrowser;

/// <summary>One entry in a directory listing.</summary>
/// <param name="Name">The file or directory name, with no path.</param>
/// <param name="Path">Path relative to the browser's root, using forward slashes.</param>
/// <param name="IsDirectory">True for a directory, where <paramref name="Size"/> is zero.</param>
/// <param name="Size">Length in bytes.</param>
/// <param name="LastModifiedUtc">Last write time.</param>
/// <param name="ContentType">What a download of this file would be served as. Null for a directory.</param>
public sealed record FileEntry(
    string Name,
    string Path,
    bool IsDirectory,
    long Size,
    DateTimeOffset LastModifiedUtc,
    string? ContentType
);

/// <summary>A directory and what is in it.</summary>
/// <param name="Path">The directory's own path, relative to the root. Empty at the root.</param>
/// <param name="Entries">Directories first, then files, each alphabetically.</param>
public sealed record DirectoryListing(string Path, IReadOnlyList<FileEntry> Entries);

/// <summary>
/// Serialization metadata for the file browser's own responses.
/// <para>
/// Declared here so the module works without the app registering anything — the whole point of this
/// being a drop-in module is that wiring it up is one line.
/// </para>
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(FileEntry))]
[JsonSerializable(typeof(DirectoryListing))]
public partial class FileBrowserJsonContext : JsonSerializerContext;

/// <summary>What the file browser exposes, and what it refuses to.</summary>
public sealed class FileBrowserOptions
{
    /// <summary>
    /// The directory being served. Everything is resolved inside it and nothing outside it is
    /// reachable — on a phone this is normally
    /// <c>FileSystem.AppDataDirectory</c>.
    /// </summary>
    public string RootPath { get; set; } = null!;

    /// <summary>
    /// Allows <c>PUT</c>. Off by default: read-only is the version of this that cannot be used to
    /// fill a device's storage or replace a file something else depends on.
    /// </summary>
    public bool AllowWrite { get; set; }

    /// <summary>Allows <c>DELETE</c>. Off by default, for the same reason.</summary>
    public bool AllowDelete { get; set; }

    /// <summary>
    /// Allows creating directories with <c>PUT</c> on a path ending in <c>/</c>. Requires
    /// <see cref="AllowWrite"/>.
    /// </summary>
    public bool AllowCreateDirectories { get; set; } = true;

    /// <summary>
    /// Largest file this will accept on a write. A device has finite storage and the caller is on
    /// the other side of a network, so this is a limit rather than a suggestion.
    /// </summary>
    public long MaxUploadBytes { get; set; } = 64 * 1024 * 1024;

    /// <summary>
    /// Shows dotfiles. Off by default — a content directory routinely holds a <c>.env</c> or a
    /// database journal, and listing them is how they get fetched.
    /// </summary>
    public bool ServeHiddenFiles { get; set; }

    /// <summary>
    /// Decides whether an entry is visible at all, by path relative to the root. Return false to
    /// hide it from listings and refuse every operation on it.
    /// </summary>
    public Func<string, bool>? Filter { get; set; }

    /// <summary>
    /// Content type for a file whose extension is not recognised. Downloads only — this never
    /// affects what is stored.
    /// </summary>
    public string DefaultContentType { get; set; } = "application/octet-stream";

    internal string ResolvedRoot => System.IO.Path.TrimEndingDirectorySeparator(
        System.IO.Path.GetFullPath(this.RootPath ?? throw new InvalidOperationException(
            $"{nameof(FileBrowserOptions)}.{nameof(this.RootPath)} is required."
        ))
    );
}
