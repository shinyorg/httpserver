using System.Collections.Concurrent;
using Shiny.Net.HttpServer.Versioning;

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

        if (pattern.Contains(DocumentNameToken, StringComparison.OrdinalIgnoreCase))
            return server.MapVersionedOpenApi(pattern, options);

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

    const string DocumentNameToken = "{documentName}";

    /// <summary>
    /// One document per API version, at a URL naming it — the form ASP.NET Core's own
    /// <c>MapOpenApi("/openapi/{documentName}.json")</c> uses.
    /// <para>
    /// The token may share its segment with literal text (<c>{documentName}.json</c>), which a route
    /// template cannot, so the segment is routed as a plain parameter and the literal part is checked
    /// here. A document name is the version as <see cref="ApiVersioningOptions.FormatGroupName"/>
    /// writes it (<c>v1</c>, <c>v2.1</c>) or any text <see cref="ApiVersion"/> parses (<c>1.0</c>).
    /// A name no endpoint serves is a 404. Each version's document is built on first request and
    /// cached, like the single document.
    /// </para>
    /// </summary>
    static HttpServer MapVersionedOpenApi(this HttpServer server, string pattern, OpenApiOptions options)
    {
        var segments = pattern.Split('/');
        var index = Array.FindIndex(segments, s => s.Contains(DocumentNameToken, StringComparison.OrdinalIgnoreCase));
        var segment = segments[index];
        var tokenAt = segment.IndexOf(DocumentNameToken, StringComparison.OrdinalIgnoreCase);
        var prefix = segment[..tokenAt];
        var suffix = segment[(tokenAt + DocumentNameToken.Length)..];

        segments[index] = DocumentNameToken;
        var route = string.Join('/', segments);

        var documents = new ConcurrentDictionary<ApiVersion, byte[]>();

        server.MapGet(route, ctx =>
        {
            var name = ctx.Request.RouteValues["documentName"] ?? string.Empty;

            if (name.Length < prefix.Length + suffix.Length ||
                !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return NotFound(ctx);

            var version = Resolve(server, name[prefix.Length..^suffix.Length]);
            if (version is null)
                return NotFound(ctx);

            var document = documents.GetOrAdd(version, v => OpenApiDocumentBuilder.Build(server, options, v));

            return ctx.Response.WriteBytesAsync(
                document,
                "application/json; charset=utf-8",
                ctx.RequestAborted
            );
        });

        return server.Describe(o => o.Exclude = true);

        static ValueTask NotFound(HttpContext ctx)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            ctx.Response.ContentLength = 0;
            return default;
        }
    }

    static ApiVersion? Resolve(HttpServer server, string documentName)
    {
        var versioning = OpenApiDocumentBuilder.VersioningOptions(server);
        ApiVersion.TryParse(documentName, out var parsed);

        foreach (var version in OpenApiDocumentBuilder.GetApiVersions(server))
        {
            if (string.Equals(versioning.FormatGroupName(version), documentName, StringComparison.OrdinalIgnoreCase) ||
                version == parsed)
                return version;
        }

        return null;
    }

    /// <summary>
    /// Describes the most recently mapped route, which is how a raw handler gets into the document
    /// with something more useful than its path.
    /// <code>
    /// app.MapGet("/ping", ctx => ctx.Response.WriteAsync("pong"))
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
