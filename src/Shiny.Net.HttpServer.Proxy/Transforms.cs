namespace Shiny.Net.HttpServer.Proxy;

/// <summary>How the <c>X-Forwarded-*</c> headers are written.</summary>
public enum ForwardedHeadersMode
{
    /// <summary>Not written at all. For an upstream that already sits behind something else that does it.</summary>
    Off = 0,

    /// <summary>Replaces whatever the caller sent. The safe default: a caller can otherwise forge its own origin.</summary>
    Set,

    /// <summary>Appends to what the caller sent, for a deliberate chain of proxies.</summary>
    Append
}

/// <summary>The outbound request, while it is still changeable.</summary>
public sealed class RequestTransformContext
{
    internal RequestTransformContext(HttpContext httpContext, HttpRequestMessage proxyRequest, ProxyDestination destination, string path, ProxyQuery query)
    {
        this.HttpContext = httpContext;
        this.ProxyRequest = proxyRequest;
        this.Destination = destination;
        this.Path = path;
        this.Query = query;
    }

    /// <summary>The inbound exchange this request was built from.</summary>
    public HttpContext HttpContext { get; }

    /// <summary>The message that will be sent. Its <c>RequestUri</c> is assembled after the transforms run.</summary>
    public HttpRequestMessage ProxyRequest { get; }

    /// <summary>Which destination was chosen.</summary>
    public ProxyDestination Destination { get; }

    /// <summary>The path on the destination, starting with '/'. Assign to rewrite it outright.</summary>
    public string Path { get; set; }

    /// <summary>The outbound query.</summary>
    public ProxyQuery Query { get; }

    /// <summary>
    /// Overrides the <c>Host</c> the outbound request carries. Null uses whatever the cluster's
    /// <see cref="ProxyOptions.RewriteHost"/> decided.
    /// </summary>
    public string? Host { get; set; }
}

/// <summary>The upstream's answer, after its headers have been copied onto the response.</summary>
public sealed class ResponseTransformContext
{
    internal ResponseTransformContext(HttpContext httpContext, HttpResponseMessage proxyResponse)
    {
        this.HttpContext = httpContext;
        this.ProxyResponse = proxyResponse;
    }

    /// <summary>The exchange being answered. Change <c>HttpContext.Response</c> to change what the caller sees.</summary>
    public HttpContext HttpContext { get; }

    /// <summary>What the upstream actually said, before any of this ran.</summary>
    public HttpResponseMessage ProxyResponse { get; }
}

/// <summary>
/// The transforms applied to a proxied exchange, in the order they were added.
/// <para>
/// Request transforms run after the default path and query have been worked out and before the
/// request is sent; response transforms run after the upstream's headers have been copied onto the
/// response and before the body is streamed back, which is the last moment anything can be changed.
/// </para>
/// <code>
/// cluster.Transforms
///     .RemovePathPrefix("/api")
///     .SetRequestHeader("X-Tenant", "acme")
///     .RemoveResponseHeader("Server");
/// </code>
/// </summary>
public sealed class TransformBuilder
{
    readonly List<Func<RequestTransformContext, ValueTask>> request = [];
    readonly List<Func<ResponseTransformContext, ValueTask>> response = [];

    /// <summary>How the <c>X-Forwarded-*</c> headers describing the original caller are written.</summary>
    public ForwardedHeadersMode ForwardedHeaders { get; set; } = ForwardedHeadersMode.Set;

    internal bool HasRequestTransforms => this.request.Count > 0;

    internal bool HasResponseTransforms => this.response.Count > 0;

    /// <summary>Adds an arbitrary request transform.</summary>
    public TransformBuilder AddRequestTransform(Func<RequestTransformContext, ValueTask> transform)
    {
        ArgumentNullException.ThrowIfNull(transform);

        this.request.Add(transform);
        return this;
    }

    /// <summary>Adds an arbitrary synchronous request transform.</summary>
    public TransformBuilder AddRequestTransform(Action<RequestTransformContext> transform)
    {
        ArgumentNullException.ThrowIfNull(transform);

        return this.AddRequestTransform(ctx =>
        {
            transform(ctx);
            return default;
        });
    }

    /// <summary>Adds an arbitrary response transform.</summary>
    public TransformBuilder AddResponseTransform(Func<ResponseTransformContext, ValueTask> transform)
    {
        ArgumentNullException.ThrowIfNull(transform);

        this.response.Add(transform);
        return this;
    }

    /// <summary>Adds an arbitrary synchronous response transform.</summary>
    public TransformBuilder AddResponseTransform(Action<ResponseTransformContext> transform)
    {
        ArgumentNullException.ThrowIfNull(transform);

        return this.AddResponseTransform(ctx =>
        {
            transform(ctx);
            return default;
        });
    }

    /// <summary>Puts a prefix on the outbound path.</summary>
    public TransformBuilder AddPathPrefix(string prefix)
    {
        ArgumentException.ThrowIfNullOrEmpty(prefix);

        return this.AddRequestTransform(ctx => ctx.Path = Combine(prefix, ctx.Path));
    }

    /// <summary>
    /// Takes a prefix off the outbound path. A path that does not start with it is left alone, so a
    /// route whose template already stripped the prefix does not lose a second segment.
    /// </summary>
    public TransformBuilder RemovePathPrefix(string prefix)
    {
        ArgumentException.ThrowIfNullOrEmpty(prefix);

        var trimmed = prefix.TrimEnd('/');

        return this.AddRequestTransform(ctx =>
        {
            if (!ctx.Path.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase))
                return;

            var remainder = ctx.Path[trimmed.Length..];
            if (remainder.Length > 0 && remainder[0] != '/')
                return;

            ctx.Path = remainder.Length == 0 ? "/" : remainder;
        });
    }

    /// <summary>Replaces the outbound path entirely.</summary>
    public TransformBuilder SetPath(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        return this.AddRequestTransform(ctx => ctx.Path = path);
    }

    /// <summary>Sets a query value on the outbound request, replacing any the caller sent.</summary>
    public TransformBuilder SetQueryValue(string key, string value)
        => this.AddRequestTransform(ctx => ctx.Query.Set(key, value));

    /// <summary>Adds a query value without touching any the caller sent.</summary>
    public TransformBuilder AppendQueryValue(string key, string value)
        => this.AddRequestTransform(ctx => ctx.Query.Append(key, value));

    /// <summary>Copies a route value into the query, e.g. a tenant captured by the route template.</summary>
    public TransformBuilder SetQueryRouteValue(string key, string routeValueKey)
        => this.AddRequestTransform(ctx =>
        {
            if (ctx.HttpContext.Request.RouteValues[routeValueKey] is { } value)
                ctx.Query.Set(key, value);
        });

    /// <summary>Drops a query value.</summary>
    public TransformBuilder RemoveQueryValue(string key)
        => this.AddRequestTransform(ctx => ctx.Query.Remove(key));

    /// <summary>Sets a header on the outbound request, replacing whatever the caller sent.</summary>
    public TransformBuilder SetRequestHeader(string name, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        return this.AddRequestTransform(ctx =>
        {
            ctx.ProxyRequest.Headers.Remove(name);
            ctx.ProxyRequest.Content?.Headers.Remove(name);
            ctx.ProxyRequest.Headers.TryAddWithoutValidation(name, value);
        });
    }

    /// <summary>Adds a header value to the outbound request, keeping any the caller sent.</summary>
    public TransformBuilder AppendRequestHeader(string name, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        return this.AddRequestTransform(ctx => ctx.ProxyRequest.Headers.TryAddWithoutValidation(name, value));
    }

    /// <summary>Copies a header from the caller under a different name.</summary>
    public TransformBuilder CopyRequestHeader(string from, string to)
    {
        ArgumentException.ThrowIfNullOrEmpty(from);
        ArgumentException.ThrowIfNullOrEmpty(to);

        return this.AddRequestTransform(ctx =>
        {
            if (ctx.HttpContext.Request.Headers.GetFirst(from) is { } value)
                ctx.ProxyRequest.Headers.TryAddWithoutValidation(to, value);
        });
    }

    /// <summary>Drops a header from the outbound request.</summary>
    public TransformBuilder RemoveRequestHeader(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        return this.AddRequestTransform(ctx =>
        {
            ctx.ProxyRequest.Headers.Remove(name);
            ctx.ProxyRequest.Content?.Headers.Remove(name);
        });
    }

    /// <summary>Sets the <c>Host</c> the outbound request carries, whatever the cluster decided.</summary>
    public TransformBuilder SetRequestHost(string host)
    {
        ArgumentException.ThrowIfNullOrEmpty(host);

        return this.AddRequestTransform(ctx => ctx.Host = host);
    }

    /// <summary>Sets a header on the response going back, replacing whatever the upstream sent.</summary>
    public TransformBuilder SetResponseHeader(string name, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        return this.AddResponseTransform(ctx => ctx.HttpContext.Response.Headers.Set(name, value));
    }

    /// <summary>Adds a header value to the response going back.</summary>
    public TransformBuilder AppendResponseHeader(string name, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        return this.AddResponseTransform(ctx => ctx.HttpContext.Response.Headers.Append(name, value));
    }

    /// <summary>Drops a header the upstream sent before the caller sees it.</summary>
    public TransformBuilder RemoveResponseHeader(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        return this.AddResponseTransform(ctx => ctx.HttpContext.Response.Headers.Remove(name));
    }

    /// <summary>Turns the <c>X-Forwarded-*</c> headers off, or changes how they are written.</summary>
    public TransformBuilder WithForwardedHeaders(ForwardedHeadersMode mode)
    {
        this.ForwardedHeaders = mode;
        return this;
    }

    internal async ValueTask ApplyRequestAsync(RequestTransformContext context)
    {
        foreach (var transform in this.request)
            await transform(context).ConfigureAwait(false);
    }

    internal async ValueTask ApplyResponseAsync(ResponseTransformContext context)
    {
        foreach (var transform in this.response)
            await transform(context).ConfigureAwait(false);
    }

    static string Combine(string prefix, string path)
    {
        var head = prefix.TrimEnd('/');
        var tail = path.TrimStart('/');

        return tail.Length == 0 ? (head.Length == 0 ? "/" : head) : head + "/" + tail;
    }
}
