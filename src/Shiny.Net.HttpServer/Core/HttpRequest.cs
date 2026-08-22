using System.IO.Pipelines;
using Shiny.Net.HttpServer.Http1;

namespace Shiny.Net.HttpServer;

/// <summary>
/// The inbound side of a request. Mirrors ASP.NET Core's <c>HttpRequest</c> closely enough that
/// handler code reads the same, including raw <see cref="Body"/> stream access for streaming
/// uploads and a <see cref="BodyReader"/> for pipeline-style reads.
/// </summary>
public sealed class HttpRequest
{
    Stream? body;

    internal HttpRequest(HttpContext context) => this.HttpContext = context;

    public HttpContext HttpContext { get; }

    /// <summary>The HTTP method, interned to a <see cref="HttpMethods"/> constant when known.</summary>
    public string Method { get; internal set; } = HttpMethods.Get;

    /// <summary>"http" or "https". Reflects the transport unless forwarded headers are honoured.</summary>
    public string Scheme { get; internal set; } = "http";

    public bool IsHttps => string.Equals(this.Scheme, "https", StringComparison.OrdinalIgnoreCase);

    /// <summary>The Host header value, including port when present.</summary>
    public string? Host { get; internal set; }

    /// <summary>The request protocol, e.g. "HTTP/1.1".</summary>
    public string Protocol { get; internal set; } = HttpProtocols.Http11;

    /// <summary>The decoded, absolute path. Always starts with '/'.</summary>
    public string Path { get; internal set; } = "/";

    /// <summary>The raw query string including the leading '?', or null when absent.</summary>
    public string? QueryString { get; internal set; }

    /// <summary>The raw request target exactly as it appeared on the request line.</summary>
    public string RawTarget { get; internal set; } = "/";

    public HeaderDictionary Headers { get; } = new();

    public QueryCollection Query { get; } = new();

    public RequestCookieCollection Cookies { get; } = new();

    /// <summary>Route parameters captured by the router. Empty when no route matched.</summary>
    public RouteValueDictionary RouteValues { get; } = new();

    public string? ContentType
    {
        get => this.Headers.ContentType;
        set => this.Headers.ContentType = value;
    }

    /// <summary>
    /// Declared body length. Null for chunked bodies (where the length is unknown up front)
    /// and for bodies that are absent entirely.
    /// </summary>
    public long? ContentLength => this.Headers.ContentLength;

    /// <summary>True when the request used chunked transfer encoding.</summary>
    public bool IsChunked { get; internal set; }

    /// <summary>
    /// The request body as a stream. As it arrives off the connection it is read-only and
    /// forward-only — suitable for streaming a large upload straight to disk without buffering.
    /// Never null; an empty stream when there is no body.
    /// <para>
    /// Settable so a middleware can put something else in front of the handler: the usual reason is
    /// to read the body once and hand a rewound <see cref="MemoryStream"/> on, which is what makes
    /// a body readable twice — by a recorder or a logger, and then by the handler. Assigning also
    /// discards any <see cref="BodyReader"/> already handed out, so the two never disagree about
    /// where the body starts.
    /// </para>
    /// </summary>
    public Stream Body
    {
        get => this.body ??= EmptyReadStream.Instance;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            this.body = value;

            // a reader built over the old stream would keep reading the old stream, and its
            // buffered-but-unconsumed bytes would be lost either way
            this.bodyReader = null;
        }
    }

    /// <summary>
    /// The body as a <see cref="PipeReader"/>, for zero-copy parsing of large payloads.
    /// </summary>
    public PipeReader BodyReader => this.bodyReader ??= PipeReader.Create(
        this.Body,
        new StreamPipeReaderOptions(leaveOpen: true)
    );
    PipeReader? bodyReader;

    /// <summary>True when a body is present (either a positive Content-Length or chunked).</summary>
    public bool HasBody => this.IsChunked || this.ContentLength > 0;

    internal void Reset()
    {
        this.Method = HttpMethods.Get;
        this.Scheme = "http";
        this.Host = null;
        this.Protocol = HttpProtocols.Http11;
        this.Path = "/";
        this.QueryString = null;
        this.RawTarget = "/";
        this.IsChunked = false;
        this.body = null;
        this.bodyReader = null;
        this.Headers.Reset();
        this.Query.Reset();
        this.Cookies.Reset();
        this.RouteValues.Reset();
    }
}
