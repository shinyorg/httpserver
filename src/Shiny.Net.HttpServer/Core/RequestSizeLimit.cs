namespace Shiny.Net.HttpServer;

/// <summary>
/// An endpoint's own request body limit, read by routing and applied to
/// <see cref="HttpRequest.MaxBodySize"/> before the handler runs. Both attributes implement it, so
/// a raw route can take either as metadata.
/// </summary>
public interface IRequestSizeLimitMetadata
{
    /// <summary>The limit in bytes, or null for no limit at all.</summary>
    long? MaxBodySize { get; }
}

/// <summary>
/// Overrides <see cref="HttpServerLimits.MaxRequestBodySize"/> for one endpoint — higher for an
/// upload, lower for a form that never needs more than a few kilobytes.
/// <code>
/// [Route("/images")]
/// public class ImageEndpoints
/// {
///     [Post("/")] [RequestSizeLimit(500 * 1024 * 1024)] public Task&lt;IActionResult&gt; Upload(HttpContext ctx) => ...;
/// }
///
/// app.MapPost("/images", Upload).WithRequestSizeLimit(500 * 1024 * 1024);
/// </code>
/// <para>
/// Applies to HTTP/1.1 and HTTP/2. HTTP/3 receives the whole body before routing runs, so it stays
/// bound by the server-wide limit.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class RequestSizeLimitAttribute : Attribute, IRequestSizeLimitMetadata
{
    /// <param name="bytes">The most body the endpoint accepts.</param>
    public RequestSizeLimitAttribute(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        this.Bytes = bytes;
    }

    public long Bytes { get; }

    long? IRequestSizeLimitMetadata.MaxBodySize => this.Bytes;
}

/// <summary>
/// Removes the request body limit from one endpoint — a large-file upload streamed straight to disk,
/// where the handler decides what is too big. HTTP/3 stays bound by the server-wide limit.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class DisableRequestSizeLimitAttribute : Attribute, IRequestSizeLimitMetadata
{
    long? IRequestSizeLimitMetadata.MaxBodySize => null;
}

public static class RequestSizeLimitExtensions
{
    /// <summary>
    /// Overrides the request body limit for the most recently mapped route.
    /// <code>
    /// app.MapPost("/upload", Handler).WithRequestSizeLimit(500 * 1024 * 1024);
    /// </code>
    /// </summary>
    public static HttpServer WithRequestSizeLimit(this HttpServer server, long bytes)
    {
        ArgumentNullException.ThrowIfNull(server);

        LastEndpoint(server).WithMetadata(new RequestSizeLimitAttribute(bytes));
        return server;
    }

    /// <summary>Removes the request body limit from the most recently mapped route.</summary>
    public static HttpServer DisableRequestSizeLimit(this HttpServer server)
    {
        ArgumentNullException.ThrowIfNull(server);

        LastEndpoint(server).WithMetadata(new DisableRequestSizeLimitAttribute());
        return server;
    }

    static Routing.Endpoint LastEndpoint(HttpServer server)
    {
        if (server.Router.Endpoints.Count == 0)
            throw new InvalidOperationException(
                "WithRequestSizeLimit applies to the most recently mapped route, and no route has been mapped yet."
            );

        return server.Router.Endpoints[^1];
    }
}
