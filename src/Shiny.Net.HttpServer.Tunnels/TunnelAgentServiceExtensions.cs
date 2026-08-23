using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Shiny.Net.HttpServer.Tunnels;

/// <summary>Registering a tunnel agent with a host.</summary>
public static class TunnelAgentServiceExtensions
{
    /// <summary>
    /// Runs a Cloudflare quick tunnel for the lifetime of the host.
    /// <code>
    /// builder.Services.AddHttpServer(o => o.Port = 8080);
    /// builder.Services.AddCloudflareTunnel();
    /// </code>
    /// </summary>
    public static ShinyHttpServerBuilder AddCloudflareTunnel(
        this ShinyHttpServerBuilder builder,
        Action<CloudflareTunnelOptions>? configure = null
    )
        => builder.AddTunnelAgent(sp =>
        {
            var options = new CloudflareTunnelOptions();
            configure?.Invoke(options);

            return new CloudflareTunnel(options, sp.GetService<ILoggerFactory>()?.CreateLogger<CloudflareTunnel>());
        });

    /// <summary>Runs an ngrok tunnel for the lifetime of the host.</summary>
    public static ShinyHttpServerBuilder AddNgrokTunnel(
        this ShinyHttpServerBuilder builder,
        Action<NgrokTunnelOptions>? configure = null
    )
        => builder.AddTunnelAgent(sp =>
        {
            var options = new NgrokTunnelOptions();
            configure?.Invoke(options);

            return new NgrokTunnel(options, sp.GetService<ILoggerFactory>()?.CreateLogger<NgrokTunnel>());
        });

    /// <summary>Runs Tailscale Funnel (or tailnet-only serve) for the lifetime of the host.</summary>
    public static ShinyHttpServerBuilder AddTailscaleFunnel(
        this ShinyHttpServerBuilder builder,
        Action<TailscaleFunnelOptions>? configure = null
    )
        => builder.AddTunnelAgent(sp =>
        {
            var options = new TailscaleFunnelOptions();
            configure?.Invoke(options);

            return new TailscaleFunnel(options, sp.GetService<ILoggerFactory>()?.CreateLogger<TailscaleFunnel>());
        });

    /// <summary>Registers any agent, and the hosted service that starts it once the server is up.</summary>
    public static ShinyHttpServerBuilder AddTunnelAgent(this ShinyHttpServerBuilder builder, Func<IServiceProvider, ITunnelAgent> factory)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(factory);

        builder.Services.TryAddSingleton(factory);
        builder.Services.AddHostedService<TunnelAgentHostedService>();

        return builder;
    }
}

/// <summary>
/// Starts the agent after the server is listening and kills it on shutdown.
/// <para>
/// Registered after the server's own hosted service, which is what guarantees the port exists
/// before the agent is told to forward to it — hosted services start in registration order.
/// </para>
/// </summary>
sealed class TunnelAgentHostedService(
    ITunnelAgent agent,
    HttpServer server,
    ILogger<TunnelAgentHostedService> logger
) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var url = await agent.StartAsync(server, cancellationToken).ConfigureAwait(false);

        logger.LogInformation("{Agent} is publishing {Local} at {Public}", agent.Name, server.ListenUrl, url);
    }

    public Task StopAsync(CancellationToken cancellationToken) => agent.StopAsync(cancellationToken);
}
