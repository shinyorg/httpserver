using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;

namespace Shiny.Net.HttpServer.Negotiation;

/// <summary>
/// Writes XML for the callers that still need it — an integration on the other side of a corporate
/// gateway, a SOAP-era client, a device that ships with an XML parser and nothing else.
/// <para>
/// Not registered by default, and hand-written rather than delegating to
/// <c>XmlSerializer</c>: that class builds its mapping by reflecting over the type, which a trimmed
/// or AOT-published app cannot do. This one reads the same registered <c>JsonTypeInfo</c> the JSON
/// formatter reads, so a type gets an XML representation on exactly the same terms — its metadata
/// was registered — and needs no XML attributes of its own.
/// </para>
/// </summary>
public sealed class XmlOutputFormatter : IOutputFormatter
{
    public const string DefaultMediaType = "application/xml";

    /// <summary>The other spelling in common use. Registered alongside the first by <c>AddXml</c>.</summary>
    public const string TextMediaType = "text/xml";

    public XmlOutputFormatter(string mediaType = DefaultMediaType)
        => this.MediaType = mediaType ?? throw new ArgumentNullException(nameof(mediaType));

    public string MediaType { get; }

    /// <summary>
    /// Below JSON, so a client sending <c>*/*</c> still gets JSON. XML is for clients that ask.
    /// </summary>
    public int Priority => 40;

    /// <summary>
    /// The element name to wrap a response in. Null derives it from the value's type, which is what
    /// makes the common case need no configuration.
    /// </summary>
    public string? RootElementName { get; set; }

    /// <summary>The element name for collection items.</summary>
    public string ItemElementName { get; set; } = "item";

    public bool CanWrite(object? value) => value is null || JsonTypeInfoRegistry.TryGet(value.GetType(), out _);

    public async ValueTask WriteAsync(HttpContext context, object? value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var typeInfo = value is null ? null : JsonTypeInfoRegistry.GetRequired(value.GetType());
        var json = typeInfo is null
            ? "null"u8.ToArray()
            : JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);

        var xml = XmlTranscoder.ToXml(
            json,
            this.RootElementName ?? RootNameFor(value),
            this.ItemElementName
        );

        await context.Response.WriteBytesAsync(xml, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The value's type name, minus the arity suffix a generic carries. Only a label — the reader
    /// side ignores the root element's name, because the endpoint decides what type a body is.
    /// </summary>
    static string RootNameFor(object? value)
    {
        if (value is null)
            return "value";

        var name = value.GetType().Name;
        var arity = name.IndexOf('`');

        return arity > 0 ? name[..arity] : name;
    }
}

/// <summary>
/// Reads an XML request body into the type an endpoint asked for, guided by that type's registered
/// JSON metadata.
/// <para>
/// The metadata is what makes this trustworthy rather than a heuristic. XML says only that
/// <c>&lt;Value&gt;21.5&lt;/Value&gt;</c> is text; whether that is a number, a string or an enum
/// ordinal comes from the member it is being read into.
/// </para>
/// </summary>
public sealed class XmlInputFormatter : IInputFormatter
{
    public string MediaType => XmlOutputFormatter.DefaultMediaType;

    public int Priority => 40;

    /// <summary>
    /// How much body to buffer. XML has to be parsed into a tree before it can be walked against the
    /// metadata, so unlike the JSON formatter this one cannot stream.
    /// </summary>
    public int MaxBodyBytes { get; set; } = 1024 * 1024;

    public bool CanRead(string mediaType, Type targetType)
    {
        ArgumentNullException.ThrowIfNull(targetType);

        return IsXml(mediaType) && JsonTypeInfoRegistry.TryGet(targetType, out _);
    }

    /// <summary>
    /// Accepts <c>application/xml</c>, <c>text/xml</c> and any <c>+xml</c> structured suffix
    /// (<c>application/atom+xml</c>).
    /// </summary>
    internal static bool IsXml(string mediaType)
        => mediaType.Equals(XmlOutputFormatter.DefaultMediaType, StringComparison.OrdinalIgnoreCase)
        || mediaType.Equals(XmlOutputFormatter.TextMediaType, StringComparison.OrdinalIgnoreCase)
        || mediaType.EndsWith("+xml", StringComparison.OrdinalIgnoreCase);

    public async ValueTask<InputFormatterResult> ReadAsync(
        HttpContext context,
        Type targetType,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(targetType);

        var typeInfo = JsonTypeInfoRegistry.GetRequired(targetType);

        // Over the limit throws with a 413, which the connection writes as-is.
        var body = await context.Request
            .ReadBodyAsBytesAsync(this.MaxBodyBytes, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            using var stream = new MemoryStream(body, writable: false);
            using var reader = XmlReader.Create(stream, XmlTranscoder.ReaderSettings(this.MaxBodyBytes));

            var root = XElement.Load(reader);
            var json = XmlTranscoder.ToJson(root, typeInfo);

            return InputFormatterResult.FromValue(JsonSerializer.Deserialize(json, typeInfo));
        }
        catch (XmlException)
        {
            return InputFormatterResult.Malformed;
        }
        catch (XmlTranscodeException)
        {
            return InputFormatterResult.Malformed;
        }
        catch (JsonException)
        {
            // Well-formed XML whose content the target type rejects — a date that is not a date, a
            // missing required member.
            return InputFormatterResult.Malformed;
        }
    }
}

/// <summary>Registering the XML formatters.</summary>
public static class XmlFormatterExtensions
{
    /// <summary>
    /// Adds XML in both directions, under both <c>application/xml</c> and <c>text/xml</c>.
    /// <code>
    /// builder.Services.AddContentNegotiation(o => o.AddXml());
    /// </code>
    /// Responses need <c>Results.Negotiate(value)</c>, or
    /// <see cref="ContentNegotiationOptions.NegotiateByDefault"/> to make every
    /// <c>Results.Ok(value)</c> honour the <c>Accept</c> header. Request bodies need nothing —
    /// <c>Content-Type: application/xml</c> is enough from the moment this is registered.
    /// </summary>
    public static ContentNegotiationOptions AddXml(
        this ContentNegotiationOptions options,
        Action<XmlOutputFormatter>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(options);

        var primary = new XmlOutputFormatter();
        var alias = new XmlOutputFormatter(XmlOutputFormatter.TextMediaType);

        configure?.Invoke(primary);
        configure?.Invoke(alias);

        options.Add(primary, new XmlInputFormatter());

        // The input formatter matches every spelling by string; the output side matches an Accept
        // range against one media type, so text/xml needs its own instance.
        options.Formatters.Add(alias);

        return options;
    }

    /// <summary>Registers content negotiation with XML added, for an app that wants nothing else.</summary>
    public static IServiceCollection AddXmlFormatters(
        this IServiceCollection services,
        Action<ContentNegotiationOptions>? configure = null
    ) => services.AddContentNegotiation(o =>
    {
        o.AddXml();
        configure?.Invoke(o);
    });
}
