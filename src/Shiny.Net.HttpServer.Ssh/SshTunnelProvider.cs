using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Renci.SshNet;
using Renci.SshNet.Common;
using Shiny.Net.HttpServer.Transports;
using Shiny.Net.HttpServer.Tunneling;

namespace Shiny.Net.HttpServer.Ssh;

/// <summary>
/// Publishes an embedded <see cref="HttpServer"/> through SSH remote port forwarding — <c>ssh -R</c>,
/// in library form.
/// <para>
/// The device opens an outbound SSH connection and asks the server to forward a port back down it,
/// so nothing ever connects *to* the device. Any SSH endpoint you can log in to will do: a VPS you
/// own, or a hosted tunnel like sish or localhost.run.
/// </para>
/// <code>
/// var provider = new SshTunnelProvider(new SshTunnelOptions
/// {
///     Host = "tunnel.example.com",
///     Username = "tunnel",
///     PrivateKeyPath = keyPath,
///     RemoteBindAddress = "0.0.0.0",
///     RemotePort = 8080,
///     HostKeyFingerprints = { "SHA256:…" }
/// });
///
/// await app.RunTunnelAsync(provider, cancellationToken: token);
/// </code>
/// </summary>
public sealed class SshTunnelProvider : ITunnelProvider
{
    readonly SshTunnelOptions options;
    readonly ILogger logger;

    // SSH remote forwarding delivers to a host:port, so the tunnel owns a private loopback socket
    // and hands what arrives on it to the server. The app never binds a port of its own, and only
    // processes on this machine can reach the loopback one.
    readonly SocketConnectionListener listener;
    readonly CancellationTokenSource stopping = new();

    SshClient? client;
    ForwardedPortRemote? forwardedPort;
    ShellStream? session;
    Task? supervisor;
    int disposed;

    public SshTunnelProvider(SshTunnelOptions options, ILogger<SshTunnelProvider>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        this.options = options;
        this.logger = logger ?? NullLogger<SshTunnelProvider>.Instance;
        this.listener = new SocketConnectionListener(
            new HttpServerOptions(),
            new HttpServerEndpoint(IPAddress.Loopback, options.LocalPort),
            this.logger
        );
    }

    public string Name => "ssh";

    /// <summary>
    /// The address the world uses. Taken from <see cref="SshTunnelOptions.PublicUrl"/>, or the
    /// banner when <see cref="SshTunnelOptions.CaptureUrlFromSession"/> is on, or derived from the
    /// forwarded port.
    /// </summary>
    public string? PublicUrl
    {
        get => this.publicUrl;
        private set
        {
            if (this.publicUrl == value)
                return;

            this.publicUrl = value;
            this.PublicUrlChanged?.Invoke(this, value);
        }
    }

    string? publicUrl;

    /// <summary>The port the SSH server bound, which is only known after connecting if you asked for 0.</summary>
    public int RemotePort { get; private set; }

    public string ListenDescription => this.PublicUrl ?? "ssh (not yet connected)";

    /// <summary>True while the SSH connection is up. Goes false between drop and reconnect.</summary>
    public bool IsConnected => this.client?.IsConnected ?? false;

    /// <summary>Raised when the SSH connection drops or comes back, for surfacing status in a UI.</summary>
    public event EventHandler<bool>? ConnectivityChanged;

    /// <summary>
    /// Raised when the public address changes.
    /// <para>
    /// Not a formality: a hosted tunnel assigns a fresh address on every reconnect, so an app that
    /// captured the URL once and showed it in a UI would be displaying a dead link after the first
    /// network change. Raised on a background thread — marshal before touching UI state.
    /// </para>
    /// </summary>
    public event EventHandler<string?>? PublicUrlChanged;

    public async ValueTask BindAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed != 0, this);

        if (this.client is not null)
            throw new InvalidOperationException("The tunnel is already connected.");

        await this.listener.BindAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await this.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await this.listener.UnbindAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        if (this.options.AutoReconnect)
            this.supervisor = Task.Run(() => this.SuperviseAsync(this.stopping.Token), CancellationToken.None);
    }

    public async ValueTask<IConnection?> AcceptAsync(CancellationToken cancellationToken = default)
    {
        var connection = await this.listener.AcceptAsync(cancellationToken).ConfigureAwait(false);

        // Marked tunnelled so the pipeline treats it as it should: RemoteEndPoint is the loopback
        // end of the forward, not the caller, and IsEncrypted describes a hop that is not the
        // caller's. Both are the tunnel's business to declare, not the socket's.
        return connection is null ? null : new ForwardedConnection(connection);
    }

    public async ValueTask UnbindAsync(CancellationToken cancellationToken = default)
    {
        if (!this.stopping.IsCancellationRequested)
            await this.stopping.CancelAsync().ConfigureAwait(false);

        await this.listener.UnbindAsync(cancellationToken).ConfigureAwait(false);

        if (this.supervisor is { } supervising)
        {
            try
            {
                await supervising.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
            }

            this.supervisor = null;
        }

        this.Disconnect();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) != 0)
            return;

        await this.UnbindAsync(CancellationToken.None).ConfigureAwait(false);
        await this.listener.DisposeAsync().ConfigureAwait(false);

        this.stopping.Dispose();
    }

    async Task ConnectAsync(CancellationToken cancellationToken)
    {
        var connectionInfo = this.options.CreateConnectionInfo();
        var sshClient = new SshClient(connectionInfo) { KeepAliveInterval = this.options.KeepAliveInterval };

        sshClient.HostKeyReceived += this.OnHostKeyReceived;
        sshClient.ErrorOccurred += this.OnErrorOccurred;

        try
        {
            await sshClient.ConnectAsync(cancellationToken).ConfigureAwait(false);

            var local = this.listener.BoundEndPoint
                ?? throw new InvalidOperationException("The loopback listener is not bound.");

            var port = new ForwardedPortRemote(
                this.options.RemoteBindAddress,
                (uint)this.options.RemotePort,
                local.Address.ToString(),
                (uint)local.Port
            );

            port.Exception += (_, e) => this.logger.LogWarning(e.Exception, "A forwarded connection failed");

            sshClient.AddForwardedPort(port);

            // Start() blocks until the server answers the forwarding request, and answers by
            // closing the channel if it refuses — which is how a missing GatewayPorts or a
            // permitopen restriction shows up.
            await Task.Run(port.Start, cancellationToken).ConfigureAwait(false);

            this.client = sshClient;
            this.forwardedPort = port;
            this.RemotePort = (int)port.BoundPort;

            this.PublicUrl = this.options.PublicUrl
                ?? (this.options.CaptureUrlFromSession
                    ? await this.CaptureUrlAsync(sshClient, cancellationToken).ConfigureAwait(false)
                    : null)
                ?? this.DeriveUrl();

            this.logger.LogInformation(
                "SSH tunnel up: {RemoteBind}:{RemotePort} -> {Local}, public at {Url}",
                this.options.RemoteBindAddress,
                this.RemotePort,
                local,
                this.PublicUrl
            );

            this.ConnectivityChanged?.Invoke(this, true);
        }
        catch
        {
            sshClient.HostKeyReceived -= this.OnHostKeyReceived;
            sshClient.ErrorOccurred -= this.OnErrorOccurred;
            sshClient.Dispose();

            throw;
        }
    }

    /// <summary>
    /// Watches the SSH connection and rebuilds it after a drop.
    /// <para>
    /// Not optional on a phone: changing from Wi-Fi to cellular kills the TCP connection under the
    /// tunnel, and without this the app would look fine while being unreachable.
    /// </para>
    /// </summary>
    async Task SuperviseAsync(CancellationToken cancellationToken)
    {
        var delay = this.options.ReconnectDelay;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);

                if (this.client?.IsConnected == true)
                {
                    delay = this.options.ReconnectDelay;
                    continue;
                }

                this.logger.LogWarning("The SSH tunnel dropped; reconnecting in {Delay}", delay);
                this.ConnectivityChanged?.Invoke(this, false);

                this.Disconnect();

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                await this.ConnectAsync(cancellationToken).ConfigureAwait(false);

                delay = this.options.ReconnectDelay;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // Back off rather than hammering a server that is down, or an auth failure that
                // will fail identically every time.
                delay = delay < this.options.MaxReconnectDelay
                    ? TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, this.options.MaxReconnectDelay.Ticks))
                    : this.options.MaxReconnectDelay;

                this.logger.LogWarning(ex, "Reconnecting the SSH tunnel failed; retrying in {Delay}", delay);
            }
        }
    }

    /// <summary>
    /// Reads the URL a hosted tunnel assigns.
    /// <para>
    /// sish, localhost.run and Serveo all print it on the session channel once forwarding is up,
    /// and there is no other way to learn it — the address is chosen by the server. The channel
    /// stays open afterwards, because some providers tear the forward down when it closes.
    /// </para>
    /// </summary>
    async Task<string?> CaptureUrlAsync(SshClient sshClient, CancellationToken cancellationToken)
    {
        try
        {
            var shell = sshClient.CreateShellStreamNoTerminal(bufferSize: 4096);
            this.session = shell;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(this.options.UrlCaptureTimeout);

            var buffer = new byte[1024];
            var text = new StringBuilder();

            while (!timeout.IsCancellationRequested)
            {
                var read = await shell.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                if (read == 0)
                    break;

                text.Append(Encoding.UTF8.GetString(buffer, 0, read));

                var match = this.options.UrlPattern.Match(text.ToString());
                if (match.Success)
                {
                    var url = match.Value.TrimEnd('.', ',', ';');
                    this.logger.LogInformation("The SSH endpoint assigned {Url}", url);

                    return url;
                }
            }

            this.logger.LogWarning(
                "No URL appeared in the session output within {Timeout}; falling back to the forwarded port",
                this.options.UrlCaptureTimeout
            );
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SshException)
        {
            this.logger.LogWarning(ex, "Could not read the assigned URL from the session");
        }

        return null;
    }

    /// <summary>
    /// Best effort when nothing better is known: the forwarded port on the SSH host.
    /// <para>
    /// Right for a direct forward, wrong as soon as a reverse proxy terminates TLS in front of it —
    /// which is why <see cref="SshTunnelOptions.PublicUrl"/> exists.
    /// </para>
    /// </summary>
    string DeriveUrl() => string.Create(
        CultureInfo.InvariantCulture,
        $"http://{this.options.Host}:{this.RemotePort}"
    );

    void Disconnect()
    {
        var port = Interlocked.Exchange(ref this.forwardedPort, null);
        var sshClient = Interlocked.Exchange(ref this.client, null);
        var shell = Interlocked.Exchange(ref this.session, null);

        try
        {
            shell?.Dispose();

            if (port is not null)
            {
                if (port.IsStarted)
                    port.Stop();

                port.Dispose();
            }

            if (sshClient is not null)
            {
                sshClient.HostKeyReceived -= this.OnHostKeyReceived;
                sshClient.ErrorOccurred -= this.OnErrorOccurred;
                sshClient.Dispose();
            }
        }
        catch (Exception ex)
        {
            this.logger.LogDebug(ex, "Tearing down the SSH connection threw; it is going away regardless");
        }
    }

    /// <summary>
    /// Verifies the server's host key.
    /// <para>
    /// SSH.NET accepts any key unless told otherwise. An unverified key means anything on the path
    /// can pose as the server, and a tunnel exists to cross networks you do not control — so an
    /// unpinned connection is refused unless the caller has explicitly opted out.
    /// </para>
    /// </summary>
    void OnHostKeyReceived(object? sender, HostKeyEventArgs e)
    {
        if (this.options.AcceptAnyHostKey)
        {
            this.logger.LogWarning(
                "Accepting an unverified host key ({Fingerprint}). Pin it in HostKeyFingerprints.",
                e.FingerPrintSHA256
            );

            e.CanTrust = true;
            return;
        }

        // ssh-keygen prints "SHA256:…"; the SDK gives the bare base64. Accept either, and compare
        // without the base64 padding some tools keep and others strip.
        e.CanTrust = this.options.HostKeyFingerprints.Any(pinned => Matches(pinned, e.FingerPrintSHA256));

        if (!e.CanTrust)
            this.logger.LogError(
                "Rejected the host key {Fingerprint}: it is not among the pinned fingerprints",
                e.FingerPrintSHA256
            );
    }

    internal static bool Matches(string pinned, string offered)
    {
        return Normalize(pinned).Equals(Normalize(offered), StringComparison.Ordinal);

        static string Normalize(string fingerprint)
        {
            var value = fingerprint.Trim();

            if (value.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase))
                value = value["SHA256:".Length..];

            return value.TrimEnd('=');
        }
    }

    void OnErrorOccurred(object? sender, ExceptionEventArgs e) =>
        this.logger.LogWarning(e.Exception, "The SSH connection reported an error");
}
