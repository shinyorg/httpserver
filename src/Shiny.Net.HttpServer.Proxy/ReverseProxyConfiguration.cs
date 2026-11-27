using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Shiny.Net.HttpServer.Proxy;

/// <summary>One route: which requests it answers, which cluster they go to, and what happens on the way.</summary>
public sealed class ProxyRouteConfig
{
    public ProxyRouteConfig(string routeId, string clusterId, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clusterId);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        this.RouteId = routeId;
        this.ClusterId = clusterId;
        this.Path = path;
    }

    public string RouteId { get; }

    public string ClusterId { get; }

    /// <summary>The route template, e.g. <c>/api/{*rest}</c>.</summary>
    public string Path { get; }

    /// <summary>Lowest first. Only matters when two routes share a template and differ by host.</summary>
    public int Order { get; set; }

    /// <summary>
    /// Host names this route answers on. Empty answers any. A leading <c>*.</c> matches one or more
    /// leading labels, so <c>*.example.com</c> covers <c>a.example.com</c> but not <c>example.com</c>.
    /// </summary>
    public IList<string> Hosts { get; } = [];

    /// <summary>Methods this route answers. Empty means the cluster's list.</summary>
    public IList<string> Methods { get; } = [];

    /// <summary>Transforms applied to this route, on top of the cluster's forwarding options.</summary>
    public TransformBuilder Transforms { get; } = new();

    internal bool MatchesHost(HttpContext context)
    {
        if (this.Hosts.Count == 0)
            return true;

        var host = context.Request.Host;
        if (host is null)
            return false;

        // The port is part of the Host header and never part of a configured host name, so it is
        // taken off rather than made everybody's problem to write.
        var colon = host.LastIndexOf(':');
        if (colon > 0 && host.IndexOf(']') < colon)
            host = host[..colon];

        foreach (var candidate in this.Hosts)
        {
            if (candidate.StartsWith("*.", StringComparison.Ordinal))
            {
                var suffix = candidate[1..];
                if (host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            else if (string.Equals(host, candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    internal bool MatchesMethod(string method)
    {
        if (this.Methods.Count == 0)
            return true;

        foreach (var candidate in this.Methods)
        {
            if (HttpMethods.Equals(candidate, method))
                return true;
        }

        return false;
    }
}

/// <summary>A whole proxy: the routes and the clusters they point at.</summary>
public sealed class ReverseProxyConfiguration
{
    public ReverseProxyConfiguration(IReadOnlyList<ProxyRouteConfig> routes, IReadOnlyDictionary<string, ProxyCluster> clusters)
    {
        this.Routes = routes;
        this.Clusters = clusters;
    }

    public IReadOnlyList<ProxyRouteConfig> Routes { get; }

    public IReadOnlyDictionary<string, ProxyCluster> Clusters { get; }

    public static ReverseProxyConfiguration Empty { get; } = new([], new Dictionary<string, ProxyCluster>());
}

/// <summary>
/// Reads a proxy out of <see cref="IConfiguration"/>.
/// <para>
/// Every value is read by name and parsed by hand rather than bound by reflection. That is not
/// stubbornness: the reflection binder is exactly the thing that does not survive trimming, and a
/// proxy whose configuration silently comes back empty in a published AOT build is a worse outcome
/// than fifty lines of parsing.
/// </para>
/// <code>
/// {
///   "ReverseProxy": {
///     "Routes": {
///       "api": {
///         "ClusterId": "backend",
///         "Match": { "Path": "/api/{**rest}" },
///         "Transforms": [ { "PathRemovePrefix": "/api" } ]
///       }
///     },
///     "Clusters": {
///       "backend": {
///         "LoadBalancingPolicy": "LeastRequests",
///         "Destinations": { "d1": { "Address": "https://a.internal" } }
///       }
///     }
///   }
/// }
/// </code>
/// </summary>
public static class ReverseProxyConfigurationLoader
{
    /// <summary>
    /// Builds a configuration from a section.
    /// <para>
    /// <paramref name="existing"/> is the previous one, when this is a reload: a cluster whose id
    /// survives keeps its <see cref="ProxyCluster"/> instance, so a quarantined destination stays
    /// quarantined and a probe loop is not torn down and rebuilt on every save.
    /// </para>
    /// </summary>
    public static ReverseProxyConfiguration Load(IConfiguration section, ReverseProxyConfiguration? existing = null)
    {
        var (configuration, apply) = LoadStaged(section, existing);
        apply();

        return configuration;
    }

    /// <summary>
    /// Reads and validates the whole section without touching a cluster that is already live, and
    /// hands back the changes to those clusters as a step to run later.
    /// <para>
    /// A carried cluster is serving traffic while this reads. Writing its settings as they were
    /// parsed meant a file that failed further down - a route with no ClusterId - left the running
    /// proxy with half of the new configuration, even though the reload reported failure and claimed
    /// the old one stayed in force.
    /// </para>
    /// </summary>
    internal static (ReverseProxyConfiguration Configuration, Action Apply) LoadStaged(IConfiguration section, ReverseProxyConfiguration? existing = null)
    {
        ArgumentNullException.ThrowIfNull(section);

        var clusters = new Dictionary<string, ProxyCluster>(StringComparer.OrdinalIgnoreCase);
        var changes = new List<Action>();

        foreach (var child in section.GetSection("Clusters").GetChildren())
        {
            var id = child["ClusterId"] ?? child.Key;

            var cluster = existing is not null && existing.Clusters.TryGetValue(id, out var carried)
                ? carried
                : new ProxyCluster(id);

            changes.Add(ReadCluster(child, cluster));
            clusters[id] = cluster;
        }

        var routes = new List<ProxyRouteConfig>();

        foreach (var child in section.GetSection("Routes").GetChildren())
        {
            var routeId = child["RouteId"] ?? child.Key;
            var clusterId = child["ClusterId"];

            if (clusterId is not { Length: > 0 })
                throw new InvalidOperationException($"Proxy route '{routeId}' has no ClusterId.");

            var path = child["Match:Path"] ?? child["Path"];
            if (path is not { Length: > 0 })
                throw new InvalidOperationException($"Proxy route '{routeId}' has no Match:Path.");

            var route = new ProxyRouteConfig(routeId, clusterId, NormalizeTemplate(path))
            {
                Order = Int(child["Order"]) ?? 0
            };

            foreach (var host in Values(child.GetSection("Match:Hosts")))
                route.Hosts.Add(host);

            foreach (var method in Values(child.GetSection("Match:Methods")))
                route.Methods.Add(method.ToUpperInvariant());

            ReadTransforms(child.GetSection("Transforms"), route.Transforms);
            routes.Add(route);
        }

        routes.Sort((a, b) => a.Order.CompareTo(b.Order));

        return (new ReverseProxyConfiguration(routes, clusters), () =>
        {
            foreach (var change in changes)
                change();
        });
    }

    /// <summary>
    /// YARP writes a catch-all as <c>{**rest}</c> and this server's router writes it <c>{*rest}</c>.
    /// The two mean the same thing, and a configuration file copied from one to the other should not
    /// fail to parse over a star.
    /// </summary>
    internal static string NormalizeTemplate(string path) => path.Replace("{**", "{*", StringComparison.Ordinal);

    /// <summary>Parses one cluster's section, and returns the step that writes it onto the cluster.</summary>
    static Action ReadCluster(IConfigurationSection section, ProxyCluster cluster)
    {
        // Everything that can throw happens here, before anything is written.
        var policy = LoadBalancingPolicies.Parse(section["LoadBalancingPolicy"]);

        var affinity = section.GetSection("SessionAffinity");
        var hasAffinity = affinity.Exists();
        var affinityMode = Enum<SessionAffinityMode>(affinity["Mode"]);
        var cookieName = affinity["CookieName"];
        var headerName = affinity["HeaderName"];
        var failurePolicy = Enum<AffinityFailurePolicy>(affinity["FailurePolicy"]);
        var secureCookie = Bool(affinity["SecureCookie"]);
        var cookieMaxAge = Time(affinity["CookieMaxAge"]);

        var active = section.GetSection("HealthCheck:Active");
        var hasActive = active.Exists();
        var activeEnabled = Bool(active["Enabled"]);
        var activeInterval = Time(active["Interval"]);
        var activeTimeout = Time(active["Timeout"]);
        var activePath = active["Path"];

        var passive = section.GetSection("HealthCheck:Passive");
        var hasPassive = passive.Exists();
        var passiveEnabled = Bool(passive["Enabled"]);
        var failureThreshold = Int(passive["FailureThreshold"]);
        var reactivationPeriod = Time(passive["ReactivationPeriod"]);

        var request = section.GetSection("HttpRequest");
        var hasRequest = request.Exists();
        var activityTimeout = Time(request["ActivityTimeout"]);
        var rewriteHost = Bool(request["RewriteHost"]);
        var forwardUpgrades = Bool(request["ForwardUpgrades"]);
        var requestVersion = request["Version"] is { Length: > 0 } version && Version.TryParse(version, out var parsed) ? parsed : null;

        var destinations = new List<ProxyDestination>();

        foreach (var child in section.GetSection("Destinations").GetChildren())
        {
            var address = child["Address"];
            if (address is not { Length: > 0 })
                throw new InvalidOperationException($"Destination '{child.Key}' in cluster '{cluster.Id}' has no Address.");

            var destination = new ProxyDestination(child["DestinationId"] ?? child.Key, new Uri(address, UriKind.Absolute));

            if (child["Health"] is { Length: > 0 } health)
                destination.Health = new Uri(health, UriKind.Absolute);

            destinations.Add(destination);
        }

        return () =>
        {
            if (policy is not null)
                cluster.LoadBalancer = policy;

            if (hasAffinity)
            {
                cluster.SessionAffinity.Mode = affinityMode ?? cluster.SessionAffinity.Mode;
                cluster.SessionAffinity.CookieName = cookieName ?? cluster.SessionAffinity.CookieName;
                cluster.SessionAffinity.HeaderName = headerName ?? cluster.SessionAffinity.HeaderName;
                cluster.SessionAffinity.FailurePolicy = failurePolicy ?? cluster.SessionAffinity.FailurePolicy;
                cluster.SessionAffinity.SecureCookie = secureCookie ?? cluster.SessionAffinity.SecureCookie;
                cluster.SessionAffinity.CookieMaxAge = cookieMaxAge ?? cluster.SessionAffinity.CookieMaxAge;
            }

            if (hasActive)
            {
                cluster.HealthCheck.Active.Enabled = activeEnabled ?? cluster.HealthCheck.Active.Enabled;
                cluster.HealthCheck.Active.Interval = activeInterval ?? cluster.HealthCheck.Active.Interval;
                cluster.HealthCheck.Active.Timeout = activeTimeout ?? cluster.HealthCheck.Active.Timeout;
                cluster.HealthCheck.Active.Path = activePath ?? cluster.HealthCheck.Active.Path;
            }

            if (hasPassive)
            {
                cluster.HealthCheck.Passive.Enabled = passiveEnabled ?? cluster.HealthCheck.Passive.Enabled;
                cluster.HealthCheck.Passive.FailureThreshold = failureThreshold ?? cluster.HealthCheck.Passive.FailureThreshold;
                cluster.HealthCheck.Passive.ReactivationPeriod = reactivationPeriod ?? cluster.HealthCheck.Passive.ReactivationPeriod;
            }

            if (hasRequest)
            {
                cluster.Forwarding.Timeout = activityTimeout ?? cluster.Forwarding.Timeout;
                cluster.Forwarding.RewriteHost = rewriteHost ?? cluster.Forwarding.RewriteHost;
                cluster.Forwarding.ForwardUpgrades = forwardUpgrades ?? cluster.Forwarding.ForwardUpgrades;

                if (requestVersion is not null)
                    cluster.Forwarding.RequestVersion = requestVersion;
            }

            cluster.SetDestinations(destinations);
        };
    }

    static void ReadTransforms(IConfigurationSection section, TransformBuilder transforms)
    {
        foreach (var entry in section.GetChildren())
        {
            if (entry["PathPrefix"] is { Length: > 0 } prefix)
                transforms.AddPathPrefix(prefix);

            if (entry["PathRemovePrefix"] is { Length: > 0 } removePrefix)
                transforms.RemovePathPrefix(removePrefix);

            if (entry["PathSet"] is { Length: > 0 } path)
                transforms.SetPath(path);

            if (entry["RequestHeader"] is { Length: > 0 } requestHeader)
            {
                if (entry["Set"] is { } set)
                    transforms.SetRequestHeader(requestHeader, set);
                else if (entry["Append"] is { } append)
                    transforms.AppendRequestHeader(requestHeader, append);
            }

            if (entry["RequestHeaderRemove"] is { Length: > 0 } removeRequestHeader)
                transforms.RemoveRequestHeader(removeRequestHeader);

            if (entry["RequestHeaderCopy"] is { Length: > 0 } copyFrom && entry["To"] is { Length: > 0 } copyTo)
                transforms.CopyRequestHeader(copyFrom, copyTo);

            if (entry["RequestHost"] is { Length: > 0 } host)
                transforms.SetRequestHost(host);

            if (entry["ResponseHeader"] is { Length: > 0 } responseHeader)
            {
                if (entry["Set"] is { } set)
                    transforms.SetResponseHeader(responseHeader, set);
                else if (entry["Append"] is { } append)
                    transforms.AppendResponseHeader(responseHeader, append);
            }

            if (entry["ResponseHeaderRemove"] is { Length: > 0 } removeResponseHeader)
                transforms.RemoveResponseHeader(removeResponseHeader);

            if (entry["QueryValue"] is { Length: > 0 } queryValue)
            {
                if (entry["Set"] is { } set)
                    transforms.SetQueryValue(queryValue, set);
                else if (entry["Append"] is { } append)
                    transforms.AppendQueryValue(queryValue, append);
            }

            if (entry["QueryRouteValue"] is { Length: > 0 } queryRouteValue && entry["From"] is { Length: > 0 } from)
                transforms.SetQueryRouteValue(queryRouteValue, from);

            if (entry["QueryRemove"] is { Length: > 0 } queryRemove)
                transforms.RemoveQueryValue(queryRemove);

            if (entry["X-Forwarded"] is { Length: > 0 } forwarded && Enum<ForwardedHeadersMode>(forwarded) is { } mode)
                transforms.WithForwardedHeaders(mode);
        }
    }

    static IEnumerable<string> Values(IConfigurationSection section)
    {
        foreach (var child in section.GetChildren())
        {
            if (child.Value is { Length: > 0 } value)
                yield return value;
        }
    }

    static bool? Bool(string? value)
        => value is { Length: > 0 } && bool.TryParse(value, out var parsed) ? parsed : null;

    static int? Int(string? value)
        => value is { Length: > 0 } && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    static TimeSpan? Time(string? value)
    {
        if (value is not { Length: > 0 })
            return null;

        // A bare number is seconds. "Interval": 10 is what everyone writes first, and failing on it
        // teaches nothing that accepting it does not.
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            return TimeSpan.FromSeconds(seconds);

        return TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    static T? Enum<T>(string? value) where T : struct
        => value is { Length: > 0 } && System.Enum.TryParse<T>(value, ignoreCase: true, out var parsed) ? parsed : null;
}
