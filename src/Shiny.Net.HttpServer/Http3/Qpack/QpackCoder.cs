using System.Buffers;
using System.Text;
using Shiny.Net.HttpServer.Http2.Hpack;

namespace Shiny.Net.HttpServer.Http3.Qpack;

public sealed class QpackException(string message) : Exception(message);

/// <summary>
/// QPACK prefixed integers (RFC 9204 §4.1.1) — the same scheme HPACK uses.
/// <para>
/// A value that fits in the N low bits of the first byte is written there; otherwise those bits are
/// all set and the remainder follows as a continuation of 7-bit groups.
/// </para>
/// </summary>
static class QpackInteger
{
    public static bool TryDecode(ReadOnlySpan<byte> source, int prefixBits, out long value, out int consumed)
    {
        value = 0;
        consumed = 0;

        if (source.IsEmpty)
            return false;

        var mask = (1 << prefixBits) - 1;
        value = source[0] & mask;
        consumed = 1;

        if (value < mask)
            return true;

        var shift = 0;

        while (consumed < source.Length)
        {
            var b = source[consumed++];
            value += (long)(b & 0x7F) << shift;

            if ((b & 0x80) == 0)
                return true;

            shift += 7;

            // 62 bits is the widest anything in this protocol can be; more than that is a peer
            // trying to make the decoder allocate or loop.
            if (shift > 62)
                throw new QpackException("An integer is longer than the encoding allows.");
        }

        return false;
    }

    public static void Encode(IBufferWriter<byte> writer, long value, int prefixBits, byte prefixPattern)
    {
        var mask = (1 << prefixBits) - 1;

        if (value < mask)
        {
            var span = writer.GetSpan(1);
            span[0] = (byte)(prefixPattern | (byte)value);
            writer.Advance(1);

            return;
        }

        var buffer = writer.GetSpan(10);
        buffer[0] = (byte)(prefixPattern | (byte)mask);

        var written = 1;
        var remaining = value - mask;

        while (remaining >= 0x80)
        {
            buffer[written++] = (byte)((remaining & 0x7F) | 0x80);
            remaining >>= 7;
        }

        buffer[written++] = (byte)remaining;
        writer.Advance(written);
    }
}

/// <summary>
/// Decodes a QPACK field section.
/// <para>
/// The server announces a dynamic table capacity of zero, so a peer may only reference the static
/// table or send literals. That is spec-legal and deliberate: the dynamic table is what makes QPACK
/// complicated — it requires an encoder stream, insert-count tracking and the ability to block a
/// request stream until the table catches up. Refusing it costs a few bytes per response and
/// removes the entire class of head-of-line bugs that come with it.
/// </para>
/// </summary>
sealed class QpackDecoder(int maxFieldSectionSize = 32 * 1024)
{
    public List<HeaderField> Decode(ReadOnlySpan<byte> payload)
    {
        var fields = new List<HeaderField>(16);
        var offset = 0;
        var totalSize = 0;

        // Field section prefix: required insert count, then a signed delta base. Both are zero
        // while the dynamic table is unused, but they are always present and must be consumed.
        if (!QpackInteger.TryDecode(payload, 8, out var requiredInsertCount, out var consumed))
            throw new QpackException("The field section prefix is truncated.");

        offset += consumed;

        if (requiredInsertCount != 0)
            throw new QpackException(
                "The peer referenced the dynamic table, which this server declined by announcing a capacity of zero."
            );

        if (offset >= payload.Length || !QpackInteger.TryDecode(payload[offset..], 7, out _, out consumed))
            throw new QpackException("The field section prefix is truncated.");

        offset += consumed;

        while (offset < payload.Length)
        {
            var first = payload[offset];

            HeaderField field;

            if ((first & 0x80) != 0)
            {
                // 1Txxxxxx — indexed field line. T set means the static table.
                if (!QpackInteger.TryDecode(payload[offset..], 6, out var index, out consumed))
                    throw new QpackException("An indexed field line is truncated.");

                offset += consumed;

                if ((first & 0x40) == 0)
                    throw new QpackException("A dynamic table reference is not accepted.");

                if (!QpackStaticTable.TryGet(index, out field))
                    throw new QpackException($"Static table index {index} does not exist.");
            }
            else if ((first & 0x40) != 0)
            {
                // 01NTxxxx — literal with a name reference.
                if (!QpackInteger.TryDecode(payload[offset..], 4, out var index, out consumed))
                    throw new QpackException("A literal field line is truncated.");

                offset += consumed;

                if ((first & 0x10) == 0)
                    throw new QpackException("A dynamic table name reference is not accepted.");

                if (!QpackStaticTable.TryGet(index, out var nameEntry))
                    throw new QpackException($"Static table index {index} does not exist.");

                var value = ReadString(payload, ref offset, prefixBits: 7);
                field = new HeaderField(nameEntry.Name, value);
            }
            else if ((first & 0x20) != 0)
            {
                // 001Nxxxx — literal with a literal name.
                var name = ReadString(payload, ref offset, prefixBits: 3);
                var value = ReadString(payload, ref offset, prefixBits: 7);

                field = new HeaderField(name, value);
            }
            else
            {
                // 0001xxxx and 0000xxxx are the post-base forms, which only exist to reference the
                // dynamic table.
                throw new QpackException("A post-base field line is not accepted without a dynamic table.");
            }

            totalSize += field.Name.Length + field.Value.Length + 32;

            if (totalSize > maxFieldSectionSize)
                throw new QpackException("The field section is larger than the configured limit.");

            fields.Add(field);
        }

        return fields;
    }

    static string ReadString(ReadOnlySpan<byte> payload, ref int offset, int prefixBits)
    {
        if (offset >= payload.Length)
            throw new QpackException("A string literal is truncated.");

        var huffman = (payload[offset] & (1 << prefixBits)) != 0;

        if (!QpackInteger.TryDecode(payload[offset..], prefixBits, out var length, out var consumed))
            throw new QpackException("A string length is truncated.");

        offset += consumed;

        if (length < 0 || offset + length > payload.Length)
            throw new QpackException("A string literal runs past the end of the field section.");

        var bytes = payload.Slice(offset, (int)length);
        offset += (int)length;

        if (!huffman)
            return Encoding.Latin1.GetString(bytes);

        // Same Huffman code as HPACK (RFC 7541 Appendix B); QPACK reuses it unchanged.
        var buffer = new byte[HpackHuffman.GetMaxDecodedLength(bytes.Length)];
        var decoded = HpackHuffman.Decode(bytes, buffer);

        return Encoding.Latin1.GetString(buffer.AsSpan(0, decoded));
    }
}

/// <summary>
/// Encodes a QPACK field section.
/// <para>
/// Static references where they exist, literals otherwise, and never an insertion — so the encoder
/// stream stays empty and no response can ever be blocked waiting for the peer's table to catch up.
/// </para>
/// </summary>
sealed class QpackEncoder
{
    public void Encode(IBufferWriter<byte> writer, IReadOnlyList<HeaderField> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        // Required insert count 0 and base delta 0: this section references no dynamic entries, so
        // the decoder never has to wait for one.
        var prefix = writer.GetSpan(2);
        prefix[0] = 0;
        prefix[1] = 0;
        writer.Advance(2);

        foreach (var field in fields)
        {
            var name = field.Name.ToLowerInvariant();
            var exact = QpackStaticTable.FindExact(name, field.Value);

            if (exact >= 0)
            {
                // 1 1 index(6+) — indexed field line, static table.
                QpackInteger.Encode(writer, exact, 6, 0xC0);
                continue;
            }

            var byName = QpackStaticTable.FindName(name);

            if (byName >= 0)
            {
                // 0 1 N=0 T=1 index(4+) — literal with a static name reference.
                QpackInteger.Encode(writer, byName, 4, 0x50);
                WriteString(writer, field.Value, prefixBits: 7);

                continue;
            }

            // 0 0 1 N=0 H index(3+) — literal with a literal name.
            WriteString(writer, name, prefixBits: 3, prefixPattern: 0x20);
            WriteString(writer, field.Value, prefixBits: 7);
        }
    }

    static void WriteString(IBufferWriter<byte> writer, string value, int prefixBits, byte prefixPattern = 0)
    {
        var raw = Encoding.Latin1.GetBytes(value);
        var huffmanLength = HpackHuffman.GetEncodedLength(raw);

        // Huffman only when it actually helps. On short values it routinely does not, and sending
        // the longer form would be paying CPU to make the response bigger.
        if (huffmanLength < raw.Length)
        {
            var huffmanFlag = (byte)(prefixPattern | (byte)(1 << prefixBits));
            QpackInteger.Encode(writer, huffmanLength, prefixBits, huffmanFlag);

            var span = writer.GetSpan(huffmanLength);
            var written = HpackHuffman.Encode(raw, span);
            writer.Advance(written);

            return;
        }

        QpackInteger.Encode(writer, raw.Length, prefixBits, prefixPattern);
        writer.Write(raw);
    }
}
