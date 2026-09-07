using System.Net;
using Microsoft.Extensions.Logging;

namespace Shiny.Net.HttpServer.Proxy;

/// <summary>
/// A group of interchangeable destinations, and the rules for choosing between them.
/// <para>
/// A cluster is the unit everything else hangs off: load balancing, both health checks, session
/// affinity and the forwarding options all belong to it, and a route points at one. The
/// single-destination <c>MapProxy</c> overload builds a cluster of one behind your back, so there is
/// only ever one code path forwarding a request.
/// </para>
/// <code>
/// app.MapProxy("/api/{*path}", cluster =>
/// {
///     cluster.AddDestination("a", "https://a.internal");
///     cluster.AddDestination("b", "https://b.internal");
///     cluster.LoadBalancing = LoadBalancingPolicy.LeastRequests;
///     cluster.HealthCheck.Active.Enabled = true;
/// });
/// </code>
/// </summary>
public sealed class ProxyCluster : IDisposable
{
    readonly Lock sync = new();
    readonly List<ProxyDestination> destinations = [];

    ProxyDestination[] snapshot = [];
    CancellationTokenSource? probing;
    HttpMessageInvoker? probeClient;
    ILogger? logger;
    bool attached;
    bool disposed;

    public ProxyCluster(string? id = null)
        => this.Id = id is { Length: > 0 } ? id : "cluster-" + Guid.NewGuid().ToString("n")[..8];

    /// <summary>Identifier used in logs and as part of the affinity key.</summary>
    public string Id { get; }

    /// <summary>The destinations, as a snapshot that a reload cannot mutate underneath a request.</summary>
    public IReadOnlyList<ProxyDestination> Destinations => this.snapshot;

    /// <summary>Which built-in policy chooses between destinations. Ignored when <see cref="LoadBalancer"/> is set.</summary>
    public LoadBalancingPolicy LoadBalancing { get; set; } = LoadBalancingPolicy.PowerOfTwoChoices;

    /// <summary>A policy of your own, which wins over <see cref="LoadBalancing"/>.</summary>
    public ILoadBalancingPolicy? LoadBalancer { get; set; }

    /// <summary>Pinning a caller to the destination it reached first. Off by default.</summary>
    public SessionAffinityOptions SessionAffinity { get; } = new();

    /// <summary>Active probing and passive observation.</summary>
    public ClusterHealthCheckOptions HealthCheck { get; } = new();

    /// <summary>How a request is forwarded once a destination has been chosen.</summary>
    public ProxyOptions Forwarding { get; } = new();

    /// <summary>Shorthand for <c>Forwarding.Transforms</c>.</summary>
    public TransformBuilder Transforms => this.Forwarding.Transforms;

    /// <summary>The running active-probe loop, so a test can wait for the first round to land.</summary>
    internal Task? ProbeLoop { get; private set; }

    /// <summary>
    /// The methods a route pointing at this cluster answers. Defaults to everything with a defined
    /// meaning over a proxy; <c>CONNECT</c> and <c>TRACE</c> are not in it, and adding them is a
    /// deliberate act.
    /// </summary>
    public IList<string> Methods { get; } =
    [
        HttpMethods.Get, HttpMethods.Post, HttpMethods.Put, HttpMethods.Patch,
        HttpMethods.Delete, HttpMethods.Head, HttpMethods.Options
    ];

    /// <summary>Adds a destination, taking its id from the address authority.</summary>
    public ProxyDestination AddDestination(string address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);

        var uri = new Uri(address, UriKind.Absolute);
        return this.AddDestination(uri.Authority, uri);
    }

    /// <summary>Adds a destination.</summary>
    public ProxyDestination AddDestination(string id, string address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        return this.AddDestination(id, new Uri(address, UriKind.Absolute));
    }

    /// <summary>Adds a destination.</summary>
    public ProxyDestination AddDestination(string id, Uri address)
    {
        var destination = new ProxyDestination(id, address);
        this.AddDestination(destination);

        return destination;
    }

    /// <summary>Adds a destination that has already been built — one carrying its own health address.</summary>
    public ProxyCluster AddDestination(ProxyDestination destination)
    {
        ArgumentNullException.ThrowIfNull(destination);

        lock (this.sync)
        {
            this.destinations.RemoveAll(x => string.Equals(x.Id, destination.Id, StringComparison.Ordinal));
            this.destinations.Add(destination);
            this.snapshot = [.. this.destinations];
        }

        return this;
    }

    /// <summary>Removes a destination by id. Returns false when there was nothing to remove.</summary>
    public bool RemoveDestination(string id)
    {
        lock (this.sync)
        {
            var removed = this.destinations.RemoveAll(x => string.Equals(x.Id, id, StringComparison.Ordinal)) > 0;
            if (removed)
                this.snapshot = [.. this.destinations];

            return removed;
        }
    }

    /// <summary>Replaces the whole destination list, keeping the health state of any id that survives.</summary>
    public void SetDestinations(IEnumerable<ProxyDestination> replacements)
    {
        ArgumentNullException.ThrowIfNull(replacements);

        lock (this.sync)
        {
            // Carrying the existing instance across a reload is what keeps a quarantine in force
            // while a configuration file is being edited. Replacing every destination object would
            // silently return known-bad upstreams to rotation on every save.
            var kept = new List<ProxyDestination>();

            foreach (var replacement in replacements)
            {
                var existing = this.destinations.Find(x =>
                    string.Equals(x.Id, replacement.Id, StringComparison.Ordinal) && x.Address == replacement.Address);

                kept.Add(existing ?? replacement);
            }

            this.destinations.Clear();
            this.destinations.AddRange(kept);
            this.snapshot = [.. this.destinations];
        }
    }

    /// <summary>
    /// Chooses the destination for one request, honouring affinity first and the load-balancing
    /// policy after it. Null means every destination is out of rotation.
    /// </summary>
    public ProxyDestination? SelectDestination(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var all = this.snapshot;
        if (all.Length == 0)
            return null;

        var available = Available(all);
        if (available.Count == 0)
            return null;

        var affinity = this.SessionAffinity;

        if (affinity.Mode != SessionAffinityMode.Disabled && ReadAffinityKey(context, affinity) is { Length: > 0 } key)
        {
            foreach (var destination in available)
            {
                if (string.Equals(AffinityKey.For(this.Id, destination.Id), key, StringComparison.Ordinal))
                    return destination;
            }

            // The caller is pinned to something that is gone or unhealthy. Whether that is worth
            // failing over depends entirely on what the upstream keeps in memory, so it is a choice
            // rather than a default.
            if (affinity.FailurePolicy == AffinityFailurePolicy.Return503)
                return null;
        }

        var picked = (this.LoadBalancer ?? LoadBalancingPolicies.For(this.LoadBalancing)).Pick(available, context);

        if (picked is not null && affinity.Mode != SessionAffinityMode.Disabled)
            WriteAffinityKey(context, affinity, AffinityKey.For(this.Id, picked.Id));

        return picked;
    }

    static List<ProxyDestination> Available(ProxyDestination[] all)
    {
        var available = new List<ProxyDestination>(all.Length);

        foreach (var destination in all)
        {
            if (destination.IsAvailable)
                available.Add(destination);
        }

        return available;
    }

    static string? ReadAffinityKey(HttpContext context, SessionAffinityOptions affinity)
        => affinity.Mode == SessionAffinityMode.Cookie
            ? context.Request.Cookies[affinity.CookieName]
            : context.Request.Headers.GetFirst(affinity.HeaderName);

    static void WriteAffinityKey(HttpContext context, SessionAffinityOptions affinity, string key)
    {
        if (affinity.Mode == SessionAffinityMode.Cookie)
        {
            context.Response.Cookies.Append(affinity.CookieName, key, new CookieOptions
            {
                Path = affinity.CookiePath,
                HttpOnly = true,
                Secure = affinity.SecureCookie,
                SameSite = affinity.SameSite,
                MaxAge = affinity.CookieMaxAge
            });
        }
        else
        {
            context.Response.Headers.Set(affinity.HeaderName, key);
        }
    }

    /// <summary>
    /// Ties the active health checks to the server's lifetime.
    /// <para>
    /// The server's own state is the lifecycle hook here rather than a hosted service, because an
    /// embedded server starts and stops repeatedly — a phone backgrounds, a toggle goes off — and a
    /// probe loop that keeps polling upstreams while the server is stopped is a device radio kept
    /// awake for nothing.
    /// </para>
    /// </summary>
    internal void AttachTo(HttpServer server, ILogger? logger)
    {
        this.logger ??= logger;

        if (this.attached || !this.HealthCheck.Active.Enabled)
            return;

        this.attached = true;

        server.StateChanged += (_, state) =>
        {
            if (state == HttpServerState.Running)
                this.StartProbing();
            else if (state == HttpServerState.Stopped)
                this.StopProbing();
        };

        if (server.IsRunning)
            this.StartProbing();
    }

    void StartProbing()
    {
        lock (this.sync)
        {
            if (this.disposed || this.probing is not null)
                return;

            this.probing = new CancellationTokenSource();
            this.probeClient ??= new HttpMessageInvoker(new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                ConnectTimeout = this.HealthCheck.Active.Timeout
            });

            this.ProbeLoop = Task.Run(() => this.ProbeLoopAsync(this.probing.Token));
        }
    }

    void StopProbing()
    {
        CancellationTokenSource? source;

        lock (this.sync)
        {
            source = this.probing;
            this.probing = null;
        }

        source?.Cancel();
        source?.Dispose();
    }

    async Task ProbeLoopAsync(CancellationToken cancellationToken)
    {
        var options = this.HealthCheck.Active;

        using var timer = new PeriodicTimer(options.Interval);

        // Probe once immediately: waiting a full interval before the first check means the cluster
        // spends that interval sending traffic to destinations nobody has asked about.
        await this.ProbeAllAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                await this.ProbeAllAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The server stopped, or the cluster was disposed.
        }
    }

    async Task ProbeAllAsync(CancellationToken cancellationToken)
    {
        var destinations = this.snapshot;
        if (destinations.Length == 0)
            return;

        var probes = new Task[destinations.Length];
        for (var i = 0; i < destinations.Length; i++)
            probes[i] = this.ProbeAsync(destinations[i], cancellationToken);

        await Task.WhenAll(probes).ConfigureAwait(false);
    }

    async Task ProbeAsync(ProxyDestination destination, CancellationToken cancellationToken)
    {
        var options = this.HealthCheck.Active;
        var client = this.probeClient;

        if (client is null)
            return;

        var basePath = (destination.Health ?? destination.Address);
        var uri = new Uri(basePath, options.Path);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);

        var healthy = false;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);

            healthy = options.Policy?.Invoke(response) ?? response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            healthy = false;
        }

        var previous = destination.ActiveHealth;
        destination.ActiveHealth = healthy ? DestinationHealth.Healthy : DestinationHealth.Unhealthy;

        if (previous != destination.ActiveHealth)
        {
            this.logger?.LogInformation(
                "Destination {Destination} in cluster {Cluster} is now {Health}",
                destination.Id, this.Id, destination.ActiveHealth
            );
        }
    }

    public void Dispose()
    {
        if (this.disposed)
            return;

        this.disposed = true;

        this.StopProbing();
        this.probeClient?.Dispose();
        this.probeClient = null;
    }
}
