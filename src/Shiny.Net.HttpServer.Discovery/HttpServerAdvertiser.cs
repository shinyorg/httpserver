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

    /// <summary>
    /// How many times publishing the advertisement is attempted before it is given up on. Three by
    /// default.
    /// <para>
    /// The moments this runs are the moments a responder is least likely to answer: the server has
    /// just bound after a network change, and the platform's mDNS stack is coming back at its own
    /// pace. One refused registration otherwise leaves a server that is running and unfindable —
    /// which, from the user's side of it, is a server that is not there.
    /// </para>
    /// </summary>
    public int PublishAttempts { get; set; } = 3;

    /// <summary>
    /// How long to wait before the second publish attempt. One second by default, doubling up to
    /// <see cref="MaxPublishRetryDelay"/>.
    /// </summary>
    public TimeSpan PublishRetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>The ceiling the doubling backoff stops at. Fifteen seconds by default.</summary>
    public TimeSpan MaxPublishRetryDelay { get; set; } = TimeSpan.FromSeconds(15);
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
/// <para>
/// It follows the <em>reason</em> too. A stop that is one half of a restart or a rebind leaves the
/// record standing, because a service that blinks out and back is worse for the peers watching it
/// than one that never moved; a stop that is a stop takes the record down with a goodbye packet. And
/// a publication the responder refuses is retried rather than dropped, because the failure mode it
/// leaves behind — a server running perfectly and findable by nobody — looks like success from
/// inside the app.
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

    // Stamped on the thread that caused the change and compared again once the work reaches the
    // front of the gate. See OnStateChanged for what goes wrong without it.
    long sequence;
    long applied;

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

        // Both hooks matter and for different reasons: StateTransitioned covers a server that is
        // toggled by a button or restarted onto a new port, and NetworkAddressesChanged covers the
        // device moving networks, which is when a stale advertisement is worst.
        //
        // StateTransitioned rather than StateChanged because the reason is load-bearing here, not
        // decoration - see OnStateChanged.
        this.server.StateTransitioned += this.OnStateChanged;
        this.server.NetworkAddressesChanged += this.OnAddressesChanged;

        if (this.server.IsRunning)
            await this.PublishAsync(this.Next(), cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        this.started = false;
        this.server.StateTransitioned -= this.OnStateChanged;
        this.server.NetworkAddressesChanged -= this.OnAddressesChanged;

        // Stamped like everything else: an explicit stop must not be undone by a publication that
        // was already in flight when it was called.
        await this.WithdrawAsync(this.Next()).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await this.StopAsync(CancellationToken.None).ConfigureAwait(false);
        this.gate.Dispose();
    }

    void OnStateChanged(object? sender, HttpServerStateChange change)
    {
        if (change.State is not (HttpServerState.Running or HttpServerState.Stopped))
            return;

        // Stamped here, on the server's own lifecycle thread, where the order is still the order the
        // server actually moved in. The mDNS work itself has to come off that thread — it talks to a
        // platform responder, and it is slow — and the moment it does, two transitions milliseconds
        // apart are free to land in either order. A withdrawal applied on top of the publication
        // that followed it leaves a server running and unfindable, with nothing coming to correct it.
        var stamp = this.Next();

        _ = Task.Run(() => this.ApplyStateAsync(stamp, change));
    }

    internal async Task ApplyStateAsync(long stamp, HttpServerStateChange change)
    {
        // A stop that is one half of a restart or a rebind is not a stop, and withdrawing on it is
        // wrong twice over: peers get a goodbye and then a fresh announcement for a service that
        // never actually went away, and anything holding a resolved address drops it and has to find
        // the device again — over a gap that is usually milliseconds. Held instead, and the Running
        // that follows either finds the same port and does nothing at all, or finds a new one and
        // moves the record onto it. If the start half never lands, the core reports Stopped a second
        // time with BindFailed, and that one is a real stop and does withdraw.
        //
        // ListenerFaulted is deliberately not in this list. Recovering from it is optional and it is
        // not instant, and a listener that died underneath the server is exactly the outage the
        // peers watching it should be told about.
        if (change.State == HttpServerState.Stopped &&
            change.Reason is HttpServerStateReason.Restarting or HttpServerStateReason.NetworkChanged)
        {
            this.logger.LogDebug("Holding the mDNS advertisement through a {Reason} stop", change.Reason);
            return;
        }

        try
        {
            if (change.State == HttpServerState.Running)
                await this.PublishAsync(stamp, CancellationToken.None).ConfigureAwait(false);
            else
                await this.WithdrawAsync(stamp).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Nothing awaits this task, so an escaping exception is one reported nowhere. Error
            // rather than warning: an advertisement that did not update is an app that cannot be
            // found, and it will go on looking healthy from the inside.
            this.logger.LogError(ex, "Failed to update the mDNS advertisement for the {State} server ({Reason})", change.State, change.Reason);
        }
    }

    void OnAddressesChanged(object? sender, IReadOnlyList<System.Net.IPAddress> addresses)
    {
        // Re-announced rather than left alone: a device that just joined a network needs its
        // records seen by peers that were already there, and a fresh announcement is how that
        // happens. The name is preserved by the responder, so peers see an update, not a new
        // service.
        if (!this.server.IsRunning)
            return;

        var stamp = this.Next();

        _ = Task.Run(async () =>
        {
            try
            {
                await this.PublishAsync(stamp, CancellationToken.None, force: true).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.logger.LogError(ex, "Failed to re-announce the mDNS advertisement after a network change");
            }
        });
    }

    /// <summary>The stamp for one request to change the advertisement. Taken on the thread that caused it.</summary>
    internal long Next() => Interlocked.Increment(ref this.sequence);

    /// <summary>
    /// Whether a newer request has already been applied, in which case this one is stale and must do
    /// nothing. Called inside the gate, so the comparison and the update cannot interleave.
    /// </summary>
    bool Superseded(long stamp)
    {
        if (stamp < this.applied)
            return true;

        this.applied = stamp;
        return false;
    }

    async Task PublishAsync(long stamp, CancellationToken cancellationToken, bool force = false)
    {
        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (this.Superseded(stamp))
            {
                this.logger.LogDebug("Skipping a stale advertisement update; a newer one has already been applied");
                return;
            }

            var port = PortOf(this.server);
            if (port is null)
            {
                // Not silence, and not an exception either. A server reachable only through a tunnel
                // never binds a local port and has nothing to advertise on the link, which is fine;
                // a server that says it is running and cannot name a port is not fine. The two are
                // not distinguishable from here, and both end with an app nobody on the link can
                // find, so both get said out loud.
                this.logger.LogWarning(
                    "The server is running but reports no listen URL, so there is no port to advertise and nothing was published - expected only for a server served entirely through a tunnel"
                );
                return;
            }

            if (this.Publication is not null && !force && this.advertisedPort == port.Value)
                return;

            await this.WithdrawCoreAsync().ConfigureAwait(false);
            await this.PublishCoreAsync(stamp, port.Value, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            this.gate.Release();
        }
    }

    async Task PublishCoreAsync(long stamp, int port, CancellationToken cancellationToken)
    {
        var attempts = Math.Max(1, this.options.PublishAttempts);
        var delay = Clamp(this.options.PublishRetryDelay);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var registration = new MdnsServiceRegistration
                {
                    InstanceName = this.options.InstanceName ?? Environment.MachineName,
                    ServiceType = this.options.ServiceType,
                    Port = port,
                    TxtRecords = this.BuildTxtRecords()
                };

                this.Publication = await this.mdns.Publish(registration, cancellationToken).ConfigureAwait(false);
                this.advertisedPort = port;

                this.logger.LogInformation(
                    "Advertising {Instance} as {ServiceType} on port {Port}",
                    this.Publication.InstanceName,
                    this.Publication.ServiceType,
                    this.Publication.Port
                );

                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (attempt >= attempts)
                {
                    // Error, not warning. The server is up and answering and the app looks perfectly
                    // healthy from the inside, while no peer on the link can find it — and a crash
                    // reporter's logging bridge files an event at Error and only a breadcrumb at
                    // Warning, so a warning here is a failure nobody ever sees.
                    this.logger.LogError(
                        ex,
                        "Gave up advertising the server on port {Port} after {Attempts} attempt(s). It is running but will not be discovered until the server restarts or the device changes network",
                        port,
                        attempts
                    );
                    return;
                }

                this.logger.LogWarning(
                    ex,
                    "Failed to advertise the server on port {Port} (attempt {Attempt} of {Attempts}); retrying in {Delay}",
                    port,
                    attempt,
                    attempts,
                    delay
                );

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay = Clamp(TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, this.options.MaxPublishRetryDelay.Ticks)));

                // A newer transition is waiting on the gate this loop is holding, and it knows more
                // about the server than an attempt that has already failed twice. Retrying a port
                // that has since been unbound would advertise a service that is not there.
                if (Interlocked.Read(ref this.sequence) != stamp)
                {
                    this.logger.LogDebug("Abandoning the advertisement retry; the server has changed state since it started");
                    return;
                }
            }
        }
    }

    async Task WithdrawAsync(long stamp)
    {
        await this.gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (this.Superseded(stamp))
            {
                this.logger.LogDebug("Skipping a stale withdrawal; a newer advertisement update has already been applied");
                return;
            }

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
            // Usually benign - the responder had already dropped it - but not always, and the case
            // where it is not is a record peers will hold and offer the user for as long as its TTL
            // runs. There is nothing to retry here, since the publication is gone from this side
            // either way, so the honest thing is to say it happened.
            this.logger.LogWarning(ex, "The mDNS publication did not withdraw cleanly; peers may hold a stale record until it expires");
        }
    }

    /// <summary>A misconfigured negative delay is a configuration mistake, not a reason to throw out of a retry loop.</summary>
    static TimeSpan Clamp(TimeSpan delay) => delay < TimeSpan.Zero ? TimeSpan.Zero : delay;

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
