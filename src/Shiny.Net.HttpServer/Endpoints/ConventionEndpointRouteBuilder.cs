namespace Shiny.Net.HttpServer;

/// <summary>
/// A group builder that applies a convention to every route mapped through it — and through any
/// group nested under it.
/// <para>
/// The convention runs as each route is mapped, before the route's own metadata is attached, so
/// anything a route says about itself (generated attributes, a chained <c>Disable…</c>) comes later
/// and wins under <see cref="Routing.Endpoint.GetMetadata{T}"/>'s last-wins rule.
/// </para>
/// </summary>
sealed class ConventionEndpointRouteBuilder(IEndpointRouteBuilder inner, Action<RouteEndpointBuilder> convention) : IEndpointRouteBuilder
{
    public IServiceProvider? Services => inner.Services;

    public string Prefix => inner.Prefix;

    public RouteEndpointBuilder Map(string method, string pattern, RequestDelegate handler)
    {
        var route = inner.Map(method, pattern, handler);
        convention(route);

        return route;
    }

    public IEndpointRouteBuilder MapGroup(string prefix)
        => new ConventionEndpointRouteBuilder(inner.MapGroup(prefix), convention);
}
