using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Shiny.Net.HttpServer.Proxy;

/// <summary>
/// Holds the proxy that is currently in force and keeps the route table matching it.
/// <para>
/// Reload is the reason this exists as a live object rather than a one-shot registration. Routes in
/// this server are not frozen when it starts — the table is swapped atomically — so a configuration
/// change can be applied to a running server without restarting it, and without dropping the
/// requests in flight through the routes that did not change.
/// </para>
/// </summary>
public sealed class ReverseProxyRuntime : IDisposable
{
    readonly IConfiguration? section;
    readonly Lock sync = new();

    IDisposable? reload;
    HttpServer? server;
    ILogger? logger;
    bool disposed;

    /// <summary>A fixed proxy, built in code.</summary>
    public ReverseProxyRuntime(ReverseProxyConfiguration configuration)
        => this.Current = configuration ?? throw new ArgumentNullException(nameof(configuration));

    /// <summary>A proxy read from configuration, and re-read whenever the source says it changed.</summary>
    public ReverseProxyRuntime(IConfiguration section)
    {
        ArgumentNullException.ThrowIfNull(section);

        this.section = section;
        this.Current = ReverseProxyConfigurationLoader.Load(section);
    }

    /// <summary>What is in force right now.</summary>
    public ReverseProxyConfiguration Current { get; private set; }

    /// <summary>Raised after a reload has been applied, with the configuration that is now in force.</summary>
    public event EventHandler<ReverseProxyConfiguration>? Reloaded;

    /// <summary>Maps the routes onto a server and, for a configuration-backed proxy, keeps them current.</summary>
    public void Apply(HttpServer server)
    {
        ArgumentNullException.ThrowIfNull(server);

        this.server = server;
        this.logger = ProxyExtensions.LoggerFor(server);

        this.Map();

        if (this.section is not null)
            this.reload ??= ChangeToken.OnChange(this.section.GetReloadToken, this.OnConfigurationChanged);
    }

    void OnConfigurationChanged()
    {
        if (this.section is null || this.server is null)
            return;

        lock (this.sync)
        {
            if (this.disposed)
                return;

            try
            {
                this.Current = ReverseProxyConfigurationLoader.Load(this.section, this.Current);
                this.Map();
            }
            catch (Exception ex)
            {
                // A configuration file saved half-written is the common case here. Keeping the
                // routes that are already working beats tearing down a functioning proxy over a
                // missing brace.
                this.logger?.LogError(ex, "The reverse proxy configuration could not be reloaded; the previous one stays in force");
                return;
            }
        }

        this.Reloaded?.Invoke(this, this.Current);
    }

    void Map()
    {
        var target = this.server;
        if (target is null)
            return;

        // Only routes this runtime put there. A MapProxy written in code is not configuration's to
        // remove, and a reload that quietly unmapped one would be very hard to explain.
        target.UnmapAll(endpoint => endpoint.GetMetadata<ProxyRouteMetadata>()?.RouteId is not null);

        foreach (var group in this.Current.Routes.GroupBy(x => x.Path, StringComparer.Ordinal))
        {
            var candidates = new List<Candidate>();
            var methods = new List<string>();
            var clusters = new List<ProxyCluster>();

            foreach (var route in group)
            {
                if (!this.Current.Clusters.TryGetValue(route.ClusterId, out var cluster))
                {
                    this.logger?.LogWarning(
                        "Proxy route {Route} points at cluster {Cluster}, which is not configured",
                        route.RouteId, route.ClusterId
                    );

                    continue;
                }

                candidates.Add(new Candidate(route, ProxyExtensions.Handler(
                    cluster,
                    cluster.Forwarding.With(route.Transforms),
                    this.logger
                )));

                cluster.AttachTo(target, this.logger);

                if (!clusters.Contains(cluster))
                    clusters.Add(cluster);

                foreach (var method in route.Methods.Count > 0 ? route.Methods : cluster.Methods)
                {
                    if (!methods.Contains(method, StringComparer.Ordinal))
                        methods.Add(method);
                }
            }

            if (candidates.Count == 0)
                continue;

            var handler = Dispatch(candidates);
            var metadata = new ProxyRouteMetadata(clusters, [.. candidates.Select(x => x.Route.RouteId)]);

            foreach (var method in methods)
                target.Map(method, group.Key, handler, metadata);
        }
    }

    /// <summary>
    /// Picks between routes that share a template.
    /// <para>
    /// The router matches on method and path, so two routes differing only by host would otherwise
    /// collide in the table and one would silently win. They are collapsed into one endpoint that
    /// tries them in <see cref="ProxyRouteConfig.Order"/>, which is also where a host or method
    /// filter that matches nothing turns into a 404 rather than a forward to the wrong backend.
    /// </para>
    /// </summary>
    static RequestDelegate Dispatch(List<Candidate> candidates)
    {
        if (candidates.Count == 1 && candidates[0].Route.Hosts.Count == 0 && candidates[0].Route.Methods.Count == 0)
            return candidates[0].Handler;

        return async context =>
        {
            foreach (var candidate in candidates)
            {
                if (!candidate.Route.MatchesMethod(context.Request.Method) || !candidate.Route.MatchesHost(context))
                    continue;

                await candidate.Handler(context).ConfigureAwait(false);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status404NotFound;
            context.Response.ContentLength = 0;

            await context.Response.StartAsync(context.RequestAborted).ConfigureAwait(false);
        };
    }

    public void Dispose()
    {
        lock (this.sync)
        {
            if (this.disposed)
                return;

            this.disposed = true;
        }

        this.reload?.Dispose();
        this.reload = null;

        foreach (var cluster in this.Current.Clusters.Values)
            cluster.Dispose();
    }

    readonly record struct Candidate(ProxyRouteConfig Route, RequestDelegate Handler);
}

/// <summary>Registering a configuration-driven reverse proxy.</summary>
public static class ReverseProxyExtensions
{
    /// <summary>
    /// Reads routes and clusters from a configuration section and keeps them current as it changes.
    /// <code>
    /// builder.AddReverseProxy(configuration.GetSection("ReverseProxy"));
    /// </code>
    /// </summary>
    public static ShinyHttpServerBuilder AddReverseProxy(this ShinyHttpServerBuilder builder, IConfiguration section)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(section);

        builder.Services.TryAddSingleton(_ => new ReverseProxyRuntime(section));

        return builder.Configure(server => server.MapReverseProxy());
    }

    /// <summary>Registers a proxy built in code, for a host that would rather not have a configuration file.</summary>
    public static ShinyHttpServerBuilder AddReverseProxy(this ShinyHttpServerBuilder builder, ReverseProxyConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        builder.Services.TryAddSingleton(_ => new ReverseProxyRuntime(configuration));

        return builder.Configure(server => server.MapReverseProxy());
    }

    /// <summary>Maps the routes of the proxy registered with <c>AddReverseProxy</c>.</summary>
    public static HttpServer MapReverseProxy(this HttpServer server)
    {
        ArgumentNullException.ThrowIfNull(server);

        var runtime = server.Services?.GetService<ReverseProxyRuntime>()
            ?? throw new InvalidOperationException(
                "No reverse proxy has been registered. Call builder.AddReverseProxy(configuration.GetSection(\"ReverseProxy\")) " +
                "first, or use the MapReverseProxy(IConfiguration) overload to map one without the container."
            );

        runtime.Apply(server);

        return server;
    }

    /// <summary>
    /// Maps a proxy straight from a configuration section, for a server built without a container.
    /// The returned runtime owns the reload subscription and the clusters' probe loops — dispose it
    /// when the server goes.
    /// </summary>
    public static ReverseProxyRuntime MapReverseProxy(this HttpServer server, IConfiguration section)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(section);

        var runtime = new ReverseProxyRuntime(section);
        runtime.Apply(server);

        return runtime;
    }
}
