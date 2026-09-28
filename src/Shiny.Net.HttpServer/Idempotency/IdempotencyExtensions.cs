using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Shiny.Net.HttpServer.Idempotency;

/// <summary>Registering idempotency keys.</summary>
public static class IdempotencyServiceCollectionExtensions
{
    /// <summary>
    /// Registers the options, and an in-memory store when nothing else has claimed the role.
    /// <code>
    /// builder.AddIdempotency(o => o.Expiration = TimeSpan.FromHours(6));
    /// </code>
    /// </summary>
    public static ShinyHttpServerBuilder AddIdempotency(
        this ShinyHttpServerBuilder builder,
        Action<IdempotencyOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(builder);

        OptionsRegistration.Configure<IdempotencyOptions>(builder.Services, configure);

        builder.Services.TryAddSingleton<IIdempotencyStore>(_ => new MemoryIdempotencyStore());

        return builder;
    }
}

/// <summary>Putting idempotency keys in the pipeline, and opting endpoints in.</summary>
public static class HttpServerIdempotencyExtensions
{
    /// <summary>
    /// Honours <c>Idempotency-Key</c> on the endpoints that opted in.
    /// <para>
    /// Runs after routing. Call it <b>after</b> <c>UseAuthorization()</c>: the caller is part of the
    /// key, and a request that is going to be refused should not claim one.
    /// </para>
    /// </summary>
    public static HttpServer UseIdempotency(this HttpServer server)
    {
        ArgumentNullException.ThrowIfNull(server);

        var options = server.Services?.GetService<IdempotencyOptions>() ?? new IdempotencyOptions();
        var store = server.Services?.GetService<IIdempotencyStore>() ?? new MemoryIdempotencyStore();

        return server.UseIdempotency(options, store);
    }

    /// <summary>Honours <c>Idempotency-Key</c> using pieces built elsewhere — no container needed.</summary>
    public static HttpServer UseIdempotency(this HttpServer server, IdempotencyOptions options, IIdempotencyStore? store = null)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(options);

        var logger = server.Services
            ?.GetService<ILoggerFactory>()
            ?.CreateLogger<IdempotencyMiddleware>();

        return server.UseAfterRouting(new IdempotencyMiddleware(options, store ?? new MemoryIdempotencyStore(), logger));
    }

    /// <summary>
    /// Requires an <c>Idempotency-Key</c> on the most recently mapped route; a request without one is
    /// refused with a 400.
    /// <code>
    /// app.MapPost("/payments", Charge).RequireIdempotencyKey();
    /// </code>
    /// </summary>
    public static HttpServer RequireIdempotencyKey(this HttpServer server, TimeSpan? expiration = null)
    {
        ArgumentNullException.ThrowIfNull(server);

        Apply(LastEndpointMetadata(server), required: true, expiration);
        return server;
    }

    /// <summary>
    /// Honours an <c>Idempotency-Key</c> on the most recently mapped route when the client sends one,
    /// and lets requests without one through.
    /// </summary>
    public static HttpServer WithIdempotency(this HttpServer server, TimeSpan? expiration = null)
    {
        ArgumentNullException.ThrowIfNull(server);

        Apply(LastEndpointMetadata(server), required: false, expiration);
        return server;
    }

    /// <summary>Requires an <c>Idempotency-Key</c> on this route.</summary>
    public static RouteEndpointBuilder RequireIdempotencyKey(this RouteEndpointBuilder route, TimeSpan? expiration = null)
    {
        ArgumentNullException.ThrowIfNull(route);

        Apply(GetOrAdd(route), required: true, expiration);
        return route;
    }

    /// <summary>Honours an <c>Idempotency-Key</c> on this route when the client sends one.</summary>
    public static RouteEndpointBuilder WithIdempotency(this RouteEndpointBuilder route, TimeSpan? expiration = null)
    {
        ArgumentNullException.ThrowIfNull(route);

        Apply(GetOrAdd(route), required: false, expiration);
        return route;
    }

    /// <summary>Exempts this route from a group's idempotency convention.</summary>
    public static RouteEndpointBuilder DisableIdempotency(this RouteEndpointBuilder route)
    {
        ArgumentNullException.ThrowIfNull(route);

        GetOrAdd(route).Disabled = true;
        return route;
    }

    /// <summary>
    /// Requires an <c>Idempotency-Key</c> on every route mapped through the returned builder,
    /// including nested groups and generated endpoint classes mapped into it.
    /// <code>
    /// app.MapGroup("/api/payments", group =>
    /// {
    ///     var payments = group.RequireIdempotencyKey();
    ///     payments.MapPost("/", Charge);
    ///     payments.MapPost("/{id}/refund", Refund);
    /// });
    /// </code>
    /// Use the builder this returns — the one passed in is left as it was.
    /// </summary>
    public static IEndpointRouteBuilder RequireIdempotencyKey(this IEndpointRouteBuilder group, TimeSpan? expiration = null)
    {
        ArgumentNullException.ThrowIfNull(group);
        return new ConventionEndpointRouteBuilder(group, route => route.RequireIdempotencyKey(expiration));
    }

    /// <summary>
    /// Honours an <c>Idempotency-Key</c>, when one is sent, on every route mapped through the returned
    /// builder.
    /// </summary>
    public static IEndpointRouteBuilder WithIdempotency(this IEndpointRouteBuilder group, TimeSpan? expiration = null)
    {
        ArgumentNullException.ThrowIfNull(group);
        return new ConventionEndpointRouteBuilder(group, route => route.WithIdempotency(expiration));
    }

    static void Apply(IdempotencyMetadata metadata, bool required, TimeSpan? expiration)
    {
        if (expiration is { } value && value <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(expiration), "An idempotency expiration must be positive.");

        metadata.Required = required;
        metadata.Disabled = false;

        if (expiration is not null)
            metadata.Expiration = expiration;
    }

    static IdempotencyMetadata GetOrAdd(RouteEndpointBuilder route)
    {
        if (route.Endpoint.GetMetadata<IdempotencyMetadata>() is { } existing)
            return existing;

        var created = new IdempotencyMetadata();
        route.WithMetadata(created);

        return created;
    }

    static IdempotencyMetadata LastEndpointMetadata(HttpServer server)
    {
        if (server.Router.Endpoints.Count == 0)
            throw new InvalidOperationException(
                "RequireIdempotencyKey applies to the most recently mapped route, and no route has been mapped yet."
            );

        return GetOrAdd(new RouteEndpointBuilder(server.Router.Endpoints[^1]));
    }
}
