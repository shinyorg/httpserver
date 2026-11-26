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

    /// <summary>
    /// Ceiling on the decoded header list, counted the way SETTINGS_MAX_HEADER_LIST_SIZE counts it:
    /// name plus value plus 32 for every field (RFC 9113 §6.5.2). Null means no ceiling.
    /// <para>
    /// Counted on the decoded size, not the encoded one, because the two can differ by orders of
    /// magnitude: a one-byte index can stand for a 4 KB entry in the dynamic table, and a block of
    /// them repeats it thousands of times.
    /// </para>
    /// </summary>
    public int? MaxHeaderListSize { get; set; }

    /// <summary>
    /// Decodes <paramref name="block"/> into <paramref name="destination"/>. Returns false when the
    /// decoded list went over <see cref="MaxHeaderListSize"/>: the block is still decoded to the end,
    /// because the dynamic table has to stay in step with the peer's, but fields past the limit are
    /// not collected and the request must be refused.
    /// </summary>
    public bool Decode(ReadOnlySpan<byte> block, List<HeaderField> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);

        var offset = 0;
        long listSize = 0;
        var withinLimit = true;

        while (offset < block.Length)
        {
            var first = block[offset];
            HeaderField field;

            if ((first & 0x80) != 0)
            {
                // 1xxxxxxx — indexed header field.
                field = this.Resolve(ReadInteger(block, ref offset, 7));
            }
            else if ((first & 0x40) != 0)
            {
                // 01xxxxxx — literal, and the peer is adding it to its dynamic table too.
                field = this.ReadLiteral(block, ref offset, 6);
                this.table.Add(field);
            }
            else if ((first & 0x20) != 0)
            {
                // 001xxxxx — dynamic table size update. Not a field, so nothing to collect.
                var size = ReadInteger(block, ref offset, 5);

                if (size > this.MaxAllowedTableSize)
                    throw new HpackException($"The peer asked for a {size} byte dynamic table, above the agreed maximum.");

                this.table.Resize(size);
                field = default;
            }
            else
            {
                // 0000xxxx or 0001xxxx — literal without indexing. The "never indexed" form differs
                // only in what an intermediary may do with it, which does not apply here.
                field = this.ReadLiteral(block, ref offset, 4);
            }

            if (field.Name is not null && withinLimit)
            {
                listSize += field.Name.Length + field.Value.Length + 32;
                withinLimit = this.MaxHeaderListSize is not { } max || listSize <= max;

                if (withinLimit)
                    destination.Add(field);
            }
        }

        return withinLimit;
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
        var nameIndex = ReadInteger(block, ref offset, prefixBits);

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
        var length = ReadInteger(block, ref offset, 7);

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
    /// <para>
    /// Capped at <see cref="int.MaxValue"/>, since every use is an index, a length or a size. A
    /// larger value is not a big number to clamp but a peer trying to wrap one negative, past the
    /// bounds checks that follow.
    /// </para>
    /// </summary>
    static int ReadInteger(ReadOnlySpan<byte> block, ref int offset, int prefixBits)
    {
        if (offset >= block.Length)
            throw new HpackException("The header block ended inside an integer.");

        var mask = (1 << prefixBits) - 1;
        long value = block[offset++] & mask;

        if (value < mask)
            return (int)value;

        var shift = 0;
        while (true)
        {
            if (offset >= block.Length)
                throw new HpackException("The header block ended inside an integer.");

            var b = block[offset++];
            value += (long)(b & 0x7F) << shift;

            if (value > int.MaxValue)
                throw new HpackException("A header integer is too large.");

            if ((b & 0x80) == 0)
                return (int)value;

            shift += 7;

            // Past 28 bits of shift a further group can only overflow, or pad with zeros that a
            // peer has no reason to send.
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
