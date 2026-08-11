namespace Shiny.Net.HttpServer.DocumentDb;

/// <summary>
/// The routes one <c>MapDocuments</c> call just registered, so a policy can be stated once for the
/// whole resource.
/// <para>
/// ASP.NET has <c>RouteGroupBuilder</c> for this; this server attaches metadata per route, because a
/// route is the thing its middleware looks at. Rather than make every caller loop, this holds the
/// routes the call produced and fans each method out across all of them — so
/// <c>.RequireAuthorization("orders")</c> protects the list, the by-id, the writes and the stream,
/// and adding an operation later cannot quietly leave one unprotected.
/// </para>
/// <code>
/// app.MapDocuments&lt;Order&gt;("/orders", o => o.Operations = DocumentEndpoints.All)
///    .RequireAuthorization("orders")
///    .RequireRateLimiting("api");
/// </code>
/// </summary>
public sealed class DocumentResourceBuilder
{
    readonly List<RouteEndpointBuilder> routes;

    internal DocumentResourceBuilder(List<RouteEndpointBuilder> routes) => this.routes = routes;

    /// <summary>Every route this resource mapped, for anything the methods below do not cover.</summary>
    public IReadOnlyList<RouteEndpointBuilder> Routes => this.routes;

    /// <summary>Requires authorization on every route in the resource, optionally against named policies.</summary>
    public DocumentResourceBuilder RequireAuthorization(params string[] policies)
        => this.ForEach(route => route.RequireAuthorization(policies));

    /// <summary>Exempts every route in the resource from authorization, including from a fallback policy.</summary>
    public DocumentResourceBuilder AllowAnonymous()
        => this.ForEach(route => route.AllowAnonymous());

    /// <summary>Applies a named CORS policy to every route in the resource.</summary>
    public DocumentResourceBuilder RequireCors(string policyName)
        => this.ForEach(route => route.RequireCors(policyName));

    /// <summary>Exempts every route in the resource from CORS.</summary>
    public DocumentResourceBuilder DisableCors()
        => this.ForEach(route => route.DisableCors());

    /// <summary>Applies a named rate limit policy to every route in the resource.</summary>
    public DocumentResourceBuilder RequireRateLimiting(string policyName)
        => this.ForEach(route => route.RequireRateLimiting(policyName));

    /// <summary>Exempts every route in the resource from rate limiting.</summary>
    public DocumentResourceBuilder DisableRateLimiting()
        => this.ForEach(route => route.DisableRateLimiting());

    /// <summary>Applies a named IP filter policy to every route in the resource.</summary>
    public DocumentResourceBuilder RequireIpFilter(string policyName)
        => this.ForEach(route => route.RequireIpFilter(policyName));

    /// <summary>Exempts every route in the resource from the IP filter.</summary>
    public DocumentResourceBuilder AllowAnyIp()
        => this.ForEach(route => route.AllowAnyIp());

    /// <summary>Adds OpenAPI tags to every route in the resource.</summary>
    public DocumentResourceBuilder WithTags(params string[] tags)
        => this.ForEach(route => route.WithTags(tags));

    /// <summary>Omits the whole resource from the OpenAPI document without unmapping it.</summary>
    public DocumentResourceBuilder ExcludeFromDescription()
        => this.ForEach(route => route.ExcludeFromDescription());

    /// <summary>Attaches arbitrary metadata to every route in the resource.</summary>
    public DocumentResourceBuilder WithMetadata(object metadata)
        => this.ForEach(route => route.WithMetadata(metadata));

    /// <summary>Runs <paramref name="configure"/> against every route, for anything not covered above.</summary>
    public DocumentResourceBuilder ForEach(Action<RouteEndpointBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        foreach (var route in this.routes)
            configure(route);

        return this;
    }
}
