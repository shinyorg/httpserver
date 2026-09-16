namespace Shiny.Net.HttpServer;

/// <summary>
/// Requires authorization for an endpoint class or a single method.
/// <code>
/// [Route("/api/admin")]
/// [Authorize(Policy = "admin")]
/// public class AdminEndpoints
/// {
///     [Get("/stats")] public Stats Get() => ...;              // needs the admin policy
///     [Get("/health")] [AllowAnonymous] public string Ping() => "ok";
/// }
/// </code>
/// <para>
/// Applied to a class it covers every endpoint on it; applied to a method it adds to whatever the
/// class asked for. A bare <c>[Authorize]</c> uses <c>AuthorizationOptions.DefaultPolicy</c>.
/// </para>
/// <para>
/// A raw route can take an instance as metadata, with the same effect:
/// <c>app.Map("GET", "/admin", handler, new AuthorizeAttribute("admin"))</c>.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class AuthorizeAttribute : Attribute
{
    public AuthorizeAttribute()
    {
    }

    public AuthorizeAttribute(string policy) => this.Policy = policy;

    /// <summary>Name of a policy registered with <c>AddAuthorization</c>.</summary>
    public string? Policy { get; set; }

    /// <summary>Comma-separated roles; any one of them satisfies the requirement.</summary>
    public string? Roles { get; set; }
}

/// <summary>
/// Exempts an endpoint from authorization, including from a class-level <c>[Authorize]</c> and from
/// <c>AuthorizationOptions.FallbackPolicy</c>. Always wins — also when a raw route is given an instance as
/// metadata alongside an <see cref="AuthorizeAttribute"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class AllowAnonymousAttribute : Attribute;
