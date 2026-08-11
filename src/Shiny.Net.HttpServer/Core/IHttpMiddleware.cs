namespace Shiny.Net.HttpServer;

/// <summary>
/// Middleware as a class rather than a lambda.
/// <para>
/// The delegate form (<see cref="HttpServer.Use(Func{HttpContext, RequestDelegate, ValueTask})"/>)
/// is fine for a stopwatch or a header check. Once middleware needs dependencies, its own tests, or
/// more than a screenful of code, it wants to be a type — which is what this is. Same contract
/// either way: do work, call <paramref name="next"/> or don't, do more work.
/// </para>
/// <code>
/// public sealed class ApiKeyMiddleware(IKeyStore keys) : IHttpMiddleware
/// {
///     public async ValueTask InvokeAsync(HttpContext context, RequestDelegate next)
///     {
///         if (!keys.IsValid(context.Request.Headers.GetFirst("X-Api-Key")))
///         {
///             context.Response.StatusCode = StatusCodes.Status401Unauthorized;
///             return;
///         }
///         await next(context);
///     }
/// }
///
/// builder.Services.AddSingleton&lt;ApiKeyMiddleware&gt;();
/// app.Use&lt;ApiKeyMiddleware&gt;();
/// </code>
/// </summary>
public interface IHttpMiddleware
{
    /// <summary>
    /// Handles one request. Not calling <paramref name="next"/> short-circuits everything below,
    /// including routing.
    /// </summary>
    ValueTask InvokeAsync(HttpContext context, RequestDelegate next);
}
