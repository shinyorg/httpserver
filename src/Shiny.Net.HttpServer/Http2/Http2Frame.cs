using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;

namespace Shiny.Net.HttpServer.Http2;

/// <summary>Frame types (RFC 9113 §6). Unknown types must be ignored, not rejected.</summary>
public enum Http2FrameType : byte
{
    Data = 0x0,
    Headers = 0x1,
    Priority = 0x2,
    RstStream = 0x3,
    Settings = 0x4,
    PushPromise = 0x5,
    Ping = 0x6,
    GoAway = 0x7,
    WindowUpdate = 0x8,
    Continuation = 0x9
}

/// <summary>Frame flags. Meaning depends on the frame type, which is why they share bit values.</summary>
[Flags]
public enum Http2FrameFlags : byte
{
    None = 0x0,
    EndStream = 0x1,
    Ack = 0x1,
    EndHeaders = 0x4,
    Padded = 0x8,
    Priority = 0x20
}

/// <summary>Error codes (RFC 9113 §7).</summary>
public enum Http2ErrorCode : uint
{
    NoError = 0x0,
    ProtocolError = 0x1,
    InternalError = 0x2,
    FlowControlError = 0x3,
    SettingsTimeout = 0x4,
    StreamClosed = 0x5,
    FrameSizeError = 0x6,
    RefusedStream = 0x7,
    Cancel = 0x8,
    CompressionError = 0x9,
    ConnectError = 0xa,
    EnhanceYourCalm = 0xb,
    InadequateSecurity = 0xc,
    Http11Required = 0xd
}

/// <summary>A frame header: nine bytes of length, type, flags and stream id.</summary>
public readonly record struct Http2FrameHeader(
    int Length,
    Http2FrameType Type,
    Http2FrameFlags Flags,
    uint StreamId
)
{
    public const int Size = 9;

    public bool Has(Http2FrameFlags flag) => (this.Flags & flag) != 0;
}

/// <summary>An error that ends the whole connection.</summary>
public sealed class Http2ConnectionException(Http2ErrorCode code, string message) : Exception(message)
{
    public Http2ErrorCode Code { get; } = code;
}

/// <summary>An error that ends one stream and leaves the connection alone.</summary>
public sealed class Http2StreamException(uint streamId, Http2ErrorCode code, string message) : Exception(message)
{
    public uint StreamId { get; } = streamId;

    public Http2ErrorCode Code { get; } = code;
}

/// <summary>Reading and writing frame headers.</summary>
static class Http2Frame
{
    /// <summary>The client connection preface (RFC 9113 §3.4). Also how h2c is recognised.</summary>
    public static ReadOnlySpan<byte> Preface => "PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"u8;

    public static bool TryReadHeader(ref ReadOnlySequence<byte> buffer, out Http2FrameHeader header)
    {
        header = default;

        if (buffer.Length < Http2FrameHeader.Size)
            return false;

        Span<byte> raw = stackalloc byte[Http2FrameHeader.Size];
        buffer.Slice(0, Http2FrameHeader.Size).CopyTo(raw);

        var length = (raw[0] << 16) | (raw[1] << 8) | raw[2];
        var streamId = BinaryPrimitives.ReadUInt32BigEndian(raw[5..]) & 0x7FFFFFFF;

        header = new Http2FrameHeader(length, (Http2FrameType)raw[3], (Http2FrameFlags)raw[4], streamId);
        buffer = buffer.Slice(Http2FrameHeader.Size);

        return true;
    }

    public static void WriteHeader(
        IBufferWriter<byte> writer,
        Http2FrameType type,
        Http2FrameFlags flags,
        uint streamId,
        int length
    )
    {
        var span = writer.GetSpan(Http2FrameHeader.Size);

        span[0] = (byte)(length >> 16);
        span[1] = (byte)(length >> 8);
        span[2] = (byte)length;
        span[3] = (byte)type;
        span[4] = (byte)flags;

        // The top bit of the stream id is reserved and must be written as zero.
        BinaryPrimitives.WriteUInt32BigEndian(span[5..], streamId & 0x7FFFFFFF);

        writer.Advance(Http2FrameHeader.Size);
    }

    /// <summary>Writes a complete frame with a payload already in hand.</summary>
    public static void Write(
        IBufferWriter<byte> writer,
        Http2FrameType type,
        Http2FrameFlags flags,
        uint streamId,
        ReadOnlySpan<byte> payload
    )
    {
        WriteHeader(writer, type, flags, streamId, payload.Length);

        if (payload.IsEmpty)
            return;

        var span = writer.GetSpan(payload.Length);
        payload.CopyTo(span);
        writer.Advance(payload.Length);
    }

    /// <summary>
    /// Strips the padding a frame may carry, returning the real payload. Padding exists to obscure
    /// message sizes; a pad length that does not fit is a protocol error rather than a clamp.
    /// </summary>
    public static ReadOnlySequence<byte> RemovePadding(in Http2FrameHeader header, ReadOnlySequence<byte> payload)
    {
        if (!header.Has(Http2FrameFlags.Padded))
            return payload;

        if (payload.Length < 1)
            throw new Http2ConnectionException(Http2ErrorCode.ProtocolError, "A padded frame has no pad length.");

        Span<byte> first = stackalloc byte[1];
        payload.Slice(0, 1).CopyTo(first);

        var padding = first[0];
        var body = payload.Slice(1);

        if (padding > body.Length)
            throw new Http2ConnectionException(Http2ErrorCode.ProtocolError, "A frame's padding is longer than the frame.");

        return body.Slice(0, body.Length - padding);
    }
}

/// <summary>Serializes frame writes, so two streams cannot interleave halves of a frame.</summary>
sealed class Http2FrameWriter(PipeWriter writer) : IAsyncDisposable
{
    readonly SemaphoreSlim gate = new(1, 1);
    int disposed;

    /// <summary>Runs <paramref name="write"/> with exclusive access to the connection, then flushes.</summary>
    public async ValueTask WriteAsync(Action<PipeWriter> write, CancellationToken cancellationToken)
    {
        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            write(writer);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            this.gate.Release();
        }
    }

    public ValueTask WriteFrameAsync(
        Http2FrameType type,
        Http2FrameFlags flags,
        uint streamId,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken
    ) => this.WriteAsync(w => Http2Frame.Write(w, type, flags, streamId, payload.Span), cancellationToken);

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) == 0)
            this.gate.Dispose();

        return ValueTask.CompletedTask;
    }
}
