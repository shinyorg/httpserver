namespace Shiny.Net.HttpServer;

/// <summary>
/// Applies a named CORS policy to a generated endpoint class or one of its methods.
/// <code>
/// [Route("/api/public")]
/// [EnableCors("public")]
/// public class PublicEndpoints
/// {
///     [Get("/status")] public string Status() => "ok";
///     [Get("/internal")] [DisableCors] public string Internal() => "...";
/// }
/// </code>
/// <para>
/// On a method it replaces whatever the class asked for, because a route has exactly one CORS
/// policy — unlike authorization, where two requirements can both apply.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class EnableCorsAttribute(string policy) : Attribute
{
    /// <summary>Name of a policy registered with <c>AddCors</c>.</summary>
    public string Policy { get; } = policy;
}

/// <summary>
/// Exempts an endpoint from CORS entirely, including from the default policy and from a class-level
/// <c>[EnableCors]</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class DisableCorsAttribute : Attribute;
