using System.Buffers;
using System.IO.Pipelines;

namespace Shiny.Net.HttpServer.Http2;

/// <summary>
/// The response side of one HTTP/2 stream.
/// <para>
/// The staging buffer is not an optimisation: body bytes cannot go straight onto the connection,
/// because DATA frames are subject to flow control and have to be split at the peer's maximum frame
/// size. Bytes are collected here and turned into correctly sized, correctly credited frames on
/// flush.
/// </para>
/// </summary>
sealed class Http2ResponseBodyControl(Http2Connection connection, Http2Stream stream, HttpResponse response)
    : IResponseBodyControl
{
    readonly ArrayBufferWriter<byte> staged = new(4096);
    Http2ResponseStream? bodyStream;
    PipeWriter? bodyWriter;
    bool completed;

    public bool HasStarted { get; private set; }

    public Stream Stream => this.bodyStream ??= new Http2ResponseStream(this);

    public PipeWriter Writer => this.bodyWriter ??= new Http2ResponsePipeWriter(this);

    public async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        if (this.HasStarted)
            return;

        await response.InvokeOnStartingAsync().ConfigureAwait(false);
        this.HasStarted = true;

        // Content-Length is advisory in HTTP/2 — END_STREAM is what actually ends the body — but it
        // is still sent, because clients use it for progress and for validating what they received.
        await connection.WriteHeadersAsync(stream, response, endStream: false, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask CompleteAsync(CancellationToken cancellationToken)
    {
        if (this.completed)
            return;

        this.completed = true;

        if (!this.HasStarted)
        {
            // Nothing was written. One HEADERS frame with END_STREAM is the whole response, which
            // saves an empty DATA frame on every 204 and 304.
            await response.InvokeOnStartingAsync().ConfigureAwait(false);
            this.HasStarted = true;

            await connection.WriteHeadersAsync(stream, response, endStream: true, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (response.HasTrailers)
        {
            // The trailing HEADERS frame carries END_STREAM, so the last DATA frame must not: two
            // frames claiming to end the same stream is a protocol error.
            await this.FlushAsync(endStream: false, cancellationToken).ConfigureAwait(false);
            await connection.WriteTrailersAsync(stream, response.Trailers, cancellationToken).ConfigureAwait(false);

            return;
        }

        await this.FlushAsync(endStream: true, cancellationToken).ConfigureAwait(false);
    }

    internal void Stage(ReadOnlySpan<byte> data) => this.staged.Write(data);

    internal Span<byte> GetStagingSpan(int sizeHint) => this.staged.GetSpan(sizeHint);

    internal Memory<byte> GetStagingMemory(int sizeHint) => this.staged.GetMemory(sizeHint);

    internal void AdvanceStaging(int count) => this.staged.Advance(count);

    internal async ValueTask FlushAsync(bool endStream, CancellationToken cancellationToken)
    {
        await this.StartAsync(cancellationToken).ConfigureAwait(false);

        var payload = this.staged.WrittenMemory;

        if (payload.IsEmpty && !endStream)
            return;

        await connection.WriteDataAsync(stream, payload, endStream, cancellationToken).ConfigureAwait(false);
        this.staged.Clear();
    }
}

/// <summary>The response body as a <see cref="Stream"/>, for handlers that copy into it.</summary>
sealed class Http2ResponseStream(Http2ResponseBodyControl control) : Stream
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

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        control.Stage(buffer.Span);
        await control.FlushAsync(endStream: false, cancellationToken).ConfigureAwait(false);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => this.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count)
        => this.WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Flush() => this.FlushAsync(CancellationToken.None).GetAwaiter().GetResult();

    public override Task FlushAsync(CancellationToken cancellationToken)
        => control.FlushAsync(endStream: false, cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

/// <summary>The response body as a <see cref="PipeWriter"/>, for allocation-free writes.</summary>
sealed class Http2ResponsePipeWriter(Http2ResponseBodyControl control) : PipeWriter
{
    public override void Advance(int bytes) => control.AdvanceStaging(bytes);

    // Handed out from the staging buffer itself. Returning a copy here would silently discard
    // everything the caller wrote into it.
    public override Memory<byte> GetMemory(int sizeHint = 0)
        => control.GetStagingMemory(sizeHint == 0 ? 1 : sizeHint);

    public override Span<byte> GetSpan(int sizeHint = 0) => control.GetStagingSpan(sizeHint == 0 ? 1 : sizeHint);

    public override async ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
    {
        await control.FlushAsync(endStream: false, cancellationToken).ConfigureAwait(false);
        return new FlushResult(isCanceled: false, isCompleted: false);
    }

    public override void CancelPendingFlush()
    {
    }

    public override void Complete(Exception? exception = null)
    {
    }
}
