using System.Text.Json;

namespace Shiny.Net.HttpServer.Negotiation;

/// <summary>Why a request body could not become the type an endpoint asked for.</summary>
public enum BodyReadStatus
{
    /// <summary>The body was read and produced a value.</summary>
    Success,

    /// <summary>There was no body at all. A 400 — the endpoint needs one.</summary>
    NoBody,

    /// <summary>
    /// There was a body in the right format, but it could not become the target type: a syntax
    /// error, or a document that legitimately deserializes to null. Either way a 400.
    /// </summary>
    Malformed,

    /// <summary>
    /// The body's <c>Content-Type</c> is not one any registered formatter reads. A 415, which is
    /// the difference between "your JSON is broken" and "I do not speak protobuf".
    /// </summary>
    UnsupportedMediaType
}

/// <summary>The outcome of one formatter reading one body.</summary>
/// <param name="Status">Success, or the reason there is no value.</param>
/// <param name="Value">The deserialized body, boxed. Only meaningful when <paramref name="Status"/> is success.</param>
public readonly record struct InputFormatterResult(BodyReadStatus Status, object? Value)
{
    public bool Success => this.Status == BodyReadStatus.Success;

    /// <summary>A value was read. Null is reported as <see cref="BodyReadStatus.Malformed"/>.</summary>
    public static InputFormatterResult FromValue(object? value)
        => value is null ? Malformed : new InputFormatterResult(BodyReadStatus.Success, value);

    public static InputFormatterResult NoBody { get; } = new(BodyReadStatus.NoBody, null);

    public static InputFormatterResult Malformed { get; } = new(BodyReadStatus.Malformed, null);

    public static InputFormatterResult UnsupportedMediaType { get; } = new(BodyReadStatus.UnsupportedMediaType, null);
}

/// <summary>
/// Reads a request body in one representation.
/// <para>
/// The mirror of <see cref="IOutputFormatter"/>, and non-generic for the same reason: the formatter
/// is chosen at runtime from the <c>Content-Type</c> header, so it is handed the target type rather
/// than being generic over it. Each format closes the gap its own way — JSON through
/// <see cref="JsonTypeInfoRegistry"/>, protobuf through a registry of generated parsers — because
/// the one thing none of them may do is reflect over the type at runtime.
/// </para>
/// </summary>
public interface IInputFormatter
{
    /// <summary>The media type this reads, without parameters — <c>application/json</c>.</summary>
    string MediaType { get; }

    /// <summary>Preference when more than one formatter claims the same media type. Higher wins.</summary>
    int Priority { get; }

    /// <summary>
    /// Whether this formatter can read <paramref name="mediaType"/> into <paramref name="targetType"/>.
    /// <para>
    /// Both halves matter. A formatter that claims a media type it has no metadata for turns a clean
    /// 415 into a 500 at deserialization time.
    /// </para>
    /// </summary>
    bool CanRead(string mediaType, Type targetType);

    /// <summary>Reads the body. The caller has already checked that a body exists.</summary>
    ValueTask<InputFormatterResult> ReadAsync(HttpContext context, Type targetType, CancellationToken cancellationToken);
}

/// <summary>
/// Reads JSON using compile-time metadata from <see cref="JsonTypeInfoRegistry"/>.
/// <para>
/// Registered by default, and deliberately lenient about the header: a body with no
/// <c>Content-Type</c> at all is read as JSON. Plenty of clients omit it, this server has always
/// treated those bodies as JSON, and answering 415 to them now would break working callers to make
/// a point about a header.
/// </para>
/// </summary>
public sealed class JsonInputFormatter : IInputFormatter
{
    public string MediaType => "application/json";

    public int Priority => 100;

    public bool CanRead(string mediaType, Type targetType)
    {
        ArgumentNullException.ThrowIfNull(targetType);

        return IsJson(mediaType) && JsonTypeInfoRegistry.TryGet(targetType, out _);
    }

    /// <summary>
    /// Accepts <c>application/json</c>, <c>text/json</c>, any <c>+json</c> structured suffix
    /// (<c>application/merge-patch+json</c>), and an absent header.
    /// </summary>
    internal static bool IsJson(string mediaType)
        => mediaType.Length == 0
        || mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
        || mediaType.Equals("text/json", StringComparison.OrdinalIgnoreCase)
        || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase);

    public async ValueTask<InputFormatterResult> ReadAsync(
        HttpContext context,
        Type targetType,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(targetType);

        var typeInfo = JsonTypeInfoRegistry.GetRequired(targetType);

        try
        {
            var value = await JsonSerializer
                .DeserializeAsync(context.Request.Body, typeInfo, cancellationToken)
                .ConfigureAwait(false);

            return InputFormatterResult.FromValue(value);
        }
        catch (JsonException)
        {
            // Malformed JSON is the client's mistake, not the server's — it becomes a 400 rather
            // than bubbling up into the connection's 500 path.
            return InputFormatterResult.Malformed;
        }
    }
}

/// <summary>
/// An input formatter built from a delegate, for a representation that does not earn a type.
/// <code>
/// options.AddInputFormatter("text/csv", async (ctx, type, ct) =>
///     InputFormatterResult.FromValue(ParseCsv(type, await ReadAllAsync(ctx.Request.Body, ct))));
/// </code>
/// </summary>
public sealed class DelegateInputFormatter(
    string mediaType,
    Func<HttpContext, Type, CancellationToken, ValueTask<InputFormatterResult>> read,
    int priority = 50,
    Func<Type, bool>? canRead = null
) : IInputFormatter
{
    public string MediaType { get; } = mediaType ?? throw new ArgumentNullException(nameof(mediaType));

    public int Priority { get; } = priority;

    public bool CanRead(string mediaType, Type targetType)
        => this.MediaType.Equals(mediaType, StringComparison.OrdinalIgnoreCase)
        && (canRead?.Invoke(targetType) ?? true);

    public ValueTask<InputFormatterResult> ReadAsync(
        HttpContext context,
        Type targetType,
        CancellationToken cancellationToken
    ) => read(context, targetType, cancellationToken);
}
