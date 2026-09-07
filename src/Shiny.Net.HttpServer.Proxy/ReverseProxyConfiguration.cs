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
        ArgumentNullException.ThrowIfNull(section);

        var clusters = new Dictionary<string, ProxyCluster>(StringComparer.OrdinalIgnoreCase);

        foreach (var child in section.GetSection("Clusters").GetChildren())
        {
            var id = child["ClusterId"] ?? child.Key;

            var cluster = existing is not null && existing.Clusters.TryGetValue(id, out var carried)
                ? carried
                : new ProxyCluster(id);

            ReadCluster(child, cluster);
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

        return new ReverseProxyConfiguration(routes, clusters);
    }

    /// <summary>
    /// YARP writes a catch-all as <c>{**rest}</c> and this server's router writes it <c>{*rest}</c>.
    /// The two mean the same thing, and a configuration file copied from one to the other should not
    /// fail to parse over a star.
    /// </summary>
    internal static string NormalizeTemplate(string path) => path.Replace("{**", "{*", StringComparison.Ordinal);

    static void ReadCluster(IConfigurationSection section, ProxyCluster cluster)
    {
        if (LoadBalancingPolicies.Parse(section["LoadBalancingPolicy"]) is { } policy)
            cluster.LoadBalancer = policy;

        var affinity = section.GetSection("SessionAffinity");
        if (affinity.Exists())
        {
            cluster.SessionAffinity.Mode = Enum<SessionAffinityMode>(affinity["Mode"]) ?? cluster.SessionAffinity.Mode;
            cluster.SessionAffinity.CookieName = affinity["CookieName"] ?? cluster.SessionAffinity.CookieName;
            cluster.SessionAffinity.HeaderName = affinity["HeaderName"] ?? cluster.SessionAffinity.HeaderName;
            cluster.SessionAffinity.FailurePolicy = Enum<AffinityFailurePolicy>(affinity["FailurePolicy"]) ?? cluster.SessionAffinity.FailurePolicy;
            cluster.SessionAffinity.SecureCookie = Bool(affinity["SecureCookie"]) ?? cluster.SessionAffinity.SecureCookie;
            cluster.SessionAffinity.CookieMaxAge = Time(affinity["CookieMaxAge"]) ?? cluster.SessionAffinity.CookieMaxAge;
        }

        var active = section.GetSection("HealthCheck:Active");
        if (active.Exists())
        {
            cluster.HealthCheck.Active.Enabled = Bool(active["Enabled"]) ?? cluster.HealthCheck.Active.Enabled;
            cluster.HealthCheck.Active.Interval = Time(active["Interval"]) ?? cluster.HealthCheck.Active.Interval;
            cluster.HealthCheck.Active.Timeout = Time(active["Timeout"]) ?? cluster.HealthCheck.Active.Timeout;
            cluster.HealthCheck.Active.Path = active["Path"] ?? cluster.HealthCheck.Active.Path;
        }

        var passive = section.GetSection("HealthCheck:Passive");
        if (passive.Exists())
        {
            cluster.HealthCheck.Passive.Enabled = Bool(passive["Enabled"]) ?? cluster.HealthCheck.Passive.Enabled;
            cluster.HealthCheck.Passive.FailureThreshold = Int(passive["FailureThreshold"]) ?? cluster.HealthCheck.Passive.FailureThreshold;
            cluster.HealthCheck.Passive.ReactivationPeriod = Time(passive["ReactivationPeriod"]) ?? cluster.HealthCheck.Passive.ReactivationPeriod;
        }

        var request = section.GetSection("HttpRequest");
        if (request.Exists())
        {
            cluster.Forwarding.Timeout = Time(request["ActivityTimeout"]) ?? cluster.Forwarding.Timeout;
            cluster.Forwarding.RewriteHost = Bool(request["RewriteHost"]) ?? cluster.Forwarding.RewriteHost;
            cluster.Forwarding.ForwardUpgrades = Bool(request["ForwardUpgrades"]) ?? cluster.Forwarding.ForwardUpgrades;

            if (request["Version"] is { Length: > 0 } version && Version.TryParse(version, out var parsed))
                cluster.Forwarding.RequestVersion = parsed;
        }

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

        cluster.SetDestinations(destinations);
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
