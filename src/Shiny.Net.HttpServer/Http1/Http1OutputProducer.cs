using System.Buffers;
using System.Buffers.Text;
using System.Globalization;
using System.IO.Pipelines;
using System.Text;

namespace Shiny.Net.HttpServer.Http1;

/// <summary>
/// Frames a response onto the connection's <see cref="PipeWriter"/>.
/// <para>
/// Headers are written lazily on the first body flush (or on completion), which is what makes
/// <see cref="HttpResponse.ContentLength"/> and status changes safe right up until bytes actually
/// leave. If a length was declared we write the body straight through; otherwise HTTP/1.1 responses
/// are chunk-framed and HTTP/1.0 responses fall back to close-delimited.
/// </para>
/// <para>
/// Every body write — <see cref="HttpResponse.Body"/>, <see cref="HttpResponse.BodyWriter"/>, or the
/// convenience helpers — funnels through <see cref="ResponseBodyPipeWriter"/>. That single path is
/// what guarantees the status line always precedes payload bytes, no matter which API the handler
/// reached for, since <c>GetSpan</c> cannot await the header write itself.
/// </para>
/// </summary>
sealed class Http1OutputProducer : IResponseBodyControl, IInformationalResponseWriter, IDisposable
{
    readonly PipeWriter pipe;
    readonly HttpServerOptions options;
    readonly ResponseBodyStream stream;
    readonly ResponseBodyPipeWriter bodyWriter;
    readonly PipeOperationTimer writeTimer;
    readonly CancellationToken draining;

    HttpResponse response = null!;
    bool chunked;
    bool suppressBody;
    bool bodyForbidden;
    bool keepAlive;
    bool completed;
    long? declaredLength;
    long bytesWritten;

    /// <param name="pipe">The connection's output.</param>
    /// <param name="options">Server options; the write timeout comes from its limits.</param>
    /// <param name="draining">Signalled when the server starts shutting down, after which every response says <c>Connection: close</c>.</param>
    public Http1OutputProducer(PipeWriter pipe, HttpServerOptions options, CancellationToken draining = default)
    {
        this.pipe = pipe;
        this.options = options;
        this.draining = draining;
        this.stream = new ResponseBodyStream(this);
        this.bodyWriter = new ResponseBodyPipeWriter(this);
        this.writeTimer = new PipeOperationTimer(pipe.CancelPendingFlush);
    }

    /// <summary>
    /// True once writing to the connection itself has failed — the client reset it, or stopped
    /// reading for longer than <see cref="HttpServerLimits.ResponseWriteTimeout"/>. What the handler
    /// sees then is the transport going away, not a fault of its own.
    /// </summary>
    public bool TransportFaulted { get; private set; }

    /// <summary>True when a flush outlived <see cref="HttpServerLimits.ResponseWriteTimeout"/>.</summary>
    public bool WriteTimedOut => this.writeTimer.Expired;

    public bool HasStarted { get; private set; }

    public Stream Stream => this.stream;

    public PipeWriter Writer => this.bodyWriter;

    /// <summary>Body bytes written, excluding chunk framing. Used for logging.</summary>
    public long BytesWritten => this.bytesWritten;

    /// <summary>
    /// True when the response as written is compatible with reusing the connection. A response of
    /// unknown length on HTTP/1.0 forces a close, since closing is the only end-of-body signal there.
    /// </summary>
    public bool AllowsKeepAlive => this.keepAlive;

    /// <summary>
    /// True once a 101 has gone out. From that point the bytes on this connection belong to
    /// whatever protocol was negotiated, so this producer writes nothing further — no chunk
    /// terminator, no framing, no second response.
    /// </summary>
    public bool IsUpgrade { get; private set; }

    public void Begin(HttpResponse response, bool suppressBody, bool keepAlive)
    {
        this.response = response;
        this.HasStarted = false;
        this.chunked = false;
        this.suppressBody = suppressBody;
        this.bodyForbidden = false;
        this.keepAlive = keepAlive;
        this.completed = false;
        this.IsUpgrade = false;
        this.declaredLength = null;
        this.bytesWritten = 0;
        this.bodyWriter.Reset();
        response.Bind(this);
    }

    /// <summary>
    /// Writes the head and puts it on the wire.
    /// <para>
    /// The flush is the point. A caller reaching for StartAsync explicitly is one that needs the
    /// client to see the head <em>now</em> — a protocol upgrade waiting on a 101, or an event
    /// stream whose first event may be minutes away. Leaving the head staged there deadlocks both.
    /// </para>
    /// </summary>
    public async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        var alreadyStarted = this.HasStarted;

        await this.EnsureStartedAsync().ConfigureAwait(false);

        if (!alreadyStarted)
            await this.FlushTransportAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Flushes the connection's output under <see cref="HttpServerLimits.ResponseWriteTimeout"/>.
    /// Every flush in this producer — and the connection's own 100 Continue — comes through here, so
    /// a client that stops reading cannot hold a handler (and its connection slot) forever, and a
    /// failure here is known to be the transport's.
    /// </summary>
    public async ValueTask<FlushResult> FlushTransportAsync(CancellationToken cancellationToken)
    {
        if (this.writeTimer.Expired)
            throw this.WriteTimeout();

        FlushResult result;
        this.writeTimer.Start(this.options.Limits.ResponseWriteTimeout);
        try
        {
            result = await this.pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or ObjectDisposedException)
        {
            this.TransportFaulted = true;
            throw;
        }
        finally
        {
            this.writeTimer.Stop();
        }

        if (this.writeTimer.Expired)
            throw this.WriteTimeout();

        return result;
    }

    IOException WriteTimeout()
    {
        this.TransportFaulted = true;
        this.keepAlive = false;
        return new IOException(
            $"The client did not accept response data within {this.options.Limits.ResponseWriteTimeout.TotalSeconds:0.#}s."
        );
    }

    public void Dispose() => this.writeTimer.Dispose();

    /// <summary>
    /// Stages the head without flushing. The body path uses this, because it is about to flush
    /// anyway and two writes where one would do is a real cost on every small response.
    /// </summary>
    async ValueTask EnsureStartedAsync()
    {
        if (this.HasStarted)
            return;

        await this.response.InvokeOnStartingAsync().ConfigureAwait(false);
        this.WriteResponseHead();
        this.HasStarted = true;
        this.response.Headers.IsReadOnly = true;
    }

    void WriteResponseHead()
    {
        var res = this.response;
        var status = res.StatusCode;

        // Validated before a byte is staged: a throw half way through the head would leave the start of
        // a response in the pipe for the error response to be written after.
        ValidateReasonPhrase(res.ReasonPhrase);

        // 1xx, 204 and 304 must not carry a body, and framing headers on them confuse clients.
        var bodyForbidden = IsBodyForbidden(status);
        this.bodyForbidden = bodyForbidden;
        this.declaredLength = res.ContentLength;

        if (bodyForbidden)
        {
            this.chunked = false;
            this.declaredLength = null;
            res.Headers.Remove(HeaderNames.ContentLength);
            res.Headers.Remove(HeaderNames.TransferEncoding);
        }
        else if (this.declaredLength is null)
        {
            // Unknown length: chunk it on 1.1; on 1.0 the only available framing is closing.
            if (res.HttpContext.Request.Protocol == HttpProtocols.Http11)
            {
                this.chunked = true;
                res.Headers.Set(HeaderNames.TransferEncoding, "chunked");
            }
            else
            {
                this.chunked = false;
                this.keepAlive = false;
            }
        }
        else
        {
            this.chunked = false;
            res.Headers.Remove(HeaderNames.TransferEncoding);
        }

        // A 101 that names an Upgrade is a protocol handover, not a response with an empty body.
        // Its Connection header is part of the handshake and must survive untouched.
        this.IsUpgrade = status == StatusCodes.Status101SwitchingProtocols
            && res.Headers.ContainsKey(HeaderNames.Upgrade);

        if (this.options.ServerHeader is { } server && !res.Headers.ContainsKey(HeaderNames.Server))
            res.Headers.Set(HeaderNames.Server, server);

        if (this.options.IncludeDateHeader && !res.Headers.ContainsKey(HeaderNames.Date))
            res.Headers.Set(HeaderNames.Date, DateHeaderCache.GetValue());

        // A server that is shutting down finishes this exchange and then closes; saying so lets the
        // client open its next request elsewhere instead of finding the connection gone.
        if (this.draining.IsCancellationRequested)
            this.keepAlive = false;

        if (this.IsUpgrade)
            this.keepAlive = false;
        else
            res.Headers.Set(HeaderNames.Connection, this.keepAlive ? "keep-alive" : "close");

        WriteStatusLine(this.pipe, status, res.ReasonPhrase);
        WriteHeaders(this.pipe, res.Headers);
    }

    /// <summary>
    /// Writes an interim 1xx status line and header block straight onto the connection and flushes
    /// it, ahead of the final response.
    /// <para>
    /// Straight onto the pipe, not through the body writer: an interim response is a complete message
    /// of its own, not part of this response's body, and anything the handler has staged but not yet
    /// flushed still belongs after the final head. No Server, Date or Connection headers are added —
    /// those describe the final response, and the client is about to get one anyway.
    /// </para>
    /// <para>
    /// HTTP/1.0 has no interim responses: a 1.0 client reads the first status line as <em>the</em>
    /// response. So over 1.0 this sends nothing and says so.
    /// </para>
    /// </summary>
    public async ValueTask<bool> WriteInformationalAsync(
        int statusCode,
        HeaderDictionary headers,
        CancellationToken cancellationToken
    )
    {
        if (this.response.HttpContext.Request.Protocol != HttpProtocols.Http11)
            return false;

        WriteStatusLine(this.pipe, statusCode, null);
        WriteHeaders(this.pipe, headers);
        await this.FlushTransportAsync(cancellationToken).ConfigureAwait(false);

        return true;
    }

    static bool IsBodyForbidden(int status)
        => status is StatusCodes.Status204NoContent or StatusCodes.Status304NotModified
            || status is >= 100 and < 200;

    /// <summary>
    /// A reason phrase is free text on the status line, so a CR or LF in one would end the line and
    /// write headers of the handler's choosing — or a whole second response.
    /// </summary>
    static void ValidateReasonPhrase(string? reasonPhrase)
    {
        if (reasonPhrase is null)
            return;

        foreach (var c in reasonPhrase)
        {
            // reason-phrase = *( HTAB / SP / VCHAR / obs-text ), RFC 9112 §4.
            if (c != '\t' && (c < ' ' || c == '\x7F'))
                throw new InvalidOperationException(
                    $"Invalid control character 0x{(int)c:X2} in the response reason phrase."
                );
        }
    }

    static void WriteStatusLine(PipeWriter writer, int statusCode, string? reasonPhrase)
    {
        var reason = reasonPhrase ?? StatusCodes.GetReasonPhrase(statusCode);

        // "HTTP/1.1 " + 3 digits + " " + reason + CRLF
        var span = writer.GetSpan(9 + 3 + 1 + Encoding.ASCII.GetByteCount(reason) + 2);
        var written = 0;

        "HTTP/1.1 "u8.CopyTo(span);
        written += 9;

        Utf8Formatter.TryFormat(statusCode, span[written..], out var statusBytes);
        written += statusBytes;

        span[written++] = (byte)' ';
        written += Encoding.ASCII.GetBytes(reason, span[written..]);
        span[written++] = (byte)'\r';
        span[written++] = (byte)'\n';

        writer.Advance(written);
    }

    static void WriteHeaders(PipeWriter writer, HeaderDictionary headers)
    {
        foreach (var header in headers)
        {
            var values = header.Value;
            for (var i = 0; i < values.Count; i++)
            {
                if (values[i] is { } value)
                    WriteHeaderLine(writer, header.Key, value);
            }
        }

        var terminator = writer.GetSpan(2);
        terminator[0] = (byte)'\r';
        terminator[1] = (byte)'\n';
        writer.Advance(2);
    }

    static void WriteHeaderLine(PipeWriter writer, string name, string value)
    {
        var span = writer.GetSpan(name.Length + 2 + Encoding.UTF8.GetByteCount(value) + 2);
        var written = Encoding.ASCII.GetBytes(name, span);
        span[written++] = (byte)':';
        span[written++] = (byte)' ';
        written += Encoding.UTF8.GetBytes(value, span[written..]);
        span[written++] = (byte)'\r';
        span[written++] = (byte)'\n';
        writer.Advance(written);
    }

    /// <summary>
    /// Emits staged body bytes with the correct framing, writing headers first if they have not gone
    /// out yet. This is the only place payload bytes reach the connection pipe.
    /// </summary>
    async ValueTask<FlushResult> EmitAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        // A 204 or 304 has no body and so no framing: bytes written to one would reach the client
        // as the start of the next response. Refused loudly, as Kestrel does, rather than dropped —
        // a handler writing a body here has a bug worth hearing about. A HEAD discards the bytes
        // anyway, so it stays quiet there, as Kestrel does. Checked before the head is staged when
        // it can be, so the handler's exception can still become a clean 500.
        // A 101 is left alone: what follows it belongs to the protocol it switched to.
        var forbidden = this.HasStarted
            ? this.bodyForbidden && !this.IsUpgrade
            : IsBodyForbidden(this.response.StatusCode) && this.response.StatusCode != StatusCodes.Status101SwitchingProtocols;
        if (!body.IsEmpty && forbidden && !this.suppressBody)
            throw new InvalidOperationException(
                $"Writing to the response body is invalid for responses with status code {this.response.StatusCode}."
            );

        await this.EnsureStartedAsync().ConfigureAwait(false);

        if (!body.IsEmpty && !this.suppressBody)
        {
            this.Track(body.Length);

            if (this.chunked)
                WriteChunk(this.pipe, body.Span);
            else
                this.pipe.Write(body.Span);
        }
        else if (!body.IsEmpty)
        {
            // HEAD: the length still has to be accounted for so Content-Length validation and
            // logging see what the handler intended to send, we just do not put it on the wire.
            this.Track(body.Length);
        }

        return await this.FlushTransportAsync(cancellationToken).ConfigureAwait(false);
    }

    void Track(int count)
    {
        this.bytesWritten += count;

        // Overrunning the declared length would desynchronise the connection, so fail loudly rather
        // than emit a response the client will misparse.
        if (this.declaredLength is { } declared && this.bytesWritten > declared)
            throw new InvalidOperationException(
                $"Wrote {this.bytesWritten} body bytes but Content-Length declared {declared}."
            );
    }

    static void WriteChunk(PipeWriter writer, ReadOnlySpan<byte> data)
    {
        // size-in-hex CRLF payload CRLF
        var header = writer.GetSpan(18);
        var digits = WriteHexLength(header, data.Length);
        header[digits++] = (byte)'\r';
        header[digits++] = (byte)'\n';
        writer.Advance(digits);

        writer.Write(data);

        var trailer = writer.GetSpan(2);
        trailer[0] = (byte)'\r';
        trailer[1] = (byte)'\n';
        writer.Advance(2);
    }

    static int WriteHexLength(Span<byte> destination, int value)
    {
        if (value == 0)
        {
            destination[0] = (byte)'0';
            return 1;
        }

        Span<byte> scratch = stackalloc byte[8];
        var index = scratch.Length;
        while (value > 0)
        {
            var nibble = value & 0xF;
            scratch[--index] = (byte)(nibble < 10 ? '0' + nibble : 'a' + nibble - 10);
            value >>= 4;
        }

        var digits = scratch.Length - index;
        scratch[index..].CopyTo(destination);
        return digits;
    }

    public async ValueTask CompleteAsync(CancellationToken cancellationToken)
    {
        // Idempotent: the connection completes the response on the happy path, and error handling may
        // ask again. Framing the terminator twice would corrupt the stream.
        if (this.completed)
            return;

        this.completed = true;

        // After a 101 the transport belongs to the protocol that took over, which writes and flushes it itself -
        // a WebSocket's keep-alive may be flushing it this moment from another thread, and a second flush on the same
        // PipeWriter can send its bytes twice. There is no HTTP response left to finish.
        if (this.IsUpgrade)
            return;

        // Anything still staged has to go out before the terminating chunk.
        await this.bodyWriter.FlushAsync(cancellationToken).ConfigureAwait(false);

        if (!this.HasStarted)
        {
            // Nothing was written at all. Declare a zero-length body so the client is not left
            // waiting, unless the status code forbids a body anyway.
            if (!IsBodyForbidden(this.response.StatusCode) && this.response.ContentLength is null)
                this.response.ContentLength = 0;

            await this.EnsureStartedAsync().ConfigureAwait(false);
        }

        if (this.chunked && !this.suppressBody)
        {
            if (this.response.HasTrailers)
            {
                // The trailer section hangs off the zero-length chunk: "0" CRLF, the trailer lines,
                // then the blank line that ends the message. Only chunked responses have anywhere to
                // put it — a Content-Length body ends at its last byte, with nothing after it.
                var terminator = this.pipe.GetSpan(3);
                "0\r\n"u8.CopyTo(terminator);
                this.pipe.Advance(3);

                WriteHeaders(this.pipe, this.response.Trailers);
            }
            else
            {
                var terminator = this.pipe.GetSpan(5);
                "0\r\n\r\n"u8.CopyTo(terminator);
                this.pipe.Advance(5);
            }
        }

        // A HEAD response declares the length a GET would have had and sends none of it, which is
        // exactly right — not a shortfall.
        var shortfall = !this.suppressBody
            && this.declaredLength is { } declared
            && this.bytesWritten < declared;

        if (shortfall)
            // We promised more than we delivered. The connection can no longer be reused, or the
            // client would read our next response as the tail of this body.
            this.keepAlive = false;

        await this.FlushTransportAsync(cancellationToken).ConfigureAwait(false);

        if (shortfall)
            throw new InvalidOperationException(
                $"Content-Length declared {this.declaredLength} bytes but only {this.bytesWritten} were written."
            );
    }

    /// <summary>
    /// Stages body bytes, then emits them with correct framing on flush. Staging exists because
    /// chunk framing needs the payload length up front, and <c>GetSpan</c>/<c>Advance</c> does not
    /// reveal it until after the caller has written.
    /// </summary>
    sealed class ResponseBodyPipeWriter : PipeWriter
    {
        readonly Http1OutputProducer owner;
        byte[] staging = new byte[4096];
        int staged;

        public ResponseBodyPipeWriter(Http1OutputProducer owner) => this.owner = owner;

        public void Reset()
        {
            this.staged = 0;

            // Do not hold a large buffer hostage across requests on a long-lived connection.
            if (this.staging.Length > 64 * 1024)
                this.staging = new byte[4096];
        }

        public override void Advance(int bytes) => this.staged += bytes;

        public override Memory<byte> GetMemory(int sizeHint = 0)
        {
            this.EnsureCapacity(sizeHint);
            return this.staging.AsMemory(this.staged);
        }

        public override Span<byte> GetSpan(int sizeHint = 0)
        {
            this.EnsureCapacity(sizeHint);
            return this.staging.AsSpan(this.staged);
        }

        void EnsureCapacity(int sizeHint)
        {
            var required = this.staged + Math.Max(sizeHint, 256);
            if (required <= this.staging.Length)
                return;

            var size = this.staging.Length;
            while (size < required)
                size *= 2;

            Array.Resize(ref this.staging, size);
        }

        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            var count = this.staged;
            this.staged = 0;
            return this.owner.EmitAsync(this.staging.AsMemory(0, count), cancellationToken);
        }

        /// <summary>Writes directly, flushing anything already staged first to preserve ordering.</summary>
        public async ValueTask WriteDirectAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            if (this.staged > 0)
                await this.FlushAsync(cancellationToken).ConfigureAwait(false);

            await this.owner.EmitAsync(data, cancellationToken).ConfigureAwait(false);
        }

        public override void CancelPendingFlush() => this.owner.pipe.CancelPendingFlush();

        // The connection owns the underlying pipe's lifetime; completing it here would cut off
        // every subsequent request on a keep-alive connection.
        public override void Complete(Exception? exception = null)
        {
        }
    }

    /// <summary>
    /// Adapts the response body to a <see cref="Stream"/> so callers can <c>CopyToAsync</c> into it.
    /// Write-only and forward-only.
    /// </summary>
    sealed class ResponseBodyStream : Stream
    {
        readonly Http1OutputProducer owner;

        public ResponseBodyStream(Http1OutputProducer owner) => this.owner = owner;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => this.owner.bytesWritten;
            set => throw new NotSupportedException();
        }

        public override void Flush() => this.FlushAsync(CancellationToken.None).GetAwaiter().GetResult();

        public override Task FlushAsync(CancellationToken cancellationToken)
            => this.owner.bodyWriter.FlushAsync(cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => this.WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult();

        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken
        ) => this.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default
        ) => this.owner.bodyWriter.WriteDirectAsync(buffer, cancellationToken);
    }
}

/// <summary>
/// Caches the formatted Date header for one second. Formatting an RFC 1123 date per response is pure
/// waste when the value only changes once a second.
/// </summary>
static class DateHeaderCache
{
    static long lastSeconds = -1;
    static string lastValue = string.Empty;

    public static string GetValue()
    {
        var now = DateTimeOffset.UtcNow;
        var seconds = now.ToUnixTimeSeconds();

        // Racing threads may both format the same second; the result is identical either way.
        if (Volatile.Read(ref lastSeconds) != seconds)
        {
            lastValue = now.ToString("R", CultureInfo.InvariantCulture);
            Volatile.Write(ref lastSeconds, seconds);
        }
        return lastValue;
    }
}
