namespace Shiny.Net.HttpServer;

/// <summary>
/// A machine-readable error body, as RFC 9457 defines it.
/// <para>
/// The point is that a client can tell two failures apart without parsing prose. A bare 400 with
/// "Bad Request" in the body tells a caller nothing it did not already know from the status line;
/// a <c>type</c> it can branch on, and a <c>detail</c> it can show a user, do.
/// </para>
/// <code>
/// return Results.Problem(
///     StatusCodes.Status409Conflict,
///     detail: "The device is already paired to another account.",
///     type: "https://example.com/problems/already-paired"
/// );
/// </code>
/// </summary>
public class ProblemDetails
{
    /// <summary>
    /// URI identifying the problem *type*. Clients branch on this, so it must stay stable — the
    /// status code is not specific enough and the wording of <see cref="Detail"/> is not stable.
    /// <para>
    /// Left null, a URI describing the status code is used. It need not resolve to anything.
    /// </para>
    /// </summary>
    public string? Type { get; set; }

    /// <summary>Short, human-readable summary of the problem type. Should not change per occurrence.</summary>
    public string? Title { get; set; }

    public int? Status { get; set; }

    /// <summary>Explanation specific to this occurrence — the part that varies and is safe to show.</summary>
    public string? Detail { get; set; }

    /// <summary>URI identifying this specific occurrence. Defaults to the request path.</summary>
    public string? Instance { get; set; }

    /// <summary>
    /// Additional members, written alongside the standard ones.
    /// <para>
    /// Values are serialized without reflection, so they must be primitives (string, bool, the
    /// numeric types, <see cref="DateTime"/>, <see cref="DateTimeOffset"/>, <see cref="Guid"/>,
    /// <see cref="Uri"/>, <see cref="TimeSpan"/>), a <see cref="System.Text.Json.JsonElement"/>,
    /// a sequence of those, or a nested dictionary. Anything else is written as its string form.
    /// </para>
    /// </summary>
    public IDictionary<string, object?> Extensions { get; } =
        new Dictionary<string, object?>(StringComparer.Ordinal);
}

/// <summary>
/// A problem carrying per-field errors — what a failed validation should return.
/// <para>
/// Serialized with an <c>errors</c> member keyed by field name, which is the shape ASP.NET Core
/// produces and therefore the one existing clients already parse.
/// </para>
/// </summary>
public sealed class ValidationProblemDetails : ProblemDetails
{
    public ValidationProblemDetails()
    {
    }

    public ValidationProblemDetails(IDictionary<string, string[]> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);

        foreach (var (field, messages) in errors)
            this.Errors[field] = messages;
    }

    /// <summary>Field name to the messages for it. An empty key carries errors about the whole request.</summary>
    public IDictionary<string, string[]> Errors { get; } =
        new Dictionary<string, string[]>(StringComparer.Ordinal);
}

/// <summary>Defaults for the parts of a problem the caller did not fill in.</summary>
public static class ProblemDetailsDefaults
{
    /// <summary>
    /// The type URI for a status code.
    /// <para>
    /// These point at the RFC that defines the status, which is what ASP.NET Core emits — so a
    /// client that already recognises those URIs keeps working against this server.
    /// </para>
    /// </summary>
    public static string GetTypeUri(int statusCode) => statusCode switch
    {
        400 => "https://tools.ietf.org/html/rfc9110#section-15.5.1",
        401 => "https://tools.ietf.org/html/rfc9110#section-15.5.2",
        403 => "https://tools.ietf.org/html/rfc9110#section-15.5.4",
        404 => "https://tools.ietf.org/html/rfc9110#section-15.5.5",
        405 => "https://tools.ietf.org/html/rfc9110#section-15.5.6",
        406 => "https://tools.ietf.org/html/rfc9110#section-15.5.7",
        408 => "https://tools.ietf.org/html/rfc9110#section-15.5.9",
        409 => "https://tools.ietf.org/html/rfc9110#section-15.5.10",
        410 => "https://tools.ietf.org/html/rfc9110#section-15.5.11",
        411 => "https://tools.ietf.org/html/rfc9110#section-15.5.12",
        412 => "https://tools.ietf.org/html/rfc9110#section-15.5.13",
        413 => "https://tools.ietf.org/html/rfc9110#section-15.5.14",
        414 => "https://tools.ietf.org/html/rfc9110#section-15.5.15",
        415 => "https://tools.ietf.org/html/rfc9110#section-15.5.16",
        416 => "https://tools.ietf.org/html/rfc9110#section-15.5.17",
        421 => "https://tools.ietf.org/html/rfc9110#section-15.5.20",
        422 => "https://tools.ietf.org/html/rfc9110#section-15.5.21",
        426 => "https://tools.ietf.org/html/rfc9110#section-15.5.22",
        429 => "https://tools.ietf.org/html/rfc6585#section-4",
        500 => "https://tools.ietf.org/html/rfc9110#section-15.6.1",
        501 => "https://tools.ietf.org/html/rfc9110#section-15.6.2",
        502 => "https://tools.ietf.org/html/rfc9110#section-15.6.3",
        503 => "https://tools.ietf.org/html/rfc9110#section-15.6.4",
        504 => "https://tools.ietf.org/html/rfc9110#section-15.6.5",
        505 => "https://tools.ietf.org/html/rfc9110#section-15.6.6",

        // RFC 9457: an absent type means "about:blank", i.e. the status code and nothing more.
        _ => "about:blank"
    };

    /// <summary>The title for a status code — its reason phrase, which is exactly what a title is.</summary>
    public static string GetTitle(int statusCode) => StatusCodes.GetReasonPhrase(statusCode);

    /// <summary>Fills in type, title, status and instance where the caller left them empty.</summary>
    public static void ApplyDefaults(ProblemDetails problem, HttpContext? context, int fallbackStatusCode)
    {
        ArgumentNullException.ThrowIfNull(problem);

        problem.Status ??= fallbackStatusCode;
        problem.Type ??= GetTypeUri(problem.Status.Value);
        problem.Title ??= problem is ValidationProblemDetails
            ? "One or more validation errors occurred."
            : GetTitle(problem.Status.Value);

        if (problem.Instance is null && context is not null)
            problem.Instance = context.Request.Path;
    }
}
