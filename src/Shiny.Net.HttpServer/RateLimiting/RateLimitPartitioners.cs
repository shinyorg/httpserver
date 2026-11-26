using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using Shiny.Net.HttpServer.Routing;
using Shiny.Net.HttpServer.Security;

namespace Shiny.Net.HttpServer.RateLimiting;

/// <summary>
/// Ready-made answers to "who is this request counted against?".
/// <para>
/// A partitioner returning null exempts the request from the policy entirely, which is how
/// <see cref="ByHeader"/> can limit callers presenting an API key while leaving everyone else to a
/// different policy.
/// </para>
/// </summary>
public static class RateLimitPartitioners
{
    /// <summary>Everything in one bucket — a limit on the server as a whole.</summary>
    public static Func<HttpContext, string?> Global { get; } = static _ => "global";

    /// <summary>
    /// By caller address, which is the sensible default. IPv4-mapped IPv6 is unmapped first, so the
    /// same client does not get two buckets depending on which socket accepted it.
    /// <para>
    /// IPv6 is counted per <c>/64</c>, not per address. A single host is routinely handed a whole /64 —
    /// and privacy extensions rotate through it on their own — so a per-address bucket is one an abusive
    /// client can swap for a fresh one at will.
    /// </para>
    /// </summary>
    public static Func<HttpContext, string?> ByIpAddress { get; } = static context
        => context.Connection.RemoteIpAddress is { } address
            ? PartitionAddress(address)
            : "unknown";

    /// <summary>By a header's value — an API key, a tenant id. Requests without it are exempt.</summary>
    public static Func<HttpContext, string?> ByHeader(string headerName)
    {
        ArgumentException.ThrowIfNullOrEmpty(headerName);

        return context => context.Request.Headers.GetFirst(headerName) is { Length: > 0 } value ? value : null;
    }

    /// <summary>
    /// By authenticated identity, falling back to the caller's address for anonymous requests — so
    /// signing out is not a way to get a fresh allowance.
    /// </summary>
    public static Func<HttpContext, string?> ByUser { get; } = static context =>
    {
        var user = context.User;

        if (user.Identity?.IsAuthenticated == true)
        {
            var id = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? user.FindFirst("sub")?.Value
                ?? user.Identity.Name;

            if (!string.IsNullOrEmpty(id))
                return $"user:{id}";
        }

        return ByIpAddress(context);
    };

    /// <summary>
    /// By address and endpoint together, so one expensive endpoint cannot starve the others.
    /// <para>
    /// The endpoint is the route the request selects — its template, so <c>/users/1</c> and
    /// <c>/users/2</c> share <c>/users/{id}</c>'s allowance — or, for a request no route claims, its path
    /// normalised the way the router compares paths: case-insensitive and ignoring a trailing slash. Keyed
    /// on the raw path, <c>/Login</c>, <c>/LOGIN</c> and <c>/login/</c> all reached the same handler on
    /// separate allowances.
    /// </para>
    /// </summary>
    public static Func<HttpContext, string?> ByIpAddressAndPath { get; } = static context
        => $"{ByIpAddress(context)}|{EndpointKey(context)}";

    /// <summary>Where <see cref="RateLimitMiddleware"/> leaves the endpoint it resolved, for the partitioner to read.</summary>
    internal static readonly object ResolvedEndpointKey = new();

    static string EndpointKey(HttpContext context)
    {
        var endpoint = context.Endpoint
            ?? (context.Items.TryGetValue(ResolvedEndpointKey, out var resolved) ? resolved as Endpoint : null);

        if (endpoint is RouteEndpoint route)
            return "route:" + route.Template.RawText;

        // Upper-cased because that is what OrdinalIgnoreCase, the router's comparison, folds to.
        var path = context.Request.Path;

        if (path.Length > 1 && path[^1] == '/')
            path = path[..^1];

        return "path:" + path.ToUpperInvariant();
    }

    static string PartitionAddress(IPAddress address)
    {
        var normalized = IpAddressRange.Normalize(address);

        if (normalized.AddressFamily != AddressFamily.InterNetworkV6)
            return normalized.ToString();

        Span<byte> bytes = stackalloc byte[16];
        normalized.TryWriteBytes(bytes, out _);
        bytes[8..].Clear();

        return new IPAddress(bytes).ToString() + "/64";
    }
}
