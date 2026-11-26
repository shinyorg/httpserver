using Shiny.Net.HttpServer.Routing;

namespace Shiny.Net.HttpServer.Security;

/// <summary>
/// What an endpoint requires, attached to it as metadata. The generator emits this from
/// <c>[Authorize]</c> and <c>[AllowAnonymous]</c>; raw routes get it from
/// <see cref="HttpServerAuthorizationExtensions.RequireAuthorization"/>, or from passing either attribute
/// as metadata — <c>Map(…, new AuthorizeAttribute("admin"))</c> — which is folded in here when it is attached.
/// </summary>
public sealed class AuthorizationMetadata
{
    /// <summary>Named policies that must all pass. Empty with <see cref="Required"/> means the default policy.</summary>
    public IList<string> Policies { get; } = [];

    /// <summary>
    /// Roles, any one of which satisfies the requirement — a single requirement, as though it came from
    /// one <c>[Authorize(Roles = "a,b")]</c>. Use <see cref="RoleRequirements"/> for roles that must all be met.
    /// </summary>
    public IList<string> Roles { get; } = [];

    /// <summary>
    /// Role requirements that must <b>all</b> be met, one per <c>[Authorize(Roles = …)]</c>: within one
    /// requirement any listed role will do, across requirements every one has to pass. A class marked
    /// <c>[Authorize(Roles = "admin")]</c> with a method marked <c>[Authorize(Roles = "viewer")]</c> needs a
    /// caller in both roles — the method narrows the class, it cannot widen it.
    /// </summary>
    public IList<IReadOnlyList<string>> RoleRequirements { get; } = [];

    /// <summary>True when the endpoint carries <c>[Authorize]</c> in any form.</summary>
    public bool Required { get; set; }

    /// <summary>True when the endpoint opted out. Beats everything else, including the fallback policy.</summary>
    public bool AllowAnonymous { get; set; }
}

/// <summary>Requiring authorization on a raw route, without an attribute to hang it on.</summary>
public static class HttpServerAuthorizationExtensions
{
    /// <summary>
    /// Requires authorization for every route the preceding call mapped — the one route of a
    /// <c>MapGet</c>, or every route of a <c>MapGroup</c>, <c>MapModule</c> or generated <c>Map{Class}()</c>.
    /// <code>
    /// app.MapGet("/secrets", ctx => ...).RequireAuthorization("admin");
    /// app.MapGroup("/admin", admin => { ... }).RequireAuthorization("admin");
    /// </code>
    /// With no policy named, the default policy applies — an authenticated caller. A route in the set that
    /// says <c>[AllowAnonymous]</c> about itself stays open.
    /// </summary>
    public static HttpServer RequireAuthorization(this HttpServer server, params string[] policies)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(policies);

        LastMappedConventions.Apply<AuthorizationMetadata>(server, nameof(RequireAuthorization), metadata =>
        {
            metadata.Required = true;

            foreach (var policy in policies)
                metadata.Policies.Add(policy);
        });

        return server;
    }

    /// <summary>Exempts every route the preceding call mapped, including from a fallback policy.</summary>
    public static HttpServer AllowAnonymous(this HttpServer server)
    {
        ArgumentNullException.ThrowIfNull(server);

        LastMappedConventions.Apply<AuthorizationMetadata>(server, nameof(AllowAnonymous), m => m.AllowAnonymous = true);
        return server;
    }
}
