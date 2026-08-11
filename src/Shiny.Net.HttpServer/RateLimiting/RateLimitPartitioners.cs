using System.Security.Claims;
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
    /// </summary>
    public static Func<HttpContext, string?> ByIpAddress { get; } = static context
        => context.Connection.RemoteIpAddress is { } address
            ? IpAddressRange.Normalize(address).ToString()
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

    /// <summary>By address and path together, so one expensive endpoint cannot starve the others.</summary>
    public static Func<HttpContext, string?> ByIpAddressAndPath { get; } = static context
        => $"{ByIpAddress(context)}|{context.Request.Path}";
}
