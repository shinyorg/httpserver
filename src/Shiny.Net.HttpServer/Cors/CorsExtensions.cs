using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Shiny.Net.HttpServer.Cors;

/// <summary>Registering CORS policies.</summary>
public static class CorsServiceCollectionExtensions
{
    /// <summary>Registers the CORS options and any named policies.</summary>
    public static IServiceCollection AddCors(this IServiceCollection services, Action<CorsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(_ =>
        {
            var options = new CorsOptions();
            configure?.Invoke(options);
            return options;
        });

        return services;
    }
}

/// <summary>Putting CORS in the pipeline.</summary>
public static class HttpServerCorsExtensions
{
    /// <summary>
    /// Applies <see cref="CorsOptions.DefaultPolicy"/> to cross-origin requests, and whatever
    /// individual endpoints asked for with <see cref="RequireCors"/>.
    /// <para>
    /// Add it early — before authentication and routing — so a preflight is answered before anything
    /// else can reject it. A browser sends a preflight without credentials, so an authentication
    /// middleware ahead of this one would see an anonymous request and 401 it, and the real request
    /// would never leave the browser.
    /// </para>
    /// </summary>
    public static HttpServer UseCors(this HttpServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return server.UseCors(policy: null);
    }

    /// <summary>Applies a named policy to every cross-origin request that does not name its own.</summary>
    public static HttpServer UseCors(this HttpServer server, string policyName)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrEmpty(policyName);

        return server.UseCors(Options(server).GetPolicy(policyName));
    }

    /// <summary>
    /// Applies a policy declared right here — no container, no registration.
    /// <code>
    /// app.UseCors(p => p.WithOrigins("https://app.example.com").AllowAnyHeader().AllowAnyMethod());
    /// </code>
    /// </summary>
    public static HttpServer UseCors(this HttpServer server, Action<CorsPolicyBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(configure);

        return server.UseCors(CorsPolicy.Create(configure));
    }

    /// <summary>Applies an already-built policy.</summary>
    public static HttpServer UseCors(this HttpServer server, CorsPolicy? policy)
    {
        ArgumentNullException.ThrowIfNull(server);

        var options = server.Services?.GetService<CorsOptions>()
            ?? (policy is not null
                ? new CorsOptions()
                : throw new InvalidOperationException(
                    "UseCors has no policy to apply. Either pass one inline — " +
                    "app.UseCors(p => p.WithOrigins(\"https://example.com\")) — or register them " +
                    "with services.AddCors(o => ...)."
                ));

        return server.Use(new CorsMiddleware(server.Router, options, policy));
    }

    /// <summary>
    /// Applies a named CORS policy to the most recently mapped route.
    /// <code>
    /// app.MapGet("/status", Handler).RequireCors("public");
    /// </code>
    /// </summary>
    public static HttpServer RequireCors(this HttpServer server, string policyName)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrEmpty(policyName);

        LastEndpointMetadata(server).PolicyName = policyName;
        return server;
    }

    /// <summary>Exempts the most recently mapped route from CORS, including from the default policy.</summary>
    public static HttpServer DisableCors(this HttpServer server)
    {
        ArgumentNullException.ThrowIfNull(server);

        LastEndpointMetadata(server).Disabled = true;
        return server;
    }

    static CorsOptions Options(HttpServer server)
        => server.Services?.GetService<CorsOptions>()
            ?? throw new InvalidOperationException(
                "Named CORS policies live in CorsOptions. Register them with " +
                "services.AddCors(o => o.AddPolicy(\"name\", p => ...)), or pass a policy inline " +
                "with app.UseCors(p => ...)."
            );

    static CorsMetadata LastEndpointMetadata(HttpServer server)
    {
        if (server.Router.Endpoints.Count == 0)
            throw new InvalidOperationException(
                "RequireCors applies to the most recently mapped route, and no route has been mapped yet."
            );

        var endpoint = server.Router.Endpoints[^1];
        var metadata = endpoint.GetMetadata<CorsMetadata>();

        if (metadata is null)
        {
            metadata = new CorsMetadata();
            endpoint.WithMetadata(metadata);
        }

        return metadata;
    }
}
