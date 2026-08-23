namespace Shiny.Net.HttpServer.Logging;

/// <summary>
/// The fields a W3C extended log line can carry, in the order they are written.
/// <para>
/// The names are the ones the format defines, so a log written here opens in the same tools that
/// read IIS logs — the prefixes are the spec's: <c>c-</c> is the client, <c>s-</c> the server,
/// <c>cs-</c> client to server, <c>sc-</c> server to client.
/// </para>
/// </summary>
[Flags]
public enum W3CLoggingFields
{
    None = 0,

    /// <summary><c>date</c> — UTC, <c>yyyy-MM-dd</c>.</summary>
    Date = 1 << 0,

    /// <summary><c>time</c> — UTC, <c>HH:mm:ss</c>.</summary>
    Time = 1 << 1,

    /// <summary><c>c-ip</c> — the caller's address.</summary>
    ClientIpAddress = 1 << 2,

    /// <summary><c>cs-username</c> — the authenticated name, when there is one.</summary>
    UserName = 1 << 3,

    /// <summary><c>s-ip</c> — the local address the connection arrived on.</summary>
    ServerIpAddress = 1 << 4,

    /// <summary><c>s-port</c> — the local port.</summary>
    ServerPort = 1 << 5,

    /// <summary><c>cs-method</c>.</summary>
    Method = 1 << 6,

    /// <summary><c>cs-uri-stem</c> — the path, without the query.</summary>
    UriStem = 1 << 7,

    /// <summary><c>cs-uri-query</c> — the query, without the leading '?'.</summary>
    UriQuery = 1 << 8,

    /// <summary><c>sc-status</c>.</summary>
    ProtocolStatus = 1 << 9,

    /// <summary><c>sc-bytes</c> — response body bytes, when the length was known.</summary>
    BytesSent = 1 << 10,

    /// <summary><c>cs-bytes</c> — request body bytes, from <c>Content-Length</c>.</summary>
    BytesReceived = 1 << 11,

    /// <summary><c>time-taken</c> — milliseconds.</summary>
    TimeTaken = 1 << 12,

    /// <summary><c>cs-version</c> — HTTP/1.1, HTTP/2, HTTP/3.</summary>
    ProtocolVersion = 1 << 13,

    /// <summary><c>cs-host</c>.</summary>
    Host = 1 << 14,

    /// <summary><c>cs(User-Agent)</c>.</summary>
    UserAgent = 1 << 15,

    /// <summary><c>cs(Referer)</c>.</summary>
    Referer = 1 << 16,

    /// <summary><c>cs(Cookie)</c>. Off by default — see <see cref="W3CLoggerOptions.Fields"/>.</summary>
    Cookie = 1 << 17,

    /// <summary><c>x-route</c> — the route template the router matched. Not a W3C field; the <c>x-</c> prefix is how the format says to add one.</summary>
    Route = 1 << 18,

    /// <summary><c>x-connection-id</c> — ties the lines from one connection together.</summary>
    ConnectionId = 1 << 19,

    /// <summary>Everything except <see cref="Cookie"/>.</summary>
    Default = Date | Time | ClientIpAddress | UserName | ServerIpAddress | ServerPort | Method
        | UriStem | UriQuery | ProtocolStatus | BytesSent | BytesReceived | TimeTaken
        | ProtocolVersion | Host | UserAgent | Referer,

    All = Default | Cookie | Route | ConnectionId
}

/// <summary>What goes in the log, and where the file lives.</summary>
public sealed class W3CLoggerOptions
{
    string logDirectory = Path.Combine(AppContext.BaseDirectory, "logs");

    /// <summary>
    /// Which fields to write. <see cref="W3CLoggingFields.Default"/> is everything except the cookie
    /// header, which carries session tokens and has no business in a file that gets copied around.
    /// </summary>
    public W3CLoggingFields Fields { get; set; } = W3CLoggingFields.Default;

    /// <summary>
    /// Where the files go. Created if it does not exist.
    /// <para>
    /// On a device this must be somewhere the app can actually write —
    /// <c>FileSystem.AppDataDirectory</c> in MAUI. The default is a <c>logs</c> folder beside the
    /// app, which is right for a console host and wrong for a sandboxed one.
    /// </para>
    /// </summary>
    public string LogDirectory
    {
        get => this.logDirectory;
        set
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            this.logDirectory = value;
        }
    }

    /// <summary>Start of each file name. The date and a counter follow it.</summary>
    public string FileNamePrefix { get; set; } = "w3clog-";

    /// <summary>
    /// Roll to a new file past this many bytes. Small by server standards on purpose: this runs on
    /// devices, and one enormous file is the hardest kind to get off one.
    /// </summary>
    public long FileSizeLimit { get; set; } = 10 * 1024 * 1024;

    /// <summary>
    /// How many files to keep. The oldest are deleted past this.
    /// <para>
    /// A log that grows forever on a phone is a bug report about storage, so there is no "keep
    /// everything" setting — raise the number instead.
    /// </para>
    /// </summary>
    public int RetainedFileCountLimit { get; set; } = 4;

    /// <summary>How often the queued lines are written to disk.</summary>
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How many lines may wait to be written before new ones are dropped.
    /// <para>
    /// Dropped, not blocked: a request must never wait on a disk that is busy or full. The count of
    /// what was dropped is written into the file when it recovers, so the gap is visible rather
    /// than silent.
    /// </para>
    /// </summary>
    public int MaxQueuedLines { get; set; } = 4096;

    /// <summary>Additional request headers to log, each as its own <c>cs(Name)</c> field.</summary>
    public IList<string> AdditionalRequestHeaders { get; } = [];

    /// <summary>
    /// Decides whether a request is logged at all. The way to keep a health probe polled every
    /// second, or a static-file flood, out of the file.
    /// </summary>
    public Func<HttpContext, bool>? ShouldLog { get; set; }
}
