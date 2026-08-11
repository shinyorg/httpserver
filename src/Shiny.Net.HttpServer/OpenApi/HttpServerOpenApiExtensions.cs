namespace Shiny.Net.HttpServer.OpenApi;

/// <summary>Serving and describing the OpenAPI document.</summary>
public static class HttpServerOpenApiExtensions
{
    /// <summary>
    /// Serves the OpenAPI document.
    /// <code>
    /// app.MapOpenApi(configure: o =>
    /// {
    ///     o.Title = "Widgets";
    ///     o.Version = "1.0.0";
    /// });
    /// </code>
    /// <para>
    /// The document is built on the first request and cached, because the route table is frozen by
    /// the time anything can ask for it. The endpoint excludes itself from what it publishes.
    /// </para>
    /// </summary>
    public static HttpServer MapOpenApi(
        this HttpServer server,
        string pattern = "/openapi.json",
        Action<OpenApiOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(server);

        var options = new OpenApiOptions();
        configure?.Invoke(options);

        byte[]? document = null;

        server.MapGet(pattern, ctx =>
        {
            // Racing here just builds the document twice and keeps one; a lock on a once-per-process
            // path would cost more than the occasional duplicate.
            document ??= OpenApiDocumentBuilder.Build(server, options);

            return ctx.Response.WriteBytesAsync(
                document,
                "application/json; charset=utf-8",
                ctx.RequestAborted
            );
        });

        return server.Describe(o => o.Exclude = true);
    }

    /// <summary>
    /// Describes the most recently mapped route, which is how a raw handler gets into the document
    /// with something more useful than its path.
    /// <code>
    /// app.OnGet("/ping", ctx => ctx.Response.WriteAsync("pong"))
    ///    .Describe(o =>
    ///    {
    ///        o.Summary = "Liveness probe";
    ///        o.Tags.Add("ops");
    ///        o.Responses.Add(new ApiResponse { StatusCode = 200, Type = typeof(string), ContentType = "text/plain" });
    ///    });
    /// </code>
    /// Generated endpoints are described already; this augments whatever the generator worked out.
    /// </summary>
    public static HttpServer Describe(this HttpServer server, Action<ApiOperation> configure)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(configure);

        if (server.Router.Endpoints.Count == 0)
            throw new InvalidOperationException(
                $"{nameof(Describe)} applies to the most recently mapped route, and no route has been mapped yet."
            );

        var endpoint = server.Router.Endpoints[^1];
        var operation = endpoint.GetMetadata<ApiOperation>();

        if (operation is null)
        {
            operation = new ApiOperation();
            endpoint.WithMetadata(operation);
        }

        configure(operation);
        return server;
    }
}
