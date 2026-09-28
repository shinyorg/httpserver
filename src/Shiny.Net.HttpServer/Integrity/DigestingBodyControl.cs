using System.Buffers;
using System.IO.Pipelines;
using System.Security.Cryptography;
using Shiny.Net.HttpServer.Compression;

namespace Shiny.Net.HttpServer.Integrity;

/// <summary>
/// Hashes the response body on its way out and attaches the digest — as a header when the body was
/// small enough to hold, as a trailer when it streamed.
/// <para>
/// Holding the body first is what makes the header form possible at all: a header goes out before
/// the first byte of body, and a digest cannot be known until after the last. A small response is
/// held, hashed and sent with the digest in its headers — and with a <c>Content-Length</c>, as a
/// side effect. A large one, or one whose handler flushed deliberately, streams and gets the digest
/// in the trailer section instead.
/// </para>
/// </summary>
sealed class DigestingBodyControl : IResponseBodyControl
{
    readonly HttpResponse response;
    readonly IResponseBodyControl inner;
    readonly int maxBuffered;
    readonly string? contentAlgorithm;
    readonly string? reprAlgorithm;

    // Set when this control sits inside response compression, which means the bytes seen here are not
    // the bytes on the wire. A digest of the wrong bytes is worse than none.
    readonly CompressingBodyControl? compressor;

    ArrayBufferWriter<byte>? buffer = new(1024);
    IncrementalHash? contentHash;
    IncrementalHash? reprHash;
    DigestBodyStream? stream;
    PipeWriter? writer;
    bool streaming;
    bool trailersAllowed;
    bool finished;

    public DigestingBodyControl(HttpResponse response, int maxBuffered, string? contentAlgorithm, string? reprAlgorithm)
    {
        this.response = response;
        this.inner = response.BodyControl;
        this.compressor = this.inner as CompressingBodyControl;
        this.maxBuffered = maxBuffered;
        this.contentAlgorithm = contentAlgorithm;
        this.reprAlgorithm = reprAlgorithm;
    }

    public bool HasStarted => this.inner.HasStarted;

    // Always our own stream, even once streaming: every byte has to pass the hash.
    public Stream Stream => this.stream ??= new DigestBodyStream(this);

    public PipeWriter Writer => this.writer ??= PipeWriter.Create(this.Stream, new StreamPipeWriterOptions(leaveOpen: true));

    /// <summary>A handler flushing its headers is streaming; the digest will have to be a trailer.</summary>
    public async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        await this.SpillAsync(cancellationToken).ConfigureAwait(false);
        await this.inner.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask CompleteAsync(CancellationToken cancellationToken) => this.inner.CompleteAsync(cancellationToken);

    /// <summary>
    /// Sends whatever was held, with its digest in the headers, or attaches the digest of a streamed
    /// body as a trailer. Called by the middleware once the handler returns — the connection completes
    /// its own producer, not whatever the response ended up bound to.
    /// </summary>
    public async ValueTask FinishAsync(CancellationToken cancellationToken)
    {
        if (this.finished)
            return;

        this.finished = true;

        if (this.streaming)
        {
            if (this.trailersAllowed && this.CanDescribe(bodyWritten: true))
            {
                if (this.contentHash is { } content)
                    this.response.AppendTrailer(DigestFields.ContentDigest, DigestFields.Format(this.contentAlgorithm!, content.GetHashAndReset()));

                if (this.reprHash is { } repr && this.IsFullRepresentation())
                    this.response.AppendTrailer(DigestFields.ReprDigest, DigestFields.Format(this.reprAlgorithm!, repr.GetHashAndReset()));
            }

            this.DisposeHashes();
            return;
        }

        var held = this.buffer!;
        this.buffer = null;

        if (!this.inner.HasStarted)
        {
            if (this.CanDescribe(bodyWritten: false))
            {
                var headers = this.response.Headers;

                // A digest the handler set itself — a precomputed one for a file, say — is left alone.
                if (this.contentAlgorithm is { } content && !headers.ContainsKey(DigestFields.ContentDigest))
                    headers.Set(DigestFields.ContentDigest, DigestFields.Compute(held.WrittenSpan, content));

                if (this.reprAlgorithm is { } repr && this.IsFullRepresentation() && !headers.ContainsKey(DigestFields.ReprDigest))
                    headers.Set(DigestFields.ReprDigest, DigestFields.Compute(held.WrittenSpan, repr));
            }

            if (held.WrittenCount > 0)
                this.response.ContentLength ??= held.WrittenCount;
        }

        if (held.WrittenCount > 0)
            await this.inner.Stream.WriteAsync(held.WrittenMemory, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Switches to streaming: sends what was held and hashes from here on.</summary>
    async ValueTask SpillAsync(CancellationToken cancellationToken)
    {
        if (this.streaming || this.finished)
            return;

        this.streaming = true;

        var protocol = this.response.HttpContext.Request.Protocol;

        // HTTP/1.1 only has somewhere to put a trailer when the body is chunked, which it is exactly
        // when no length was declared. HTTP/1.0 has no trailers at all. HTTP/2 and HTTP/3 always do.
        this.trailersAllowed = protocol switch
        {
            HttpProtocols.Http11 => this.response.ContentLength is null,
            HttpProtocols.Http10 => false,
            _ => true
        };

        if (this.trailersAllowed)
        {
            if (this.contentAlgorithm is { } content)
                this.contentHash = DigestFields.CreateHash(content);

            if (this.reprAlgorithm is { } repr)
                this.reprHash = DigestFields.CreateHash(repr);

            // An HTTP/1.1 intermediary may drop a trailer it was not told to expect. Announcing it is
            // only possible before the headers go, which is now or never.
            if (!this.inner.HasStarted)
            {
                if (this.contentHash is not null)
                    this.response.DeclareTrailer(DigestFields.ContentDigest);

                if (this.reprHash is not null)
                    this.response.DeclareTrailer(DigestFields.ReprDigest);
            }
        }

        var held = this.buffer!;
        this.buffer = null;

        if (held.WrittenCount > 0)
        {
            this.Hash(held.WrittenSpan);
            await this.inner.Stream.WriteAsync(held.WrittenMemory, cancellationToken).ConfigureAwait(false);
        }
    }

    async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (!this.streaming && !this.finished)
        {
            if (this.buffer!.WrittenCount + data.Length <= this.maxBuffered)
            {
                this.buffer.Write(data.Span);
                return;
            }

            await this.SpillAsync(cancellationToken).ConfigureAwait(false);
        }

        this.Hash(data.Span);
        await this.inner.Stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
    }

    ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        // Deliberately not forwarded while holding: a flush would start the response and defeat the
        // point of holding it. A handler that needs the bytes out calls StartAsync.
        return this.streaming
            ? new ValueTask(this.inner.Stream.FlushAsync(cancellationToken))
            : default;
    }

    void Hash(ReadOnlySpan<byte> data)
    {
        this.contentHash?.AppendData(data);
        this.reprHash?.AppendData(data);
    }

    /// <summary>
    /// Whether a digest can honestly describe this response: it has content, and the content is the
    /// bytes this control saw rather than a compressed version of them.
    /// <para>
    /// Compression decides on the first byte it is given, so before the held body has been written
    /// through there is no way to know whether it will compress — and a guess that is wrong ships a
    /// digest that fails every check. Inside compression, a held response goes without.
    /// </para>
    /// </summary>
    bool CanDescribe(bool bodyWritten)
    {
        var status = this.response.StatusCode;

        if (status is < 200 or StatusCodes.Status204NoContent or StatusCodes.Status304NotModified)
            return false;

        if (this.compressor is null)
            return true;

        return bodyWritten && this.compressor.AppliedEncoding is null;
    }

    /// <summary>
    /// A digest of the content is also a digest of the representation when the content is the whole
    /// of it — not a range of it.
    /// </summary>
    bool IsFullRepresentation()
        => this.response.StatusCode != StatusCodes.Status206PartialContent
            && !this.response.Headers.ContainsKey(HeaderNames.ContentRange);

    void DisposeHashes()
    {
        this.contentHash?.Dispose();
        this.reprHash?.Dispose();
    }

    sealed class DigestBodyStream(DigestingBodyControl owner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
            => this.Write(new ReadOnlySpan<byte>(buffer, offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            var copy = buffer.ToArray();
            owner.WriteAsync(copy, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => owner.WriteAsync(new ReadOnlyMemory<byte>(buffer, offset, count), cancellationToken).AsTask();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => owner.WriteAsync(buffer, cancellationToken);

        public override void Flush() => owner.FlushAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();

        public override Task FlushAsync(CancellationToken cancellationToken) => owner.FlushAsync(cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
