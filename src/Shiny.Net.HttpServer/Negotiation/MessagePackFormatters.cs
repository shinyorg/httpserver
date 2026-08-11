using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Shiny.Net.HttpServer.Negotiation;

/// <summary>
/// Writes MessagePack — a binary encoding of the same shape the JSON formatter produces, typically
/// 20–40% smaller on the wire and cheaper to parse on a constrained client.
/// <para>
/// Not registered by default. Nothing about it needs a dependency or an attribute, but a format the
/// caller cannot read is worse than no format at all, so it goes on the list only when an app says
/// its clients speak it.
/// </para>
/// </summary>
/// <param name="mediaType">
/// Which spelling this instance answers to. Defaults to the IANA registration; the pre-registration
/// <c>application/x-msgpack</c> is still what a lot of clients send, so
/// <see cref="MessagePackFormatterExtensions.AddMessagePack"/> registers an instance for it too.
/// Matching on the way out is exact, so a spelling nobody registered is a 406.
/// </param>
public sealed class MessagePackOutputFormatter(string mediaType = MessagePackOutputFormatter.DefaultMediaType)
    : IOutputFormatter
{
    /// <summary>The IANA registration.</summary>
    public const string DefaultMediaType = "application/msgpack";

    /// <summary>The spelling that predates the registration, and is still widely sent.</summary>
    public const string LegacyMediaType = "application/x-msgpack";

    public string MediaType { get; } = mediaType ?? throw new ArgumentNullException(nameof(mediaType));

    /// <summary>None. These are bytes, not text in an encoding.</summary>
    public string? Charset => null;

    /// <summary>
    /// Below JSON, so a client sending <c>*/*</c> still gets JSON. A binary body is what you want
    /// when you asked for it and the last thing you want when you were guessing.
    /// </summary>
    public int Priority => 50;

    public bool CanWrite(object? value) => value is null || JsonTypeInfoRegistry.TryGet(value.GetType(), out _);

    public async ValueTask WriteAsync(HttpContext context, object? value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // 0xc0 is MessagePack nil. A zero-length body would be a different statement.
        if (value is null)
        {
            await context.Response.WriteBytesAsync(new byte[] { 0xc0 }, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return;
        }

        var typeInfo = JsonTypeInfoRegistry.GetRequired(value.GetType());
        var json = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);

        await context.Response
            .WriteBytesAsync(MessagePackCodec.FromJson(json), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>
/// Reads a MessagePack request body into the type an endpoint asked for, using the same registered
/// <c>JsonTypeInfo</c> the JSON formatter uses.
/// <para>
/// Maps must be string-keyed. MessagePack's integer-key mode is more compact still, but the keys
/// carry no member names, so nothing here could bind them to a property.
/// </para>
/// </summary>
public sealed class MessagePackInputFormatter : IInputFormatter
{
    public string MediaType => MessagePackOutputFormatter.DefaultMediaType;

    public int Priority => 50;

    /// <summary>
    /// How much body to buffer. MessagePack has to be decoded whole before it can be transcoded, so
    /// unlike the JSON formatter this one cannot stream — and an unbounded buffer on a request path
    /// is a way to be knocked over by one client.
    /// </summary>
    public int MaxBodyBytes { get; set; } = 1024 * 1024;

    public bool CanRead(string mediaType, Type targetType)
    {
        ArgumentNullException.ThrowIfNull(targetType);

        return IsMessagePack(mediaType) && JsonTypeInfoRegistry.TryGet(targetType, out _);
    }

    internal static bool IsMessagePack(string mediaType)
        => mediaType.Equals(MessagePackOutputFormatter.DefaultMediaType, StringComparison.OrdinalIgnoreCase)
        || mediaType.Equals(MessagePackOutputFormatter.LegacyMediaType, StringComparison.OrdinalIgnoreCase)
        || mediaType.Equals("application/vnd.msgpack", StringComparison.OrdinalIgnoreCase);

    public async ValueTask<InputFormatterResult> ReadAsync(
        HttpContext context,
        Type targetType,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(targetType);

        var typeInfo = JsonTypeInfoRegistry.GetRequired(targetType);

        // A body over the limit throws with a 413, which the connection writes as-is — the right
        // answer, and a different one from "this MessagePack is broken".
        var body = await context.Request
            .ReadBodyAsBytesAsync(this.MaxBodyBytes, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var json = MessagePackCodec.ToJson(body);

            return InputFormatterResult.FromValue(JsonSerializer.Deserialize(json, typeInfo));
        }
        catch (MessagePackFormatException)
        {
            return InputFormatterResult.Malformed;
        }
        catch (JsonException)
        {
            // Valid MessagePack whose shape does not fit the target type — a string where a number
            // belongs, a missing required member. The client's mistake either way.
            return InputFormatterResult.Malformed;
        }
    }
}

/// <summary>Registering the MessagePack formatters.</summary>
public static class MessagePackFormatterExtensions
{
    /// <summary>
    /// Adds MessagePack in both directions.
    /// <code>
    /// builder.Services.AddContentNegotiation(o => o.AddMessagePack());
    /// </code>
    /// Responses need <c>Results.Negotiate(value)</c>, or
    /// <see cref="ContentNegotiationOptions.NegotiateByDefault"/> to make every
    /// <c>Results.Ok(value)</c> honour the <c>Accept</c> header. Request bodies need nothing —
    /// <c>Content-Type: application/msgpack</c> is enough from the moment this is registered.
    /// </summary>
    public static ContentNegotiationOptions AddMessagePack(this ContentNegotiationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.Add(new MessagePackOutputFormatter(), new MessagePackInputFormatter());

        // One input formatter covers every spelling because it is matched by string; the output side
        // matches an Accept range against a formatter's own media type, so the alias needs its own.
        options.Formatters.Add(new MessagePackOutputFormatter(MessagePackOutputFormatter.LegacyMediaType));

        return options;
    }

    /// <summary>Registers content negotiation with MessagePack added, for an app that wants nothing else.</summary>
    public static IServiceCollection AddMessagePackFormatters(
        this IServiceCollection services,
        Action<ContentNegotiationOptions>? configure = null
    ) => services.AddContentNegotiation(o =>
    {
        o.AddMessagePack();
        configure?.Invoke(o);
    });
}
