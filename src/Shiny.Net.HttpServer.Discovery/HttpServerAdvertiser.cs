using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shiny.Net.Discovery;

namespace Shiny.Net.HttpServer.Discovery;

/// <summary>What to advertise the server as on the local link.</summary>
public sealed class HttpServerAdvertisementOptions
{
    /// <summary>
    /// The DNS-SD service type. <c>_http._tcp</c> by default, which is what a browser, a printer
    /// dialog and every generic Bonjour tool already look for.
    /// <para>
    /// Use a private type — <c>_myapp._tcp</c> — when the point is for <em>your</em> app to find
    /// <em>your</em> other instance. Browsing <c>_http._tcp</c> on a busy network turns up printers,
    /// routers and NAS boxes, and telling yours apart from those means filtering on a TXT record
    /// anyway.
    /// </para>
    /// </summary>
    public string ServiceType { get; set; } = "_http._tcp";

    /// <summary>
    /// The human-readable instance name. Defaults to the machine name, which is what the user
    /// already calls this device.
    /// <para>
    /// Not guaranteed to survive: when the link already has a service with this name the platform
    /// renames it, so read the final one back from <see cref="IHttpServerAdvertiser.Publication"/>.
    /// </para>
    /// </summary>
    public string? InstanceName { get; set; }

    /// <summary>
    /// The path the service lives at, published as the conventional <c>path</c> TXT record so a
    /// generic browser opens the right URL.
    /// </summary>
    public string Path { get; set; } = "/";

    /// <summary>
    /// Extra TXT records. Keep the total small — RFC 6763 wants the whole record inside one packet.
    /// <para>
    /// This is where an app puts what a peer needs to decide whether it cares: an app id, a
    /// protocol version, a device role, a pairing state.
    /// </para>
    /// </summary>
    public IDictionary<string, string> TxtRecords { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Advertises a running server on the local link, and stops when it stops.</summary>
public interface IHttpServerAdvertiser : IAsyncDisposable
{
    /// <summary>The live publication, once there is one. Carries the name actually in use.</summary>
    IMdnsPublication? Publication { get; }

    /// <summary>Publishes the advertisement. Waits for the server to be listening if it is not yet.</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Sends a goodbye and stops advertising. Idempotent.</summary>
    Task StopAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The missing half of "a phone can host a server".
/// <para>
/// Binding a port on a device solves being reachable. It does not solve being <em>found</em>: the
/// address is assigned by whatever network the device joined, it changes when the device moves, and
/// the only ways out of that are a QR code, typing an IP, or this. mDNS is what every printer,
/// speaker and NAS on the link already uses, it needs no server, no account and no internet, and it
/// is the one mechanism a peer app can rely on being able to use.
/// </para>
/// <para>
/// The advertisement follows the server: published when it starts listening, withdrawn when it
/// stops, and re-published on the new port if the server is restarted onto one. The addresses are
/// the responder's business — on Apple and Android the OS answers with whatever the device
/// currently has, which is exactly the part that would otherwise go stale.
/// </para>
/// </summary>
public sealed class HttpServerAdvertiser : IHttpServerAdvertiser
{
    readonly IMdnsManager mdns;
    readonly HttpServer server;
    readonly HttpServerAdvertisementOptions options;
    readonly ILogger logger;
    readonly SemaphoreSlim gate = new(1, 1);

    int advertisedPort;
    bool started;

    public HttpServerAdvertiser(
        IMdnsManager mdns,
        HttpServer server,
        HttpServerAdvertisementOptions options,
        ILogger<HttpServerAdvertiser>? logger = null
    )
    {
        ArgumentNullException.ThrowIfNull(mdns);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(options);

        this.mdns = mdns;
        this.server = server;
        this.options = options;
        this.logger = logger ?? NullLogger<HttpServerAdvertiser>.Instance;
    }

    public IMdnsPublication? Publication { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (this.started)
            return;

        this.started = true;

        // Both hooks matter and for different reasons: StateChanged covers a server that is toggled
        // by a button or restarted onto a new port, and NetworkAddressesChanged covers the device
        // moving networks, which is when a stale advertisement is worst.
        this.server.StateChanged += this.OnStateChanged;
        this.server.NetworkAddressesChanged += this.OnAddressesChanged;

        if (this.server.IsRunning)
            await this.PublishAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        this.started = false;
        this.server.StateChanged -= this.OnStateChanged;
        this.server.NetworkAddressesChanged -= this.OnAddressesChanged;

        await this.WithdrawAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await this.StopAsync(CancellationToken.None).ConfigureAwait(false);
        this.gate.Dispose();
    }

    void OnStateChanged(object? sender, HttpServerState state) => _ = Task.Run(async () =>
    {
        try
        {
            if (state == HttpServerState.Running)
                await this.PublishAsync(CancellationToken.None).ConfigureAwait(false);
            else if (state == HttpServerState.Stopped)
                await this.WithdrawAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Raised from the server's own lifecycle thread, so nothing is awaiting this and an
            // escaping exception would take the process with it.
            this.logger.LogError(ex, "Failed to update the mDNS advertisement");
        }
    });

    void OnAddressesChanged(object? sender, IReadOnlyList<System.Net.IPAddress> addresses) => _ = Task.Run(async () =>
    {
        try
        {
            // Re-announced rather than left alone: a device that just joined a network needs its
            // records seen by peers that were already there, and a fresh announcement is how that
            // happens. The name is preserved by the responder, so peers see an update, not a new
            // service.
            if (this.server.IsRunning)
                await this.PublishAsync(CancellationToken.None, force: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "Failed to re-announce the mDNS advertisement after a network change");
        }
    });

    async Task PublishAsync(CancellationToken cancellationToken, bool force = false)
    {
        var port = PortOf(this.server);
        if (port is null)
            return;

        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (this.Publication is not null && !force && this.advertisedPort == port.Value)
                return;

            await this.WithdrawCoreAsync().ConfigureAwait(false);

            var registration = new MdnsServiceRegistration
            {
                InstanceName = this.options.InstanceName ?? Environment.MachineName,
                ServiceType = this.options.ServiceType,
                Port = port.Value,
                TxtRecords = this.BuildTxtRecords()
            };

            this.Publication = await this.mdns.Publish(registration, cancellationToken).ConfigureAwait(false);
            this.advertisedPort = port.Value;

            this.logger.LogInformation(
                "Advertising {Instance} as {ServiceType} on port {Port}",
                this.Publication.InstanceName,
                this.Publication.ServiceType,
                this.Publication.Port
            );
        }
        finally
        {
            this.gate.Release();
        }
    }

    async Task WithdrawAsync()
    {
        await this.gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await this.WithdrawCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            this.gate.Release();
        }
    }

    async Task WithdrawCoreAsync()
    {
        if (this.Publication is not { } publication)
            return;

        this.Publication = null;
        this.advertisedPort = 0;

        try
        {
            // Disposing sends the goodbye packet, which is what stops peers holding a record for a
            // service that has gone. Without it they wait for the TTL to expire.
            await publication.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.logger.LogDebug(ex, "The mDNS publication had already gone when it was withdrawn");
        }
    }

    IReadOnlyDictionary<string, string> BuildTxtRecords()
    {
        var records = new Dictionary<string, string>(this.options.TxtRecords, StringComparer.OrdinalIgnoreCase);

        // The conventional record for _http._tcp, and the one a generic browser uses to build a URL.
        if (!records.ContainsKey("path"))
            records["path"] = this.options.Path;

        if (!records.ContainsKey("scheme"))
            records["scheme"] = SchemeOf(this.server);

        return records;
    }

    static string SchemeOf(HttpServer server)
    {
        foreach (var endpoint in server.Options.Endpoints)
        {
            if (endpoint.Https is not null)
                return "https";
        }

        return server.Options.Https is not null ? "https" : "http";
    }

    /// <summary>The port actually bound, read back from the listen URL so an OS-assigned one is right.</summary>
    static int? PortOf(HttpServer server)
        => server.ListenUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.Port > 0
            ? parsed.Port
            : null;
}
