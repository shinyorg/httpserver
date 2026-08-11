using System.Buffers;

namespace Shiny.Net.HttpServer.Http3;

/// <summary>Frame types (RFC 9114 §7.2). Unknown types are ignored by design, which is how the protocol extends.</summary>
enum Http3FrameType : long
{
    Data = 0x00,
    Headers = 0x01,
    CancelPush = 0x03,
    Settings = 0x04,
    PushPromise = 0x05,
    Goaway = 0x07,
    MaxPushId = 0x0d
}

/// <summary>Error codes (RFC 9114 §8.1).</summary>
static class Http3ErrorCode
{
    public const long NoError = 0x0100;
    public const long GeneralProtocolError = 0x0101;
    public const long InternalError = 0x0102;
    public const long StreamCreationError = 0x0103;
    public const long ClosedCriticalStream = 0x0104;
    public const long FrameUnexpected = 0x0105;
    public const long FrameError = 0x0106;
    public const long ExcessiveLoad = 0x0107;
    public const long IdError = 0x0108;
    public const long SettingsError = 0x0109;
    public const long MissingSettings = 0x010a;
    public const long RequestRejected = 0x010b;
    public const long RequestCancelled = 0x010c;
    public const long MessageError = 0x010e;
    public const long QpackDecompressionFailed = 0x0200;
}

/// <summary>Unidirectional stream types (RFC 9114 §6.2).</summary>
static class Http3StreamType
{
    public const long Control = 0x00;
    public const long Push = 0x01;
    public const long QpackEncoder = 0x02;
    public const long QpackDecoder = 0x03;
}

/// <summary>Settings identifiers (RFC 9114 §7.2.4.1, RFC 9204 §5).</summary>
static class Http3SettingId
{
    public const long QpackMaxTableCapacity = 0x01;
    public const long MaxFieldSectionSize = 0x06;
    public const long QpackBlockedStreams = 0x07;
    public const long EnableConnectProtocol = 0x08;
}

/// <summary>A frame header: a type and a payload length, both varints.</summary>
readonly record struct Http3FrameHeader(long Type, long Length)
{
    public Http3FrameType KnownType => (Http3FrameType)this.Type;
}

/// <summary>
/// Reads and writes HTTP/3 frames.
/// <para>
/// Far simpler than HTTP/2's: QUIC already provides streams, ordering and flow control, so a frame
/// is nothing but a type, a length and a payload. There is no stream id in the frame — the QUIC
/// stream <em>is</em> the stream.
/// </para>
/// </summary>
static class Http3Frame
{
    /// <summary>
    /// Reads a frame header from a buffer, or returns false when it is not all there yet.
    /// </summary>
    public static bool TryReadHeader(in ReadOnlySequence<byte> buffer, out Http3FrameHeader header, out SequencePosition consumed)
    {
        var reader = new SequenceReader<byte>(buffer);

        if (VariableLengthInteger.TryRead(ref reader, out var type)
            && VariableLengthInteger.TryRead(ref reader, out var length))
        {
            header = new Http3FrameHeader(type, length);
            consumed = reader.Position;

            return true;
        }

        header = default;
        consumed = buffer.Start;

        return false;
    }

    public static void WriteHeader(IBufferWriter<byte> writer, Http3FrameType type, long payloadLength)
    {
        VariableLengthInteger.Write(writer, (long)type);
        VariableLengthInteger.Write(writer, payloadLength);
    }

    /// <summary>Writes a whole frame in one go.</summary>
    public static void Write(IBufferWriter<byte> writer, Http3FrameType type, ReadOnlySpan<byte> payload)
    {
        WriteHeader(writer, type, payload.Length);
        writer.Write(payload);
    }

    /// <summary>
    /// Builds a SETTINGS payload.
    /// <para>
    /// Sent first on the control stream and required before anything else — a peer that opens a
    /// control stream and starts with another frame is a protocol error, because the settings
    /// govern how everything after them is read.
    /// </para>
    /// </summary>
    public static byte[] BuildSettings(IEnumerable<(long Id, long Value)> settings)
    {
        var buffer = new ArrayBufferWriter<byte>(32);

        foreach (var (id, value) in settings)
        {
            VariableLengthInteger.Write(buffer, id);
            VariableLengthInteger.Write(buffer, value);
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Parses a SETTINGS payload into pairs. A duplicate identifier is a connection error, which the
    /// caller decides how to raise.
    /// </summary>
    public static bool TryParseSettings(ReadOnlySpan<byte> payload, out List<(long Id, long Value)> settings)
    {
        settings = [];

        var seen = new HashSet<long>();
        var offset = 0;

        while (offset < payload.Length)
        {
            if (!VariableLengthInteger.TryRead(payload[offset..], out var id, out var idLength))
                return false;

            offset += idLength;

            if (!VariableLengthInteger.TryRead(payload[offset..], out var value, out var valueLength))
                return false;

            offset += valueLength;

            if (!seen.Add(id))
                return false;

            settings.Add((id, value));
        }

        return true;
    }
}
