namespace Shiny.Net.HttpServer.WebDav;

/// <summary>What a WebDAV mount exposes, and what it refuses to.</summary>
public sealed class WebDavOptions
{
    /// <summary>
    /// The directory being served. Everything is resolved inside it and nothing outside it is
    /// reachable — on a phone this is normally <c>FileSystem.AppDataDirectory</c>.
    /// </summary>
    public string RootPath { get; set; } = null!;

    /// <summary>
    /// Allows <c>PUT</c>, <c>MKCOL</c>, <c>COPY</c>, <c>PROPPATCH</c> and <c>LOCK</c>. Off by
    /// default: a read-only share is the version of this that cannot be used to fill a device's
    /// storage or replace a file something else depends on.
    /// <para>
    /// A mount without it still answers <c>OPTIONS</c>, <c>PROPFIND</c>, <c>GET</c> and
    /// <c>HEAD</c>, which is enough for a client to browse and read it.
    /// </para>
    /// </summary>
    public bool AllowWrite { get; set; }

    /// <summary>
    /// Allows <c>DELETE</c>. Off by default, for the same reason — and note that unlike the file
    /// browser, this one deletes a collection's whole subtree, because a client that drags a folder
    /// to the trash expects what is in it to go too.
    /// </summary>
    public bool AllowDelete { get; set; }

    /// <summary><c>MOVE</c> needs both: it writes at the destination and removes the source.</summary>
    internal bool AllowMove => this.AllowWrite && this.AllowDelete;

    /// <summary>
    /// Advertises and serves compliance class 2 — <c>LOCK</c> and <c>UNLOCK</c>. On by default,
    /// because it is not really optional in practice: Finder and the Windows redirector both mount
    /// a class 1 server read-only, whatever <see cref="AllowWrite"/> says.
    /// <para>
    /// Locks live in memory and belong to the mount, so they do not survive a restart. That is the
    /// right lifetime for an embedded server, whose process is the thing being restarted.
    /// </para>
    /// </summary>
    public bool EnableLocking { get; set; } = true;

    /// <summary>How long a lock lasts when the client did not ask for a specific duration.</summary>
    public TimeSpan DefaultLockTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The longest lock this will grant, whatever the client asked for. A client that takes a lock
    /// and then dies would otherwise hold it until the process ends.
    /// </summary>
    public TimeSpan MaxLockTimeout { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Largest file this will accept on a <c>PUT</c>. A device has finite storage and the caller is
    /// on the other side of a network, so this is a limit rather than a suggestion.
    /// </summary>
    public long MaxUploadBytes { get; set; } = 64 * 1024 * 1024;

    /// <summary>
    /// Largest XML body this will read on a <c>PROPFIND</c>, <c>PROPPATCH</c> or <c>LOCK</c>.
    /// These are small requests; anything approaching this is not one.
    /// </summary>
    public long MaxXmlBodyBytes { get; set; } = 1024 * 1024;

    /// <summary>
    /// Answers <c>PROPFIND</c> with <c>Depth: infinity</c>. Off by default, and RFC 4918 §9.1
    /// anticipates exactly that: the refusal is a 403 carrying
    /// <c>&lt;DAV:propfind-finite-depth/&gt;</c>, which tells the client to ask again with a depth
    /// it can bound. One unbounded walk of a device's storage is all it takes.
    /// <para>
    /// <c>DELETE</c>, <c>MOVE</c> and <c>COPY</c> are unaffected — those are recursive by
    /// definition and are gated by <see cref="AllowWrite"/> and <see cref="AllowDelete"/> instead.
    /// </para>
    /// </summary>
    public bool AllowInfiniteDepth { get; set; }

    /// <summary>
    /// Most resources a single <c>PROPFIND</c> will describe before answering 507. The response is
    /// built in memory so it can carry a Content-Length, and this is what bounds that.
    /// </summary>
    public int MaxPropFindResults { get; set; } = 50_000;

    /// <summary>
    /// Serves an HTML index when a browser <c>GET</c>s a collection. WebDAV says nothing about
    /// this; it is here because the first thing anyone does with a new mount is open it in a
    /// browser to see whether it works.
    /// </summary>
    public bool DirectoryBrowsing { get; set; } = true;

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

    /// <summary>
    /// The root collection's <c>displayname</c>. Some clients use it to label the mount; null uses
    /// the directory's own name.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Where dead properties — the ones a <c>PROPPATCH</c> sets that are not part of the protocol —
    /// are kept. In memory when left null, which means they are lost on restart.
    /// <para>
    /// Worth replacing if clients rely on them. Windows Explorer sets its own file-attribute
    /// properties on every write, and macOS keeps some metadata this way; losing those is untidy
    /// rather than fatal, which is why the default is the simple one.
    /// </para>
    /// </summary>
    public IWebDavPropertyStore? PropertyStore { get; set; }

    internal string ResolvedRoot => System.IO.Path.TrimEndingDirectorySeparator(
        System.IO.Path.GetFullPath(this.RootPath ?? throw new InvalidOperationException(
            $"{nameof(WebDavOptions)}.{nameof(this.RootPath)} is required."
        ))
    );
}
