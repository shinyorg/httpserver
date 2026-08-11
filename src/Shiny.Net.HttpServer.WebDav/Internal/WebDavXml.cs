using System.Text;
using System.Xml;

namespace Shiny.Net.HttpServer.WebDav.Internal;

/// <summary>
/// Reading and writing the <c>DAV:</c> XML, with <see cref="XmlReader"/> and
/// <see cref="XmlWriter"/> and nothing else.
/// <para>
/// No serializer, no document object model. Partly because the shapes are small enough not to need
/// one, and mostly because the reflective ones do not survive trimming — which is the constraint
/// every package in this repo is built under.
/// </para>
/// </summary>
static class WebDavXml
{
    public const string Ns = "DAV:";
    public const string Prefix = "D";

    /// <summary>What a WebDAV body is served as. <c>text/xml</c> is what most clients send.</summary>
    public const string ContentType = "application/xml; charset=utf-8";

    /// <summary>
    /// Hardened on purpose. A WebDAV body arrives from the network, and an XML parser that resolves
    /// entities is the shortest route from "accepts XML" to "reads /etc/passwd" — so no DTD, no
    /// resolver, and no entity expansion at all.
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
        // No BOM. Some clients read the body with a strict parser that treats a leading BOM on a
        // declared-UTF-8 document as content.
        Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        Indent = false,
        CloseOutput = false,
        OmitXmlDeclaration = false
    };

    /// <summary>Serialises a body and sends it with a Content-Length.</summary>
    /// <remarks>
    /// Buffered rather than streamed straight to the socket. A multistatus is built from a walk that
    /// can fail partway, and a length-delimited body that either arrives whole or not at all is
    /// easier for a client to trust than a chunked one that stops early.
    /// </remarks>
    public static async ValueTask WriteAsync(
        HttpContext context,
        int statusCode,
        Action<XmlWriter> write,
        CancellationToken cancellationToken
    )
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
            .WriteBytesAsync(buffer.GetBuffer().AsMemory(0, (int)buffer.Length), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a <c>&lt;DAV:error&gt;</c> naming the precondition that failed. RFC 4918 §16 — the
    /// difference between a client that can explain itself to a user and one that shows "403".
    /// </summary>
    public static ValueTask WriteErrorAsync(
        HttpContext context,
        int statusCode,
        string conditionName,
        string? href,
        CancellationToken cancellationToken
    ) => WriteAsync(context, statusCode, writer =>
    {
        writer.WriteStartElement(Prefix, "error", Ns);
        writer.WriteStartElement(Prefix, conditionName, Ns);

        if (href is not null)
            writer.WriteElementString(Prefix, "href", Ns, href);

        writer.WriteEndElement();
        writer.WriteEndElement();
    }, cancellationToken);

    /// <summary>The status line a <c>&lt;DAV:status&gt;</c> carries.</summary>
    public static string StatusLine(int statusCode)
        => $"HTTP/1.1 {statusCode} {StatusCodes.GetReasonPhrase(statusCode)}";

    /// <summary>
    /// Percent-encodes a path for a <c>&lt;DAV:href&gt;</c>. Segment by segment, so the separators
    /// survive and everything else — spaces, <c>#</c>, <c>?</c>, non-ASCII — does not leak into the
    /// URL's syntax.
    /// </summary>
    public static string Href(string basePath, string relativePath, bool isCollection)
    {
        var builder = new StringBuilder(basePath);

        if (relativePath.Length > 0)
        {
            foreach (var segment in relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                builder.Append('/');
                builder.Append(Uri.EscapeDataString(segment));
            }
        }

        // A collection's href ends in a slash. Clients resolve member hrefs against it, and one
        // without the slash resolves them against its parent.
        if (isCollection && (builder.Length == 0 || builder[^1] != '/'))
            builder.Append('/');

        return builder.ToString();
    }

    /// <summary>Reads the whole body, or returns null once it passes <paramref name="maxBytes"/>.</summary>
    public static async ValueTask<MemoryStream?> ReadBodyAsync(
        HttpContext context,
        long maxBytes,
        CancellationToken cancellationToken
    )
    {
        // Counted as it reads rather than trusting Content-Length, which a client is free to
        // understate or omit entirely.
        var buffer = new byte[8 * 1024];
        var body = new MemoryStream();

        int read;
        while ((read = await context.Request.Body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (body.Length + read > maxBytes)
            {
                body.Dispose();
                return null;
            }

            body.Write(buffer, 0, read);
        }

        body.Position = 0;
        return body;
    }

    /// <summary>The element name the reader is positioned on.</summary>
    public static WebDavPropertyName NameOf(XmlReader reader) => new(reader.NamespaceURI, reader.LocalName);

    /// <summary>True when the reader is on an element in the <c>DAV:</c> namespace with this name.</summary>
    public static bool IsDav(XmlReader reader, string localName)
        => reader.NodeType == XmlNodeType.Element
            && string.Equals(reader.NamespaceURI, Ns, StringComparison.Ordinal)
            && string.Equals(reader.LocalName, localName, StringComparison.Ordinal);
}
