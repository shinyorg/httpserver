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
    IInformationalResponseWriter? informational;

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
    /// Headers sent <em>after</em> the body, which is the only way to report something you could not
    /// know before you started writing — a checksum, a row count, or the status of an RPC whose
    /// failure surfaced halfway through the stream. gRPC is built on them.
    /// <para>
    /// Unlike <see cref="Headers"/> these stay writable for the whole response and are read at
    /// completion. They ride a trailing HEADERS frame on HTTP/2 and HTTP/3, and the terminating
    /// chunk on HTTP/1.1 — which means <b>an HTTP/1.1 response that declared a Content-Length cannot
    /// carry them</b>, since there is no chunk to attach them to. Leave the length unset when
    /// trailers matter. Name them in <see cref="DeclareTrailer"/> so an HTTP/1.1 client knows to
    /// keep reading.
    /// </para>
    /// </summary>
    public HeaderDictionary Trailers => this.trailers ??= new HeaderDictionary(4);

    HeaderDictionary? trailers;

    /// <summary>True when any trailer has been set, without allocating the collection to find out.</summary>
    internal bool HasTrailers => this.trailers is { Count: > 0 };

    /// <summary>Appends a trailing header, preserving any existing values for the same name.</summary>
    public void AppendTrailer(string name, string value) => this.Trailers.Append(name, value);

    /// <summary>
    /// Announces a trailer in the <c>Trailer</c> response header. Optional on HTTP/2 and HTTP/3,
    /// but HTTP/1.1 intermediaries are entitled to drop trailers they were not told to expect.
    /// </summary>
    public void DeclareTrailer(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        this.Headers.Append(HeaderNames.Trailer, name);
    }

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

    // ---- Informational (1xx) responses. ----

    /// <summary>
    /// Sends <c>103 Early Hints</c> (RFC 8297): an interim response carrying <c>Link</c> headers the
    /// browser can start fetching — stylesheets, scripts, fonts, a preconnect to a CDN — while this
    /// handler is still working out the real response. It turns server "think time" (a database
    /// query, a template render) into download time for the page's critical subresources.
    /// <para>
    /// Each value is one <c>Link</c> header value, for example
    /// <c>&lt;/app.css&gt;; rel=preload; as=style</c> or <c>&lt;https://cdn.example.com&gt;; rel=preconnect</c>.
    /// Browsers act on <c>preload</c> and <c>preconnect</c> hints, and only for navigations; repeat the
    /// same <c>Link</c> headers on the final response too, since hints are advisory and a client or
    /// intermediary is free to discard them.
    /// </para>
    /// <para>
    /// Must be called before the final response has started. Returns false, having sent nothing, when
    /// the request arrived over HTTP/1.0 (which predates 1xx responses, so a 1.0 client would read the
    /// hint as the final response) or when the response is not attached to a connection.
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">The final response has already started.</exception>
    public ValueTask<bool> SendEarlyHintsAsync(IEnumerable<string> links, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(links);

        var headers = new HeaderDictionary(1);
        foreach (var link in links)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(link, nameof(links));
            headers.Append(HeaderNames.Link, link);
        }

        if (headers.Count == 0)
            throw new ArgumentException("Early Hints need at least one Link value.", nameof(links));

        return this.SendInformationalResponseAsync(StatusCodes.Status103EarlyHints, headers, cancellationToken);
    }

    /// <summary>
    /// Sends <c>103 Early Hints</c> for one or more <c>Link</c> header values. See
    /// <see cref="SendEarlyHintsAsync(IEnumerable{string}, CancellationToken)"/>.
    /// </summary>
    public ValueTask<bool> SendEarlyHintsAsync(params string[] links)
        => this.SendEarlyHintsAsync((IEnumerable<string>)links, CancellationToken.None);

    /// <summary>
    /// Sends an interim <c>1xx</c> response ahead of the final one. Most callers want
    /// <see cref="SendEarlyHintsAsync(IEnumerable{string}, CancellationToken)"/>; this is the general
    /// form, for a <c>102 Processing</c> keep-alive on a slow operation or a 1xx code of your own.
    /// <para>
    /// Any number may be sent, in order, before the final response. Each goes out immediately —
    /// framed as an interim status line and header block on HTTP/1.1, and as a HEADERS frame without
    /// END_STREAM on HTTP/2 and HTTP/3. Nothing here touches <see cref="Headers"/> or
    /// <see cref="StatusCode"/>; the final response is still yours to shape afterwards.
    /// </para>
    /// <para>
    /// <c>101 Switching Protocols</c> is refused: it is not interim, it ends HTTP on the connection,
    /// and the WebSocket and upgrade paths own it. <c>100 Continue</c> is allowed but rarely needed —
    /// the server already answers <c>Expect: 100-continue</c> itself on HTTP/1.1.
    /// </para>
    /// </summary>
    /// <returns>
    /// True when the response was sent. False, having sent nothing, over HTTP/1.0 (which has no
    /// interim responses — a 1.0 client would take it as the final answer) or when the response is not
    /// attached to a connection.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">The status is not 1xx, or is 101.</exception>
    /// <exception cref="ArgumentException">A header would corrupt the framing (a line break, or a body-framing header).</exception>
    /// <exception cref="InvalidOperationException">The final response has already started.</exception>
    public ValueTask<bool> SendInformationalResponseAsync(
        int statusCode,
        HeaderDictionary? headers = null,
        CancellationToken cancellationToken = default
    )
    {
        if (statusCode is < 100 or > 199 || statusCode == StatusCodes.Status101SwitchingProtocols)
            throw new ArgumentOutOfRangeException(
                nameof(statusCode),
                statusCode,
                "An informational response needs a 1xx status other than 101 Switching Protocols."
            );

        headers ??= new HeaderDictionary(0);
        ValidateInformationalHeaders(headers);

        // Throwing rather than ignoring: once the final status line is out, anything 1xx that follows
        // would be read as garbage in the body (HTTP/1.1) or a malformed stream (HTTP/2 and 3). A call
        // here is a bug in the handler's ordering, and it should hear about it.
        if (this.HasStarted)
            throw new InvalidOperationException(
                "Informational (1xx) responses must be sent before the final response starts."
            );

        return this.informational is { } writer
            ? writer.WriteInformationalAsync(statusCode, headers, cancellationToken)
            : ValueTask.FromResult(false);
    }

    static void ValidateInformationalHeaders(HeaderDictionary headers)
    {
        foreach (var (name, values) in headers)
        {
            // An interim response has no body, so body framing on it is meaningless at best — and on
            // HTTP/1.1 a client that believed it would wait for bytes that never come.
            if (name.Equals(HeaderNames.ContentLength, StringComparison.OrdinalIgnoreCase)
                || name.Equals(HeaderNames.TransferEncoding, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"'{name}' cannot be sent on an informational response.", nameof(headers));

            if (name.AsSpan().IndexOfAny('\r', '\n') >= 0)
                throw new ArgumentException($"Header name '{name}' contains a line break.", nameof(headers));

            foreach (var value in values)
            {
                // Written straight into an HTTP/1.1 head, a line break would let a value inject its own
                // headers — or end the interim response early and start forging the final one.
                if (value is not null && value.AsSpan().IndexOfAny('\r', '\n') >= 0)
                    throw new ArgumentException($"Header '{name}' contains a line break.", nameof(headers));
            }
        }
    }

    /// <summary>
    /// Puts <paramref name="bodyControl"/> in charge of framing this response. A middleware that
    /// wants to see or transform the body wraps <see cref="BodyControl"/> and binds the wrapper —
    /// do it before calling the next delegate, and make sure whatever the wrapper buffered is
    /// flushed afterwards, because the connection completes its own producer rather than whatever
    /// the response ended up bound to.
    /// </summary>
    public void Bind(IResponseBodyControl bodyControl)
    {
        ArgumentNullException.ThrowIfNull(bodyControl);
        this.control = bodyControl;

        // Only the connection's own control can frame an interim response, so a wrapper bound on top
        // of it (compression, a recorder) must not hide it. Captured here and left alone by anything
        // that does not implement it.
        if (bodyControl is IInformationalResponseWriter writer)
            this.informational = writer;
    }

    /// <summary>
    /// The control currently framing this response, so a middleware can wrap it — which is how
    /// response compression inserts itself without every writer knowing about it.
    /// </summary>
    public IResponseBodyControl BodyControl => this.control;

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
        this.informational = null;
        this.Headers.Reset();
        this.trailers?.Reset();
    }
}
