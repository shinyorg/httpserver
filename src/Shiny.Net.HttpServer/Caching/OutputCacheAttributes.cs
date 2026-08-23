namespace Shiny.Net.HttpServer;

/// <summary>
/// Caches a generated endpoint's response.
/// <code>
/// [Route("/api/catalog")]
/// public class CatalogEndpoints
/// {
///     [Get("/")] [OutputCache(Seconds = 30)] public Task&lt;IActionResult&gt; List(CancellationToken ct) => ...;
///     [Get("/live")] [NoOutputCache] public Task&lt;IActionResult&gt; Live(CancellationToken ct) => ...;
/// }
/// </code>
/// <para>
/// Only GET and HEAD are ever stored, and only for unauthenticated callers unless the policy says
/// otherwise — a cache keyed on the URL alone cannot tell two users apart.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class OutputCacheAttribute : Attribute
{
    public OutputCacheAttribute()
    {
    }

    /// <param name="policy">Name of a policy registered with <c>AddOutputCache</c>.</param>
    public OutputCacheAttribute(string policy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policy);
        this.Policy = policy;
    }

    /// <summary>How long to keep the response. Wins over <see cref="Policy"/>.</summary>
    public int Seconds { get; set; }

    public string? Policy { get; }
}

/// <summary>Exempts an endpoint from output caching, the default policy included.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class NoOutputCacheAttribute : Attribute;
