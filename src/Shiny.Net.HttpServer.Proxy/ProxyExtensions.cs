using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Shiny.Net.HttpServer.Proxy;

/// <summary>Attached to every route the proxy registers, so it can find its own again.</summary>
public sealed class ProxyRouteMetadata
{
    public ProxyRouteMetadata(ProxyCluster cluster, string? routeId = null)
        : this([cluster], routeId is null ? [] : [routeId])
    {
    }

    internal ProxyRouteMetadata(IReadOnlyList<ProxyCluster> clusters, IReadOnlyList<string> routeIds)
    {
        this.Clusters = clusters;
        this.RouteIds = routeIds;
    }

    /// <summary>
    /// Every cluster this endpoint can reach. More than one when several configured routes share a
    /// template and are told apart by host or method.
    /// </summary>
    public IReadOnlyList<ProxyCluster> Clusters { get; }

    /// <summary>The configured route ids behind this endpoint. Empty when it was mapped in code.</summary>
    public IReadOnlyList<string> RouteIds { get; }

    public ProxyCluster Cluster => this.Clusters[0];

    /// <summary>The route's id when it came from configuration, null when it was mapped in code.</summary>
    public string? RouteId => this.RouteIds.Count > 0 ? this.RouteIds[0] : null;
}

/// <summary>
/// Forwarding a route to another server.
/// <para>
/// The reason this belongs beside an embedded server: the device is often the only thing that can
/// reach what the caller wants. A phone bridges its own loopback services to a tunnel; a Raspberry
/// Pi fronts a printer or a camera that speaks HTTP but has no TLS and no authentication; a dev
/// server serves the app and forwards <c>/api</c> to the real backend so the browser sees one
/// origin.
/// </para>
/// <code>
/// app.MapProxy("/api/{*path}", "https://api.example.com");
/// </code>
/// </summary>
public static class ProxyExtensions
{
    /// <summary>Forwards every method on <paramref name="pattern"/> to <paramref name="destination"/>.</summary>
    public static HttpServer MapProxy(
        this HttpServer server,
        string pattern,
        string destination,
        Action<ProxyOptions>? configure = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        return server.MapProxy(pattern, new Uri(destination, UriKind.Absolute), configure);
    }

    /// <summary>Forwards every method on <paramref name="pattern"/> to <paramref name="destination"/>.</summary>
    public static HttpServer MapProxy(
        this HttpServer server,
        string pattern,
        Uri destination,
        Action<ProxyOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(destination);

        if (!destination.IsAbsoluteUri)
            throw new ArgumentException("The proxy destination must be an absolute URI.", nameof(destination));

        var cluster = new ProxyCluster(destination.Authority);
        cluster.AddDestination(destination.Authority, destination);

        // Passive health checks are off for a cluster of one. Quarantining the only destination
        // there is would turn a 502 that names a real upstream failure into a 503 that names
        // nothing, and there is nowhere else for the request to go either way.
        cluster.HealthCheck.Passive.Enabled = false;

        configure?.Invoke(cluster.Forwarding);

        server.MapProxy(pattern, cluster);

        return server;
    }

    /// <summary>
    /// Forwards a route to a cluster of destinations, with load balancing, health checks and
    /// session affinity.
    /// <code>
    /// app.MapProxy("/api/{*path}", cluster =>
    /// {
    ///     cluster.AddDestination("a", "https://a.internal");
    ///     cluster.AddDestination("b", "https://b.internal");
    ///     cluster.LoadBalancing = LoadBalancingPolicy.LeastRequests;
    /// });
    /// </code>
    /// </summary>
    public static ProxyCluster MapProxy(this HttpServer server, string pattern, Action<ProxyCluster> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var cluster = new ProxyCluster();
        configure(cluster);

        return server.MapProxy(pattern, cluster);
    }

    /// <summary>Forwards a route to a cluster that already exists — the same backend behind two paths.</summary>
    public static ProxyCluster MapProxy(this HttpServer server, string pattern, ProxyCluster cluster)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        ArgumentNullException.ThrowIfNull(cluster);

        var logger = LoggerFor(server);
        var handler = Handler(cluster, cluster.Forwarding, logger);

        foreach (var method in cluster.Methods)
            server.Map(method, pattern, handler, new ProxyRouteMetadata(cluster));

        cluster.AttachTo(server, logger);

        return cluster;
    }

    internal static ILogger? LoggerFor(HttpServer server)
        => server.Services?.GetService<ILoggerFactory>()?.CreateLogger("Shiny.Net.HttpServer.Proxy");

    internal static RequestDelegate Handler(ProxyCluster cluster, ProxyOptions options, ILogger? logger)
        => async context =>
        {
            var destination = cluster.SelectDestination(context);

            if (destination is null)
            {
                logger?.LogWarning("Cluster {Cluster} has no available destination", cluster.Id);
                await HttpForwarder.FailAsync(context, options, ProxyError.NoAvailableDestination, null).ConfigureAwait(false);

                return;
            }

            var client = options.Client ?? HttpForwarder.SharedClient();

            destination.BeginRequest();
            try
            {
                var error = await HttpForwarder
                    .SendAsync(context, destination, options, client, logger, context.RequestAborted)
                    .ConfigureAwait(false);

                // Only the failures that say something about the *destination* count against it. A
                // caller that hung up, or an upstream that answered 500, says nothing about whether
                // this instance is reachable.
                if (error is ProxyError.Request or ProxyError.RequestTimeout)
                    destination.ReportFailure(cluster.HealthCheck.Passive);
                else if (error is ProxyError.None)
                    destination.ReportSuccess();
            }
            finally
            {
                destination.EndRequest();
            }
        };
}
