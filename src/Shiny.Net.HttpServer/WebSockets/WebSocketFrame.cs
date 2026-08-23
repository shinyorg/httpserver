using System.Buffers;
using System.Buffers.Binary;

namespace Shiny.Net.HttpServer.WebSockets;

/// <summary>WebSocket opcodes (RFC 6455 §5.2).</summary>
public enum WebSocketOpcode : byte
{
    Continuation = 0x0,
    Text = 0x1,
    Binary = 0x2,
    Close = 0x8,
    Ping = 0x9,
    Pong = 0xA
}

/// <summary>Why a WebSocket closed (RFC 6455 §7.4.1).</summary>
public enum WebSocketCloseStatus : ushort
{
    NormalClosure = 1000,
    EndpointUnavailable = 1001,
    ProtocolError = 1002,
    InvalidMessageType = 1003,
    /// <summary>Never sent on the wire; means the peer closed without a status.</summary>
    NoStatusReceived = 1005,
    InvalidPayloadData = 1007,
    PolicyViolation = 1008,
    MessageTooBig = 1009,
    InternalServerError = 1011
}

/// <summary>One decoded frame header.</summary>
readonly struct WebSocketFrameHeader
{
    public required bool Fin { get; init; }
    public required WebSocketOpcode Opcode { get; init; }

    /// <summary>
    /// The first reserved bit. With permessage-deflate negotiated it means "this message is
    /// compressed", set on the first frame of a message only; with no extension negotiated it is a
    /// protocol error.
    /// </summary>
    public bool Rsv1 { get; init; }
    public required bool Masked { get; init; }
    public required long PayloadLength { get; init; }
    public required uint MaskingKey { get; init; }

    /// <summary>Control frames carry status and keepalives; everything else is message data.</summary>
    public bool IsControl => ((byte)this.Opcode & 0x8) != 0;
}

/// <summary>
/// The WebSocket framing layer.
/// <para>
/// Small and fiddly in equal measure. Two details matter more than the rest: a client-to-server
/// frame <em>must</em> be masked and an unmasked one is a protocol error to be closed on, and a
/// control frame may be interleaved in the middle of a fragmented message — so a reader that
/// assumes continuation frames follow their header uninterrupted will hang the first time a browser
/// pings mid-message.
/// </para>
/// </summary>
static class WebSocketFrameCodec
{
    /// <summary>Largest payload accepted in one frame, before the message limit applies.</summary>
    public const long MaxFrameLength = 64 * 1024 * 1024;

    /// <summary>
    /// Reads a frame header if a whole one is buffered, advancing <paramref name="buffer"/> past it.
    /// </summary>
    public static bool TryReadHeader(ref ReadOnlySequence<byte> buffer, out WebSocketFrameHeader header, bool allowRsv1 = false)
    {
        header = default;

        if (buffer.Length < 2)
            return false;

        Span<byte> prefix = stackalloc byte[2];
        buffer.Slice(0, 2).CopyTo(prefix);

        var fin = (prefix[0] & 0x80) != 0;
        var rsv1 = (prefix[0] & 0x40) != 0;
        var reserved = prefix[0] & (allowRsv1 ? 0x30 : 0x70);
        var opcode = (WebSocketOpcode)(prefix[0] & 0x0F);
        var masked = (prefix[1] & 0x80) != 0;
        long length = prefix[1] & 0x7F;

        // A reserved bit only means something when an extension defined it. Anything else set is
        // the peer speaking a protocol this server never agreed to.
        if (reserved != 0)
            throw new WebSocketProtocolException(WebSocketCloseStatus.ProtocolError, "Reserved bits are set.");

        // RFC 7692 §6: compression applies to data messages. A compressed control frame would have
        // to be inflated before it could be acted on, which the spec forbids for good reason.
        if (rsv1 && ((byte)(prefix[0] & 0x0F) & 0x8) != 0)
            throw new WebSocketProtocolException(WebSocketCloseStatus.ProtocolError, "A control frame cannot be compressed.");

        var offset = 2;

        if (length == 126)
        {
            if (buffer.Length < offset + 2)
                return false;

            Span<byte> extended = stackalloc byte[2];
            buffer.Slice(offset, 2).CopyTo(extended);
            length = BinaryPrimitives.ReadUInt16BigEndian(extended);
            offset += 2;
        }
        else if (length == 127)
        {
            if (buffer.Length < offset + 8)
                return false;

            Span<byte> extended = stackalloc byte[8];
            buffer.Slice(offset, 8).CopyTo(extended);
            var wide = BinaryPrimitives.ReadUInt64BigEndian(extended);

            if (wide > long.MaxValue)
                throw new WebSocketProtocolException(WebSocketCloseStatus.MessageTooBig, "Frame length overflows.");

            length = (long)wide;
            offset += 8;
        }

        uint maskingKey = 0;
        if (masked)
        {
            if (buffer.Length < offset + 4)
                return false;

            Span<byte> key = stackalloc byte[4];
            buffer.Slice(offset, 4).CopyTo(key);
            maskingKey = BinaryPrimitives.ReadUInt32BigEndian(key);
            offset += 4;
        }

        // A control frame's payload must fit in one frame and stay small enough to handle inline.
        if (((byte)opcode & 0x8) != 0)
        {
            if (!fin)
                throw new WebSocketProtocolException(WebSocketCloseStatus.ProtocolError, "Control frames cannot be fragmented.");

            if (length > 125)
                throw new WebSocketProtocolException(WebSocketCloseStatus.ProtocolError, "Control frame payload exceeds 125 bytes.");
        }

        if (length > MaxFrameLength)
            throw new WebSocketProtocolException(WebSocketCloseStatus.MessageTooBig, "Frame is too large.");

        header = new WebSocketFrameHeader
        {
            Fin = fin,
            Opcode = opcode,
            Rsv1 = rsv1,
            Masked = masked,
            PayloadLength = length,
            MaskingKey = maskingKey
        };

        buffer = buffer.Slice(offset);
        return true;
    }

    /// <summary>
    /// Unmasks a payload in place. The mask is a repeating four-byte XOR, so
    /// <paramref name="offset"/> is where in that cycle this chunk starts — which matters when a
    /// payload arrives across several reads.
    /// </summary>
    public static void Unmask(Span<byte> payload, uint maskingKey, int offset)
    {
        if (maskingKey == 0)
            return;

        Span<byte> key = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(key, maskingKey);

        for (var i = 0; i < payload.Length; i++)
            payload[i] ^= key[(offset + i) & 3];
    }

    /// <summary>Writes a frame header. Server-to-client frames are never masked.</summary>
    public static void WriteHeader(IBufferWriter<byte> writer, WebSocketOpcode opcode, bool fin, long length, bool rsv1 = false)
    {
        var headerSize = 2 + length switch
        {
            <= 125 => 0,
            <= ushort.MaxValue => 2,
            _ => 8
        };

        var span = writer.GetSpan(headerSize);
        span[0] = (byte)((fin ? 0x80 : 0x00) | (rsv1 ? 0x40 : 0x00) | (byte)opcode);

        if (length <= 125)
        {
            span[1] = (byte)length;
        }
        else if (length <= ushort.MaxValue)
        {
            span[1] = 126;
            BinaryPrimitives.WriteUInt16BigEndian(span[2..], (ushort)length);
        }
        else
        {
            span[1] = 127;
            BinaryPrimitives.WriteUInt64BigEndian(span[2..], (ulong)length);
        }

        writer.Advance(headerSize);
    }
}

/// <summary>Thrown when a peer breaks the protocol. Carries the status to close with.</summary>
public sealed class WebSocketProtocolException(WebSocketCloseStatus status, string message) : Exception(message)
{
    public WebSocketCloseStatus Status { get; } = status;
}
