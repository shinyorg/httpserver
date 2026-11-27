using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Shiny.Net.HttpServer.Routing;

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

    // One per method and template this runtime has put in the route table. A reload that keeps a
    // route keeps its endpoint and only swaps what the endpoint forwards to, so a request arriving
    // mid-reload finds the route either as it was or as it is now - never missing. Unmapping every
    // proxy route and mapping them back one by one left a gap in which those requests got a 404.
    readonly Dictionary<string, Slot> slots = new(StringComparer.Ordinal);

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

        lock (this.sync)
        {
            // The slots describe one server's route table; another server starts from nothing.
            if (!ReferenceEquals(this.server, server))
                this.slots.Clear();

            this.server = server;
            this.logger = ProxyExtensions.LoggerFor(server);

            this.Map(this.Current, null);
        }

        if (this.section is not null)
            this.reload ??= ChangeToken.OnChange(this.section.GetReloadToken, this.OnConfigurationChanged);
    }

    void OnConfigurationChanged()
    {
        if (this.section is null || this.server is null)
            return;

        ReverseProxyConfiguration next;
        List<ProxyCluster> dropped;

        lock (this.sync)
        {
            if (this.disposed)
                return;

            var previous = this.Current;

            try
            {
                // Read and checked in full before anything live changes: the clusters a reload
                // carries over are serving traffic, and a file that fails half way must leave them
                // exactly as they were.
                var (loaded, applyClusters) = ReverseProxyConfigurationLoader.LoadStaged(this.section, previous);

                this.Map(loaded, applyClusters);
                this.Current = next = loaded;
            }
            catch (Exception ex)
            {
                // A configuration file saved half-written is the common case here. Keeping the
                // routes that are already working beats tearing down a functioning proxy over a
                // missing brace.
                this.logger?.LogError(ex, "The reverse proxy configuration could not be reloaded; the previous one stays in force");
                return;
            }

            // A cluster the new configuration no longer has would otherwise keep its probe loop
            // polling an upstream nothing routes to, for as long as the server runs.
            dropped = [.. previous.Clusters.Values.Where(cluster => !next.Clusters.Values.Contains(cluster))];
        }

        foreach (var cluster in dropped)
            cluster.Dispose();

        this.Reloaded?.Invoke(this, next);
    }

    /// <summary>
    /// Brings the route table in line with <paramref name="configuration"/>. Everything that can fail
    /// is worked out before <paramref name="applyClusters"/> runs or the table is touched.
    /// </summary>
    void Map(ReverseProxyConfiguration configuration, Action? applyClusters)
    {
        var target = this.server;
        if (target is null)
            return;

        foreach (var route in configuration.Routes.Where(r => !configuration.Clusters.ContainsKey(r.ClusterId)))
        {
            this.logger?.LogWarning(
                "Proxy route {Route} points at cluster {Cluster}, which is not configured",
                route.RouteId, route.ClusterId
            );
        }

        var plan = Plan(configuration);
        var wanted = new HashSet<string>(plan.Select(x => x.Key), StringComparer.Ordinal);

        // Only routes this runtime put there. A MapProxy written in code is not configuration's to
        // remove, and a reload that quietly unmapped one would be very hard to explain.
        var leaving = this.slots.Where(x => !wanted.Contains(x.Key)).ToList();
        var leavingEndpoints = new HashSet<RouteEndpoint>(leaving.Select(x => x.Value.Endpoint), ReferenceEqualityComparer.Instance);
        var ours = new HashSet<RouteEndpoint>(this.slots.Values.Select(x => x.Endpoint), ReferenceEqualityComparer.Instance);
        var arriving = plan.Where(x => !this.slots.ContainsKey(x.Key)).ToList();

        // An endpoint that would collide with one staying in the table is refused now, before
        // anything has changed, rather than half way through the swap.
        var staying = target.Router.Endpoints.Where(e => !ours.Contains(e)).ToList();
        var clearFirst = new HashSet<RouteEndpoint>(ReferenceEqualityComparer.Instance);

        foreach (var entry in arriving)
        {
            var clash = staying.FirstOrDefault(e => HttpMethods.Equals(e.Method, entry.Method) && e.Template.IsEquivalentTo(entry.Template));
            if (clash is not null)
                throw new InvalidOperationException($"Proxy route '{entry.Method} {entry.Path}' clashes with '{clash.DisplayName}', which is already mapped.");

            var twin = plan.FirstOrDefault(other => !ReferenceEquals(other, entry) && HttpMethods.Equals(other.Method, entry.Method) && other.Template.IsEquivalentTo(entry.Template));
            if (twin is not null)
                throw new InvalidOperationException($"Proxy routes '{entry.Path}' and '{twin.Path}' are the same template.");

            // The same template written differently - a renamed parameter - replaces an endpoint
            // that is leaving, and the table will not hold both, so that one goes first. Only that
            // route sees a moment without an endpoint.
            foreach (var predecessor in leavingEndpoints.Where(e => HttpMethods.Equals(e.Method, entry.Method) && e.Template.IsEquivalentTo(entry.Template)))
                clearFirst.Add(predecessor);
        }

        applyClusters?.Invoke();

        var added = new List<RouteEndpoint>();
        var handlers = new Dictionary<string, (RequestDelegate Handler, IReadOnlyList<ProxyCluster> Clusters, IReadOnlyList<string> RouteIds)>(StringComparer.Ordinal);

        foreach (var entry in plan)
        {
            // One handler per template, shared by its methods. Built after the cluster changes have
            // gone in, because the forwarding options are copied when the handler is made.
            if (!handlers.TryGetValue(entry.Path, out var built))
            {
                built = (
                    Dispatch([.. entry.Targets.Select(t => new Candidate(t.Route, ProxyExtensions.Handler(t.Cluster, t.Cluster.Forwarding.With(t.Route.Transforms), this.logger)))]),
                    [.. entry.Targets.Select(t => t.Cluster).Distinct()],
                    [.. entry.Targets.Select(t => t.Route.RouteId)]
                );
                handlers[entry.Path] = built;
            }

            if (this.slots.TryGetValue(entry.Key, out var slot))
            {
                slot.Handler = built.Handler;
                slot.Metadata.Update(built.Clusters, built.RouteIds);
            }
            else
            {
                var metadata = new ProxyRouteMetadata(built.Clusters, built.RouteIds);
                var created = new Slot(built.Handler, metadata);

                created.Endpoint = new RouteEndpoint(created.InvokeAsync, entry.Method, entry.Template, metadata);
                this.slots[entry.Key] = created;
                added.Add(created.Endpoint);
            }
        }

        foreach (var cluster in plan.SelectMany(x => x.Targets).Select(t => t.Cluster).Distinct())
            cluster.AttachTo(target, this.logger);

        // Each of these is one atomic swap of the table. New routes go in before the leaving ones
        // come out, so nothing that exists both before and after is ever missing.
        if (clearFirst.Count > 0)
            target.Router.RemoveAll(clearFirst.Contains);

        if (added.Count > 0)
            target.Router.AddRange(added);

        if (leavingEndpoints.Count > 0)
            target.Router.RemoveAll(leavingEndpoints.Contains);

        foreach (var (key, _) in leaving)
            this.slots.Remove(key);
    }

    /// <summary>Every method and template the configuration wants mapped, and what each forwards to.</summary>
    static List<PlannedRoute> Plan(ReverseProxyConfiguration configuration)
    {
        var plan = new List<PlannedRoute>();

        foreach (var group in configuration.Routes.GroupBy(x => x.Path, StringComparer.Ordinal))
        {
            List<(ProxyRouteConfig Route, ProxyCluster Cluster)> targets =
            [
                .. group
                    .Where(route => configuration.Clusters.ContainsKey(route.ClusterId))
                    .Select(route => (route, configuration.Clusters[route.ClusterId]))
            ];

            if (targets.Count > 0)
            {
                // Parsed here so a template that will not parse fails the reload before it changes anything.
                var template = RouteTemplate.Parse(group.Key);
                var methods = targets
                    .SelectMany(t => t.Route.Methods.Count > 0 ? t.Route.Methods : t.Cluster.Methods)
                    .Select(m => m.ToUpperInvariant())
                    .Distinct(StringComparer.Ordinal);

                foreach (var method in methods)
                    plan.Add(new PlannedRoute(method + " " + group.Key, method, group.Key, template, targets));
            }
        }

        return plan;
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
            var match = candidates.FirstOrDefault(c => c.Route.MatchesMethod(context.Request.Method) && c.Route.MatchesHost(context));

            if (match.Handler is not null)
            {
                await match.Handler(context).ConfigureAwait(false);
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

    sealed record PlannedRoute(string Key, string Method, string Path, RouteTemplate Template, List<(ProxyRouteConfig Route, ProxyCluster Cluster)> Targets);

    /// <summary>A route this runtime owns: the endpoint in the table, and what it forwards to right now.</summary>
    sealed class Slot(RequestDelegate handler, ProxyRouteMetadata metadata)
    {
        volatile RequestDelegate handler = handler;

        public RequestDelegate Handler
        {
            get => this.handler;
            set => this.handler = value;
        }

        public ProxyRouteMetadata Metadata { get; } = metadata;

        public RouteEndpoint Endpoint { get; set; } = null!;

        public ValueTask InvokeAsync(HttpContext context) => this.handler(context);
    }
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
