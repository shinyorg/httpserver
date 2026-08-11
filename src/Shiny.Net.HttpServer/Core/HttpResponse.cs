using System.Buffers;
using System.IO.Pipelines;
using System.Text;

namespace Shiny.Net.HttpServer;

/// <summary>
/// The outbound side of a request. Set <see cref="StatusCode"/>, add headers, then write the body
/// however you like: raw bytes, a string, a <see cref="Stream"/> copy, or straight into
/// <see cref="BodyWriter"/>. Headers are flushed automatically on the first body write, so set
/// them before you start writing.
/// </summary>
public sealed class HttpResponse
{
    IResponseBodyControl control = NullResponseBodyControl.Instance;

    internal HttpResponse(HttpContext context)
    {
        this.HttpContext = context;
        this.Cookies = new ResponseCookies(this);
    }

    public HttpContext HttpContext { get; }

    public HeaderDictionary Headers { get; } = new();

    public ResponseCookies Cookies { get; }

    public int StatusCode { get; set; } = StatusCodes.Status200OK;

    /// <summary>
    /// Optional reason phrase. Left null, a standard phrase for <see cref="StatusCode"/> is used.
    /// </summary>
    public string? ReasonPhrase { get; set; }

    /// <summary>
    /// Setting this writes a Content-Length header and switches off chunked encoding. Leave it
    /// null to stream a response of unknown length (chunked on HTTP/1.1).
    /// </summary>
    public long? ContentLength
    {
        get => this.Headers.ContentLength;
        set => this.Headers.ContentLength = value;
    }

    public string? ContentType
    {
        get => this.Headers.ContentType;
        set => this.Headers.ContentType = value;
    }

    /// <summary>
    /// True once the status line and headers have gone out. After this point headers are frozen
    /// and the status code can no longer change.
    /// </summary>
    public bool HasStarted => this.control.HasStarted;

    /// <summary>
    /// The response body as a stream. Writes are forwarded to the connection, chunk-framed when
    /// no <see cref="ContentLength"/> was set.
    /// </summary>
    public Stream Body => this.control.Stream;

    /// <summary>The response body as a <see cref="PipeWriter"/> for allocation-free writes.</summary>
    public PipeWriter BodyWriter => this.control.Writer;

    /// <summary>
    /// Flushes the status line and headers without writing any body. Useful for long-lived
    /// streaming responses (SSE, for example) where the client should see headers immediately.
    /// </summary>
    public ValueTask StartAsync(CancellationToken cancellationToken = default)
        => this.control.StartAsync(cancellationToken);

    /// <summary>
    /// Registers a callback invoked immediately before headers go to the wire. The last chance to
    /// mutate <see cref="Headers"/> or <see cref="StatusCode"/>.
    /// </summary>
    public void OnStarting(Func<ValueTask> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        (this.onStarting ??= new List<Func<ValueTask>>()).Add(callback);
    }

    List<Func<ValueTask>>? onStarting;

    // ---- Convenience writers. Each sets Content-Length so the common case avoids chunking. ----

    /// <summary>
    /// Writes a UTF-8 string body. The name ASP.NET Core uses, kept as an alias for
    /// <see cref="WriteTextAsync"/> so handler code copies across unchanged.
    /// </summary>
    public ValueTask WriteAsync(
        string text,
        string contentType = "text/plain; charset=utf-8",
        CancellationToken cancellationToken = default
    ) => this.WriteTextAsync(text, contentType, cancellationToken);

    /// <summary>Writes a UTF-8 string body and completes the response.</summary>
    public async ValueTask WriteTextAsync(
        string text,
        string contentType = "text/plain; charset=utf-8",
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(text);

        var byteCount = Encoding.UTF8.GetByteCount(text);
        if (!this.HasStarted)
        {
            this.ContentType ??= contentType;
            this.ContentLength = byteCount;
        }

        var writer = this.BodyWriter;
        var span = writer.GetSpan(byteCount);
        var written = Encoding.UTF8.GetBytes(text, span);
        writer.Advance(written);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes a raw byte body and completes the response.</summary>
    public async ValueTask WriteBytesAsync(
        ReadOnlyMemory<byte> bytes,
        string? contentType = null,
        CancellationToken cancellationToken = default
    )
    {
        if (!this.HasStarted)
        {
            if (contentType is not null)
                this.ContentType ??= contentType;

            this.ContentLength = bytes.Length;
        }

        var writer = this.BodyWriter;
        writer.Write(bytes.Span);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Copies a stream to the response body. When <paramref name="source"/> can report its length
    /// a Content-Length is set; otherwise the response is chunked.
    /// </summary>
    public async ValueTask WriteStreamAsync(
        Stream source,
        string? contentType = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(source);

        if (!this.HasStarted)
        {
            if (contentType is not null)
                this.ContentType ??= contentType;

            if (this.ContentLength is null && source.CanSeek)
                this.ContentLength = source.Length - source.Position;
        }

        await source.CopyToAsync(this.Body, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends a redirect. Uses 308/301 when permanent, 307/302 otherwise.</summary>
    public void Redirect(string location, bool permanent = false, bool preserveMethod = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(location);

        this.StatusCode = (permanent, preserveMethod) switch
        {
            (true, true) => StatusCodes.Status308PermanentRedirect,
            (true, false) => StatusCodes.Status301MovedPermanently,
            (false, true) => StatusCodes.Status307TemporaryRedirect,
            (false, false) => StatusCodes.Status302Found
        };
        this.Headers.Set(HeaderNames.Location, location);
    }

    internal void Bind(IResponseBodyControl bodyControl) => this.control = bodyControl;

    /// <summary>
    /// The control currently framing this response, so a middleware can wrap it — which is how
    /// response compression inserts itself without every writer knowing about it.
    /// </summary>
    internal IResponseBodyControl BodyControl => this.control;

    internal async ValueTask InvokeOnStartingAsync()
    {
        if (this.onStarting is null)
            return;

        // Iterate by index: a callback is allowed to register another one.
        for (var i = 0; i < this.onStarting.Count; i++)
            await this.onStarting[i]().ConfigureAwait(false);
    }

    internal void Reset()
    {
        this.StatusCode = StatusCodes.Status200OK;
        this.ReasonPhrase = null;
        this.onStarting = null;
        this.control = NullResponseBodyControl.Instance;
        this.Headers.Reset();
    }
}
