using System.Buffers;
using System.IO.Pipelines;

namespace Shiny.Net.HttpServer.Http1;

/// <summary>Base for read-only, forward-only request body streams.</summary>
abstract class ReadOnlyRequestStream : Stream
{
    public sealed override bool CanRead => true;
    public sealed override bool CanSeek => false;
    public sealed override bool CanWrite => false;
    public sealed override long Length => throw new NotSupportedException();

    public sealed override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public sealed override void Flush()
    {
    }

    public sealed override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public sealed override void SetLength(long value) => throw new NotSupportedException();
    public sealed override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public sealed override int Read(byte[] buffer, int offset, int count)
        // Synchronous reads would block a pipe that is fed asynchronously. Forcing callers onto the
        // async path is better than deadlocking them.
        => throw new NotSupportedException("Request bodies must be read asynchronously.");

    public sealed override int Read(Span<byte> buffer)
        => throw new NotSupportedException("Request bodies must be read asynchronously.");

    public sealed override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken
    ) => this.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <summary>
    /// Consumes any unread body bytes. The connection calls this before serving the next request:
    /// leftover body bytes would be misparsed as the head of the following request.
    /// </summary>
    public abstract ValueTask<bool> TryDrainAsync(CancellationToken cancellationToken);
}

/// <summary>A body with no content. Reads return 0 immediately.</summary>
sealed class EmptyReadStream : ReadOnlyRequestStream
{
    public static readonly EmptyReadStream Instance = new();

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(0);

    public override ValueTask<bool> TryDrainAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(true);
}

/// <summary>A body delimited by Content-Length.</summary>
sealed class ContentLengthReadStream : ReadOnlyRequestStream
{
    readonly PipeReader reader;
    long remaining;

    public ContentLengthReadStream(PipeReader reader, long length)
    {
        this.reader = reader;
        this.remaining = length;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        if (this.remaining == 0 || buffer.IsEmpty)
            return 0;

        while (true)
        {
            var result = await this.reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var readableBuffer = result.Buffer;

            if (!readableBuffer.IsEmpty)
            {
                var toCopy = (int)Math.Min(Math.Min(readableBuffer.Length, buffer.Length), this.remaining);
                readableBuffer.Slice(0, toCopy).CopyTo(buffer.Span);
                this.reader.AdvanceTo(readableBuffer.GetPosition(toCopy));
                this.remaining -= toCopy;
                return toCopy;
            }

            this.reader.AdvanceTo(readableBuffer.Start, readableBuffer.End);

            if (result.IsCompleted)
                throw new BadHttpRequestException(
                    $"Client disconnected with {this.remaining} bytes of the declared body still outstanding."
                );

            if (result.IsCanceled)
                throw new OperationCanceledException("The request body read was cancelled.");
        }
    }

    public override async ValueTask<bool> TryDrainAsync(CancellationToken cancellationToken)
    {
        while (this.remaining > 0)
        {
            var result = await this.reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;

            if (buffer.IsEmpty && result.IsCompleted)
            {
                this.reader.AdvanceTo(buffer.Start, buffer.End);
                return false;
            }

            var toSkip = (int)Math.Min(buffer.Length, this.remaining);
            this.reader.AdvanceTo(buffer.GetPosition(toSkip));
            this.remaining -= toSkip;
        }
        return true;
    }
}

/// <summary>A body framed with chunked transfer encoding.</summary>
sealed class ChunkedReadStream : ReadOnlyRequestStream
{
    const byte CR = (byte)'\r';
    const byte LF = (byte)'\n';

    enum ChunkState
    {
        Size,
        Data,
        DataTrailingCrlf,
        Trailers,
        Complete
    }

    readonly PipeReader reader;
    readonly long? maxBodySize;
    ChunkState state = ChunkState.Size;
    long chunkRemaining;
    long totalRead;

    public ChunkedReadStream(PipeReader reader, long? maxBodySize)
    {
        this.reader = reader;
        this.maxBodySize = maxBodySize;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        if (buffer.IsEmpty)
            return 0;

        while (true)
        {
            if (this.state == ChunkState.Complete)
                return 0;

            var result = await this.reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var readable = result.Buffer;

            var copied = this.Advance(in readable, buffer.Span, out var consumedTo, out var examinedTo);
            this.reader.AdvanceTo(consumedTo, examinedTo);

            if (copied > 0)
                return copied;

            if (this.state == ChunkState.Complete)
                return 0;

            if (result.IsCompleted)
                throw new BadHttpRequestException("Client disconnected before the chunked body was terminated.");

            if (result.IsCanceled)
                throw new OperationCanceledException("The request body read was cancelled.");
        }
    }

    /// <summary>
    /// Drives the chunk state machine over whatever is currently buffered, copying body bytes into
    /// <paramref name="destination"/>. Returns the number of bytes copied; zero means "need more input".
    /// </summary>
    int Advance(
        in ReadOnlySequence<byte> readable,
        Span<byte> destination,
        out SequencePosition consumedTo,
        out SequencePosition examinedTo
    )
    {
        var reader = new SequenceReader<byte>(readable);
        var copied = 0;

        while (true)
        {
            switch (this.state)
            {
                case ChunkState.Size:
                    if (!reader.TryReadTo(out ReadOnlySequence<byte> sizeLine, LF, advancePastDelimiter: true))
                    {
                        // A chunk size line is tiny; anything long is a malformed or hostile stream.
                        if (reader.Remaining > 256)
                            throw new BadHttpRequestException("Chunk size line is implausibly long.");

                        consumedTo = reader.Position;
                        examinedTo = readable.End;
                        return copied;
                    }

                    this.chunkRemaining = ParseChunkSize(sizeLine);
                    this.state = this.chunkRemaining == 0 ? ChunkState.Trailers : ChunkState.Data;

                    if (this.state == ChunkState.Data)
                    {
                        this.totalRead += this.chunkRemaining;
                        if (this.maxBodySize is { } max && this.totalRead > max)
                            throw new BadHttpRequestException(
                                $"Request body exceeds the {max} byte limit.",
                                StatusCodes.Status413PayloadTooLarge
                            );
                    }
                    break;

                case ChunkState.Data:
                {
                    if (copied == destination.Length)
                    {
                        consumedTo = reader.Position;
                        examinedTo = reader.Position;
                        return copied;
                    }

                    var available = (int)Math.Min(
                        Math.Min(reader.Remaining, this.chunkRemaining),
                        destination.Length - copied
                    );
                    if (available == 0)
                    {
                        consumedTo = reader.Position;
                        examinedTo = readable.End;
                        return copied;
                    }

                    reader.UnreadSequence.Slice(0, available).CopyTo(destination[copied..]);
                    reader.Advance(available);
                    copied += available;
                    this.chunkRemaining -= available;

                    if (this.chunkRemaining == 0)
                        this.state = ChunkState.DataTrailingCrlf;
                    break;
                }

                case ChunkState.DataTrailingCrlf:
                    if (!reader.TryReadTo(out ReadOnlySequence<byte> _, LF, advancePastDelimiter: true))
                    {
                        consumedTo = reader.Position;
                        examinedTo = readable.End;
                        return copied;
                    }
                    this.state = ChunkState.Size;
                    break;

                case ChunkState.Trailers:
                    // Zero-length chunk reached. Trailer headers may follow, terminated by a blank
                    // line. We consume and discard them: nothing downstream consults trailers.
                    while (true)
                    {
                        if (!reader.TryReadTo(out ReadOnlySequence<byte> trailerLine, LF, advancePastDelimiter: true))
                        {
                            consumedTo = reader.Position;
                            examinedTo = readable.End;
                            return copied;
                        }

                        var length = (int)trailerLine.Length;
                        if (length == 0 || (length == 1 && FirstByteIsCr(trailerLine)))
                        {
                            this.state = ChunkState.Complete;
                            consumedTo = reader.Position;
                            examinedTo = reader.Position;
                            return copied;
                        }
                    }

                case ChunkState.Complete:
                    consumedTo = reader.Position;
                    examinedTo = reader.Position;
                    return copied;
            }
        }
    }

    static bool FirstByteIsCr(in ReadOnlySequence<byte> sequence)
    {
        var span = sequence.FirstSpan;
        return span.Length > 0 && span[0] == CR;
    }

    static long ParseChunkSize(in ReadOnlySequence<byte> line)
    {
        Span<byte> buffer = stackalloc byte[32];
        var length = (int)Math.Min(line.Length, buffer.Length);
        line.Slice(0, length).CopyTo(buffer);
        var span = buffer[..length];

        long value = 0;
        var digits = 0;
        foreach (var b in span)
        {
            // Chunk extensions (";name=value") and the trailing CR end the size field.
            if (b == CR || b == LF || b == (byte)';')
                break;

            var digit = b switch
            {
                >= (byte)'0' and <= (byte)'9' => b - (byte)'0',
                >= (byte)'a' and <= (byte)'f' => b - (byte)'a' + 10,
                >= (byte)'A' and <= (byte)'F' => b - (byte)'A' + 10,
                _ => -1
            };
            if (digit < 0)
                throw new BadHttpRequestException("Malformed chunk size.");

            // 16 hex digits is already long.MaxValue territory; more means an overflow attempt.
            if (++digits > 15)
                throw new BadHttpRequestException("Chunk size is too large.");

            value = (value << 4) | (uint)digit;
        }

        if (digits == 0)
            throw new BadHttpRequestException("Malformed chunk size: no digits.");

        return value;
    }

    public override async ValueTask<bool> TryDrainAsync(CancellationToken cancellationToken)
    {
        var scratch = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            while (this.state != ChunkState.Complete)
            {
                int read;
                try
                {
                    read = await this.ReadAsync(scratch, cancellationToken).ConfigureAwait(false);
                }
                catch (BadHttpRequestException)
                {
                    return false;
                }

                if (read == 0)
                    break;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
        return this.state == ChunkState.Complete;
    }
}
