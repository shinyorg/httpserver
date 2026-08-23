using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Shiny.Net.HttpServer;

/// <summary>
/// The catch-all: turns any unhandled exception into an RFC 9457 problem response.
/// <para>
/// Registered last, so a handler for a specific failure still gets first refusal. What it adds over
/// the connection's built-in 500 is a body a client can parse and a status code that reflects what
/// actually went wrong — a malformed request body is the caller's problem, and saying so with a 400
/// saves them looking for a server fault that is not there.
/// </para>
/// </summary>
public sealed class ProblemDetailsExceptionHandler(
    ProblemDetailsOptions options,
    ILogger<ProblemDetailsExceptionHandler>? logger = null
) : IExceptionHandler
{
    readonly ILogger logger = logger ?? NullLogger<ProblemDetailsExceptionHandler>.Instance;

    public async ValueTask<bool> TryHandleAsync(
        HttpContext context,
        Exception exception,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(exception);

        // The caller has gone. Writing a body to a socket nobody is reading achieves nothing, and
        // claiming the exception would hide a genuine cancellation bug from the logs.
        if (exception is OperationCanceledException && context.RequestAborted.IsCancellationRequested)
            return false;

        var status = options.GetStatusCode(exception);

        var problem = new ProblemDetails
        {
            Status = status,
            Detail = status < 500 ? exception.Message : null
        };

        ProblemDetailsDefaults.ApplyDefaults(problem, context, status);

        if (options.IncludeTraceId && Activity.Current?.Id is { Length: > 0 } traceId)
            problem.Extensions["traceId"] = traceId;

        if (options.IncludeExceptionDetails)
        {
            problem.Extensions["exception"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = exception.GetType().FullName,
                ["message"] = exception.Message,
                ["stackTrace"] = exception.StackTrace,
                ["innerException"] = exception.InnerException?.ToString()
            };
        }

        options.Customize?.Invoke(new ProblemDetailsContext(context, problem, exception));

        // Logged here rather than left to the connection: once this returns true the exception is
        // considered dealt with, and a 500 nobody recorded is a 500 nobody can fix.
        if (status >= 500)
            this.logger.LogError(exception, "Unhandled exception on {Method} {Path}", context.Request.Method, context.Request.Path);
        else
            this.logger.LogDebug(exception, "Request failed with {Status} on {Method} {Path}", status, context.Request.Method, context.Request.Path);

        await ProblemDetailsWriter.WriteResponseAsync(context, problem, cancellationToken).ConfigureAwait(false);

        return true;
    }
}

/// <summary>
/// Middleware that gives bodiless error responses a problem body.
/// <para>
/// Routing's 404 and authentication's 401 never throw, so the exception handler never sees them —
/// and a client that gets JSON for one failure and an empty body for the next has to handle both.
/// </para>
/// </summary>
public sealed class ProblemDetailsStatusCodeMiddleware(ProblemDetailsOptions options) : IHttpMiddleware
{
    public async ValueTask InvokeAsync(HttpContext context, RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        await next(context).ConfigureAwait(false);

        if (!options.WriteForStatusCodes)
            return;

        var response = context.Response;

        // Only an error that produced nothing. A handler that wrote its own body — even an empty
        // 204, even a 404 with a message — has said what it meant, and second-guessing it would
        // overwrite a deliberate response.
        if (response.HasStarted || response.StatusCode < 400 || response.ContentLength is > 0 || response.ContentType is not null)
            return;

        var problem = new ProblemDetails { Status = response.StatusCode };
        ProblemDetailsDefaults.ApplyDefaults(problem, context, response.StatusCode);

        if (options.IncludeTraceId && Activity.Current?.Id is { Length: > 0 } traceId)
            problem.Extensions["traceId"] = traceId;

        options.Customize?.Invoke(new ProblemDetailsContext(context, problem, Exception: null));

        await ProblemDetailsWriter.WriteResponseAsync(context, problem, context.RequestAborted).ConfigureAwait(false);
    }
}

/// <summary>Registering problem details.</summary>
public static class ProblemDetailsExtensions
{
    /// <summary>
    /// Registers the problem-details options and the catch-all exception handler.
    /// <code>
    /// builder.Services.AddProblemDetails(o =>
    /// {
    ///     o.IncludeExceptionDetails = builder.Environment.IsDevelopment();
    ///     o.MapException&lt;EntityNotFoundException&gt;(StatusCodes.Status404NotFound);
    /// });
    ///
    /// app.UseProblemDetails();   // for bodiless 4xx/5xx; the exception handler needs no wiring
    /// </code>
    /// <para>
    /// Call it after any specific <c>AddExceptionHandler</c> registrations — handlers are tried in
    /// registration order, and a catch-all registered first would never let a specific one run.
    /// </para>
    /// </summary>
    public static ShinyHttpServerBuilder AddProblemDetails(
        this ShinyHttpServerBuilder builder,
        Action<ProblemDetailsOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton(_ =>
        {
            var options = new ProblemDetailsOptions();
            configure?.Invoke(options);

            return options;
        });

        return builder.AddExceptionHandler(sp => new ProblemDetailsExceptionHandler(
            sp.GetRequiredService<ProblemDetailsOptions>(),
            sp.GetService<ILogger<ProblemDetailsExceptionHandler>>()
        ));
    }

    /// <summary>
    /// Adds the middleware that gives bodiless 4xx/5xx responses a problem body. Put it early —
    /// it has to wrap whatever produced the status code.
    /// </summary>
    public static HttpServer UseProblemDetails(this HttpServer server)
    {
        ArgumentNullException.ThrowIfNull(server);

        var options = server.Services?.GetService<ProblemDetailsOptions>()
            ?? throw new InvalidOperationException(
                $"Call {nameof(AddProblemDetails)} on the service collection before {nameof(UseProblemDetails)}."
            );

        return server.Use(new ProblemDetailsStatusCodeMiddleware(options));
    }

    /// <summary>The same, for a server built without a container.</summary>
    public static HttpServer UseProblemDetails(this HttpServer server, ProblemDetailsOptions options)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(options);

        return server.Use(new ProblemDetailsStatusCodeMiddleware(options));
    }
}
