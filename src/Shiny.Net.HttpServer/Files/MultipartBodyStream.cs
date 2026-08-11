using System.Buffers;
using System.IO.Pipelines;

namespace Shiny.Net.HttpServer.Files;

/// <summary>
/// One multipart section's body, as a stream that ends at the next boundary.
/// <para>
/// The subtle part is how much can safely be handed out before the boundary is found. A delimiter
/// can straddle two reads, so everything except the last <c>boundary.Length - 1</c> bytes is
/// releasable and the tail has to be held back until more arrives. Getting that wrong produces a
/// parser that works on small uploads and corrupts large ones.
/// </para>
/// </summary>
sealed class MultipartBodyStream(PipeReader reader, byte[] boundary) : Stream
{
    bool complete;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (this.complete || buffer.IsEmpty)
            return 0;

        while (true)
        {
            var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var available = result.Buffer;

            if (TryFindBoundary(available, boundary, out var offset))
            {
                if (offset == 0)
                {
                    // Sitting on the delimiter: consume it so the reader can look at what follows,
                    // and report end-of-section.
                    reader.AdvanceTo(available.GetPosition(boundary.Length));
                    this.complete = true;

                    return 0;
                }

                return Copy(available, buffer, offset);
            }

            // No delimiter yet. Anything before the last (boundary.Length - 1) bytes cannot be part
            // of one, so it is safe to release; the tail waits for the next read.
            var releasable = available.Length - (boundary.Length - 1);
            if (releasable > 0)
                return Copy(available, buffer, releasable);

            if (result.IsCompleted || result.IsCanceled)
                throw new BadHttpRequestException("The multipart body ended without a closing boundary.");

            reader.AdvanceTo(available.Start, available.End);
        }

        int Copy(in ReadOnlySequence<byte> available, Memory<byte> destination, long releasable)
        {
            var take = (int)Math.Min(releasable, destination.Length);
            available.Slice(0, take).CopyTo(destination.Span);
            reader.AdvanceTo(available.GetPosition(take));

            return take;
        }
    }

    /// <summary>Consumes whatever is left of this section, so the reader can move to the next one.</summary>
    public async ValueTask DrainAsync(CancellationToken cancellationToken)
    {
        if (this.complete)
            return;

        var scratch = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (await this.ReadAsync(scratch, cancellationToken).ConfigureAwait(false) > 0)
            {
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    static bool TryFindBoundary(in ReadOnlySequence<byte> buffer, ReadOnlySpan<byte> boundary, out long offset)
    {
        var reader = new SequenceReader<byte>(buffer);

        if (reader.TryReadTo(out ReadOnlySequence<byte> before, boundary, advancePastDelimiter: false))
        {
            offset = before.Length;
            return true;
        }

        offset = -1;
        return false;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => this.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count)
        => this.ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override int Read(Span<byte> buffer)
    {
        var rented = ArrayPool<byte>.Shared.Rent(buffer.Length);
        try
        {
            var read = this.ReadAsync(rented.AsMemory(0, buffer.Length)).AsTask().GetAwaiter().GetResult();
            rented.AsSpan(0, read).CopyTo(buffer);

            return read;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
