using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Shiny.Net.HttpServer.RateLimiting;

/// <summary>Registering rate limit policies.</summary>
public static class RateLimitServiceCollectionExtensions
{
    /// <summary>Registers the rate limiter's options and any named policies.</summary>
    public static IServiceCollection AddRateLimiter(
        this IServiceCollection services,
        Action<RateLimitOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(_ =>
        {
            var options = new RateLimitOptions();
            configure?.Invoke(options);
            return options;
        });

        return services;
    }
}

/// <summary>Putting the rate limiter in the pipeline.</summary>
public static class HttpServerRateLimitExtensions
{
    /// <summary>
    /// Applies <see cref="RateLimitOptions.GlobalPolicy"/> and whatever individual endpoints asked
    /// for with <see cref="RequireRateLimiting"/>.
    /// <para>
    /// Add it early. Its whole value is in the work it prevents, and everything registered before it
    /// is work a throttled request still costs.
    /// </para>
    /// </summary>
    public static HttpServer UseRateLimiter(this HttpServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return server.UseRateLimiter(policy: null);
    }

    /// <summary>Applies a named policy to every request that does not name its own.</summary>
    public static HttpServer UseRateLimiter(this HttpServer server, string policyName)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrEmpty(policyName);

        return server.UseRateLimiter(Options(server).GetPolicy(policyName));
    }

    /// <summary>
    /// Applies a policy built right here — no container, no registration.
    /// <code>
    /// app.UseRateLimiter(new FixedWindowRateLimitPolicy(100, TimeSpan.FromMinutes(1)));
    /// </code>
    /// </summary>
    public static HttpServer UseRateLimiter(this HttpServer server, RateLimitPolicy? policy)
    {
        ArgumentNullException.ThrowIfNull(server);

        var options = server.Services?.GetService<RateLimitOptions>()
            ?? (policy is not null
                ? new RateLimitOptions()
                : throw new InvalidOperationException(
                    "UseRateLimiter has no policy to apply. Either pass one inline — " +
                    "app.UseRateLimiter(new FixedWindowRateLimitPolicy(100, TimeSpan.FromMinutes(1))) — " +
                    "or register them with services.AddRateLimiter(o => ...)."
                ));

        var logger = server.Services
            ?.GetService<ILoggerFactory>()
            ?.CreateLogger<RateLimitMiddleware>();

        return server.Use(new RateLimitMiddleware(server.Router, options, policy, logger));
    }

    /// <summary>
    /// Applies a named policy to the most recently mapped route.
    /// <code>
    /// app.OnPost("/upload", Handler).RequireRateLimiting("uploads");
    /// </code>
    /// </summary>
    public static HttpServer RequireRateLimiting(this HttpServer server, string policyName)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrEmpty(policyName);

        LastEndpointMetadata(server).PolicyName = policyName;
        return server;
    }

    /// <summary>
    /// Exempts the most recently mapped route from rate limiting, including from the global policy —
    /// a health check that a monitor hits every second, say.
    /// </summary>
    public static HttpServer DisableRateLimiting(this HttpServer server)
    {
        ArgumentNullException.ThrowIfNull(server);

        LastEndpointMetadata(server).Disabled = true;
        return server;
    }

    static RateLimitOptions Options(HttpServer server)
        => server.Services?.GetService<RateLimitOptions>()
            ?? throw new InvalidOperationException(
                "Named rate limit policies live in RateLimitOptions. Register them with " +
                "services.AddRateLimiter(o => o.AddFixedWindow(\"name\", 100, TimeSpan.FromMinutes(1))), " +
                "or pass a policy inline to UseRateLimiter."
            );

    static RateLimitMetadata LastEndpointMetadata(HttpServer server)
    {
        if (server.Router.Endpoints.Count == 0)
            throw new InvalidOperationException(
                "RequireRateLimiting applies to the most recently mapped route, and no route has been mapped yet."
            );

        var endpoint = server.Router.Endpoints[^1];
        var metadata = endpoint.GetMetadata<RateLimitMetadata>();

        if (metadata is null)
        {
            metadata = new RateLimitMetadata();
            endpoint.WithMetadata(metadata);
        }

        return metadata;
    }
}
