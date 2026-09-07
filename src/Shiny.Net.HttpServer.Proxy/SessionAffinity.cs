using System.Security.Cryptography;
using System.Text;

namespace Shiny.Net.HttpServer.Proxy;

/// <summary>How a caller is pinned to a destination, if at all.</summary>
public enum SessionAffinityMode
{
    Disabled = 0,

    /// <summary>A cookie the proxy sets and reads. What a browser wants.</summary>
    Cookie,

    /// <summary>A request header the caller echoes back. What a non-browser client wants.</summary>
    Header
}

/// <summary>What to do when the destination a caller is pinned to is no longer available.</summary>
public enum AffinityFailurePolicy
{
    /// <summary>Pick a new destination and re-pin. The caller loses whatever state was on the old one.</summary>
    Redistribute = 0,

    /// <summary>Answer 503. For an upstream where landing on a different instance is worse than failing.</summary>
    Return503
}

/// <summary>Pinning a caller to the destination it reached first.</summary>
public sealed class SessionAffinityOptions
{
    public SessionAffinityMode Mode { get; set; } = SessionAffinityMode.Disabled;

    /// <summary>Cookie carrying the affinity key, in <see cref="SessionAffinityMode.Cookie"/> mode.</summary>
    public string CookieName { get; set; } = ".Shiny.Proxy.Affinity";

    /// <summary>Header carrying the affinity key, in <see cref="SessionAffinityMode.Header"/> mode.</summary>
    public string HeaderName { get; set; } = "X-Shiny-Affinity";

    public AffinityFailurePolicy FailurePolicy { get; set; } = AffinityFailurePolicy.Redistribute;

    /// <summary>Lifetime of the affinity cookie. Null makes it a session cookie.</summary>
    public TimeSpan? CookieMaxAge { get; set; }

    /// <summary>Marks the affinity cookie <c>Secure</c>. Set it when the edge is HTTPS.</summary>
    public bool SecureCookie { get; set; }

    public SameSiteMode SameSite { get; set; } = SameSiteMode.Lax;

    /// <summary>Path the affinity cookie is scoped to.</summary>
    public string CookiePath { get; set; } = "/";
}

/// <summary>
/// Reading and writing the affinity key.
/// <para>
/// The key is a hash of the cluster and destination ids rather than the destination id itself. A
/// destination id is usually a host name, sometimes an internal one, and putting it in a cookie
/// publishes the shape of the backend to anyone who opens dev tools - for no gain, since the proxy
/// is the only thing that ever has to recognise it.
/// </para>
/// </summary>
static class AffinityKey
{
    public static string For(string clusterId, string destinationId)
    {
        var bytes = Encoding.UTF8.GetBytes(clusterId + " " + destinationId);
        var hash = SHA256.HashData(bytes);

        // 12 bytes is 96 bits of a hash whose only job is to be told apart from the handful of
        // other destinations in the same cluster. Base64url so it survives a cookie and a header
        // without escaping.
        return Convert.ToBase64String(hash, 0, 12).Replace('+', '-').Replace('/', '_');
    }
}
