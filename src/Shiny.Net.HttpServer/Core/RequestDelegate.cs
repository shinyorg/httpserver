namespace Shiny.Net.HttpServer;

/// <summary>
/// The unit of request handling. Returns <see cref="ValueTask"/> because the majority of handlers
/// complete synchronously (a cache hit, a small JSON write) and should not allocate a Task to say so.
/// </summary>
public delegate ValueTask RequestDelegate(HttpContext context);

/// <summary>Wraps the next delegate in a pipeline. Same shape as ASP.NET Core middleware.</summary>
public delegate RequestDelegate MiddlewareDelegate(RequestDelegate next);
