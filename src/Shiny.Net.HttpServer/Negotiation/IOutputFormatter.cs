using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Shiny.Net.HttpServer.Negotiation;

/// <summary>
/// Writes a value in one representation.
/// <para>
/// Non-generic on purpose: a formatter is selected at runtime from the <c>Accept</c> header, so the
/// value's static type is not available at the selection point. The JSON formatter closes that gap
/// through <see cref="JsonTypeInfoRegistry"/> rather than reflection.
/// </para>
/// </summary>
public interface IOutputFormatter
{
    /// <summary>The media type this produces, without parameters — <c>application/json</c>.</summary>
    string MediaType { get; }

    /// <summary>
    /// Preference among formatters the client accepts equally. Higher wins, which is how a browser
    /// sending <c>*/*</c> gets JSON rather than whatever happens to be registered first.
    /// </summary>
    int Priority { get; }

    /// <summary>
    /// The <c>charset</c> to advertise alongside the media type, or null for a format where the
    /// question does not apply.
    /// <para>
    /// UTF-8 by default, which is right for every textual format here. A binary format returns null:
    /// <c>application/msgpack; charset=utf-8</c> describes bytes that are not text in any encoding,
    /// and a client that believed it would try to decode them as such.
    /// </para>
    /// </summary>
    string? Charset => "utf-8";

    /// <summary>Whether this formatter can write the value at all.</summary>
    bool CanWrite(object? value);

    /// <summary>Writes the body. The content type is already set.</summary>
    ValueTask WriteAsync(HttpContext context, object? value, CancellationToken cancellationToken);
}

/// <summary>
/// Writes JSON using compile-time metadata from <see cref="JsonTypeInfoRegistry"/>.
/// <para>
/// The default, and the one that keeps negotiation honest about this server's constraints: a type
/// gets a representation because its metadata was registered, not because something reflected over
/// it at runtime.
/// </para>
/// </summary>
public sealed class JsonOutputFormatter : IOutputFormatter
{
    public string MediaType => "application/json";

    public int Priority => 100;

    public bool CanWrite(object? value) => value is null || JsonTypeInfoRegistry.TryGet(value.GetType(), out _);

    public async ValueTask WriteAsync(HttpContext context, object? value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (value is null)
        {
            await context.Response.WriteBytesAsync("null"u8.ToArray(), cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return;
        }

        var typeInfo = JsonTypeInfoRegistry.GetRequired(value.GetType());
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);

        await context.Response.WriteBytesAsync(bytes, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Writes <c>text/plain</c> from a value's string form.
/// <para>
/// Registered after JSON, so it only wins when the client actually asked for text — which is the
/// case worth having: a <c>curl</c> or a shell script reading a status endpoint wants a line, not a
/// quoted JSON string.
/// </para>
/// </summary>
public sealed class PlainTextOutputFormatter : IOutputFormatter
{
    public string MediaType => "text/plain";

    public int Priority => 10;

    /// <summary>
    /// Only for values with a meaningful string form. An object whose <c>ToString</c> is its type
    /// name would produce a response that looks successful and says nothing.
    /// </summary>
    public bool CanWrite(object? value) => value is null
        or string
        or bool
        or char
        or Guid
        or Uri
        or DateTime
        or DateTimeOffset
        or TimeSpan
        or DateOnly
        or TimeOnly
        || (value is not null && value.GetType().IsPrimitive)
        || value is decimal;

    public ValueTask WriteAsync(HttpContext context, object? value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var text = value switch
        {
            null => string.Empty,
            string s => s,
            IFormattable formattable => formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };

        return context.Response.WriteBytesAsync(
            Encoding.UTF8.GetBytes(text),
            cancellationToken: cancellationToken
        );
    }
}

/// <summary>
/// A formatter built from a delegate, for a representation that does not earn a type.
/// <code>
/// options.AddFormatter("text/csv", async (ctx, value, ct) =>
///     await ctx.Response.WriteTextAsync(ToCsv(value), "text/csv", ct));
/// </code>
/// </summary>
public sealed class DelegateOutputFormatter(
    string mediaType,
    Func<HttpContext, object?, CancellationToken, ValueTask> write,
    int priority = 50,
    Func<object?, bool>? canWrite = null
) : IOutputFormatter
{
    public string MediaType { get; } = mediaType ?? throw new ArgumentNullException(nameof(mediaType));

    public int Priority { get; } = priority;

    public bool CanWrite(object? value) => canWrite?.Invoke(value) ?? true;

    public ValueTask WriteAsync(HttpContext context, object? value, CancellationToken cancellationToken)
        => write(context, value, cancellationToken);
}
