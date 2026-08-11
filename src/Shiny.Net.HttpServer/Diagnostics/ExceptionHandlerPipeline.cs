using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Shiny.Net.HttpServer;

/// <summary>
/// Runs the registered <see cref="IExceptionHandler"/>s in order and reports whether any of them
/// took the exception. Resolved once at startup and used on every failing request.
/// </summary>
public sealed class ExceptionHandlerPipeline(
    IEnumerable<IExceptionHandler> handlers,
    ILogger<ExceptionHandlerPipeline>? logger = null
)
{
    readonly IExceptionHandler[] handlers = [.. handlers];
    readonly ILogger logger = logger ?? NullLogger<ExceptionHandlerPipeline>.Instance;

    public bool IsEmpty => this.handlers.Length == 0;

    /// <summary>
    /// Offers the exception to each handler until one claims it. Returns false when nobody did, so
    /// the connection can fall back to its own 500.
    /// </summary>
    public async ValueTask<bool> TryHandleAsync(
        HttpContext context,
        Exception exception,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(exception);

        foreach (var handler in this.handlers)
        {
            if (context.Response.HasStarted)
            {
                // Someone wrote a body and then threw. There is no status code left to change, so
                // the honest outcome is a broken response rather than a plausible-looking one.
                this.logger.LogWarning(
                    "Cannot handle an exception on {Method} {Path}: the response had already started",
                    context.Request.Method,
                    context.Request.Path
                );
                return false;
            }

            try
            {
                if (await handler.TryHandleAsync(context, exception, cancellationToken).ConfigureAwait(false))
                    return true;
            }
            catch (Exception handlerFailure)
            {
                // A handler that throws is a decline, not a new problem to report. Replacing the
                // original exception here would hide the thing the caller actually needs to see.
                this.logger.LogError(
                    handlerFailure,
                    "Exception handler {Handler} threw while handling {Exception}; declining",
                    handler.GetType().Name,
                    exception.GetType().Name
                );
            }
        }

        return false;
    }
}

/// <summary>Registering exception handlers.</summary>
public static class ExceptionHandlerServiceCollectionExtensions
{
    /// <summary>
    /// Adds a handler to the chain. Call it several times to build a chain — order of registration
    /// is the order they are tried, so put the specific ones before the catch-all.
    /// </summary>
    public static IServiceCollection AddExceptionHandler<
        [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors
        )] THandler
    >(this IServiceCollection services) where THandler : class, IExceptionHandler
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IExceptionHandler, THandler>();
        return services.AddExceptionHandlerPipeline();
    }

    /// <summary>Adds a handler built by a factory, avoiding reflective activation entirely.</summary>
    public static IServiceCollection AddExceptionHandler<THandler>(
        this IServiceCollection services,
        Func<IServiceProvider, THandler> factory
    ) where THandler : class, IExceptionHandler
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(factory);

        services.AddSingleton<IExceptionHandler>(factory);
        return services.AddExceptionHandlerPipeline();
    }

    /// <summary>
    /// Adds an inline handler, for the cases that do not earn a type.
    /// <code>
    /// services.AddExceptionHandler((ctx, ex, ct) =>
    /// {
    ///     if (ex is not TimeoutException) return new ValueTask&lt;bool&gt;(false);
    ///     ctx.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
    ///     return ...;
    /// });
    /// </code>
    /// </summary>
    public static IServiceCollection AddExceptionHandler(
        this IServiceCollection services,
        Func<HttpContext, Exception, CancellationToken, ValueTask<bool>> handler
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(handler);

        services.AddSingleton<IExceptionHandler>(new DelegateExceptionHandler(handler));
        return services.AddExceptionHandlerPipeline();
    }

    /// <summary>
    /// Registers the pipeline itself, built by an explicit factory so nothing is activated by
    /// reflection and a missing logger is simply absent rather than a resolution failure.
    /// </summary>
    static IServiceCollection AddExceptionHandlerPipeline(this IServiceCollection services)
    {
        services.TryAddSingleton(sp => new ExceptionHandlerPipeline(
            sp.GetServices<IExceptionHandler>(),
            sp.GetService<ILogger<ExceptionHandlerPipeline>>()
        ));

        return services;
    }
}

sealed class DelegateExceptionHandler(
    Func<HttpContext, Exception, CancellationToken, ValueTask<bool>> handler
) : IExceptionHandler
{
    public ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
        => handler(context, exception, cancellationToken);
}
