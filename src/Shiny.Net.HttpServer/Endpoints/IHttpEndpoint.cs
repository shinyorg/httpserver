using Microsoft.Extensions.DependencyInjection;

namespace Shiny.Net.HttpServer;

/// <summary>
/// One endpoint, one class.
/// <para>
/// The verb and route go on the class instead of on a method, and the class has exactly one handler
/// — so the file you open to change an endpoint contains that endpoint and nothing else. Binding,
/// DI and results work identically to a <c>[Route]</c> controller; this is the same machinery with
/// the grouping turned off.
/// </para>
/// <code>
/// [Get("/health/{component}")]
/// public class HealthEndpoint(IHealthChecks checks) : IHttpEndpoint
/// {
///     public async Task&lt;IActionResult&gt; HandleAsync(string component, CancellationToken ct)
///         => await checks.RunAsync(component, ct) ? new OkResult() : new StatusCodeResult(503);
/// }
///
/// app.MapMyAppEndpoints();   // picks these up alongside controllers
/// </code>
/// <para>
/// The handler is the single public method named <c>Handle</c> or <c>HandleAsync</c>. Everything the
/// generator does for a controller method — route, query, header, body and service binding, the
/// return-type conventions, <c>[Authorize]</c>, <c>[Produces]</c> — applies unchanged.
/// </para>
/// </summary>
public interface IHttpEndpoint;

/// <summary>
/// A group of routes that registers itself.
/// <para>
/// Where <see cref="IHttpEndpoint"/> is one endpoint decided at compile time, a module is a set of
/// them decided at run time — a plugin, a feature that is only mounted when a licence says so, an
/// admin surface that appears when a toggle flips. Modules can be mounted and unmounted while the
/// server is running.
/// </para>
/// <code>
/// public sealed class AdminModule : IEndpointModule
/// {
///     public void Map(IEndpointRouteBuilder endpoints)
///     {
///         endpoints.MapGet("/admin/stats", ctx => ...);
///         endpoints.MapPost("/admin/reset", ctx => ...).RequireAuthorization("admin");
///     }
/// }
///
/// app.MapModule(new AdminModule());          // and later:
/// app.UnmapModule(typeof(AdminModule));
/// </code>
/// </summary>
public interface IEndpointModule
{
    /// <summary>Registers this module's routes.</summary>
    void Map(IEndpointRouteBuilder endpoints);
}

/// <summary>
/// What a module gets to register routes with. Every route added through it is tagged with the
/// module, which is what makes unmounting the whole group one call.
/// </summary>
public interface IEndpointRouteBuilder
{
    /// <summary>Services available while mapping. Not the request scope — this runs at mount time.</summary>
    IServiceProvider? Services { get; }

    /// <summary>Prefix applied to every route registered through this builder.</summary>
    string Prefix { get; }

    /// <summary>Maps a route and returns the endpoint, so metadata can be attached to it.</summary>
    RouteEndpointBuilder Map(string method, string pattern, RequestDelegate handler);

    /// <summary>Returns a builder that prefixes everything mapped through it.</summary>
    IEndpointRouteBuilder MapGroup(string prefix);
}
