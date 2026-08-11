using System.IO.Pipelines;

namespace Shiny.Net.HttpServer.Compression;

/// <summary>
/// Wraps the response body in a compressor.
/// <para>
/// The decision cannot be made when the middleware runs, because nothing has set a content type
/// yet — so it is made on the first byte written, which is the last moment before headers go to the
/// wire and the first moment the content type is known. Everything funnels through
/// <see cref="IResponseBodyControl"/>, so a handler writing to <c>Body</c>, <c>BodyWriter</c> or any
/// of the convenience helpers is covered without knowing this exists.
/// </para>
/// </summary>
sealed class CompressingBodyControl(
    IResponseBodyControl inner,
    HttpResponse response,
    ResponseCompressionOptions options,
    ICompressionProvider provider
) : IResponseBodyControl
{
    Stream? compressor;
    CompressionBodyStream? bodyStream;
    PipeWriter? bodyWriter;

    bool decided;
    bool compressing;
    bool finished;

    public bool HasStarted => inner.HasStarted;

    /// <summary>The coding actually applied, or null when the response was passed through.</summary>
    public string? AppliedEncoding => this.compressing ? provider.EncodingName : null;

    public Stream Stream => this.PassThrough ? inner.Stream : this.bodyStream ??= new CompressionBodyStream(this);

    public PipeWriter Writer => this.PassThrough
        ? inner.Writer

        // Built over our own stream rather than wrapping the inner writer, so both write paths meet
        // at one place and the compressor sees every byte exactly once.
        : this.bodyWriter ??= PipeWriter.Create(this.Stream, new StreamPipeWriterOptions(leaveOpen: true));

    /// <summary>
    /// True once it is settled that this response is not being compressed — from then on the inner
    /// control is handed out directly, so nothing pays for a wrapper that is doing nothing.
    /// </summary>
    bool PassThrough => (this.decided && !this.compressing) || this.finished;

    public ValueTask StartAsync(CancellationToken cancellationToken)
    {
        // Callers that flush headers before writing a body — a file download, an event stream — get
        // the decision made here instead, because after this there is no header left to change.
        this.EnsureDecided();

        return inner.StartAsync(cancellationToken);
    }

    public ValueTask CompleteAsync(CancellationToken cancellationToken) => inner.CompleteAsync(cancellationToken);

    /// <summary>
    /// Flushes and releases the compressor.
    /// <para>
    /// Called by the middleware rather than through <see cref="CompleteAsync"/>: the connection
    /// completes its own producer, not whatever the response is currently bound to, so a trailing
    /// block left unflushed here would truncate every compressed response.
    /// </para>
    /// </summary>
    public async ValueTask FinishAsync()
    {
        if (this.finished)
            return;

        this.finished = true;

        if (this.compressor is not { } stream)
            return;

        this.compressor = null;

        // Disposing is what writes the final block; flushing alone leaves the stream unterminated.
        await stream.DisposeAsync().ConfigureAwait(false);
    }

    void EnsureDecided()
    {
        if (this.decided)
            return;

        this.decided = true;

        // Headers are already gone, so there is no way to announce a coding.
        if (inner.HasStarted)
            return;

        if (!this.ShouldCompress())
            return;

        this.compressing = true;

        response.Headers[HeaderNames.ContentEncoding] = provider.EncodingName;

        // The declared length described the uncompressed body. The compressed length is not known
        // until the last block is written, so the response becomes chunked.
        response.ContentLength = null;

        this.compressor = provider.CreateStream(inner.Stream, options.Level);
    }

    bool ShouldCompress()
    {
        // Already encoded by the handler — recompressing would produce a body no client can decode
        // from the single Content-Encoding it is told about.
        if (response.Headers.ContainsKey(HeaderNames.ContentEncoding))
            return false;

        // A range is expressed over the encoded entity, so compressing after the fact would make
        // Content-Range describe bytes that no longer exist.
        if (response.StatusCode == StatusCodes.Status206PartialContent
            || response.Headers.ContainsKey(HeaderNames.ContentRange))
            return false;

        // Informational, no-content and not-modified responses have no body to compress.
        if (response.StatusCode is < 200 or StatusCodes.Status204NoContent or StatusCodes.Status304NotModified)
            return false;

        if (!options.IsCompressibleType(response.ContentType))
            return false;

        // Only when the length is known. A streamed response of unknown size is compressed, since
        // "unknown" is exactly the case where the payload might be large.
        return response.ContentLength is not { } length || length >= options.MinimumBytes;
    }

    /// <summary>Writes body bytes, through the compressor once one exists.</summary>
    async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        this.EnsureDecided();

        var destination = this.compressor ?? inner.Stream;

        await destination.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    async ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        this.EnsureDecided();

        // Flushing the compressor emits a sync point so the bytes so far are decodable — which is
        // what a handler that flushes deliberately is asking for.
        if (this.compressor is { } stream)
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        await inner.Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The response body as a stream, feeding whatever the decision produced.</summary>
    sealed class CompressionBodyStream(CompressingBodyControl owner) : Stream
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
            // Synchronous writes are rare here — every convenience helper is async — so paying for
            // a copy beats duplicating the whole write path.
            var copy = buffer.ToArray();
            this.WriteAsync(copy, 0, copy.Length, CancellationToken.None).GetAwaiter().GetResult();
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => owner.WriteAsync(new ReadOnlyMemory<byte>(buffer, offset, count), cancellationToken).AsTask();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => owner.WriteAsync(buffer, cancellationToken);

        public override void Flush() => this.FlushAsync(CancellationToken.None).GetAwaiter().GetResult();

        public override Task FlushAsync(CancellationToken cancellationToken)
            => owner.FlushAsync(cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
