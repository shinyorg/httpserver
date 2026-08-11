using System.Buffers;
using System.Globalization;
using System.IO.Pipelines;
using System.Net.Quic;
using Shiny.Net.HttpServer.Http2.Hpack;
using Shiny.Net.HttpServer.Http3.Qpack;

#pragma warning disable CA1416 // Guarded by QuicListener.IsSupported before any of this runs.

namespace Shiny.Net.HttpServer.Http3;

/// <summary>
/// The response side of one HTTP/3 request stream.
/// <para>
/// Simpler than its HTTP/2 counterpart: QUIC handles flow control and there is no maximum frame
/// size to split against, so a DATA frame is a length and some bytes. Body writes are staged only
/// so that a handler making many small writes produces a few frames rather than hundreds.
/// </para>
/// </summary>
sealed class Http3ResponseBodyControl(QuicStream stream, HttpResponse response, QpackEncoder encoder)
    : IResponseBodyControl
{
    readonly ArrayBufferWriter<byte> staged = new(4096);

    Http3ResponseStream? bodyStream;
    PipeWriter? bodyWriter;
    bool completed;

    public bool HasStarted { get; private set; }

    public Stream Stream => this.bodyStream ??= new Http3ResponseStream(this);

    public PipeWriter Writer => this.bodyWriter ??= PipeWriter.Create(
        this.Stream,
        new StreamPipeWriterOptions(leaveOpen: true)
    );

    public async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        if (this.HasStarted)
            return;

        await response.InvokeOnStartingAsync().ConfigureAwait(false);
        this.HasStarted = true;
        response.Headers.IsReadOnly = true;

        var fields = new List<HeaderField>(response.Headers.Count + 1)
        {
            new(":status", response.StatusCode.ToString(CultureInfo.InvariantCulture))
        };

        foreach (var (name, values) in response.Headers)
        {
            // Connection-specific headers have no meaning here and are a protocol error if sent.
            if (IsConnectionSpecific(name))
                continue;

            foreach (var value in values)
            {
                if (value is not null)
                    fields.Add(new HeaderField(name.ToLowerInvariant(), value));
            }
        }

        await this.WriteHeaderFrameAsync(fields, cancellationToken).ConfigureAwait(false);
    }

    async ValueTask WriteHeaderFrameAsync(List<HeaderField> fields, CancellationToken cancellationToken)
    {
        var payload = new ArrayBufferWriter<byte>(256);
        encoder.Encode(payload, fields);

        var frame = new ArrayBufferWriter<byte>(payload.WrittenCount + 16);
        Http3Frame.Write(frame, Http3FrameType.Headers, payload.WrittenSpan);

        await stream.WriteAsync(frame.WrittenMemory, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask CompleteAsync(CancellationToken cancellationToken)
    {
        if (this.completed)
            return;

        this.completed = true;

        if (this.bodyWriter is not null)
            await this.bodyWriter.FlushAsync(cancellationToken).ConfigureAwait(false);

        await this.StartAsync(cancellationToken).ConfigureAwait(false);
        await this.FlushStagedAsync(cancellationToken).ConfigureAwait(false);

        if (response.HasTrailers)
        {
            // A second HEADERS frame, after the last DATA. No pseudo-headers — the status was
            // settled by the first one.
            var trailers = new List<HeaderField>(response.Trailers.Count);

            foreach (var (name, values) in response.Trailers)
            {
                if (IsConnectionSpecific(name))
                    continue;

                foreach (var value in values)
                {
                    if (value is not null)
                        trailers.Add(new HeaderField(name.ToLowerInvariant(), value));
                }
            }

            if (trailers.Count > 0)
                await this.WriteHeaderFrameAsync(trailers, cancellationToken).ConfigureAwait(false);
        }

        // The end of the body is the end of the stream in one direction. QUIC carries that as a
        // FIN, so there is no terminating frame to write.
        stream.CompleteWrites();
    }

    async ValueTask WriteBodyAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        await this.StartAsync(cancellationToken).ConfigureAwait(false);

        this.staged.Write(data.Span);

        // Flushed at a size that keeps framing overhead negligible without holding a large response
        // in memory.
        if (this.staged.WrittenCount >= 16 * 1024)
            await this.FlushStagedAsync(cancellationToken).ConfigureAwait(false);
    }

    async ValueTask FlushStagedAsync(CancellationToken cancellationToken)
    {
        if (this.staged.WrittenCount == 0)
            return;

        var frame = new ArrayBufferWriter<byte>(this.staged.WrittenCount + 16);
        Http3Frame.Write(frame, Http3FrameType.Data, this.staged.WrittenSpan);

        this.staged.Clear();

        await stream.WriteAsync(frame.WrittenMemory, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    static bool IsConnectionSpecific(string name) =>
        name.Equals(HeaderNames.Connection, StringComparison.OrdinalIgnoreCase)
        || name.Equals(HeaderNames.TransferEncoding, StringComparison.OrdinalIgnoreCase)
        || name.Equals(HeaderNames.KeepAlive, StringComparison.OrdinalIgnoreCase)
        || name.Equals(HeaderNames.Upgrade, StringComparison.OrdinalIgnoreCase);

    sealed class Http3ResponseStream(Http3ResponseBodyControl owner) : Stream
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
            => this.WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => owner.WriteBodyAsync(new ReadOnlyMemory<byte>(buffer, offset, count), cancellationToken).AsTask();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => owner.WriteBodyAsync(buffer, cancellationToken);

        public override void Flush() => this.FlushAsync(CancellationToken.None).GetAwaiter().GetResult();

        public override Task FlushAsync(CancellationToken cancellationToken)
            => owner.FlushStagedAsync(cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
