namespace Shiny.Net.HttpServer.WebDav;

/// <summary>
/// What a WebDAV mount is serving: a tree of collections and files, addressed by path.
/// <para>
/// A mount answers the protocol - hrefs, depths, locks, <c>If</c> headers, the multistatus
/// XML - and this answers what is actually there. <see cref="PhysicalWebDavFileSystem"/> is a
/// directory on disk and is what <see cref="WebDavOptions.RootPath"/> builds. Implement this when
/// what a mount should show is not one directory: an app whose storage is a view over several
/// roots, a folder a platform only lets it reach through its own API, or something that is not
/// files at all.
/// </para>
/// <para>
/// <b>Paths</b> are relative to the mount root, separated by <c>/</c>, with no leading slash, and
/// the root itself is the empty string. By the time one gets here it has been percent-decoded,
/// checked for <c>.</c> and <c>..</c> segments, for dotfiles (unless
/// <see cref="WebDavOptions.ServeHiddenFiles"/>) and against <see cref="WebDavOptions.Filter"/> -
/// so an implementation never sees a path that climbs. What it still owns is anything only it can
/// know, such as a link that leads out of whatever it treats as its root.
/// </para>
/// <para>
/// <b>Refusing.</b> Throw <see cref="WebDavException"/> with the status the client should get.
/// The mount also answers <see cref="UnauthorizedAccessException"/> with 403,
/// <see cref="FileNotFoundException"/> and <see cref="DirectoryNotFoundException"/> with 404, and
/// any other <see cref="IOException"/> with 409 - the ones <c>System.IO</c> throws, so an
/// implementation over the file system rarely needs to translate anything.
/// </para>
/// <para>
/// <b>Why the reads are synchronous.</b> <see cref="GetEntry"/> and <see cref="GetChildren"/> are
/// asked from inside the evaluation of an <c>If</c> header, which is synchronous, and a
/// <c>PROPFIND</c> asks them once per resource it describes. They are metadata, which every file
/// system answers without waiting on the bytes; the members that move bytes are asynchronous.
/// </para>
/// </summary>
public interface IWebDavFileSystem
{
    /// <summary>
    /// What is at <paramref name="path"/>, or null when nothing is. Must answer for the root
    /// (<c>""</c>), which must be a collection.
    /// </summary>
    WebDavEntry? GetEntry(string path);

    /// <summary>
    /// The members of the collection at <paramref name="path"/>, in any order - the mount sorts
    /// them. Each entry's <see cref="WebDavEntry.Name"/> is the segment that, appended to
    /// <paramref name="path"/>, addresses it.
    /// </summary>
    IEnumerable<WebDavEntry> GetChildren(string path);

    /// <summary>
    /// Opens a file for reading. A seekable stream is what makes range requests work, and its
    /// <see cref="Stream.Length"/> is served as the <c>Content-Length</c> in preference to
    /// <see cref="WebDavEntry.Length"/> - so a file whose bytes are produced on demand, and whose
    /// size is only known once they have been, is served correctly by handing back a buffered one.
    /// </summary>
    ValueTask<Stream> OpenReadAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// Creates or replaces the file at <paramref name="path"/> with <paramref name="content"/>. The
    /// parent collection is known to exist. Write somewhere else first and move into place where
    /// that is possible: the stream is a request body, and a client that stops sending halfway
    /// should not leave half a file where a whole one used to be.
    /// <para>
    /// The stream enforces <see cref="WebDavOptions.MaxUploadBytes"/> itself, by throwing a
    /// <see cref="WebDavException"/> once it is passed - let that propagate.
    /// </para>
    /// </summary>
    ValueTask WriteAsync(string path, Stream content, CancellationToken cancellationToken);

    /// <summary>Creates a collection. The parent is known to exist and the path to be free.</summary>
    ValueTask CreateDirectoryAsync(string path, CancellationToken cancellationToken);

    /// <summary>Removes a file, or a collection and everything in it.</summary>
    ValueTask DeleteAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// Moves a file or collection. The destination's parent exists and the destination itself does
    /// not - an overwriting <c>MOVE</c> has already deleted it.
    /// </summary>
    ValueTask MoveAsync(string source, string destination, CancellationToken cancellationToken);

    /// <summary>
    /// Copies a file or collection, on the same terms as <see cref="MoveAsync"/>.
    /// </summary>
    /// <param name="recursive">
    /// False only for a collection copied with <c>Depth: 0</c>, which RFC 4918 §9.8.3 defines as
    /// the collection without its members.
    /// </param>
    ValueTask CopyAsync(string source, string destination, bool recursive, CancellationToken cancellationToken);

    /// <summary>
    /// Room left and room used where <paramref name="path"/> lives, for RFC 4331 quota - or null
    /// when there is no answer, and the properties are then left out.
    /// </summary>
    WebDavQuota? GetQuota(string path);
}

/// <summary>One file or collection, as a <see cref="IWebDavFileSystem"/> describes it.</summary>
/// <param name="Name">
/// The path segment this entry is addressed by. Also what a client is shown, unless
/// <see cref="DisplayName"/> says otherwise.
/// </param>
/// <param name="IsCollection">A folder, rather than a file.</param>
/// <param name="Length">Bytes, for a file. Ignored for a collection.</param>
/// <param name="CreatedUtc">Reported as <c>creationdate</c>.</param>
/// <param name="LastModifiedUtc">Reported as <c>getlastmodified</c>, and half of the default ETag.</param>
public sealed record WebDavEntry(
    string Name,
    bool IsCollection,
    long Length,
    DateTimeOffset CreatedUtc,
    DateTimeOffset LastModifiedUtc
)
{
    /// <summary>
    /// What to call it where that differs from the segment - the <c>displayname</c> property and
    /// the browser listing. Null uses <see cref="Name"/>.
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>The content type, when the file system knows better than the extension does.</summary>
    public string? ContentType { get; init; }

    /// <summary>
    /// A quoted entity tag. Null builds one from <see cref="LastModifiedUtc"/> and
    /// <see cref="Length"/>, which is the shape the static file handler uses, so a file fetched over
    /// one and written back over the other is recognised as the same entity.
    /// </summary>
    public string? ETag { get; init; }

    /// <summary>
    /// Hidden by the platform's own rules, beyond a leading dot. Treated as a dotfile is:
    /// left out unless <see cref="WebDavOptions.ServeHiddenFiles"/> is on.
    /// </summary>
    public bool IsHidden { get; init; }

    /// <summary>
    /// A link to somewhere else in the tree. Listed like anything else, but an infinite-depth
    /// <c>PROPFIND</c> does not walk into it - a link to one of its own ancestors would otherwise
    /// be a walk that only the result cap ends.
    /// </summary>
    public bool IsLink { get; init; }
}

/// <summary>RFC 4331 quota: what is left, and what is used, in bytes.</summary>
public readonly record struct WebDavQuota(long AvailableBytes, long UsedBytes);

/// <summary>
/// A refusal with the status the client should see, thrown from a <see cref="IWebDavFileSystem"/>.
/// <para>
/// The message is not sent: a WebDAV client shows the status as an error of its own, and a
/// sentence written for a file system's log is not written for whoever is looking at Finder.
/// </para>
/// </summary>
public sealed class WebDavException : Exception
{
    public WebDavException(int statusCode, string? message = null, Exception? innerException = null)
        : base(message ?? $"WebDAV request refused with {statusCode}.", innerException)
    {
        this.StatusCode = statusCode;
    }

    /// <summary>The HTTP status to answer with.</summary>
    public int StatusCode { get; }
}
