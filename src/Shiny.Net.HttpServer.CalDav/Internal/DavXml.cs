using System.Text;
using System.Xml;
using Shiny.Net.HttpServer.WebDav;

namespace Shiny.Net.HttpServer.CalDav.Internal;

/// <summary>The XML namespaces CalDAV and CardDAV clients speak, and the prefixes they are written with.</summary>
static class Ns
{
    public const string Dav = "DAV:";
    public const string CalDav = "urn:ietf:params:xml:ns:caldav";
    public const string CardDav = "urn:ietf:params:xml:ns:carddav";

    /// <summary>Apple's CalendarServer extensions — <c>getctag</c>, which older Apple clients poll.</summary>
    public const string CalendarServer = "http://calendarserver.org/ns/";

    /// <summary>Apple's iCal extensions — <c>calendar-color</c> and <c>calendar-order</c>.</summary>
    public const string AppleICal = "http://apple.com/ns/ical/";

    public static string PrefixOf(string ns) => ns switch
    {
        Dav => "D",
        CalDav => "C",
        CardDav => "CR",
        CalendarServer => "CS",
        AppleICal => "ICAL",
        _ => null!
    };
}

/// <summary>
/// Reading and writing DAV XML with <see cref="XmlReader"/> and <see cref="XmlWriter"/> only, for the
/// same reason the WebDAV package does: no serializer survives trimming, and the shapes are small.
/// </summary>
static class DavXml
{
    public const string ContentType = "application/xml; charset=utf-8";

    /// <summary>
    /// No DTD, no resolver, no entities — a request body is from the network, and an XML parser that
    /// expands entities is a way to read files off the device.
    /// </summary>
    public static readonly XmlReaderSettings ReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersFromEntities = 0,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = true,
        CloseInput = false
    };

    static readonly XmlWriterSettings WriterSettings = new()
    {
        Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        Indent = false,
        CloseOutput = false,
        OmitXmlDeclaration = false
    };

    /// <summary>Serialises a body and sends it with a Content-Length.</summary>
    public static async ValueTask WriteAsync(HttpContext context, int statusCode, Action<XmlWriter> write)
    {
        using var buffer = new MemoryStream(1024);

        using (var writer = XmlWriter.Create(buffer, WriterSettings))
        {
            writer.WriteStartDocument();
            write(writer);
            writer.WriteEndDocument();
        }

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = ContentType;

        await context.Response
            .WriteBytesAsync(buffer.GetBuffer().AsMemory(0, (int)buffer.Length), cancellationToken: context.RequestAborted)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Opens an element and declares every namespace a response might use on it, so the rest of the
    /// document is written with short, stable prefixes — some clients match on <c>D:</c> literally.
    /// </summary>
    public static void StartRoot(XmlWriter writer, string ns, string localName)
    {
        writer.WriteStartElement(Ns.PrefixOf(ns), localName, ns);

        foreach (var declared in (ReadOnlySpan<string>)[Ns.Dav, Ns.CalDav, Ns.CardDav, Ns.CalendarServer, Ns.AppleICal])
        {
            if (declared != ns)
                writer.WriteAttributeString("xmlns", Ns.PrefixOf(declared), null, declared);
        }
    }

    /// <summary>
    /// A precondition failure: <c>&lt;D:error&gt;&lt;C:no-uid-conflict&gt;…</c> (RFC 4918 §16).
    /// This is what a client shows a user instead of a bare 403.
    /// </summary>
    public static ValueTask WriteErrorAsync(
        HttpContext context,
        int statusCode,
        string ns,
        string condition,
        Action<XmlWriter>? inner = null
    ) => WriteAsync(context, statusCode, writer =>
    {
        StartRoot(writer, Ns.Dav, "error");
        Element(writer, ns, condition);

        inner?.Invoke(writer);

        writer.WriteEndElement();
        writer.WriteEndElement();
    });

    /// <summary>Opens an element; the caller closes it.</summary>
    public static void Element(XmlWriter writer, string ns, string localName)
    {
        var prefix = Ns.PrefixOf(ns);

        if (prefix is null)
            writer.WriteStartElement(localName, ns);
        else
            writer.WriteStartElement(prefix, localName, ns);
    }

    /// <summary>Writes an element with text, or empty when the text is null.</summary>
    public static void Text(XmlWriter writer, string ns, string localName, string? text)
    {
        Element(writer, ns, localName);

        if (text is not null)
            writer.WriteString(text);

        writer.WriteEndElement();
    }

    /// <summary>Writes an empty element — <c>&lt;D:collection/&gt;</c>.</summary>
    public static void Empty(XmlWriter writer, string ns, string localName)
    {
        Element(writer, ns, localName);
        writer.WriteEndElement();
    }

    /// <summary>Writes <c>&lt;D:href&gt;</c> inside a named element.</summary>
    public static void HrefProperty(XmlWriter writer, string ns, string localName, string href)
    {
        Element(writer, ns, localName);
        Text(writer, Ns.Dav, "href", href);
        writer.WriteEndElement();
    }

    public static string StatusLine(int statusCode)
        => $"HTTP/1.1 {statusCode} {StatusCodes.GetReasonPhrase(statusCode)}";

    /// <summary>Reads the whole body, or returns null once it passes <paramref name="maxBytes"/>.</summary>
    public static async ValueTask<MemoryStream?> ReadBodyAsync(HttpContext context, long maxBytes)
    {
        if (context.Request.ContentLength is { } declared && declared > maxBytes)
            return null;

        var buffer = new byte[8 * 1024];
        var body = new MemoryStream();

        int read;
        while ((read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted).ConfigureAwait(false)) > 0)
        {
            if (body.Length + read > maxBytes)
            {
                await body.DisposeAsync().ConfigureAwait(false);
                return null;
            }

            body.Write(buffer, 0, read);
        }

        body.Position = 0;
        return body;
    }

    public static WebDavPropertyName NameOf(XmlReader reader) => new(reader.NamespaceURI, reader.LocalName);

    public static bool Is(XmlReader reader, string ns, string localName)
        => reader.NodeType == XmlNodeType.Element
            && string.Equals(reader.NamespaceURI, ns, StringComparison.Ordinal)
            && string.Equals(reader.LocalName, localName, StringComparison.Ordinal);

    /// <summary>
    /// Walks the element children of the node the reader is on. The callback returns true when it
    /// consumed its element (<c>ReadInnerXml</c>, <c>Skip</c>, a nested walk), which leaves the reader
    /// on the next node already.
    /// </summary>
    public static void ForEachChild(XmlReader reader, Func<XmlReader, bool> onElement)
    {
        if (reader.IsEmptyElement)
            return;

        var depth = reader.Depth;
        var consumed = false;

        while (consumed || reader.Read())
        {
            consumed = false;

            if (reader.Depth <= depth)
                return;

            // Filters nest comp-filter inside comp-filter; a body built to nest them thousands deep
            // would take the evaluator's stack with it.
            if (reader.Depth > 48)
                throw new XmlException("The request nests elements too deeply.");

            if (reader.NodeType == XmlNodeType.Element && reader.Depth == depth + 1)
                consumed = onElement(reader);
        }
    }

    /// <summary>The text content of the element the reader is on; consumes it.</summary>
    public static string ReadText(XmlReader reader)
        => reader.IsEmptyElement ? ReadEmpty(reader) : reader.ReadElementContentAsString();

    static string ReadEmpty(XmlReader reader)
    {
        reader.Read();
        return string.Empty;
    }

    /// <summary>Moves to the document element. False when there is none.</summary>
    public static bool MoveToRoot(XmlReader reader)
    {
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element)
                return true;
        }

        return false;
    }
}
