namespace Shiny.Net.HttpServer;

/// <summary>
/// Applies a named IP filter policy to a generated endpoint class or one of its methods.
/// <code>
/// [Route("/api/admin")]
/// [RequireIpFilter("lan-only")]
/// public class AdminEndpoints
/// {
///     [Get("/keys")] public Task&lt;IActionResult&gt; Keys() => ...;
///     [Get("/ping")] [AllowAnyIp] public string Ping() => "ok";
/// }
/// </code>
/// <para>
/// On a method it replaces the class's policy — an address is inside a range or it is not, so
/// stacking two policies would only be a slower way to write one.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class RequireIpFilterAttribute(string policy) : Attribute
{
    /// <summary>Name of a policy registered with <c>AddIpFilter</c>.</summary>
    public string Policy { get; } = policy;
}

/// <summary>
/// Exempts an endpoint from the IP filter, including from the default policy and from a class-level
/// <c>[RequireIpFilter]</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class AllowAnyIpAttribute : Attribute;
