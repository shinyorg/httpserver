using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Text;

namespace Shiny.Net.HttpServer.Tunneling;

/// <summary>What a tunnel frame is for.</summary>
public enum TunnelFrameType : byte
{
    /// <summary>Client → relay. Payload: <c>token \n requested-subdomain</c>, UTF-8.</summary>
    Hello = 1,

    /// <summary>Relay → client. Payload: the public URL the tunnel is now reachable at.</summary>
    HelloAck = 2,

    /// <summary>Relay → client. Payload: why registration was refused. The tunnel then closes.</summary>
    HelloReject = 3,

    /// <summary>Relay → client. A new inbound request; payload describes the remote peer.</summary>
    Open = 4,

    /// <summary>Either direction. Payload: bytes belonging to <c>StreamId</c>.</summary>
    Data = 5,

    /// <summary>Either direction. That stream is finished; no payload.</summary>
    CloseStream = 6,

    Ping = 7,

    Pong = 8
}

/// <summary>
/// One frame. <see cref="Payload"/> points into the reader's buffer and is only valid until the
/// handler returns — copy anything that needs to outlive the callback.
/// </summary>
public readonly ref struct TunnelFrame(TunnelFrameType type, uint streamId, ReadOnlySequence<byte> payload)
{
    public TunnelFrameType Type { get; } = type;

    /// <summary>Which multiplexed exchange this belongs to. Zero for connection-level frames.</summary>
    public uint StreamId { get; } = streamId;

    public ReadOnlySequence<byte> Payload { get; } = payload;

    public string PayloadAsString() => this.Payload.IsEmpty ? string.Empty : DecodeUtf8(this.Payload);

    static string DecodeUtf8(ReadOnlySequence<byte> sequence)
    {
        if (sequence.IsSingleSegment)
            return Encoding.UTF8.GetString(sequence.FirstSpan);

        var rented = ArrayPool<byte>.Shared.Rent((int)sequence.Length);
        try
        {
            sequence.CopyTo(rented);
            return Encoding.UTF8.GetString(rented, 0, (int)sequence.Length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}

/// <summary>
/// Framing for the reference tunnel.
/// <para>
/// Deliberately tiny: a 9-byte header of type, stream id and length, then the payload. Many HTTP
/// exchanges share one outbound connection, which is the whole point — a phone behind CGNAT can
/// dial out once and serve requests that arrive from anywhere.
/// </para>
/// </summary>
public static class TunnelProtocol
{
    /// <summary>Type (1) + stream id (4) + payload length (4).</summary>
    public const int HeaderLength = 9;

    /// <summary>
    /// Largest payload in one frame. Bigger bodies are split across frames, which is what keeps one
    /// large upload from starving every other stream on the tunnel.
    /// </summary>
    public const int MaxPayloadLength = 64 * 1024;

    public static void Write(PipeWriter writer, TunnelFrameType type, uint streamId, ReadOnlySpan<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (payload.Length > MaxPayloadLength)
            throw new ArgumentOutOfRangeException(
                nameof(payload),
                $"A tunnel frame payload cannot exceed {MaxPayloadLength} bytes."
            );

        var span = writer.GetSpan(HeaderLength + payload.Length);
        span[0] = (byte)type;
        BinaryPrimitives.WriteUInt32BigEndian(span[1..], streamId);
        BinaryPrimitives.WriteUInt32BigEndian(span[5..], (uint)payload.Length);
        payload.CopyTo(span[HeaderLength..]);

        writer.Advance(HeaderLength + payload.Length);
    }

    public static void Write(PipeWriter writer, TunnelFrameType type, uint streamId, string payload)
        => Write(writer, type, streamId, Encoding.UTF8.GetBytes(payload));

    /// <summary>
    /// Writes a frame straight from a pipe's own buffer, which is how body bytes get forwarded
    /// without an intermediate copy.
    /// </summary>
    public static void Write(PipeWriter writer, TunnelFrameType type, uint streamId, in ReadOnlySequence<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(writer);

        var length = payload.Length;
        if (length > MaxPayloadLength)
            throw new ArgumentOutOfRangeException(
                nameof(payload),
                $"A tunnel frame payload cannot exceed {MaxPayloadLength} bytes."
            );

        var span = writer.GetSpan(HeaderLength + (int)length);
        span[0] = (byte)type;
        BinaryPrimitives.WriteUInt32BigEndian(span[1..], streamId);
        BinaryPrimitives.WriteUInt32BigEndian(span[5..], (uint)length);
        payload.CopyTo(span[HeaderLength..]);

        writer.Advance(HeaderLength + (int)length);
    }

    /// <summary>
    /// Reads one frame if a whole one is buffered. On success <paramref name="buffer"/> is advanced
    /// past it; on failure it is left untouched so the caller can wait for more bytes.
    /// </summary>
    public static bool TryRead(ref ReadOnlySequence<byte> buffer, out TunnelFrameType type, out uint streamId, out ReadOnlySequence<byte> payload)
    {
        type = default;
        streamId = 0;
        payload = default;

        if (buffer.Length < HeaderLength)
            return false;

        Span<byte> header = stackalloc byte[HeaderLength];
        buffer.Slice(0, HeaderLength).CopyTo(header);

        var length = BinaryPrimitives.ReadUInt32BigEndian(header[5..]);
        if (length > MaxPayloadLength)
            throw new TunnelProtocolException(
                $"Frame declares a {length} byte payload, above the {MaxPayloadLength} byte limit."
            );

        if (buffer.Length < HeaderLength + length)
            return false;

        type = (TunnelFrameType)header[0];
        streamId = BinaryPrimitives.ReadUInt32BigEndian(header[1..]);
        payload = buffer.Slice(HeaderLength, length);
        buffer = buffer.Slice(HeaderLength + length);

        return true;
    }
}

/// <summary>Thrown when the peer sends something that is not a valid frame.</summary>
public sealed class TunnelProtocolException(string message) : Exception(message);
