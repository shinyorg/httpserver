using System.Text.Json;

namespace Shiny.Net.HttpServer;

/// <summary>What is being described, for a <see cref="ProblemDetailsOptions.Customize"/> callback.</summary>
/// <param name="HttpContext">The request that failed.</param>
/// <param name="ProblemDetails">The body about to be written. Mutate it in place.</param>
/// <param name="Exception">The exception behind it, or null when the problem was returned deliberately.</param>
public readonly record struct ProblemDetailsContext(
    HttpContext HttpContext,
    ProblemDetails ProblemDetails,
    Exception? Exception
);

/// <summary>How unhandled exceptions and error status codes turn into problem responses.</summary>
public sealed class ProblemDetailsOptions
{
    readonly Dictionary<Type, int> exceptionStatusCodes = new();
    readonly HashSet<Type> clientFacingExceptions = [];

    /// <summary>
    /// Includes the exception type, message and stack trace in the response.
    /// <para>
    /// Off by default, and it must stay off in anything reachable by someone you do not trust — an
    /// exception message routinely carries a file path, a connection string, or the shape of a
    /// query. Turn it on from a development-only branch of your configuration.
    /// </para>
    /// </summary>
    public bool IncludeExceptionDetails { get; set; }

    /// <summary>
    /// Adds the current <see cref="System.Diagnostics.Activity"/> id as a <c>traceId</c> extension
    /// when one is running, so a user-visible error can be matched to a log line.
    /// </summary>
    public bool IncludeTraceId { get; set; } = true;

    /// <summary>
    /// Also converts bodiless 4xx/5xx responses — a bare 404 from routing, a 401 from
    /// authentication — into problem bodies.
    /// <para>
    /// On by default once <c>AddProblemDetails</c> is called: a client that gets JSON for one error
    /// and an empty body for another has to handle both, which defeats the point of a standard
    /// error shape.
    /// </para>
    /// </summary>
    public bool WriteForStatusCodes { get; set; } = true;

    /// <summary>
    /// Last chance to change a problem before it is written — add a support id, strip a detail,
    /// rewrite a type URI to your own documentation.
    /// </summary>
    public Action<ProblemDetailsContext>? Customize { get; set; }

    /// <summary>
    /// Maps an exception type to the status code it should produce. Exact type first, then base
    /// types, so mapping a base covers everything under it. The exception's message is not sent to the
    /// client — see <see cref="MapException{TException}(int, bool)"/>.
    /// </summary>
    public ProblemDetailsOptions MapException<TException>(int statusCode) where TException : Exception
        => this.MapException<TException>(statusCode, includeMessage: false);

    /// <summary>
    /// Maps an exception type to a status code and says whether its message is written to the
    /// problem's <c>detail</c> on a 4xx.
    /// <para>
    /// Pass <c>true</c> only for exceptions written to be read by the caller — a validation exception of
    /// your own whose message says what to fix. A framework exception's message routinely carries a file
    /// path or the shape of a query: <see cref="UnauthorizedAccessException"/>, mapped to 403 by default,
    /// names the absolute path it was refused.
    /// </para>
    /// </summary>
    public ProblemDetailsOptions MapException<TException>(int statusCode, bool includeMessage) where TException : Exception
    {
        this.exceptionStatusCodes[typeof(TException)] = statusCode;

        if (includeMessage)
            this.clientFacingExceptions.Add(typeof(TException));
        else
            this.clientFacingExceptions.Remove(typeof(TException));

        return this;
    }

    /// <summary>
    /// Whether an exception's message may be written to a 4xx problem's <c>detail</c>: always with
    /// <see cref="IncludeExceptionDetails"/>; otherwise only for exceptions written for the client —
    /// <see cref="BadHttpRequestException"/>, <see cref="JsonPatch.JsonPatchException"/> and types mapped
    /// with <c>includeMessage: true</c>.
    /// </summary>
    public bool IncludesMessage(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (this.IncludeExceptionDetails || exception is BadHttpRequestException or JsonPatch.JsonPatchException)
            return true;

        for (var type = exception.GetType(); type is not null; type = type.BaseType)
        {
            if (this.exceptionStatusCodes.ContainsKey(type))
                return this.clientFacingExceptions.Contains(type);
        }

        return false;
    }

    /// <summary>
    /// The status code for an exception: an explicit mapping if there is one, otherwise the
    /// built-in defaults, otherwise 500.
    /// </summary>
    public int GetStatusCode(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        for (var type = exception.GetType(); type is not null; type = type.BaseType)
        {
            if (this.exceptionStatusCodes.TryGetValue(type, out var mapped))
                return mapped;
        }

        return DefaultStatusCode(exception);
    }

    /// <summary>
    /// The built-in exception mapping.
    /// <para>
    /// Conservative on purpose. Only exceptions that unambiguously describe a *client* mistake get
    /// a 4xx; everything else is a 500, because reporting a server bug as a client error sends the
    /// caller off to fix something that is not theirs.
    /// </para>
    /// </summary>
    static int DefaultStatusCode(Exception exception) => exception switch
    {
        BadHttpRequestException bad => bad.StatusCode,
        JsonPatch.JsonPatchException patch => patch.StatusCode,
        ArgumentException or FormatException or JsonException => StatusCodes.Status400BadRequest,
        UnauthorizedAccessException => StatusCodes.Status403Forbidden,
        KeyNotFoundException => StatusCodes.Status404NotFound,
        NotImplementedException or NotSupportedException => StatusCodes.Status501NotImplemented,
        TimeoutException => StatusCodes.Status504GatewayTimeout,
        _ => StatusCodes.Status500InternalServerError
    };
}
