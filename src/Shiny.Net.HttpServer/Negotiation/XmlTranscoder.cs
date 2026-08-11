using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Xml;
using System.Xml.Linq;

namespace Shiny.Net.HttpServer.Negotiation;

/// <summary>An XML body this codec cannot turn into the target type. Surfaces to the client as a 400.</summary>
sealed class XmlTranscodeException(string message) : Exception(message);

/// <summary>
/// XML in and out, against the same <c>JsonTypeInfo</c> the JSON formatter uses.
/// <para>
/// <see cref="System.Xml.Serialization.XmlSerializer"/> is the obvious tool and cannot be used here:
/// it builds its mapping by reflecting over the type at runtime, which is exactly what a trimmed or
/// AOT-published app has thrown away. So the mapping comes from the metadata that is already
/// registered, and the transcode is hand-written over <see cref="XmlWriter"/> and
/// <see cref="XElement"/> — no reflection, nothing for the trimmer to guess at.
/// </para>
/// <para>
/// The document shape, in both directions:
/// <list type="bullet">
/// <item>An object is an element with one child element per member, named as the member serializes.</item>
/// <item>A collection is an element whose child elements are its items. Coming out they are named
/// <c>item</c>; going in the names are ignored, since position is all that matters.</item>
/// <item>A member name XML cannot spell — a dictionary key with a space in it — becomes
/// <c>&lt;entry key="..."&gt;</c>.</item>
/// <item>Null is an empty element carrying <c>xsi:nil="true"</c>.</item>
/// <item>Everything else is text.</item>
/// </list>
/// </para>
/// </summary>
static class XmlTranscoder
{
    const int MaxDepth = 64;

    const string XsiNamespace = "http://www.w3.org/2001/XMLSchema-instance";

    // ---- JSON to XML ----

    public static byte[] ToXml(ReadOnlyMemory<byte> utf8Json, string rootName, string itemName)
    {
        using var document = JsonDocument.Parse(utf8Json);

        var buffer = new ArrayBufferWriter<byte>(Math.Max(1, utf8Json.Length));
        var settings = new XmlWriterSettings
        {
            OmitXmlDeclaration = false,
            Indent = false,
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            CloseOutput = false
        };

        using (var stream = new BufferWriterStream(buffer))
        using (var writer = XmlWriter.Create(stream, settings))
        {
            writer.WriteStartElement(rootName);

            // Declared once on the root whether or not a nil turns up. Adding it lazily would mean
            // buffering the whole document to find out.
            writer.WriteAttributeString("xmlns", "xsi", null, XsiNamespace);

            WriteBody(writer, document.RootElement, itemName, 0);
            writer.WriteEndElement();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Writes the contents of an element — the caller has already opened it.</summary>
    static void WriteBody(XmlWriter writer, JsonElement element, string itemName, int depth)
    {
        if (depth > MaxDepth)
            throw new XmlTranscodeException($"The value nests deeper than {MaxDepth} levels.");

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (IsXmlName(property.Name))
                    {
                        writer.WriteStartElement(property.Name);
                    }
                    else
                    {
                        writer.WriteStartElement("entry");
                        writer.WriteAttributeString("key", property.Name);
                    }

                    WriteBody(writer, property.Value, itemName, depth + 1);
                    writer.WriteEndElement();
                }
                return;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    writer.WriteStartElement(itemName);
                    WriteBody(writer, item, itemName, depth + 1);
                    writer.WriteEndElement();
                }
                return;

            case JsonValueKind.String:
                writer.WriteString(element.GetString());
                return;

            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                // The raw text is already the invariant form, and reusing it means a number cannot
                // round-trip differently through XML than it does through JSON.
                writer.WriteRaw(element.GetRawText());
                return;

            default:
                writer.WriteAttributeString("nil", XsiNamespace, "true");
                return;
        }
    }

    /// <summary>Whether a member name can be an element name as it stands.</summary>
    static bool IsXmlName(string name)
    {
        if (name.Length == 0 || !XmlConvert.IsStartNCNameChar(name[0]))
            return false;

        for (var i = 1; i < name.Length; i++)
        {
            if (!XmlConvert.IsNCNameChar(name[i]))
                return false;
        }

        return true;
    }

    // ---- XML to JSON ----

    /// <summary>
    /// Rewrites an XML body as the JSON the target type's metadata expects, so the value itself is
    /// still built by <c>System.Text.Json</c> from its own registered converters.
    /// <para>
    /// Being type-directed is the whole point. XML has no types — <c>&lt;Value&gt;21.5&lt;/Value&gt;</c>
    /// is text, and only the metadata knows whether it is a number, a string that happens to look
    /// like one, or an enum written as an ordinal. Guessing from the text is how a postal code of
    /// "01234" arrives as 1234.
    /// </para>
    /// </summary>
    public static byte[] ToJson(XElement root, JsonTypeInfo typeInfo)
    {
        var buffer = new ArrayBufferWriter<byte>(256);

        using (var writer = new Utf8JsonWriter(buffer))
            WriteValue(writer, root, typeInfo, 0);

        return buffer.WrittenSpan.ToArray();
    }

    static void WriteValue(Utf8JsonWriter writer, XElement element, JsonTypeInfo? typeInfo, int depth)
    {
        if (depth > MaxDepth)
            throw new XmlTranscodeException($"The document nests deeper than {MaxDepth} levels.");

        if (IsNil(element))
        {
            writer.WriteNullValue();
            return;
        }

        switch (typeInfo?.Kind)
        {
            case JsonTypeInfoKind.Object:
                WriteObject(writer, element, typeInfo, depth);
                return;

            case JsonTypeInfoKind.Enumerable:
                WriteArray(writer, element, typeInfo, depth);
                return;

            case JsonTypeInfoKind.Dictionary:
                WriteDictionary(writer, element, typeInfo, depth);
                return;

            case JsonTypeInfoKind.None:
                WriteScalar(writer, element, typeInfo.Type);
                return;

            default:
                // No metadata for this position: a custom converter, or a type the app's context does
                // not cover. Falling back to the document's own shape keeps such a body readable
                // instead of failing outright, at the cost of guessing scalar kinds from their text.
                WriteInferred(writer, element, depth);
                return;
        }
    }

    static void WriteObject(Utf8JsonWriter writer, XElement element, JsonTypeInfo typeInfo, int depth)
    {
        writer.WriteStartObject();

        foreach (var child in element.Elements())
        {
            var property = FindProperty(typeInfo, NameOf(child));

            // Unknown members are dropped, which is what System.Text.Json does with an unrecognised
            // JSON property. A stricter answer here would be stricter than the JSON path.
            if (property is null)
                continue;

            writer.WritePropertyName(property.Name);
            WriteValue(writer, child, Resolve(typeInfo, property.PropertyType), depth + 1);
        }

        writer.WriteEndObject();
    }

    static void WriteArray(Utf8JsonWriter writer, XElement element, JsonTypeInfo typeInfo, int depth)
    {
        var itemInfo = typeInfo.ElementType is { } elementType ? Resolve(typeInfo, elementType) : null;

        writer.WriteStartArray();

        // Child names are not consulted: in a collection, position is the only thing that identifies
        // an item, so <item> and <Tag> are the same document.
        foreach (var child in element.Elements())
            WriteValue(writer, child, itemInfo, depth + 1);

        writer.WriteEndArray();
    }

    static void WriteDictionary(Utf8JsonWriter writer, XElement element, JsonTypeInfo typeInfo, int depth)
    {
        var valueInfo = typeInfo.ElementType is { } elementType ? Resolve(typeInfo, elementType) : null;

        writer.WriteStartObject();

        foreach (var child in element.Elements())
        {
            writer.WritePropertyName(NameOf(child));
            WriteValue(writer, child, valueInfo, depth + 1);
        }

        writer.WriteEndObject();
    }

    static void WriteScalar(Utf8JsonWriter writer, XElement element, Type type)
    {
        var text = element.Value;
        var target = Nullable.GetUnderlyingType(type) ?? type;

        if (target == typeof(string))
        {
            writer.WriteStringValue(text);
            return;
        }

        if (text.Length == 0)
        {
            // An empty element for a non-string member is the absence of a value, not the empty
            // string — there is no target type here that could hold one.
            writer.WriteNullValue();
            return;
        }

        if (target == typeof(bool))
        {
            if (!bool.TryParse(text.Trim(), out var flag))
                throw new XmlTranscodeException($"'{text}' in <{element.Name.LocalName}> is not a boolean.");

            writer.WriteBooleanValue(flag);
            return;
        }

        if (IsNumeric(target))
        {
            WriteNumber(writer, element, text);
            return;
        }

        // An enum serializes as an ordinal by default and as a name under JsonStringEnumConverter,
        // and the metadata does not say which. The text does.
        if (target.IsEnum && long.TryParse(text.Trim(), CultureInfo.InvariantCulture, out var ordinal))
        {
            writer.WriteNumberValue(ordinal);
            return;
        }

        // Guid, DateTime, TimeSpan, Uri, byte[] as base64, enum names: all of them are JSON strings,
        // and their own converters do the parsing.
        writer.WriteStringValue(text);
    }

    static void WriteNumber(Utf8JsonWriter writer, XElement element, string text)
    {
        var trimmed = text.Trim();

        // decimal first: it is the wider of the two where money lives, and going through double
        // would quietly round a value the target member could have held exactly.
        if (decimal.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var exact))
        {
            writer.WriteNumberValue(exact);
            return;
        }

        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var approximate)
            && double.IsFinite(approximate))
        {
            writer.WriteNumberValue(approximate);
            return;
        }

        throw new XmlTranscodeException($"'{text}' in <{element.Name.LocalName}> is not a number.");
    }

    /// <summary>The shape the document itself implies, for a position with no metadata behind it.</summary>
    static void WriteInferred(Utf8JsonWriter writer, XElement element, int depth)
    {
        if (depth > MaxDepth)
            throw new XmlTranscodeException($"The document nests deeper than {MaxDepth} levels.");

        var children = element.Elements().ToList();

        if (children.Count == 0)
        {
            var text = element.Value;

            if (bool.TryParse(text.Trim(), out var flag))
                writer.WriteBooleanValue(flag);
            else if (decimal.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                writer.WriteNumberValue(number);
            else
                writer.WriteStringValue(text);

            return;
        }

        // Every child sharing one name is how XML spells a list; distinct names are members.
        var first = NameOf(children[0]);

        if (children.Count > 1 && children.TrueForAll(c => NameOf(c) == first))
        {
            writer.WriteStartArray();
            foreach (var child in children)
                WriteInferred(writer, child, depth + 1);

            writer.WriteEndArray();
            return;
        }

        writer.WriteStartObject();
        foreach (var child in children)
        {
            writer.WritePropertyName(NameOf(child));
            WriteInferred(writer, child, depth + 1);
        }

        writer.WriteEndObject();
    }

    static JsonPropertyInfo? FindProperty(JsonTypeInfo typeInfo, string name)
    {
        foreach (var property in typeInfo.Properties)
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return property;
        }

        return null;
    }

    /// <summary>
    /// The metadata for a nested type, from the same resolver that produced its parent — never from
    /// reflection, which is the whole reason this transcoder exists.
    /// </summary>
    static JsonTypeInfo? Resolve(JsonTypeInfo parent, Type type)
        => parent.Options.TryGetTypeInfo(type, out var typeInfo) ? typeInfo : null;

    /// <summary>The member name an element stands for: its own, or the <c>key</c> an <c>entry</c> carries.</summary>
    static string NameOf(XElement element)
        => element.Name.LocalName == "entry" && element.Attribute("key") is { } key
            ? key.Value
            : element.Name.LocalName;

    static bool IsNil(XElement element)
        => element.Attribute(XName.Get("nil", XsiNamespace)) is { } nil
        && bool.TryParse(nil.Value, out var flag)
        && flag;

    static bool IsNumeric(Type type) => type == typeof(int)
        || type == typeof(long)
        || type == typeof(double)
        || type == typeof(decimal)
        || type == typeof(float)
        || type == typeof(short)
        || type == typeof(byte)
        || type == typeof(sbyte)
        || type == typeof(ushort)
        || type == typeof(uint)
        || type == typeof(ulong)
        || type == typeof(Half)
        || type == typeof(System.Numerics.BigInteger);

    /// <summary>
    /// Settings for reading a body that arrived from the network.
    /// <para>
    /// The defaults are not these, and the difference is the point: DTDs off closes external entity
    /// expansion — the way an XML endpoint gets talked into reading <c>/etc/passwd</c> — and a
    /// character cap closes the entity-expansion bomb that needs no external anything.
    /// </para>
    /// </summary>
    public static XmlReaderSettings ReaderSettings(long maxCharacters) => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersFromEntities = 1024,
        MaxCharactersInDocument = maxCharacters,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = true,
        CloseInput = false
    };

    /// <summary>
    /// Adapts an <see cref="IBufferWriter{T}"/> to the <see cref="Stream"/> that
    /// <see cref="XmlWriter"/> insists on, without a second copy of the document.
    /// </summary>
    sealed class BufferWriterStream(IBufferWriter<byte> writer) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
            => this.Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer) => writer.Write(buffer);

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
