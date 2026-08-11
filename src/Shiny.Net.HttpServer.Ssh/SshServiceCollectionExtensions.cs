using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Shiny.Net.HttpServer.Ssh;

/// <summary>Registration for apps that already own a container.</summary>
public static class SshServiceCollectionExtensions
{
    /// <summary>
    /// Registers an SSH tunnel for the registered <see cref="HttpServer"/>.
    /// <code>
    /// builder.Services.AddHttpServer(autoStart: false, configureServer: server =>
    /// {
    ///     server.MapGet("/ping", ctx => ctx.Response.WriteTextAsync("pong"));
    /// });
    ///
    /// builder.Services.AddSshTunnel(o =>
    /// {
    ///     o.Host = "tunnel.example.com";
    ///     o.Username = "tunnel";
    ///     o.PrivateKeyPath = keyPath;
    ///     o.RemoteBindAddress = "0.0.0.0";
    ///     o.RemotePort = 8080;
    ///     o.HostKeyFingerprints.Add("SHA256:…");
    /// });
    /// </code>
    /// </summary>
    /// <param name="autoStart">
    /// Start the tunnel with the host. Off, the tunnel is registered but idle — resolve
    /// <see cref="SshTunnel"/> and call <see cref="SshTunnel.StartAsync"/> when the user asks for it.
    /// </param>
    public static IServiceCollection AddSshTunnel(
        this IServiceCollection services,
        Action<SshTunnelOptions> configureOptions,
        bool autoStart = true
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureOptions);

        services.TryAddSingleton(sp =>
        {
            var options = new SshTunnelOptions();
            configureOptions(options);

            return new SshTunnel(sp.GetRequiredService<HttpServer>(), options, sp.GetService<ILoggerFactory>());
        });

        if (autoStart)
            services.AddHostedService<SshTunnelHostedService>();

        return services;
    }
}

/// <summary>
/// Starts and stops an SSH tunnel on demand.
/// <para>
/// Separate from the server's own start/stop: an app can be serving on the local network the whole
/// time and expose itself publicly only while the user has asked for it.
/// </para>
/// </summary>
public sealed class SshTunnel : IAsyncDisposable
{
    readonly HttpServer server;
    readonly SshTunnelOptions options;
    readonly ILoggerFactory? loggerFactory;
    readonly ILogger logger;
    readonly SemaphoreSlim gate = new(1, 1);

    SshTunnelProvider? provider;
    CancellationTokenSource? running;
    Task? runner;

    public SshTunnel(HttpServer server, SshTunnelOptions options, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(options);

        this.server = server;
        this.options = options;
        this.loggerFactory = loggerFactory;
        this.logger = loggerFactory?.CreateLogger<SshTunnel>() ?? NullLogger<SshTunnel>.Instance;
    }

    /// <summary>The public address, once the tunnel is up.</summary>
    public string? PublicUrl => this.provider?.PublicUrl;

    /// <summary>True while the SSH connection is up. Goes false between drop and reconnect.</summary>
    public bool IsConnected => this.provider?.IsConnected ?? false;

    /// <summary>Opens the tunnel. Returns once it is up, with traffic served in the background.</summary>
    public async Task<string?> StartAsync(CancellationToken cancellationToken = default)
    {
        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (this.runner is not null)
                return this.PublicUrl;

            var tunnelProvider = new SshTunnelProvider(
                this.options,
                this.loggerFactory?.CreateLogger<SshTunnelProvider>()
            );

            // Bound here rather than in the background loop so a bad key or a refused forward
            // surfaces to the caller, instead of faulting a task nobody awaits.
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
                    this.logger.LogWarning("The SSH tunnel did not stop cleanly");
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

    async Task ServeAsync(SshTunnelProvider tunnelProvider, CancellationToken cancellationToken)
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
            this.logger.LogError(ex, "The SSH tunnel stopped unexpectedly");
        }
    }
}

sealed class SshTunnelHostedService(SshTunnel tunnel) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => tunnel.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => tunnel.StopAsync(cancellationToken);
}
