using System.IO.Pipelines;

namespace Shiny.Net.HttpServer.CommandLine.Monitoring;


/// <summary>
/// Stands in front of the response's own body control and counts what goes through it.
/// </summary>
/// <remarks>
/// Counts rather than copies, so unlike a recorder it buffers nothing: every write is handed straight
/// to the control underneath, and there is nothing left over to flush when the handler returns. The
/// stream and the writer are wrapped separately - a handler uses one or the other, and wrapping each
/// over its own inner half means neither write path is counted twice or missed.
/// </remarks>
sealed class CountingBodyControl(IResponseBodyControl inner, HttpResponse response, TrafficEntry entry) : IResponseBodyControl
{
    Stream? stream;
    PipeWriter? writer;
    bool announced;

    public bool HasStarted => inner.HasStarted;

    public Stream Stream => this.stream ??= new CountingWriteStream(inner.Stream, this);

    public PipeWriter Writer => this.writer ??= new CountingPipeWriter(inner.Writer, this);

    public ValueTask StartAsync(CancellationToken cancellationToken)
    {
        this.Announce();
        return inner.StartAsync(cancellationToken);
    }

    public ValueTask CompleteAsync(CancellationToken cancellationToken) => inner.CompleteAsync(cancellationToken);

    /// <summary>
    /// Takes the length and type off the response the first time a body byte moves. Headers are final
    /// by then, and a download's size is what gives its progress bar something to fill.
    /// </summary>
    void Announce()
    {
        if (this.announced)
            return;

        this.announced = true;
        entry.SetResponseLength(response.ContentLength);
        entry.SetContentType(response.ContentType);
    }

    void Count(long bytes)
    {
        this.Announce();
        entry.AddBytesOut(bytes);
    }


    sealed class CountingWriteStream(Stream inner, CountingBodyControl owner) : Stream
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
            owner.Count(buffer.Length);
            inner.Write(buffer);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            owner.Count(buffer.Length);
            return inner.WriteAsync(buffer, cancellationToken);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => this.WriteAsync(new ReadOnlyMemory<byte>(buffer, offset, count), cancellationToken).AsTask();

        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }


    /// <summary>
    /// Counts at <see cref="Advance"/>, which is the one place a pipe writer commits bytes - whatever
    /// was asked for through <see cref="GetMemory"/> is only a buffer until then.
    /// </summary>
    sealed class CountingPipeWriter(PipeWriter inner, CountingBodyControl owner) : PipeWriter
    {
        public override void Advance(int bytes)
        {
            owner.Count(bytes);
            inner.Advance(bytes);
        }

        public override Memory<byte> GetMemory(int sizeHint = 0) => inner.GetMemory(sizeHint);
        public override Span<byte> GetSpan(int sizeHint = 0) => inner.GetSpan(sizeHint);

        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
            => inner.FlushAsync(cancellationToken);

        // Handed to the inner writer whole, rather than taking the base class's GetSpan/Advance/Flush
        // route - that route would count through Advance too, but lose whatever the inner writer does
        // better with a buffer it can see all of.
        public override ValueTask<FlushResult> WriteAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken = default)
        {
            owner.Count(source.Length);
            return inner.WriteAsync(source, cancellationToken);
        }

        public override void CancelPendingFlush() => inner.CancelPendingFlush();
        public override void Complete(Exception? exception = null) => inner.Complete(exception);
        public override ValueTask CompleteAsync(Exception? exception = null) => inner.CompleteAsync(exception);

        public override bool CanGetUnflushedBytes => inner.CanGetUnflushedBytes;
        public override long UnflushedBytes => inner.UnflushedBytes;
    }
}


/// <summary>Counts request body bytes as the handler reads them - which, for an upload, is as they arrive.</summary>
sealed class CountingReadStream(Stream inner, TrafficEntry entry) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => inner.Length;

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
        => this.Read(new Span<byte>(buffer, offset, count));

    public override int Read(Span<byte> buffer)
    {
        var read = inner.Read(buffer);
        entry.AddBytesIn(read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        entry.AddBytesIn(read);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => this.ReadAsync(new Memory<byte>(buffer, offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    // Passed through, so a handler that disposes the body sees exactly what it would have without
    // the count in front of it.
    protected override void Dispose(bool disposing)
    {
        if (disposing)
            inner.Dispose();
    }

    public override ValueTask DisposeAsync() => inner.DisposeAsync();
}
