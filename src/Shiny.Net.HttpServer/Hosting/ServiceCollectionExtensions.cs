using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Shiny.Net.HttpServer;

/// <summary>
/// Registration for an app that already owns a container — a generic host, a MAUI app, a Shiny host.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the server and everything configured on the builder.
    /// <code>
    /// builder.Services.AddShinyHttpServer(server =>
    /// {
    ///     server.Options.Port = 8080;
    ///     server.AddAuthentication().AddBasic(o => o.AddUser("ada", pw));
    ///     server.AddHealthChecks().AddServerCheck();
    ///     server.Configure(app =>
    ///     {
    ///         app.UseAuthentication();
    ///         app.MapMyAppEndpoints();
    ///     });
    /// });
    /// </code>
    /// <para>
    /// The callback runs immediately — it is registration, not deferred work — while the routes and
    /// middleware in <see cref="ShinyHttpServerBuilder.Configure(Action{HttpServer})"/> are applied
    /// when the server is first resolved, so they can take dependencies out of the container.
    /// </para>
    /// </summary>
    /// <param name="services">The app's services.</param>
    /// <param name="configure">Options, registrations, routes and middleware.</param>
    /// <param name="autoStart">
    /// Starts the server with the host. Turn it off for an app with a "share over Wi-Fi" toggle: the
    /// server is still registered and fully configured, just not listening until something calls
    /// <see cref="HttpServer.StartAsync"/>.
    /// </param>
    public static IServiceCollection AddShinyHttpServer(
        this IServiceCollection services,
        Action<ShinyHttpServerBuilder>? configure = null,
        bool autoStart = true
    )
    {
        ArgumentNullException.ThrowIfNull(services);

        var builder = new ShinyHttpServerBuilder(services);
        configure?.Invoke(builder);

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
