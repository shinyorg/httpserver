using System.Buffers;
using System.Text;

namespace Shiny.Net.HttpServer.Http2.Hpack;

/// <summary>
/// Decodes a header block into header fields.
/// <para>
/// One decoder per connection, because the dynamic table is connection state: block N's indices mean
/// what blocks 1..N-1 made them mean. That is also why a decode failure can only ever be a
/// connection error — once the table is out of step there is no way to resynchronise it.
/// </para>
/// </summary>
sealed class HpackDecoder(int maxDynamicTableSize = 4096)
{
    readonly HpackDynamicTable table = new(maxDynamicTableSize);

    /// <summary>Ceiling the peer may raise the table to. A dynamic table size update above it is an error.</summary>
    public int MaxAllowedTableSize { get; set; } = maxDynamicTableSize;

    /// <summary>Largest single header name or value accepted, to bound decompression.</summary>
    public int MaxStringLength { get; set; } = 32 * 1024;

    public void Decode(ReadOnlySpan<byte> block, List<HeaderField> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);

        var offset = 0;

        while (offset < block.Length)
        {
            var first = block[offset];

            if ((first & 0x80) != 0)
            {
                // 1xxxxxxx — indexed header field.
                var index = (int)ReadInteger(block, ref offset, 7);
                destination.Add(this.Resolve(index));
                continue;
            }

            if ((first & 0x40) != 0)
            {
                // 01xxxxxx — literal, and the peer is adding it to its dynamic table too.
                var field = this.ReadLiteral(block, ref offset, 6);
                this.table.Add(field);
                destination.Add(field);
                continue;
            }

            if ((first & 0x20) != 0)
            {
                // 001xxxxx — dynamic table size update.
                var size = (int)ReadInteger(block, ref offset, 5);

                if (size > this.MaxAllowedTableSize)
                    throw new HpackException($"The peer asked for a {size} byte dynamic table, above the agreed maximum.");

                this.table.Resize(size);
                continue;
            }

            // 0000xxxx or 0001xxxx — literal without indexing. The "never indexed" form differs
            // only in what an intermediary may do with it, which does not apply here.
            destination.Add(this.ReadLiteral(block, ref offset, 4));
        }
    }

    HeaderField Resolve(int index)
    {
        if (index == 0)
            throw new HpackException("Header index 0 is not valid.");

        if (index <= HpackStaticTable.Count)
            return HpackStaticTable.Entries[index];

        var dynamicIndex = index - HpackStaticTable.Count;
        if (dynamicIndex > this.table.Count)
            throw new HpackException($"Header index {index} is past the end of the dynamic table.");

        return this.table[dynamicIndex];
    }

    HeaderField ReadLiteral(ReadOnlySpan<byte> block, ref int offset, int prefixBits)
    {
        var nameIndex = (int)ReadInteger(block, ref offset, prefixBits);

        var name = nameIndex == 0
            ? this.ReadString(block, ref offset)
            : this.Resolve(nameIndex).Name;

        var value = this.ReadString(block, ref offset);

        return new HeaderField(name, value);
    }

    string ReadString(ReadOnlySpan<byte> block, ref int offset)
    {
        if (offset >= block.Length)
            throw new HpackException("The header block ended inside a string.");

        var huffman = (block[offset] & 0x80) != 0;
        var length = (int)ReadInteger(block, ref offset, 7);

        if (length > this.MaxStringLength)
            throw new HpackException($"A header string of {length} bytes exceeds the limit.");

        if (offset + length > block.Length)
            throw new HpackException("The header block ended inside a string.");

        var raw = block.Slice(offset, length);
        offset += length;

        if (!huffman)
            return Encoding.Latin1.GetString(raw);

        var max = HpackHuffman.GetMaxDecodedLength(length);
        if (max > this.MaxStringLength)
            throw new HpackException("A Huffman-coded header string could decode past the limit.");

        var buffer = ArrayPool<byte>.Shared.Rent(max);
        try
        {
            var written = HpackHuffman.Decode(raw, buffer);

            // Header values are opaque octets; Latin1 maps each byte to the same code point, which
            // round-trips anything a peer can send without inventing UTF-8 that was never there.
            return Encoding.Latin1.GetString(buffer, 0, written);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// HPACK's variable-length integer (RFC 7541 §5.1): a value that fits in the prefix is the
    /// prefix, and anything larger continues in seven-bit groups.
    /// </summary>
    static uint ReadInteger(ReadOnlySpan<byte> block, ref int offset, int prefixBits)
    {
        if (offset >= block.Length)
            throw new HpackException("The header block ended inside an integer.");

        var mask = (1 << prefixBits) - 1;
        var value = (uint)(block[offset++] & mask);

        if (value < mask)
            return value;

        var shift = 0;
        while (true)
        {
            if (offset >= block.Length)
                throw new HpackException("The header block ended inside an integer.");

            var b = block[offset++];
            value += (uint)(b & 0x7F) << shift;

            if ((b & 0x80) == 0)
                return value;

            shift += 7;

            // Five continuation bytes can already express more than a uint holds; anything longer
            // is a peer trying to make the decoder overflow.
            if (shift > 28)
                throw new HpackException("A header integer is too large.");
        }
    }
}

/// <summary>
/// Encodes header fields into a header block.
/// <para>
/// Deliberately simple: it uses the static table for exact and name-only matches and emits
/// everything else as a literal without indexing, so it keeps no dynamic table of its own. That
/// costs a few bytes per response and removes a whole class of bug — an encoder whose table has
/// drifted from the peer's produces headers that decode to something else entirely.
/// </para>
/// </summary>
sealed class HpackEncoder
{
    public void Encode(IReadOnlyList<HeaderField> fields, IBufferWriter<byte> writer)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(writer);

        foreach (var field in fields)
            this.Encode(field, writer);
    }

    public void Encode(HeaderField field, IBufferWriter<byte> writer)
    {
        var index = HpackStaticTable.Find(field.Name, field.Value);

        if (index > 0)
        {
            // 1xxxxxxx — the whole field is one static index.
            WriteInteger(writer, (uint)index, 7, 0x80);
            return;
        }

        if (index < 0)
        {
            // 0000xxxx — literal without indexing, name taken from the static table.
            WriteInteger(writer, (uint)(-index), 4, 0x00);
            WriteString(writer, field.Value);
            return;
        }

        WriteInteger(writer, 0, 4, 0x00);
        WriteString(writer, field.Name);
        WriteString(writer, field.Value);
    }

    /// <summary>Tells the peer we are keeping no dynamic table, so it can stop tracking one for us.</summary>
    public static void WriteTableSizeUpdate(IBufferWriter<byte> writer, int size)
        => WriteInteger(writer, (uint)size, 5, 0x20);

    static void WriteString(IBufferWriter<byte> writer, string value)
    {
        var byteCount = Encoding.Latin1.GetByteCount(value);

        // The 0x00 flag says "not Huffman-coded", which every decoder must accept.
        WriteInteger(writer, (uint)byteCount, 7, 0x00);

        var span = writer.GetSpan(byteCount);
        Encoding.Latin1.GetBytes(value, span);
        writer.Advance(byteCount);
    }

    static void WriteInteger(IBufferWriter<byte> writer, uint value, int prefixBits, byte flags)
    {
        var mask = (uint)((1 << prefixBits) - 1);
        var span = writer.GetSpan(6);

        if (value < mask)
        {
            span[0] = (byte)(flags | value);
            writer.Advance(1);

            return;
        }

        span[0] = (byte)(flags | mask);
        value -= mask;

        var written = 1;
        while (value >= 0x80)
        {
            span[written++] = (byte)((value & 0x7F) | 0x80);
            value >>= 7;
        }

        span[written++] = (byte)value;
        writer.Advance(written);
    }
}
