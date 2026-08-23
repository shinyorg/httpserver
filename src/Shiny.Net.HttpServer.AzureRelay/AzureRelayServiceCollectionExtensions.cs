using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shiny.Net.HttpServer.Tunneling;

namespace Shiny.Net.HttpServer.AzureRelay;

/// <summary>Registration for apps that already own a container.</summary>
public static class AzureRelayServiceCollectionExtensions
{
    /// <summary>
    /// Registers an Azure Relay tunnel for the registered <see cref="HttpServer"/>.
    /// <code>
    /// builder.Services.AddHttpServer(autoStart: false, configureServer: server =>
    /// {
    ///     server.MapGet("/ping", ctx => ctx.Response.WriteTextAsync("pong"));
    /// });
    ///
    /// builder.Services.AddAzureRelayTunnel(o =>
    /// {
    ///     o.ConnectionString = configuration["Relay:ConnectionString"];
    ///     o.HybridConnectionName = "my-device";
    /// });
    /// </code>
    /// </summary>
    /// <param name="autoStart">
    /// Start the tunnel with the host. Off, the tunnel is registered but idle — resolve
    /// <see cref="AzureRelayTunnel"/> and call <see cref="AzureRelayTunnel.StartAsync"/> when the
    /// user flips the switch, which is usually what an app with a "remote access" toggle wants.
    /// </param>
    public static ShinyHttpServerBuilder AddAzureRelayTunnel(
        this ShinyHttpServerBuilder builder,
        Action<AzureRelayOptions> configureOptions,
        bool autoStart = true
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configureOptions);

        builder.Services.TryAddSingleton(sp =>
        {
            var options = new AzureRelayOptions();
            configureOptions(options);

            return new AzureRelayTunnel(
                sp.GetRequiredService<HttpServer>(),
                options,
                sp.GetService<ILoggerFactory>()
            );
        });

        if (autoStart)
            builder.Services.AddHostedService<AzureRelayHostedService>();

        return builder;
    }
}

/// <summary>
/// Starts and stops an Azure Relay tunnel on demand.
/// <para>
/// Separate from the server's own start/stop: an app can be serving on the local network the whole
/// time and expose itself publicly only while the user has asked for it.
/// </para>
/// </summary>
public sealed class AzureRelayTunnel : IAsyncDisposable
{
    readonly HttpServer server;
    readonly AzureRelayOptions options;
    readonly ILoggerFactory? loggerFactory;
    readonly ILogger logger;
    readonly SemaphoreSlim gate = new(1, 1);

    AzureRelayTunnelProvider? provider;
    CancellationTokenSource? running;
    Task? runner;

    public AzureRelayTunnel(HttpServer server, AzureRelayOptions options, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(options);

        this.server = server;
        this.options = options;
        this.loggerFactory = loggerFactory;
        this.logger = loggerFactory?.CreateLogger<AzureRelayTunnel>()
            ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<AzureRelayTunnel>.Instance;
    }

    /// <summary>The public address, once the tunnel is open.</summary>
    public string? PublicUrl => this.provider?.PublicUrl;

    /// <summary>True while the relay reports the listener as connected.</summary>
    public bool IsOnline => this.provider?.IsOnline ?? false;

    /// <summary>Opens the tunnel. Returns once it is bound, with traffic served in the background.</summary>
    public async Task<string?> StartAsync(CancellationToken cancellationToken = default)
    {
        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (this.runner is not null)
                return this.PublicUrl;

            var tunnelProvider = new AzureRelayTunnelProvider(
                this.options,
                this.loggerFactory?.CreateLogger<AzureRelayTunnelProvider>()
            );

            // Bound here rather than inside RunTunnelAsync so a bad connection string or a rejected
            // token surfaces to the caller, instead of faulting a background task nobody awaits.
            await tunnelProvider.BindAsync(cancellationToken).ConfigureAwait(false);

            var cts = new CancellationTokenSource();
            this.provider = tunnelProvider;
            this.running = cts;
            this.runner = Task.Run(() => this.ServeAsync(tunnelProvider, cts.Token), CancellationToken.None);

            return tunnelProvider.PublicUrl;
        }
        finally
        {
            this.gate.Release();
        }
    }

    /// <summary>Closes the tunnel. The server itself keeps running.</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (this.running is not { } cts)
                return;

            await cts.CancelAsync().ConfigureAwait(false);

            if (this.provider is { } tunnelProvider)
                await tunnelProvider.DisposeAsync().ConfigureAwait(false);

            if (this.runner is { } task)
            {
                try
                {
                    await task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
                {
                    this.logger.LogWarning("The Azure Relay tunnel did not stop cleanly");
                }
            }

            cts.Dispose();
            this.running = null;
            this.runner = null;
            this.provider = null;
        }
        finally
        {
            this.gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await this.StopAsync(CancellationToken.None).ConfigureAwait(false);
        this.gate.Dispose();
    }

    async Task ServeAsync(AzureRelayTunnelProvider tunnelProvider, CancellationToken cancellationToken)
    {
        try
        {
            // BindAsync already ran; the provider rejects a second bind, so the accept loop is
            // driven directly rather than through RunTunnelAsync.
            while (!cancellationToken.IsCancellationRequested)
            {
                var connection = await tunnelProvider.AcceptAsync(cancellationToken).ConfigureAwait(false);
                if (connection is null)
                    break;

                _ = Task.Run(() => this.server.ServeAsync(connection, cancellationToken), CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "The Azure Relay tunnel stopped unexpectedly");
        }
    }
}

sealed class AzureRelayHostedService(AzureRelayTunnel tunnel) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => tunnel.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => tunnel.StopAsync(cancellationToken);
}
