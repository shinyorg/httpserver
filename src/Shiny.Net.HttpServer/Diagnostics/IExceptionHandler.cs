namespace Shiny.Net.HttpServer;

/// <summary>
/// Turns an unhandled exception into a response.
/// <para>
/// Several can be registered; they are tried in registration order and the first one to return true
/// owns the response. Returning false means "not mine" and passes the exception along, which is what
/// lets a handler for one domain — a validation failure, a missing tenant — sit alongside a
/// catch-all without either knowing about the other.
/// </para>
/// <code>
/// public sealed class NotFoundExceptionHandler : IExceptionHandler
/// {
///     public async ValueTask&lt;bool&gt; TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
///     {
///         if (exception is not EntityNotFoundException)
///             return false;
///
///         context.Response.StatusCode = StatusCodes.Status404NotFound;
///         await context.Response.WriteAsync("Not found", cancellationToken: ct);
///         return true;
///     }
/// }
///
/// builder.Services.AddExceptionHandler&lt;NotFoundExceptionHandler&gt;();
/// </code>
/// </summary>
public interface IExceptionHandler
{
    /// <summary>
    /// Handles the exception, or returns false to decline it.
    /// <para>
    /// The response has not started when this is called — if it had, there would be no status code
    /// left to change. Anything thrown from here is logged and treated as a decline, because a
    /// handler that fails must not replace the original problem with its own.
    /// </para>
    /// </summary>
    ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken);
}
