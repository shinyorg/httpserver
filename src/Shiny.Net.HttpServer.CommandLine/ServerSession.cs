using System.ComponentModel;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shiny.Net.HttpServer.CommandLine.Monitoring;
using Shiny.Net.HttpServer.Ssh;

namespace Shiny.Net.HttpServer.CommandLine;


public enum SessionState
{
    Stopped,
    Starting,
    Running,

    /// <summary>Could not listen. <see cref="ServerSession.Error"/> says why.</summary>
    Failed
}


/// <summary>
/// The server the dashboard is showing, and the tunnel in front of it - kept running, and rebuilt
/// when the settings change.
/// </summary>
/// <remarks>
/// <para>
/// A server's pipeline is composed once, so a change to anything in it - the port, TLS, who may log
/// in, what they may do - means a new server. The tunnel is the exception: it hands connections to
/// whichever server it was made for and is independent of the listener, so opening, closing or
/// re-keying it leaves the server, and every transfer running on it, alone.
/// </para>
/// <para>
/// A change that will not start is rolled back to the settings that were running, so a typo in the
/// port leaves the directory served rather than leaving nothing.
/// </para>
/// </remarks>
public sealed class ServerSession(TrafficMonitor monitor, Action<ILoggingBuilder> logging) : IAsyncDisposable
{
    /// <summary>How long a rebuild waits for in-flight requests before cutting them off.</summary>
    static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(3);

    readonly SemaphoreSlim gate = new(1, 1);

    HttpServer? server;
    QuickTunnel? tunnel;

    // One certificate for the life of the session. A client that has already clicked through the
    // warning should not have to again because the port changed.
    X509Certificate2? certificate;

    public ServeSettings? Settings { get; private set; }
    public SessionState State { get; private set; }
    public string? Error { get; private set; }

    /// <summary>The tunnel's own address, without the mount point - see <see cref="ServerUrls.Tunnel"/>.</summary>
    public string? TunnelUrl => this.tunnel?.PublicUrl;

    public QuickTunnelState TunnelState => this.tunnel?.State ?? QuickTunnelState.Stopped;
    public string? TunnelError { get; private set; }

    /// <summary>Whether the last apply built a new server, rather than only moving the tunnel.</summary>
    public bool LastApplyRebuilt { get; private set; }

    /// <summary>Raised when a connected tunnel comes back on a different address, which kills the old one.</summary>
    public event Action<string>? TunnelAddressChanged;


    /// <summary>
    /// Starts with <paramref name="next"/>, or moves a running session over to it.
    /// </summary>
    /// <returns>Why it could not, or null. A tunnel that will not open is not a failure: the
    /// directory is still served on this network, and <see cref="TunnelError"/> says what went wrong.</returns>
    public async Task<string?> ApplyAsync(ServeSettings next, CancellationToken cancellationToken)
    {
        if (next.Validate() is { } invalid)
            return invalid;

        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previous = this.Settings;
            var rebuild = previous is null || this.server is null || this.State != SessionState.Running || next.NeedsRebuildFrom(previous);
            this.LastApplyRebuilt = rebuild;

            if (rebuild)
            {
                await this.CloseTunnelAsync().ConfigureAwait(false);

                var error = await this.StartServerAsync(next, cancellationToken).ConfigureAwait(false);
                if (error is not null)
                {
                    // Put back what was working, so a bad port does not take the directory offline.
                    if (previous is not null && await this.StartServerAsync(previous, cancellationToken).ConfigureAwait(false) is null)
                    {
                        await this.SyncTunnelAsync(previous, cancellationToken).ConfigureAwait(false);
                        return $"{error} The previous settings are still running.";
                    }
                    return error;
                }
            }

            this.Settings = next;

            await this.SyncTunnelAsync(next, cancellationToken).ConfigureAwait(false);
            return null;
        }
        finally
        {
            this.gate.Release();
        }
    }


    /// <summary>Tears the current server down and builds one from <paramref name="settings"/>.</summary>
    async Task<string?> StartServerAsync(ServeSettings settings, CancellationToken cancellationToken)
    {
        await this.StopServerAsync().ConfigureAwait(false);

        this.State = SessionState.Starting;
        this.Error = null;

        if (settings.UseHttps)
            this.certificate ??= ServerFactory.CreateCertificate();

        var next = ServerFactory.Build(settings, settings.UseHttps ? this.certificate : null, logging, monitor);
        try
        {
            await next.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or IOException or InvalidOperationException)
        {
            await next.DisposeAsync().ConfigureAwait(false);

            this.State = SessionState.Failed;
            this.Error = $"Cannot listen on {settings.Address}:{settings.Port} - {ex.Message}";
            return this.Error;
        }

        this.server = next;
        this.Settings = settings;
        this.State = SessionState.Running;
        return null;
    }


    async Task StopServerAsync()
    {
        if (this.server is not { } current)
            return;

        this.server = null;

        using var drain = new CancellationTokenSource(DrainTimeout);
        try
        {
            await current.StopAsync(drain.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        await current.DisposeAsync().ConfigureAwait(false);

        this.State = SessionState.Stopped;
    }


    /// <summary>Opens, closes or re-keys the tunnel so it matches <paramref name="settings"/>.</summary>
    async Task SyncTunnelAsync(ServeSettings settings, CancellationToken cancellationToken)
    {
        if (this.server is null)
            return;

        if (!settings.UseTunnel)
        {
            await this.CloseTunnelAsync().ConfigureAwait(false);
            return;
        }

        // The token is baked into the connection, so a new one is a new tunnel.
        if (this.tunnel is not null && this.tunnelToken == settings.TunnelToken)
            return;

        await this.CloseTunnelAsync().ConfigureAwait(false);

        var opened = QuickTunnel.For(
            this.server,
            QuickTunnelHost.Pinggy,
            settings.TunnelToken,
            loggerFactory: this.server.Services?.GetService<ILoggerFactory>()
        );
        opened.PropertyChanged += this.OnTunnelChanged;

        this.tunnel = opened;
        this.tunnelToken = settings.TunnelToken;
        this.TunnelError = null;

        try
        {
            var url = await opened.StartAsync(cancellationToken).ConfigureAwait(false);
            if (url is not { Length: > 0 })
                this.TunnelError = opened.LastError ?? "The tunnel connected but never reported an address.";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            this.TunnelError = $"The tunnel could not be opened - {ex.Message}";
        }
    }

    string? tunnelToken;
    string? lastTunnelUrl;


    async Task CloseTunnelAsync()
    {
        if (this.tunnel is not { } current)
            return;

        this.tunnel = null;
        this.tunnelToken = null;
        this.lastTunnelUrl = null;
        current.PropertyChanged -= this.OnTunnelChanged;

        await current.DisposeAsync().ConfigureAwait(false);
    }


    void OnTunnelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(QuickTunnel.PublicUrl) && sender is QuickTunnel { PublicUrl: { Length: > 0 } url })
        {
            // The first address is not news; a different one after it is, because the old one is dead.
            var previous = this.lastTunnelUrl;
            this.lastTunnelUrl = url;

            if (previous is not null && previous != url)
                this.TunnelAddressChanged?.Invoke(url);
        }
    }


    public async ValueTask DisposeAsync()
    {
        await this.gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await this.CloseTunnelAsync().ConfigureAwait(false);
            await this.StopServerAsync().ConfigureAwait(false);
            this.certificate?.Dispose();
        }
        finally
        {
            this.gate.Release();
        }
    }
}
