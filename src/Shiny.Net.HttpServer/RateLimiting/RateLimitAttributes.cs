namespace Shiny.Net.HttpServer;

/// <summary>
/// Applies a named rate limit policy to a generated endpoint class or one of its methods.
/// <code>
/// [Route("/api/media")]
/// public class MediaEndpoints
/// {
///     [Post("/upload")] [EnableRateLimiting("uploads")] public Task&lt;IActionResult&gt; Upload(...) => ...;
///     [Get("/health")] [DisableRateLimiting] public string Health() => "ok";
/// }
/// </code>
/// <para>
/// On a method it replaces the class's policy: a route counts against one bucket, not two.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class EnableRateLimitingAttribute(string policy) : Attribute
{
    /// <summary>Name of a policy registered with <c>AddRateLimiter</c>.</summary>
    public string Policy { get; } = policy;
}

/// <summary>
/// Exempts an endpoint from rate limiting, including from the global policy — the health check a
/// monitor polls every second.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class DisableRateLimitingAttribute : Attribute;
