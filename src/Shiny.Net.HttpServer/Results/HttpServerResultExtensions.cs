namespace Shiny.Net.HttpServer;

/// <summary>
/// Route mapping for handlers that return an <see cref="IResult"/> rather than writing the
/// response themselves.
/// <code>
/// app.MapGet("/users/{id}", ctx => users.TryGet(ctx) is { } u ? Results.Ok(u, Json.User) : Results.NotFound());
/// </code>
/// Kept as extensions so the core <see cref="HttpServer"/> surface stays small; overload resolution
/// picks these only when the lambda actually returns a result.
/// </summary>
public static class HttpServerResultExtensions
{
    public static HttpServer Map(this HttpServer server, string method, string pattern, Func<HttpContext, IResult> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return server.Map(method, pattern, ctx => handler(ctx).ExecuteAsync(ctx));
    }

    public static HttpServer Map(
        this HttpServer server,
        string method,
        string pattern,
        Func<HttpContext, ValueTask<IResult>> handler
    )
    {
        ArgumentNullException.ThrowIfNull(handler);
        return server.Map(method, pattern, async ctx =>
        {
            var result = await handler(ctx).ConfigureAwait(false);
            await result.ExecuteAsync(ctx).ConfigureAwait(false);
        });
    }

    public static HttpServer MapGet(this HttpServer server, string pattern, Func<HttpContext, IResult> handler)
        => server.Map(HttpMethods.Get, pattern, handler);

    public static HttpServer MapGet(this HttpServer server, string pattern, Func<HttpContext, ValueTask<IResult>> handler)
        => server.Map(HttpMethods.Get, pattern, handler);

    public static HttpServer MapPost(this HttpServer server, string pattern, Func<HttpContext, IResult> handler)
        => server.Map(HttpMethods.Post, pattern, handler);

    public static HttpServer MapPost(this HttpServer server, string pattern, Func<HttpContext, ValueTask<IResult>> handler)
        => server.Map(HttpMethods.Post, pattern, handler);

    public static HttpServer MapPut(this HttpServer server, string pattern, Func<HttpContext, IResult> handler)
        => server.Map(HttpMethods.Put, pattern, handler);

    public static HttpServer MapPut(this HttpServer server, string pattern, Func<HttpContext, ValueTask<IResult>> handler)
        => server.Map(HttpMethods.Put, pattern, handler);

    public static HttpServer MapDelete(this HttpServer server, string pattern, Func<HttpContext, IResult> handler)
        => server.Map(HttpMethods.Delete, pattern, handler);

    public static HttpServer MapDelete(this HttpServer server, string pattern, Func<HttpContext, ValueTask<IResult>> handler)
        => server.Map(HttpMethods.Delete, pattern, handler);

    public static HttpServer MapPatch(this HttpServer server, string pattern, Func<HttpContext, IResult> handler)
        => server.Map(HttpMethods.Patch, pattern, handler);

    public static HttpServer MapPatch(this HttpServer server, string pattern, Func<HttpContext, ValueTask<IResult>> handler)
        => server.Map(HttpMethods.Patch, pattern, handler);

    /// <summary>The <see cref="IResult"/> equivalent of <see cref="HttpServer.OnRequest(RequestDelegate)"/>.</summary>
    public static HttpServer OnRequest(this HttpServer server, Func<HttpContext, IResult> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return server.OnRequest(ctx => handler(ctx).ExecuteAsync(ctx));
    }
}
