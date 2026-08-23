using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Shiny.Net.HttpServer.Caching;

/// <summary>Registering output caching.</summary>
public static class OutputCacheServiceCollectionExtensions
{
    /// <summary>
    /// Registers the cache options, and an in-memory store when nothing else has claimed the role.
    /// <code>
    /// builder.Services.AddOutputCache(o =>
    /// {
    ///     o.AddPolicy("lists", new OutputCachePolicy(TimeSpan.FromSeconds(30)) { VaryByHeaders = ["Accept"] });
    /// });
    /// </code>
    /// </summary>
    public static ShinyHttpServerBuilder AddOutputCache(
        this ShinyHttpServerBuilder builder,
        Action<OutputCacheOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton(_ =>
        {
            var options = new OutputCacheOptions();
            configure?.Invoke(options);

            return options;
        });

        builder.Services.TryAddSingleton<IOutputCacheStore>(_ => new MemoryOutputCacheStore());

        return builder;
    }
}

/// <summary>Putting output caching in the pipeline.</summary>
public static class HttpServerOutputCacheExtensions
{
    /// <summary>
    /// Serves stored responses for endpoints that asked to be cached.
    /// <para>
    /// Runs after routing, so a hit still pays for the middleware above it — authentication, rate
    /// limiting, CORS. That is deliberate: skipping authentication on a cache hit is how a cache
    /// turns into an authorization bypass.
    /// </para>
    /// </summary>
    public static HttpServer UseOutputCache(this HttpServer server)
    {
        ArgumentNullException.ThrowIfNull(server);

        var options = server.Services?.GetService<OutputCacheOptions>()
            ?? throw new InvalidOperationException(
                "UseOutputCache needs its options. Register them with builder.AddOutputCache()."
            );

        var store = server.Services?.GetService<IOutputCacheStore>() ?? new MemoryOutputCacheStore();

        return server.UseAfterRouting(new OutputCacheMiddleware(options, store));
    }

    /// <summary>Serves stored responses using pieces built elsewhere.</summary>
    public static HttpServer UseOutputCache(this HttpServer server, OutputCacheOptions options, IOutputCacheStore? store = null)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(options);

        return server.UseAfterRouting(new OutputCacheMiddleware(options, store ?? new MemoryOutputCacheStore()));
    }

    /// <summary>
    /// Caches the most recently mapped route for a fixed duration.
    /// <code>
    /// app.MapGet("/dashboard", Handler).CacheOutput(TimeSpan.FromSeconds(10));
    /// </code>
    /// </summary>
    public static HttpServer CacheOutput(this HttpServer server, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(server);

        LastEndpointMetadata(server).Duration = duration;
        return server;
    }

    /// <summary>Applies a named policy to the most recently mapped route.</summary>
    public static HttpServer CacheOutput(this HttpServer server, string policyName)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrEmpty(policyName);

        LastEndpointMetadata(server).PolicyName = policyName;
        return server;
    }

    /// <summary>Exempts the most recently mapped route, the default policy included.</summary>
    public static HttpServer NoOutputCache(this HttpServer server)
    {
        ArgumentNullException.ThrowIfNull(server);

        LastEndpointMetadata(server).Disabled = true;
        return server;
    }

    static OutputCacheMetadata LastEndpointMetadata(HttpServer server)
    {
        if (server.Router.Endpoints.Count == 0)
            throw new InvalidOperationException(
                "CacheOutput applies to the most recently mapped route, and no route has been mapped yet."
            );

        var endpoint = server.Router.Endpoints[^1];
        var metadata = endpoint.GetMetadata<OutputCacheMetadata>();

        if (metadata is null)
        {
            metadata = new OutputCacheMetadata();
            endpoint.WithMetadata(metadata);
        }

        return metadata;
    }
}
