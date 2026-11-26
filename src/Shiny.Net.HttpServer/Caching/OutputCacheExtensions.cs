using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shiny.Net.HttpServer.Routing;

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

        OptionsRegistration.Configure<OutputCacheOptions>(builder.Services, configure);

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
    /// Caches every route the preceding call mapped for a fixed duration.
    /// <code>
    /// app.MapGet("/dashboard", Handler).CacheOutput(TimeSpan.FromSeconds(10));
    /// </code>
    /// </summary>
    public static HttpServer CacheOutput(this HttpServer server, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(server);

        LastMappedConventions.Apply<OutputCacheMetadata>(server, "CacheOutput", m => m.Duration = duration);
        return server;
    }

    /// <summary>Applies a named policy to every route the preceding call mapped.</summary>
    public static HttpServer CacheOutput(this HttpServer server, string policyName)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrEmpty(policyName);

        LastMappedConventions.Apply<OutputCacheMetadata>(server, "CacheOutput", m => m.PolicyName = policyName);
        return server;
    }

    /// <summary>Exempts every route the preceding call mapped, the default policy included.</summary>
    public static HttpServer NoOutputCache(this HttpServer server)
    {
        ArgumentNullException.ThrowIfNull(server);

        LastMappedConventions.Apply<OutputCacheMetadata>(server, "CacheOutput", m => m.Disabled = true);
        return server;
    }
}
