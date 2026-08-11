using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Shiny.Net.HttpServer;

/// <summary>
/// Registration for apps that already own a container — a generic host, a MAUI app, a Shiny host.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the server as a singleton and starts it with the host.
    /// <code>
    /// builder.Services.AddHttpServer(o => o.Port = 8080, server =>
    /// {
    ///     server.MapGet("/ping", ctx => ctx.Response.WriteTextAsync("pong"));
    /// });
    /// </code>
    /// <paramref name="configureServer"/> runs once, immediately before the server starts, so it
    /// can resolve anything in the container while registering routes.
    /// </summary>
    public static IServiceCollection AddHttpServer(
        this IServiceCollection services,
        Action<HttpServerOptions>? configureOptions = null,
        Action<HttpServer>? configureServer = null,
        bool autoStart = true
    )
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(sp =>
        {
            var options = new HttpServerOptions();
            configureOptions?.Invoke(options);

            var server = new HttpServer(options, sp, sp.GetService<ILoggerFactory>());
            configureServer?.Invoke(server);

            return server;
        });

        // With autoStart off the server is still registered and fully configured, just not
        // listening — which is what an app with a "share over Wi-Fi" toggle wants. Resolve the
        // HttpServer and call StartAsync when the user says so.
        if (autoStart)
            services.AddHostedService<HttpServerHostedService>();

        return services;
    }
}

/// <summary>
/// Runs the server for the lifetime of the host. Shutdown is graceful: the listener unbinds first,
/// then in-flight requests are given until the host's shutdown token fires to finish.
/// </summary>
sealed class HttpServerHostedService(HttpServer server) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => server.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => server.StopAsync(cancellationToken);
}
