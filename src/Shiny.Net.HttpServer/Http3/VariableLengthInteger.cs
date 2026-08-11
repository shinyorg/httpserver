using System.Buffers;
using System.Buffers.Binary;

namespace Shiny.Net.HttpServer.Http3;

/// <summary>
/// QUIC's variable-length integer encoding (RFC 9000 §16).
/// <para>
/// The top two bits of the first byte give the length — 1, 2, 4 or 8 bytes — and the remaining 62
/// bits are the value. Everything in HTTP/3 is measured in these: frame types, lengths, stream
/// types and QPACK's own prefixed integers all sit on top of it, so this is the one piece that has
/// to be exactly right.
/// </para>
/// </summary>
static class VariableLengthInteger
{
    /// <summary>The largest value the 62-bit encoding can carry.</summary>
    public const long MaxValue = (1L << 62) - 1;

    /// <summary>Bytes needed to encode a value.</summary>
    public static int GetLength(long value) => value switch
    {
        < 0 => throw new ArgumentOutOfRangeException(nameof(value), "A varint cannot be negative."),
        < 1L << 6 => 1,
        < 1L << 14 => 2,
        < 1L << 30 => 4,
        <= MaxValue => 8,
        _ => throw new ArgumentOutOfRangeException(nameof(value), "The value exceeds the 62-bit varint range.")
    };

    public static int Write(Span<byte> destination, long value)
    {
        var length = GetLength(value);

        switch (length)
        {
            case 1:
                destination[0] = (byte)value;
                break;

            case 2:
                BinaryPrimitives.WriteUInt16BigEndian(destination, (ushort)((ushort)value | 0x4000));
                break;

            case 4:
                BinaryPrimitives.WriteUInt32BigEndian(destination, (uint)value | 0x8000_0000u);
                break;

            default:
                BinaryPrimitives.WriteUInt64BigEndian(destination, (ulong)value | 0xC000_0000_0000_0000ul);
                break;
        }

        return length;
    }

    public static void Write(IBufferWriter<byte> writer, long value)
    {
        var span = writer.GetSpan(8);
        writer.Advance(Write(span, value));
    }

    /// <summary>
    /// Reads a varint. Returns false when the buffer holds only part of one, which on a stream
    /// means "wait for more" rather than "malformed".
    /// </summary>
    public static bool TryRead(ReadOnlySpan<byte> source, out long value, out int consumed)
    {
        value = 0;
        consumed = 0;

        if (source.IsEmpty)
            return false;

        var length = 1 << (source[0] >> 6);
        if (source.Length < length)
            return false;

        // The two length bits are not part of the value.
        value = source[0] & 0x3F;

        for (var i = 1; i < length; i++)
            value = (value << 8) | source[i];

        consumed = length;
        return true;
    }

    public static bool TryRead(ref SequenceReader<byte> reader, out long value)
    {
        value = 0;

        if (!reader.TryPeek(out var first))
            return false;

        var length = 1 << (first >> 6);
        if (reader.Remaining < length)
            return false;

        Span<byte> buffer = stackalloc byte[8];
        var slice = buffer[..length];

        if (!reader.TryCopyTo(slice))
            return false;

        reader.Advance(length);

        value = slice[0] & 0x3F;

        for (var i = 1; i < length; i++)
            value = (value << 8) | slice[i];

        return true;
    }

    /// <summary>
    /// Reads a varint straight from a stream, one byte at a time.
    /// <para>
    /// Used for the first bytes of a QUIC stream, where the type has to be known before anything
    /// can be buffered against it. Returns null at end of stream.
    /// </para>
    /// </summary>
    public static async ValueTask<long?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[8];

        if (await stream.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) == 0)
            return null;

        var length = 1 << (buffer[0] >> 6);
        var read = 1;

        while (read < length)
        {
            var got = await stream.ReadAsync(buffer.AsMemory(read, length - read), cancellationToken)
                .ConfigureAwait(false);

            if (got == 0)
                return null;

            read += got;
        }

        long value = buffer[0] & 0x3F;

        for (var i = 1; i < length; i++)
            value = (value << 8) | buffer[i];

        return value;
    }
}
