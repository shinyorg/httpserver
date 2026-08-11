using Shiny.Net.HttpServer.Cors;
using Shiny.Net.HttpServer.OpenApi;
using Shiny.Net.HttpServer.RateLimiting;
using Shiny.Net.HttpServer.Routing;
using Shiny.Net.HttpServer.Security;

namespace Shiny.Net.HttpServer;

/// <summary>
/// A route that has just been mapped, so metadata can be attached to it without hunting it back out
/// of the table. Every method returns <c>this</c>, so it reads as one statement.
/// </summary>
public sealed class RouteEndpointBuilder(RouteEndpoint endpoint)
{
    /// <summary>The registered endpoint, for passing to <see cref="HttpServer.Unmap(RouteEndpoint)"/>.</summary>
    public RouteEndpoint Endpoint { get; } = endpoint;

    /// <summary>Attaches arbitrary metadata.</summary>
    public RouteEndpointBuilder WithMetadata(object metadata)
    {
        this.Endpoint.WithMetadata(metadata);
        return this;
    }

    /// <summary>Requires authorization, optionally against named policies.</summary>
    public RouteEndpointBuilder RequireAuthorization(params string[] policies)
    {
        var metadata = this.GetOrAdd<AuthorizationMetadata>();
        metadata.Required = true;

        foreach (var policy in policies)
            metadata.Policies.Add(policy);

        return this;
    }

    /// <summary>Exempts this route from authorization, including from a fallback policy.</summary>
    public RouteEndpointBuilder AllowAnonymous()
    {
        this.GetOrAdd<AuthorizationMetadata>().AllowAnonymous = true;
        return this;
    }

    /// <summary>Applies a named CORS policy to this route.</summary>
    public RouteEndpointBuilder RequireCors(string policyName)
    {
        ArgumentException.ThrowIfNullOrEmpty(policyName);

        this.GetOrAdd<CorsMetadata>().PolicyName = policyName;
        return this;
    }

    /// <summary>Exempts this route from CORS, including from the default policy.</summary>
    public RouteEndpointBuilder DisableCors()
    {
        this.GetOrAdd<CorsMetadata>().Disabled = true;
        return this;
    }

    /// <summary>Applies a named rate limit policy to this route.</summary>
    public RouteEndpointBuilder RequireRateLimiting(string policyName)
    {
        ArgumentException.ThrowIfNullOrEmpty(policyName);

        this.GetOrAdd<RateLimitMetadata>().PolicyName = policyName;
        return this;
    }

    /// <summary>Exempts this route from rate limiting, including from the global policy.</summary>
    public RouteEndpointBuilder DisableRateLimiting()
    {
        this.GetOrAdd<RateLimitMetadata>().Disabled = true;
        return this;
    }

    /// <summary>Applies a named IP filter policy to this route.</summary>
    public RouteEndpointBuilder RequireIpFilter(string policyName)
    {
        ArgumentException.ThrowIfNullOrEmpty(policyName);

        this.GetOrAdd<IpFilterMetadata>().PolicyName = policyName;
        return this;
    }

    /// <summary>Exempts this route from the IP filter, including from the default policy.</summary>
    public RouteEndpointBuilder AllowAnyIp()
    {
        this.GetOrAdd<IpFilterMetadata>().Disabled = true;
        return this;
    }

    /// <summary>Describes the route for the OpenAPI document.</summary>
    public RouteEndpointBuilder Describe(Action<ApiOperation> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        configure(this.GetOrAdd<ApiOperation>());
        return this;
    }

    /// <summary>Sets the OpenAPI summary. Shorthand for the common case of <see cref="Describe"/>.</summary>
    public RouteEndpointBuilder WithSummary(string summary)
        => this.Describe(o => o.Summary = summary);

    /// <summary>Adds OpenAPI tags.</summary>
    public RouteEndpointBuilder WithTags(params string[] tags)
        => this.Describe(o =>
        {
            foreach (var tag in tags)
                o.Tags.Add(tag);
        });

    /// <summary>Omits the route from the OpenAPI document without unmapping it.</summary>
    public RouteEndpointBuilder ExcludeFromDescription()
        => this.Describe(o => o.Exclude = true);

    T GetOrAdd<T>() where T : class, new()
    {
        if (this.Endpoint.GetMetadata<T>() is { } existing)
            return existing;

        var created = new T();
        this.Endpoint.WithMetadata(created);

        return created;
    }
}

/// <summary>
/// The default <see cref="IEndpointRouteBuilder"/>: maps into a server's route table, stamping every
/// route with a prefix and an owner tag.
/// </summary>
sealed class EndpointRouteBuilder(HttpServer server, string prefix, object? owner) : IEndpointRouteBuilder
{
    public IServiceProvider? Services => server.Services;

    public string Prefix { get; } = prefix;

    public RouteEndpointBuilder Map(string method, string pattern, RequestDelegate handler)
    {
        var endpoint = server.MapRoute(method, Combine(this.Prefix, pattern), handler);
        var builder = new RouteEndpointBuilder(endpoint);

        // Tagged with what mounted it, which is what lets the whole group be unmounted later
        // without anyone having to remember the individual routes.
        if (owner is not null)
            builder.WithMetadata(new EndpointOwner(owner));

        return builder;
    }

    public IEndpointRouteBuilder MapGroup(string prefix)
        => new EndpointRouteBuilder(server, Combine(this.Prefix, prefix), owner);

    static string Combine(string prefix, string pattern)
    {
        var left = (prefix ?? string.Empty).Trim().Trim('/');
        var right = (pattern ?? string.Empty).Trim().Trim('/');

        if (left.Length == 0)
            return right.Length == 0 ? "/" : "/" + right;

        return right.Length == 0 ? "/" + left : "/" + left + "/" + right;
    }
}

/// <summary>Identifies what mounted a route, so a whole module can be unmounted in one call.</summary>
public sealed class EndpointOwner(object owner)
{
    public object Owner { get; } = owner;

    /// <summary>The owner's type, which is what <c>UnmapModule</c> matches on.</summary>
    public Type OwnerType { get; } = owner as Type ?? owner.GetType();
}

/// <summary>Mapping modules and route groups onto a server.</summary>
public static class EndpointModuleExtensions
{
    /// <summary>
    /// Mounts a module's routes. Every route it registers is tagged with the module type, so
    /// <see cref="UnmapModule(HttpServer, Type)"/> can take them all away again.
    /// </summary>
    public static HttpServer MapModule(this HttpServer server, IEndpointModule module, string prefix = "")
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(module);

        module.Map(new EndpointRouteBuilder(server, prefix, module.GetType()));
        return server;
    }

    /// <summary>Mounts every module registered in the container.</summary>
    public static HttpServer MapModules(this HttpServer server, string prefix = "")
    {
        ArgumentNullException.ThrowIfNull(server);

        if (server.Services?.GetService(typeof(IEnumerable<IEndpointModule>)) is not IEnumerable<IEndpointModule> modules)
            return server;

        foreach (var module in modules)
            server.MapModule(module, prefix);

        return server;
    }

    /// <summary>Removes every route a module mounted. Returns how many went.</summary>
    public static int UnmapModule(this HttpServer server, Type moduleType)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(moduleType);

        return server.UnmapAll(e => e.GetMetadata<EndpointOwner>()?.OwnerType == moduleType);
    }

    /// <summary>Removes every route a module mounted.</summary>
    public static int UnmapModule<TModule>(this HttpServer server) where TModule : IEndpointModule
        => server.UnmapModule(typeof(TModule));

    /// <summary>
    /// Maps a group of routes under a shared prefix, without a module type.
    /// <code>
    /// app.MapGroup("/api/v2", api =>
    /// {
    ///     api.Map("GET", "/ping", ctx => ctx.Response.WriteAsync("pong"));
    /// });
    /// </code>
    /// </summary>
    public static HttpServer MapGroup(this HttpServer server, string prefix, Action<IEndpointRouteBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(configure);

        configure(new EndpointRouteBuilder(server, prefix, null));
        return server;
    }
}

/// <summary>Verb shorthands for <see cref="IEndpointRouteBuilder"/>.</summary>
public static class EndpointRouteBuilderExtensions
{
    public static RouteEndpointBuilder MapGet(this IEndpointRouteBuilder endpoints, string pattern, RequestDelegate handler)
        => endpoints.Map(HttpMethods.Get, pattern, handler);

    public static RouteEndpointBuilder MapPost(this IEndpointRouteBuilder endpoints, string pattern, RequestDelegate handler)
        => endpoints.Map(HttpMethods.Post, pattern, handler);

    public static RouteEndpointBuilder MapPut(this IEndpointRouteBuilder endpoints, string pattern, RequestDelegate handler)
        => endpoints.Map(HttpMethods.Put, pattern, handler);

    public static RouteEndpointBuilder MapDelete(this IEndpointRouteBuilder endpoints, string pattern, RequestDelegate handler)
        => endpoints.Map(HttpMethods.Delete, pattern, handler);

    public static RouteEndpointBuilder MapPatch(this IEndpointRouteBuilder endpoints, string pattern, RequestDelegate handler)
        => endpoints.Map(HttpMethods.Patch, pattern, handler);

    public static RouteEndpointBuilder MapGet(this IEndpointRouteBuilder endpoints, string pattern, Func<HttpContext, IResult> handler)
        => endpoints.Map(HttpMethods.Get, pattern, ctx => handler(ctx).ExecuteAsync(ctx));

    public static RouteEndpointBuilder MapPost(this IEndpointRouteBuilder endpoints, string pattern, Func<HttpContext, IResult> handler)
        => endpoints.Map(HttpMethods.Post, pattern, ctx => handler(ctx).ExecuteAsync(ctx));
}
