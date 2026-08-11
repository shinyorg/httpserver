using Shiny.Net.HttpServer.Routing;

namespace Shiny.Net.HttpServer.Mcp;

/// <summary>Mounting an MCP server on a route.</summary>
public static class McpEndpointExtensions
{
    /// <summary>
    /// Mounts the MCP endpoint. One path, four verbs: <c>POST</c> for client messages, <c>GET</c>
    /// for the server-to-client stream, <c>DELETE</c> to end a session, <c>OPTIONS</c> for the
    /// browser preflight.
    /// <code>
    /// app.MapMcp();                          // http://host:port/mcp
    /// app.MapMcp("/tools").RequireAuthorization();
    /// </code>
    /// </summary>
    public static McpEndpointConventions MapMcp(this HttpServer server, string pattern = "/mcp")
    {
        ArgumentNullException.ThrowIfNull(server);

        var handler = ResolveHandler(server.Services);

        return new McpEndpointConventions(
            new RouteEndpointBuilder(server.MapRoute(HttpMethods.Post, pattern, handler.PostAsync)),
            new RouteEndpointBuilder(server.MapRoute(HttpMethods.Get, pattern, handler.GetAsync)),
            new RouteEndpointBuilder(server.MapRoute(HttpMethods.Delete, pattern, handler.DeleteAsync)),
            new RouteEndpointBuilder(server.MapRoute(HttpMethods.Options, pattern, handler.PreflightAsync))
        );
    }

    /// <summary>Mounts the MCP endpoint from inside a module or route group.</summary>
    public static McpEndpointConventions MapMcp(this IEndpointRouteBuilder endpoints, string pattern = "/mcp")
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var handler = ResolveHandler(endpoints.Services);

        return new McpEndpointConventions(
            endpoints.Map(HttpMethods.Post, pattern, handler.PostAsync),
            endpoints.Map(HttpMethods.Get, pattern, handler.GetAsync),
            endpoints.Map(HttpMethods.Delete, pattern, handler.DeleteAsync),
            endpoints.Map(HttpMethods.Options, pattern, handler.PreflightAsync)
        );
    }

    static McpHttpHandler ResolveHandler(IServiceProvider? services)
    {
        if (services is null)
            throw new InvalidOperationException(
                "MapMcp needs a service provider, because the MCP SDK is configured through one. " +
                "Build the server with HttpServer.CreateBuilder() rather than new HttpServer()."
            );

        // Resolving the handler pulls the whole MCP server graph in behind it, tools included, so
        // this is also where a tool that cannot describe itself surfaces. Building them is not
        // deferred work that could be skipped — it is what registering an MCP server means — so the
        // only question is whether the failure arrives as something a caller can act on.
        return McpStartupValidation.Guarded(
            () => services.GetService(typeof(McpHttpHandler)) as McpHttpHandler
                ?? throw new InvalidOperationException(
                    "The MCP HTTP transport is not registered. Add it alongside the MCP server: " +
                    "services.AddMcpServer().WithTools<T>().WithHttpTransport()."
                )
        );
    }
}

/// <summary>
/// The routes <see cref="McpEndpointExtensions.MapMcp(HttpServer, string)"/> mounted, so a
/// convention can be applied to the set rather than to whichever one happened to be mapped last.
/// </summary>
public sealed class McpEndpointConventions
{
    readonly RouteEndpointBuilder[] protocol;
    readonly RouteEndpointBuilder preflight;

    internal McpEndpointConventions(
        RouteEndpointBuilder post,
        RouteEndpointBuilder get,
        RouteEndpointBuilder delete,
        RouteEndpointBuilder options
    )
    {
        this.protocol = [post, get, delete];
        this.preflight = options;

        // The MCP endpoint is not an API operation and describing it in OpenAPI would say nothing
        // useful — the interesting surface is the tool list, which is behind JSON-RPC.
        foreach (var route in this.All)
            route.ExcludeFromDescription();
    }

    IEnumerable<RouteEndpointBuilder> All
    {
        get
        {
            foreach (var route in this.protocol)
                yield return route;

            yield return this.preflight;
        }
    }

    /// <summary>The mounted endpoints, for <see cref="HttpServer.Unmap(RouteEndpoint)"/>.</summary>
    public IReadOnlyList<RouteEndpoint> Endpoints => [.. this.All.Select(r => r.Endpoint)];

    /// <summary>
    /// Requires authorization on the protocol verbs.
    /// <para>
    /// The preflight is deliberately left anonymous: a browser sends <c>OPTIONS</c> without
    /// credentials and rejecting it means the real request is never attempted, so requiring auth
    /// there would make the endpoint unreachable from a browser rather than more secure.
    /// </para>
    /// </summary>
    public McpEndpointConventions RequireAuthorization(params string[] policies)
    {
        foreach (var route in this.protocol)
            route.RequireAuthorization(policies);

        this.preflight.AllowAnonymous();

        return this;
    }

    /// <summary>Exempts the MCP endpoint from authorization, including from a fallback policy.</summary>
    public McpEndpointConventions AllowAnonymous()
    {
        foreach (var route in this.All)
            route.AllowAnonymous();

        return this;
    }

    /// <summary>Attaches metadata to every MCP route.</summary>
    public McpEndpointConventions WithMetadata(object metadata)
    {
        foreach (var route in this.All)
            route.WithMetadata(metadata);

        return this;
    }
}
