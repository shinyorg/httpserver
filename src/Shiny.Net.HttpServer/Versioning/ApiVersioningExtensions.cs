using Shiny.Net.HttpServer.Routing;

namespace Shiny.Net.HttpServer.Versioning;

/// <summary>
/// Registering API versioning.
/// <para>
/// There is no <c>UseApiVersioning</c> middleware to remember: version selection happens inside
/// routing, because it decides <em>which endpoint</em> a request reaches and that is routing's job.
/// Registering the options is all it takes, as with Asp.Versioning.
/// </para>
/// </summary>
public static class ApiVersioningServiceCollectionExtensions
{
    /// <summary>
    /// Configures API versioning.
    /// <code>
    /// builder.AddApiVersioning(o =>
    /// {
    ///     o.AssumeDefaultVersionWhenUnspecified = true;
    ///     o.ReportApiVersions = true;
    /// });
    /// </code>
    /// Calling it again adds to the same options rather than replacing them.
    /// </summary>
    public static ShinyHttpServerBuilder AddApiVersioning(this ShinyHttpServerBuilder builder, Action<ApiVersioningOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        OptionsRegistration.Configure(builder.Services, configure);
        return builder;
    }
}

/// <summary>Versioning on the server: options without a container, and routes mapped straight onto it.</summary>
public static class HttpServerApiVersioningExtensions
{
    /// <summary>
    /// Configures API versioning on a server built without a container. With one, prefer
    /// <see cref="ApiVersioningServiceCollectionExtensions.AddApiVersioning"/>; calling this as well
    /// applies <paramref name="configure"/> on top of what was registered.
    /// </summary>
    public static HttpServer UseApiVersioning(this HttpServer server, Action<ApiVersioningOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(server);

        var options = server.Router.Versioning
            ?? server.Services?.GetService(typeof(ApiVersioningOptions)) as ApiVersioningOptions
            ?? new ApiVersioningOptions();

        configure?.Invoke(options);
        server.Router.Versioning = options;

        return server;
    }

    /// <summary>
    /// Starts a version set: the versions one API declares, shared by all of its endpoints.
    /// </summary>
    public static ApiVersionSetBuilder NewApiVersionSet(this HttpServer server, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(server);
        return new ApiVersionSetBuilder(name);
    }

    /// <summary>
    /// Declares a version on the most recently mapped route.
    /// <code>
    /// app.MapGet("/users", ListV1).HasApiVersion(1.0);
    /// app.MapGet("/users", ListV2).HasApiVersion(2.0);
    /// </code>
    /// </summary>
    public static HttpServer HasApiVersion(this HttpServer server, ApiVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);

        Last(server).SupportedVersions.Add(version);
        return server;
    }

    /// <inheritdoc cref="HasApiVersion(HttpServer, ApiVersion)"/>
    public static HttpServer HasApiVersion(this HttpServer server, double version)
        => server.HasApiVersion(new ApiVersion(version));

    /// <inheritdoc cref="HasApiVersion(HttpServer, ApiVersion)"/>
    public static HttpServer HasApiVersion(this HttpServer server, string version)
        => server.HasApiVersion(ApiVersion.Parse(version));

    /// <summary>Declares a deprecated version on the most recently mapped route.</summary>
    public static HttpServer HasDeprecatedApiVersion(this HttpServer server, ApiVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);

        Last(server).DeprecatedVersions.Add(version);
        return server;
    }

    /// <inheritdoc cref="HasDeprecatedApiVersion(HttpServer, ApiVersion)"/>
    public static HttpServer HasDeprecatedApiVersion(this HttpServer server, double version)
        => server.HasDeprecatedApiVersion(new ApiVersion(version));

    /// <summary>Scopes the most recently mapped route to one of its set's versions.</summary>
    public static HttpServer MapToApiVersion(this HttpServer server, ApiVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);

        Last(server).MappedVersions.Add(version);
        return server;
    }

    /// <inheritdoc cref="MapToApiVersion(HttpServer, ApiVersion)"/>
    public static HttpServer MapToApiVersion(this HttpServer server, double version)
        => server.MapToApiVersion(new ApiVersion(version));

    /// <summary>Gives the most recently mapped route a version set's declarations.</summary>
    public static HttpServer WithApiVersionSet(this HttpServer server, ApiVersionSet set)
    {
        ArgumentNullException.ThrowIfNull(set);

        LastEndpoint(server).WithMetadata(set.CreateMetadata());
        return server;
    }

    /// <summary>Marks the most recently mapped route version-neutral.</summary>
    public static HttpServer IsApiVersionNeutral(this HttpServer server)
    {
        Last(server).IsApiVersionNeutral = true;
        return server;
    }

    static RouteEndpoint LastEndpoint(HttpServer server)
    {
        ArgumentNullException.ThrowIfNull(server);

        if (server.Router.Endpoints.Count == 0)
            throw new InvalidOperationException(
                "API versions apply to the most recently mapped route, and no route has been mapped yet."
            );

        return server.Router.Endpoints[^1];
    }

    static ApiVersionMetadata Last(HttpServer server) => ApiVersionRouteEndpointBuilderExtensions.GetOrAdd(LastEndpoint(server));
}

/// <summary>Versioning a route through the builder <c>Map</c> returns on a group or module.</summary>
public static class ApiVersionRouteEndpointBuilderExtensions
{
    /// <summary>Declares a version on this route.</summary>
    public static RouteEndpointBuilder HasApiVersion(this RouteEndpointBuilder builder, ApiVersion version)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(version);

        GetOrAdd(builder.Endpoint).SupportedVersions.Add(version);
        return builder;
    }

    /// <inheritdoc cref="HasApiVersion(RouteEndpointBuilder, ApiVersion)"/>
    public static RouteEndpointBuilder HasApiVersion(this RouteEndpointBuilder builder, double version)
        => builder.HasApiVersion(new ApiVersion(version));

    /// <inheritdoc cref="HasApiVersion(RouteEndpointBuilder, ApiVersion)"/>
    public static RouteEndpointBuilder HasApiVersion(this RouteEndpointBuilder builder, string version)
        => builder.HasApiVersion(ApiVersion.Parse(version));

    /// <summary>Declares a deprecated version on this route.</summary>
    public static RouteEndpointBuilder HasDeprecatedApiVersion(this RouteEndpointBuilder builder, ApiVersion version)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(version);

        GetOrAdd(builder.Endpoint).DeprecatedVersions.Add(version);
        return builder;
    }

    /// <inheritdoc cref="HasDeprecatedApiVersion(RouteEndpointBuilder, ApiVersion)"/>
    public static RouteEndpointBuilder HasDeprecatedApiVersion(this RouteEndpointBuilder builder, double version)
        => builder.HasDeprecatedApiVersion(new ApiVersion(version));

    /// <summary>Scopes this route to one of the versions its set or group declares.</summary>
    public static RouteEndpointBuilder MapToApiVersion(this RouteEndpointBuilder builder, ApiVersion version)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(version);

        GetOrAdd(builder.Endpoint).MappedVersions.Add(version);
        return builder;
    }

    /// <inheritdoc cref="MapToApiVersion(RouteEndpointBuilder, ApiVersion)"/>
    public static RouteEndpointBuilder MapToApiVersion(this RouteEndpointBuilder builder, double version)
        => builder.MapToApiVersion(new ApiVersion(version));

    /// <summary>Gives this route a version set's declarations.</summary>
    public static RouteEndpointBuilder WithApiVersionSet(this RouteEndpointBuilder builder, ApiVersionSet set)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(set);

        builder.Endpoint.WithMetadata(set.CreateMetadata());
        return builder;
    }

    /// <summary>Marks this route version-neutral.</summary>
    public static RouteEndpointBuilder IsApiVersionNeutral(this RouteEndpointBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        GetOrAdd(builder.Endpoint).IsApiVersionNeutral = true;
        return builder;
    }

    internal static ApiVersionMetadata GetOrAdd(Endpoint endpoint)
    {
        if (endpoint.GetMetadata<ApiVersionMetadata>() is { } existing)
            return existing;

        var created = new ApiVersionMetadata();
        endpoint.WithMetadata(created);

        return created;
    }
}

/// <summary>Versioning a whole route group at once.</summary>
public static class ApiVersionEndpointRouteBuilderExtensions
{
    /// <summary>Starts a version set.</summary>
    public static ApiVersionSetBuilder NewApiVersionSet(this IEndpointRouteBuilder endpoints, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        return new ApiVersionSetBuilder(name);
    }

    /// <summary>
    /// Returns a builder whose every route declares <paramref name="version"/> — chain it for more.
    /// <code>
    /// app.MapGroup("/api/{version:apiVersion}", api =>
    /// {
    ///     var v1 = api.HasApiVersion(1.0);
    ///     v1.MapGet("/users", ListV1);
    ///
    ///     var v2 = api.HasApiVersion(2.0);
    ///     v2.MapGet("/users", ListV2);
    ///     v2.MapGroup("/admin").MapGet("/audit", Audit);   // also 2.0
    /// });
    /// </code>
    /// </summary>
    public static IEndpointRouteBuilder HasApiVersion(this IEndpointRouteBuilder endpoints, ApiVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return Versioned(endpoints, m => m.SupportedVersions.Add(version));
    }

    /// <inheritdoc cref="HasApiVersion(IEndpointRouteBuilder, ApiVersion)"/>
    public static IEndpointRouteBuilder HasApiVersion(this IEndpointRouteBuilder endpoints, double version)
        => endpoints.HasApiVersion(new ApiVersion(version));

    /// <inheritdoc cref="HasApiVersion(IEndpointRouteBuilder, ApiVersion)"/>
    public static IEndpointRouteBuilder HasApiVersion(this IEndpointRouteBuilder endpoints, string version)
        => endpoints.HasApiVersion(ApiVersion.Parse(version));

    /// <summary>Returns a builder whose every route declares a deprecated version.</summary>
    public static IEndpointRouteBuilder HasDeprecatedApiVersion(this IEndpointRouteBuilder endpoints, ApiVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return Versioned(endpoints, m => m.DeprecatedVersions.Add(version));
    }

    /// <inheritdoc cref="HasDeprecatedApiVersion(IEndpointRouteBuilder, ApiVersion)"/>
    public static IEndpointRouteBuilder HasDeprecatedApiVersion(this IEndpointRouteBuilder endpoints, double version)
        => endpoints.HasDeprecatedApiVersion(new ApiVersion(version));

    /// <summary>
    /// Returns a builder whose every route carries a version set's declarations; narrow individual
    /// routes with <c>MapToApiVersion</c>.
    /// </summary>
    public static IEndpointRouteBuilder WithApiVersionSet(this IEndpointRouteBuilder endpoints, ApiVersionSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        return Versioned(endpoints, m =>
        {
            var declared = set.CreateMetadata();

            foreach (var version in declared.SupportedVersions)
                m.SupportedVersions.Add(version);

            foreach (var version in declared.DeprecatedVersions)
                m.DeprecatedVersions.Add(version);

            m.IsApiVersionNeutral |= declared.IsApiVersionNeutral;
        });
    }

    /// <summary>Returns a builder whose every route is version-neutral.</summary>
    public static IEndpointRouteBuilder IsApiVersionNeutral(this IEndpointRouteBuilder endpoints)
        => Versioned(endpoints, m => m.IsApiVersionNeutral = true);

    static IEndpointRouteBuilder Versioned(IEndpointRouteBuilder endpoints, Action<ApiVersionMetadata> declare)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // Chained calls accumulate onto one declaration rather than nesting wrappers, so what a
        // route gets is visible in one place.
        var declared = endpoints is VersionedEndpointRouteBuilder versioned
            ? versioned.Declared.Clone()
            : new ApiVersionMetadata();

        declare(declared);

        var inner = endpoints is VersionedEndpointRouteBuilder wrapper ? wrapper.Inner : endpoints;
        return new VersionedEndpointRouteBuilder(inner, declared);
    }
}

/// <summary>
/// A route builder that stamps its versions on every route mapped through it, and on every group
/// under it. Each route gets its own copy, so narrowing one does not narrow its neighbours.
/// </summary>
sealed class VersionedEndpointRouteBuilder(IEndpointRouteBuilder inner, ApiVersionMetadata declared) : IEndpointRouteBuilder
{
    public IEndpointRouteBuilder Inner { get; } = inner;

    public ApiVersionMetadata Declared { get; } = declared;

    public IServiceProvider? Services => this.Inner.Services;

    public string Prefix => this.Inner.Prefix;

    public RouteEndpointBuilder Map(string method, string pattern, RequestDelegate handler)
    {
        var route = this.Inner.Map(method, pattern, handler);

        // Added before anything the caller attaches, so the route's own declarations — a
        // generated class's [ApiVersion], a .HasApiVersion() — land last and win.
        route.WithMetadata(this.Declared.Clone());
        return route;
    }

    public IEndpointRouteBuilder MapGroup(string prefix)
        => new VersionedEndpointRouteBuilder(this.Inner.MapGroup(prefix), this.Declared);
}

/// <summary>Reading the version a request is being served at.</summary>
public static class ApiVersionHttpContextExtensions
{
    /// <summary>
    /// The API version the request was dispatched at — what it asked for, or the default it was
    /// given — or null when it reached an unversioned or version-neutral endpoint without naming one.
    /// </summary>
    public static ApiVersion? GetRequestedApiVersion(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ApiVersionSelection.GetRequestedVersion(context);
    }
}
