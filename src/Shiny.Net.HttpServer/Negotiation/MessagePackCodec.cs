using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace Shiny.Net.HttpServer.Negotiation;

/// <summary>A MessagePack body this codec cannot turn into JSON. Surfaces to the client as a 400.</summary>
sealed class MessagePackFormatException(string message) : Exception(message);

/// <summary>
/// MessagePack in and out, by transcoding against the JSON representation of the same value.
/// <para>
/// The deliberate choice here is to have no serializer of its own. A value reaches the wire as
/// MessagePack because its <c>JsonTypeInfo</c> is registered — the same metadata, the same property
/// names, the same converters as the JSON response — so the two representations of an endpoint can
/// never drift apart, and adding MessagePack costs an app zero attributes and zero new dependencies.
/// </para>
/// <para>
/// What that costs: one intermediate UTF-8 buffer per body, and the format's type system is JSON's.
/// A <c>byte[]</c> member travels as a base64 <c>str</c> rather than a MessagePack <c>bin</c>, and a
/// <c>decimal</c> too precise for a <c>double</c> loses digits, because that is what JSON does with
/// them. Both are worth knowing before picking MessagePack for a payload built out of either; an
/// app that needs the native encoding should write its own <see cref="IOutputFormatter"/> over the
/// MessagePack library of its choice.
/// </para>
/// </summary>
static class MessagePackCodec
{
    /// <summary>
    /// Matches <c>JsonDocument</c>'s own default. A hostile body nests to blow the stack, and both
    /// directions of this transcode recurse.
    /// </summary>
    const int MaxDepth = 64;

    // ---- JSON to MessagePack ----

    public static byte[] FromJson(ReadOnlyMemory<byte> utf8Json)
    {
        using var document = JsonDocument.Parse(utf8Json);

        var writer = new ArrayBufferWriter<byte>(Math.Max(1, utf8Json.Length));
        WriteElement(writer, document.RootElement, 0);

        return writer.WrittenSpan.ToArray();
    }

    static void WriteElement(IBufferWriter<byte> writer, JsonElement element, int depth)
    {
        if (depth > MaxDepth)
            throw new MessagePackFormatException($"The body nests deeper than {MaxDepth} levels.");

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                WriteMapHeader(writer, CountProperties(element));
                foreach (var property in element.EnumerateObject())
                {
                    WriteString(writer, property.Name);
                    WriteElement(writer, property.Value, depth + 1);
                }
                break;

            case JsonValueKind.Array:
                WriteArrayHeader(writer, element.GetArrayLength());
                foreach (var item in element.EnumerateArray())
                    WriteElement(writer, item, depth + 1);
                break;

            case JsonValueKind.String:
                WriteString(writer, element.GetString()!);
                break;

            case JsonValueKind.Number:
                WriteNumber(writer, element);
                break;

            case JsonValueKind.True:
                WriteRaw(writer, 0xc3);
                break;

            case JsonValueKind.False:
                WriteRaw(writer, 0xc2);
                break;

            default:
                WriteRaw(writer, 0xc0);
                break;
        }
    }

    /// <summary>
    /// A map header carries its entry count, so the members have to be counted before any of them is
    /// written. Walking the parsed document twice is cheap; buffering the body a second time to
    /// backfill a header is not.
    /// </summary>
    static int CountProperties(JsonElement element)
    {
        var count = 0;
        foreach (var _ in element.EnumerateObject())
            count++;

        return count;
    }

    static void WriteNumber(IBufferWriter<byte> writer, JsonElement element)
    {
        if (element.TryGetInt64(out var signed))
            WriteInt64(writer, signed);
        else if (element.TryGetUInt64(out var unsigned))
            WriteUInt64(writer, unsigned);
        else
            WriteDouble(writer, element.GetDouble());
    }

    static void WriteInt64(IBufferWriter<byte> writer, long value)
    {
        switch (value)
        {
            case >= 0 and <= 127:
                WriteRaw(writer, (byte)value);
                return;

            case >= -32 and < 0:
                WriteRaw(writer, (byte)value);
                return;

            case > 127 and <= byte.MaxValue:
                WriteRaw(writer, 0xcc, (byte)value);
                return;

            case > byte.MaxValue and <= ushort.MaxValue:
                WriteBigEndian(writer, 0xcd, (ushort)value);
                return;

            case > ushort.MaxValue and <= uint.MaxValue:
                WriteBigEndian(writer, 0xce, (uint)value);
                return;

            case >= sbyte.MinValue and < -32:
                WriteRaw(writer, 0xd0, unchecked((byte)value));
                return;

            case >= short.MinValue and < sbyte.MinValue:
                WriteBigEndian(writer, 0xd1, unchecked((ushort)(short)value));
                return;

            case >= int.MinValue and < short.MinValue:
                WriteBigEndian(writer, 0xd2, unchecked((uint)(int)value));
                return;

            default:
                WriteBigEndian(writer, value < 0 ? (byte)0xd3 : (byte)0xcf, unchecked((ulong)value));
                return;
        }
    }

    static void WriteUInt64(IBufferWriter<byte> writer, ulong value)
    {
        if (value <= long.MaxValue)
        {
            WriteInt64(writer, (long)value);
            return;
        }

        WriteBigEndian(writer, 0xcf, value);
    }

    static void WriteDouble(IBufferWriter<byte> writer, double value)
    {
        var span = writer.GetSpan(9);
        span[0] = 0xcb;
        BinaryPrimitives.WriteDoubleBigEndian(span[1..], value);
        writer.Advance(9);
    }

    static void WriteString(IBufferWriter<byte> writer, string value)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);

        switch (byteCount)
        {
            case < 32:
                WriteRaw(writer, (byte)(0xa0 | byteCount));
                break;

            case <= byte.MaxValue:
                WriteRaw(writer, 0xd9, (byte)byteCount);
                break;

            case <= ushort.MaxValue:
                WriteBigEndian(writer, 0xda, (ushort)byteCount);
                break;

            default:
                WriteBigEndian(writer, 0xdb, (uint)byteCount);
                break;
        }

        var span = writer.GetSpan(byteCount);
        writer.Advance(Encoding.UTF8.GetBytes(value, span));
    }

    static void WriteMapHeader(IBufferWriter<byte> writer, int count)
    {
        switch (count)
        {
            case < 16:
                WriteRaw(writer, (byte)(0x80 | count));
                return;

            case <= ushort.MaxValue:
                WriteBigEndian(writer, 0xde, (ushort)count);
                return;

            default:
                WriteBigEndian(writer, 0xdf, (uint)count);
                return;
        }
    }

    static void WriteArrayHeader(IBufferWriter<byte> writer, int count)
    {
        switch (count)
        {
            case < 16:
                WriteRaw(writer, (byte)(0x90 | count));
                return;

            case <= ushort.MaxValue:
                WriteBigEndian(writer, 0xdc, (ushort)count);
                return;

            default:
                WriteBigEndian(writer, 0xdd, (uint)count);
                return;
        }
    }

    static void WriteRaw(IBufferWriter<byte> writer, byte value)
    {
        writer.GetSpan(1)[0] = value;
        writer.Advance(1);
    }

    static void WriteRaw(IBufferWriter<byte> writer, byte prefix, byte value)
    {
        var span = writer.GetSpan(2);
        span[0] = prefix;
        span[1] = value;
        writer.Advance(2);
    }

    static void WriteBigEndian(IBufferWriter<byte> writer, byte prefix, ushort value)
    {
        var span = writer.GetSpan(3);
        span[0] = prefix;
        BinaryPrimitives.WriteUInt16BigEndian(span[1..], value);
        writer.Advance(3);
    }

    static void WriteBigEndian(IBufferWriter<byte> writer, byte prefix, uint value)
    {
        var span = writer.GetSpan(5);
        span[0] = prefix;
        BinaryPrimitives.WriteUInt32BigEndian(span[1..], value);
        writer.Advance(5);
    }

    static void WriteBigEndian(IBufferWriter<byte> writer, byte prefix, ulong value)
    {
        var span = writer.GetSpan(9);
        span[0] = prefix;
        BinaryPrimitives.WriteUInt64BigEndian(span[1..], value);
        writer.Advance(9);
    }

    // ---- MessagePack to JSON ----

    public static byte[] ToJson(ReadOnlySpan<byte> messagePack)
    {
        var buffer = new ArrayBufferWriter<byte>(Math.Max(1, messagePack.Length));

        using (var writer = new Utf8JsonWriter(buffer))
        {
            var position = 0;
            ReadValue(messagePack, ref position, writer, 0);

            if (position != messagePack.Length)
                throw new MessagePackFormatException("The body has trailing bytes after the top-level value.");
        }

        return buffer.WrittenSpan.ToArray();
    }

    static void ReadValue(ReadOnlySpan<byte> data, ref int position, Utf8JsonWriter writer, int depth)
    {
        if (depth > MaxDepth)
            throw new MessagePackFormatException($"The body nests deeper than {MaxDepth} levels.");

        var code = ReadByte(data, ref position);

        switch (code)
        {
            case <= 0x7f:
                writer.WriteNumberValue(code);
                return;

            case >= 0xe0:
                writer.WriteNumberValue(unchecked((sbyte)code));
                return;

            case >= 0x80 and <= 0x8f:
                ReadMap(data, ref position, writer, code & 0x0f, depth);
                return;

            case >= 0x90 and <= 0x9f:
                ReadArray(data, ref position, writer, code & 0x0f, depth);
                return;

            case >= 0xa0 and <= 0xbf:
                writer.WriteStringValue(ReadUtf8(data, ref position, code & 0x1f));
                return;

            case 0xc0:
                writer.WriteNullValue();
                return;

            case 0xc2:
                writer.WriteBooleanValue(false);
                return;

            case 0xc3:
                writer.WriteBooleanValue(true);
                return;

            // Binary has no JSON counterpart, so it arrives the way JSON always carries bytes:
            // base64. That is symmetric with what this codec emits for a byte[] member.
            case 0xc4:
                writer.WriteBase64StringValue(ReadRaw(data, ref position, ReadByte(data, ref position)));
                return;

            case 0xc5:
                writer.WriteBase64StringValue(ReadRaw(data, ref position, ReadUInt16(data, ref position)));
                return;

            case 0xc6:
                writer.WriteBase64StringValue(ReadRaw(data, ref position, ReadLength(data, ref position)));
                return;

            case 0xca:
                writer.WriteNumberValue(BinaryPrimitives.ReadSingleBigEndian(ReadRaw(data, ref position, 4)));
                return;

            case 0xcb:
                writer.WriteNumberValue(BinaryPrimitives.ReadDoubleBigEndian(ReadRaw(data, ref position, 8)));
                return;

            case 0xcc:
                writer.WriteNumberValue(ReadByte(data, ref position));
                return;

            case 0xcd:
                writer.WriteNumberValue(ReadUInt16(data, ref position));
                return;

            case 0xce:
                writer.WriteNumberValue(BinaryPrimitives.ReadUInt32BigEndian(ReadRaw(data, ref position, 4)));
                return;

            case 0xcf:
                writer.WriteNumberValue(BinaryPrimitives.ReadUInt64BigEndian(ReadRaw(data, ref position, 8)));
                return;

            case 0xd0:
                writer.WriteNumberValue(unchecked((sbyte)ReadByte(data, ref position)));
                return;

            case 0xd1:
                writer.WriteNumberValue(BinaryPrimitives.ReadInt16BigEndian(ReadRaw(data, ref position, 2)));
                return;

            case 0xd2:
                writer.WriteNumberValue(BinaryPrimitives.ReadInt32BigEndian(ReadRaw(data, ref position, 4)));
                return;

            case 0xd3:
                writer.WriteNumberValue(BinaryPrimitives.ReadInt64BigEndian(ReadRaw(data, ref position, 8)));
                return;

            case 0xd9:
                writer.WriteStringValue(ReadUtf8(data, ref position, ReadByte(data, ref position)));
                return;

            case 0xda:
                writer.WriteStringValue(ReadUtf8(data, ref position, ReadUInt16(data, ref position)));
                return;

            case 0xdb:
                writer.WriteStringValue(ReadUtf8(data, ref position, ReadLength(data, ref position)));
                return;

            case 0xdc:
                ReadArray(data, ref position, writer, ReadUInt16(data, ref position), depth);
                return;

            case 0xdd:
                ReadArray(data, ref position, writer, ReadLength(data, ref position), depth);
                return;

            case 0xde:
                ReadMap(data, ref position, writer, ReadUInt16(data, ref position), depth);
                return;

            case 0xdf:
                ReadMap(data, ref position, writer, ReadLength(data, ref position), depth);
                return;

            default:
                // 0xc1, and the ext family. An extension carries an application-defined type tag
                // that only its own serializer knows how to interpret, so guessing would be worse
                // than refusing.
                throw new MessagePackFormatException(
                    $"MessagePack type 0x{code:x2} has no JSON representation. "
                        + "Extension and timestamp types are not supported by the built-in formatter."
                );
        }
    }

    static void ReadArray(ReadOnlySpan<byte> data, ref int position, Utf8JsonWriter writer, int count, int depth)
    {
        Guard(data, position, count);

        writer.WriteStartArray();
        for (var i = 0; i < count; i++)
            ReadValue(data, ref position, writer, depth + 1);

        writer.WriteEndArray();
    }

    static void ReadMap(ReadOnlySpan<byte> data, ref int position, Utf8JsonWriter writer, int count, int depth)
    {
        Guard(data, position, count);

        writer.WriteStartObject();
        for (var i = 0; i < count; i++)
        {
            var code = ReadByte(data, ref position);

            // Integer keys are MessagePack's compact object convention, and they carry no property
            // names — nothing here could match them to a member. Refusing says so; emitting "0" as
            // a property name would deserialize to a DTO full of defaults and look like it worked.
            var key = code switch
            {
                >= 0xa0 and <= 0xbf => ReadUtf8(data, ref position, code & 0x1f),
                0xd9 => ReadUtf8(data, ref position, ReadByte(data, ref position)),
                0xda => ReadUtf8(data, ref position, ReadUInt16(data, ref position)),
                0xdb => ReadUtf8(data, ref position, ReadLength(data, ref position)),
                _ => throw new MessagePackFormatException(
                    "Map keys must be strings. Serialize with string keys — MessagePack-CSharp's "
                        + "[MessagePackObject(keyAsPropertyName: true)] or its contractless resolver."
                )
            };

            writer.WritePropertyName(key);
            ReadValue(data, ref position, writer, depth + 1);
        }

        writer.WriteEndObject();
    }

    /// <summary>
    /// Rejects a declared element count the remaining bytes cannot possibly satisfy. Without it a
    /// five-byte body claiming four billion entries would have the writer allocating on its way to
    /// discovering the truncation.
    /// </summary>
    static void Guard(ReadOnlySpan<byte> data, int position, int count)
    {
        if (count > data.Length - position)
            throw new MessagePackFormatException("The body declares more elements than it contains.");
    }

    static byte ReadByte(ReadOnlySpan<byte> data, ref int position)
    {
        if (position >= data.Length)
            throw Truncated();

        return data[position++];
    }

    static ushort ReadUInt16(ReadOnlySpan<byte> data, ref int position)
        => BinaryPrimitives.ReadUInt16BigEndian(ReadRaw(data, ref position, 2));

    /// <summary>Reads a 32-bit length, refusing one that cannot address a .NET buffer.</summary>
    static int ReadLength(ReadOnlySpan<byte> data, ref int position)
    {
        var length = BinaryPrimitives.ReadUInt32BigEndian(ReadRaw(data, ref position, 4));

        return length > int.MaxValue
            ? throw new MessagePackFormatException("The body declares a length larger than 2 GB.")
            : (int)length;
    }

    static ReadOnlySpan<byte> ReadRaw(ReadOnlySpan<byte> data, ref int position, int count)
    {
        if (count < 0 || (long)position + count > data.Length)
            throw Truncated();

        var slice = data.Slice(position, count);
        position += count;

        return slice;
    }

    static string ReadUtf8(ReadOnlySpan<byte> data, ref int position, int byteCount)
        => Encoding.UTF8.GetString(ReadRaw(data, ref position, byteCount));

    static MessagePackFormatException Truncated() => new("The MessagePack body ended mid-value.");
}
