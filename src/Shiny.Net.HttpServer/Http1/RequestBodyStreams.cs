using System.Buffers;
using System.IO.Pipelines;

namespace Shiny.Net.HttpServer.Http1;

/// <summary>
/// The connection, as a body stream sees it.
/// <para>
/// <see cref="StartAsync"/> is called once, just before the first byte is read: the connection uses
/// it to fix the request's size limit — which routing may have changed since the request arrived —
/// and to send a deferred <c>100 Continue</c>. Every read of the input goes through
/// <see cref="ReadAsync"/>, which is where the body-read timeout lives and where a transport failure
/// is told apart from an exception the handler made itself.
/// </para>
/// </summary>
interface IRequestBodySource
{
    /// <summary>Returns the size limit that applies to the body about to be read.</summary>
    ValueTask<long?> StartAsync(CancellationToken cancellationToken);

    /// <summary>Reads the connection's input on the body's behalf.</summary>
    ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken);

    /// <summary>The body has been read to its last byte; nothing of this request is left on the input.</summary>
    void OnBodyComplete();
}

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
    readonly long length;
    readonly IRequestBodySource? start;
    long remaining;
    bool started;

    public ContentLengthReadStream(PipeReader reader, long length, IRequestBodySource? start = null)
    {
        this.reader = reader;
        this.length = length;
        this.remaining = length;
        this.start = start;
        this.started = start is null;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        if (this.remaining == 0 || buffer.IsEmpty)
            return 0;

        if (!this.started)
        {
            // The declared length is known up front, so an oversized body is refused before a byte
            // of it is read — which, with a deferred 100 Continue, means before it is even sent.
            this.started = true;

            if (await this.start!.StartAsync(cancellationToken).ConfigureAwait(false) is { } max && this.length > max)
                throw new BadHttpRequestException(
                    $"Request body of {this.length} bytes exceeds the {max} byte limit.",
                    StatusCodes.Status413PayloadTooLarge
                );
        }

        while (true)
        {
            var result = await this.ReadInputAsync(cancellationToken).ConfigureAwait(false);
            var readableBuffer = result.Buffer;

            if (!readableBuffer.IsEmpty)
            {
                var toCopy = (int)Math.Min(Math.Min(readableBuffer.Length, buffer.Length), this.remaining);
                readableBuffer.Slice(0, toCopy).CopyTo(buffer.Span);
                this.reader.AdvanceTo(readableBuffer.GetPosition(toCopy));
                this.remaining -= toCopy;

                if (this.remaining == 0)
                    this.start?.OnBodyComplete();

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
        if (!this.started && this.remaining > 0)
        {
            // Nobody read the body. Draining one larger than the limit would mean accepting exactly
            // what the limit exists to refuse, so give up on the connection instead.
            this.started = true;

            if (await this.start!.StartAsync(cancellationToken).ConfigureAwait(false) is { } max && this.length > max)
                return false;
        }

        while (this.remaining > 0)
        {
            var result = await this.ReadInputAsync(cancellationToken).ConfigureAwait(false);
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

    ValueTask<ReadResult> ReadInputAsync(CancellationToken cancellationToken)
        => this.start is null ? this.reader.ReadAsync(cancellationToken) : this.start.ReadAsync(cancellationToken);
}

/// <summary>A body framed with chunked transfer encoding.</summary>
sealed class ChunkedReadStream : ReadOnlyRequestStream
{
    const byte CR = (byte)'\r';
    const byte LF = (byte)'\n';

    // A chunk size line is a few hex digits and, rarely, an extension. Anything long is malformed or
    // hostile, and an unterminated one would otherwise be buffered for as long as it kept coming.
    const int MaxChunkSizeLine = 256;

    enum ChunkState
    {
        Size,
        Data,
        DataTrailingCrlf,
        Trailers,
        Complete
    }

    readonly PipeReader reader;
    readonly IRequestBodySource? start;
    readonly int maxTrailersSize;
    long? maxBodySize;
    bool started;
    ChunkState state = ChunkState.Size;
    long chunkRemaining;
    long totalRead;
    int trailersSize;

    /// <param name="reader">The connection's input.</param>
    /// <param name="maxBodySize">A fixed limit, used when there is no <paramref name="start"/>.</param>
    /// <param name="start">Settles the limit when the body is first read, replacing <paramref name="maxBodySize"/>.</param>
    /// <param name="maxTrailersSize">Most bytes the trailer section may take: the same budget the request's headers get.</param>
    public ChunkedReadStream(
        PipeReader reader,
        long? maxBodySize,
        IRequestBodySource? start = null,
        int maxTrailersSize = 32 * 1024
    )
    {
        this.reader = reader;
        this.maxBodySize = maxBodySize;
        this.start = start;
        this.started = start is null;
        this.maxTrailersSize = maxTrailersSize;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        if (buffer.IsEmpty)
            return 0;

        if (!this.started)
        {
            this.started = true;
            this.maxBodySize = await this.start!.StartAsync(cancellationToken).ConfigureAwait(false);
        }

        while (true)
        {
            if (this.state == ChunkState.Complete)
                return 0;

            var result = await this.ReadInputAsync(cancellationToken).ConfigureAwait(false);
            var readable = result.Buffer;

            int copied;
            SequencePosition consumedTo;
            SequencePosition examinedTo;
            try
            {
                copied = this.Advance(in readable, buffer.Span, out consumedTo, out examinedTo);
            }
            catch (BadHttpRequestException)
            {
                // The reader has to be advanced before anything can read it again, and a malformed
                // body leaves nothing on it worth keeping.
                this.reader.AdvanceTo(readable.Start, readable.End);
                throw;
            }
            this.reader.AdvanceTo(consumedTo, examinedTo);

            if (this.state == ChunkState.Complete)
                this.start?.OnBodyComplete();

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

    ValueTask<ReadResult> ReadInputAsync(CancellationToken cancellationToken)
        => this.start is null ? this.reader.ReadAsync(cancellationToken) : this.start.ReadAsync(cancellationToken);

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
                        if (reader.Remaining > MaxChunkSizeLine)
                            throw new BadHttpRequestException("Chunk size line is implausibly long.");

                        consumedTo = reader.Position;
                        examinedTo = readable.End;
                        return copied;
                    }

                    if (sizeLine.Length > MaxChunkSizeLine)
                        throw new BadHttpRequestException("Chunk size line is implausibly long.");

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
                {
                    // Exactly CRLF, or the bare LF RFC 9112 §2.2 lets a recipient accept, and nothing
                    // else. Skipping ahead to the next LF instead made bytes after a chunk vanish — a
                    // body two parsers read differently — and had the server buffer everything a
                    // client sent for as long as it never sent an LF.
                    if (!reader.TryPeek(out var first))
                    {
                        consumedTo = reader.Position;
                        examinedTo = readable.End;
                        return copied;
                    }

                    if (first == LF)
                    {
                        reader.Advance(1);
                    }
                    else if (first != CR)
                    {
                        throw new BadHttpRequestException("Malformed chunk: data is not followed by CRLF.");
                    }
                    else if (!reader.TryPeek(1, out var second))
                    {
                        consumedTo = reader.Position;
                        examinedTo = readable.End;
                        return copied;
                    }
                    else if (second != LF)
                    {
                        throw new BadHttpRequestException("Malformed chunk: data is not followed by CRLF.");
                    }
                    else
                    {
                        reader.Advance(2);
                    }

                    this.state = ChunkState.Size;
                    break;
                }

                case ChunkState.Trailers:
                    // Zero-length chunk reached. Trailer fields may follow, ended by a blank line.
                    // They are checked and discarded — nothing downstream consults them — and held to
                    // the request-header budget, so a trailer section that never ends is refused
                    // rather than buffered.
                    while (true)
                    {
                        if (!reader.TryReadTo(out ReadOnlySequence<byte> trailerLine, LF, advancePastDelimiter: true))
                        {
                            if (this.trailersSize + reader.Remaining > this.maxTrailersSize)
                                throw TrailersTooLarge(this.maxTrailersSize);

                            consumedTo = reader.Position;
                            examinedTo = readable.End;
                            return copied;
                        }

                        var length = (int)trailerLine.Length;
                        this.trailersSize += length + 1;
                        if (this.trailersSize > this.maxTrailersSize)
                            throw TrailersTooLarge(this.maxTrailersSize);

                        if (length == 0 || (length == 1 && FirstByteIsCr(trailerLine)))
                        {
                            this.state = ChunkState.Complete;
                            consumedTo = reader.Position;
                            examinedTo = reader.Position;
                            return copied;
                        }

                        ValidateTrailerLine(trailerLine);
                    }

                case ChunkState.Complete:
                    consumedTo = reader.Position;
                    examinedTo = reader.Position;
                    return copied;
            }
        }
    }

    static BadHttpRequestException TrailersTooLarge(int max) => new(
        $"Request trailers exceed the {max} byte limit.",
        StatusCodes.Status431RequestHeaderFieldsTooLarge
    );

    static bool FirstByteIsCr(in ReadOnlySequence<byte> sequence)
    {
        var span = sequence.FirstSpan;
        return span.Length > 0 && span[0] == CR;
    }

    /// <summary>
    /// A trailer line is a header line: a token, a colon, a value with no control characters. One
    /// that is not is a body this parser and the next one would read differently.
    /// </summary>
    static void ValidateTrailerLine(in ReadOnlySequence<byte> line)
    {
        var buffer = line.IsSingleSegment ? line.FirstSpan : line.ToArray();
        var span = buffer.Length > 0 && buffer[^1] == CR ? buffer[..^1] : buffer;

        var colon = span.IndexOf((byte)':');
        if (colon <= 0)
            throw new BadHttpRequestException("Malformed trailer: missing colon.");

        foreach (var b in span[..colon])
        {
            if (!HeaderDictionary.IsTokenChar((char)b))
                throw new BadHttpRequestException("Malformed trailer: invalid character in name.");
        }

        if (span[(colon + 1)..].IndexOfAny(CR, (byte)0) >= 0)
            throw new BadHttpRequestException("Malformed trailer: control character in value.");
    }

    static long ParseChunkSize(in ReadOnlySequence<byte> line)
    {
        Span<byte> buffer = stackalloc byte[MaxChunkSizeLine];
        var length = (int)line.Length;
        line.CopyTo(buffer);
        var span = buffer[..length];

        // chunk-size [ chunk-ext ] CRLF. The LF is already gone; a CR may only be the last byte.
        if (span.Length > 0 && span[^1] == CR)
            span = span[..^1];

        if (span.IndexOfAny(CR, (byte)0) >= 0)
            throw new BadHttpRequestException("Malformed chunk size line.");

        long value = 0;
        var digits = 0;
        var index = 0;
        var parsing = true;
        while (parsing && index < span.Length)
        {
            var b = span[index];
            var digit = b switch
            {
                >= (byte)'0' and <= (byte)'9' => b - (byte)'0',
                >= (byte)'a' and <= (byte)'f' => b - (byte)'a' + 10,
                >= (byte)'A' and <= (byte)'F' => b - (byte)'A' + 10,
                _ => -1
            };

            if (digit < 0)
            {
                parsing = false;
            }
            else
            {
                // 16 hex digits is already long.MaxValue territory; more means an overflow attempt.
                if (++digits > 15)
                    throw new BadHttpRequestException("Chunk size is too large.");

                value = (value << 4) | (uint)digit;
                index++;
            }
        }

        if (digits == 0)
            throw new BadHttpRequestException("Malformed chunk size: no digits.");

        // Whatever follows the digits can only be an extension: optional whitespace, then ';'.
        // Extensions themselves are ignored, as RFC 9112 §7.1.1 allows.
        var rest = span[index..].TrimStart(" \t"u8);
        if (!rest.IsEmpty && rest[0] != (byte)';')
            throw new BadHttpRequestException("Malformed chunk size.");

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
