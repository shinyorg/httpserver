using System.Buffers;
using System.Collections;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace Shiny.Net.HttpServer;

/// <summary>
/// Serializes a <see cref="ProblemDetails"/> to <c>application/problem+json</c>.
/// <para>
/// Hand-written against <see cref="Utf8JsonWriter"/> rather than <c>JsonSerializer</c>, because the
/// extension bag is <c>object?</c> and reflecting over it is exactly what this server does not do.
/// The cost is a fixed set of supported value shapes; the benefit is that an error body cannot be
/// the thing that breaks an AOT build.
/// </para>
/// </summary>
public static class ProblemDetailsWriter
{
    public const string ContentType = "application/problem+json; charset=utf-8";

    static readonly JsonWriterOptions WriterOptions = new()
    {
        // Without this every non-ASCII character in a message comes out as \uXXXX, which is legal
        // and unreadable.
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        Indented = false,
        SkipValidation = true
    };

    /// <summary>Writes the problem as UTF-8 JSON.</summary>
    public static void Write(IBufferWriter<byte> destination, ProblemDetails problem)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(problem);

        using var writer = new Utf8JsonWriter(destination, WriterOptions);

        writer.WriteStartObject();

        WriteIfPresent(writer, "type", problem.Type);
        WriteIfPresent(writer, "title", problem.Title);

        if (problem.Status is { } status)
            writer.WriteNumber("status", status);

        WriteIfPresent(writer, "detail", problem.Detail);
        WriteIfPresent(writer, "instance", problem.Instance);

        if (problem is ValidationProblemDetails { Errors.Count: > 0 } validation)
        {
            writer.WritePropertyName("errors");
            writer.WriteStartObject();

            foreach (var (field, messages) in validation.Errors)
            {
                writer.WritePropertyName(field);
                writer.WriteStartArray();

                foreach (var message in messages)
                    writer.WriteStringValue(message);

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        foreach (var (name, value) in problem.Extensions)
        {
            // A member that collides with a standard one would produce a duplicate key, and the
            // standard meaning has to win.
            if (IsReserved(name, problem))
                continue;

            writer.WritePropertyName(name);
            WriteValue(writer, value);
        }

        writer.WriteEndObject();
        writer.Flush();
    }

    /// <summary>Writes the problem as the response body, setting status code and content type.</summary>
    public static async ValueTask WriteResponseAsync(
        HttpContext context,
        ProblemDetails problem,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(problem);

        var response = context.Response;

        if (!response.HasStarted)
        {
            response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
            response.ContentType = ContentType;
        }

        var buffer = new ArrayBufferWriter<byte>(512);
        Write(buffer, problem);

        await response.WriteBytesAsync(buffer.WrittenMemory, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    static bool IsReserved(string name, ProblemDetails problem) =>
        name is "type" or "title" or "status" or "detail" or "instance"
        || (name == "errors" && problem is ValidationProblemDetails);

    static void WriteIfPresent(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is not null)
            writer.WriteString(name, value);
    }

    /// <summary>
    /// Writes one extension value.
    /// <para>
    /// The supported set is deliberately closed. Falling back to <c>ToString</c> for the rest keeps
    /// an error body from throwing while it is trying to report an error — the one place where a
    /// second failure is least useful.
    /// </para>
    /// </summary>
    static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;

            case string text:
                writer.WriteStringValue(text);
                break;

            case bool flag:
                writer.WriteBooleanValue(flag);
                break;

            case int number:
                writer.WriteNumberValue(number);
                break;

            case long number:
                writer.WriteNumberValue(number);
                break;

            case short number:
                writer.WriteNumberValue(number);
                break;

            case byte number:
                writer.WriteNumberValue(number);
                break;

            case uint number:
                writer.WriteNumberValue(number);
                break;

            case ulong number:
                writer.WriteNumberValue(number);
                break;

            case ushort number:
                writer.WriteNumberValue(number);
                break;

            case sbyte number:
                writer.WriteNumberValue(number);
                break;

            case double number:
                writer.WriteNumberValue(number);
                break;

            case float number:
                writer.WriteNumberValue(number);
                break;

            case decimal number:
                writer.WriteNumberValue(number);
                break;

            case DateTime timestamp:
                writer.WriteStringValue(timestamp);
                break;

            case DateTimeOffset timestamp:
                writer.WriteStringValue(timestamp);
                break;

            case Guid id:
                writer.WriteStringValue(id);
                break;

            case TimeSpan duration:
                writer.WriteStringValue(duration.ToString("c", CultureInfo.InvariantCulture));
                break;

            case Uri uri:
                writer.WriteStringValue(uri.ToString());
                break;

            case JsonElement element:
                element.WriteTo(writer);
                break;

            case IDictionary<string, object?> nested:
                writer.WriteStartObject();

                foreach (var (name, item) in nested)
                {
                    writer.WritePropertyName(name);
                    WriteValue(writer, item);
                }

                writer.WriteEndObject();
                break;

            // Checked after the string case on purpose: a string is an IEnumerable of characters,
            // and writing one as an array of letters would be a memorable bug.
            case IEnumerable sequence:
                writer.WriteStartArray();

                foreach (var item in sequence)
                    WriteValue(writer, item);

                writer.WriteEndArray();
                break;

            default:
                writer.WriteStringValue(value.ToString());
                break;
        }
    }
}

/// <summary>An <c>application/problem+json</c> response.</summary>
public sealed class ProblemResult(ProblemDetails problem) : IActionResult
{
    public ProblemDetails ProblemDetails { get; } = problem ?? throw new ArgumentNullException(nameof(problem));

    public ValueTask ExecuteAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Applied here rather than at construction, so the instance can default to the request path
        // — which the factory has no way of knowing.
        ProblemDetailsDefaults.ApplyDefaults(
            this.ProblemDetails,
            context,
            StatusCodes.Status500InternalServerError
        );

        // Resolved rather than injected: a result is constructed by handler code that has no
        // container, and an app without AddProblemDetails simply has nothing to customize.
        if (context.RequestServices.GetService(typeof(ProblemDetailsOptions)) is ProblemDetailsOptions options)
            options.Customize?.Invoke(new ProblemDetailsContext(context, this.ProblemDetails, Exception: null));

        return ProblemDetailsWriter.WriteResponseAsync(context, this.ProblemDetails, context.RequestAborted);
    }
}
