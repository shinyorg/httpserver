namespace Shiny.Net.HttpServer.Mediator;

/// <summary>
/// Publishes every endpoint declared on this handler under a shared route prefix.
/// <para>
/// Everything set here applies to all of them, and anything set on an individual
/// <see cref="MediatorHttpAttribute"/> wins over it — which is the usual shape: the group carries
/// the policy, the endpoint carries the exception.
/// </para>
/// <code>
/// [MediatorHttpGroup("/api/widgets", RequiresAuthorization = true, Tags = ["Widgets"])]
/// public class WidgetHandlers : IRequestHandler&lt;GetWidget, Widget&gt;
/// {
///     [MediatorHttpGet("/{id}")]
///     public Task&lt;Widget&gt; Handle(GetWidget request, IMediatorContext context, CancellationToken ct) => …;
/// }
/// </code>
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class MediatorHttpGroupAttribute(string prefix) : Attribute
{
    /// <summary>The route prefix shared by every endpoint on this handler.</summary>
    public string Prefix { get; } = prefix;

    /// <summary>Requires an authenticated, authorized caller for every endpoint in the group.</summary>
    public bool RequiresAuthorization { get; set; }

    /// <summary>Named authorization policies every endpoint in the group enforces.</summary>
    public string[]? AuthorizationPolicies { get; set; }

    /// <summary>Roles every endpoint in the group requires.</summary>
    public string[]? Roles { get; set; }

    /// <summary>Exempts the group from authorization, including from a fallback policy.</summary>
    public bool AllowAnonymous { get; set; }

    /// <summary>OpenAPI tags applied to every endpoint in the group.</summary>
    public string[]? Tags { get; set; }

    /// <summary>OpenAPI summary applied to every endpoint that does not set its own.</summary>
    public string? Summary { get; set; }

    /// <summary>OpenAPI description applied to every endpoint that does not set its own.</summary>
    public string? Description { get; set; }

    /// <summary>Omits the whole group from the OpenAPI document without unmapping it.</summary>
    public bool ExcludeFromDescription { get; set; }

    /// <summary>Named CORS policy applied to every endpoint in the group.</summary>
    public string? CorsPolicy { get; set; }

    /// <summary>Exempts the group from CORS, including from the default policy.</summary>
    public bool DisableCors { get; set; }

    /// <summary>Named rate limit policy applied to every endpoint in the group.</summary>
    public string? RateLimitingPolicy { get; set; }

    /// <summary>Exempts the group from rate limiting, including from the global policy.</summary>
    public bool DisableRateLimiting { get; set; }

    /// <summary>
    /// Named IP filter policy applied to every endpoint in the group.
    /// <para>This has no ASP.NET counterpart; it is one of the things this server does itself.</para>
    /// </summary>
    public string? IpFilterPolicy { get; set; }

    /// <summary>Exempts the group from the IP filter, including from the default policy.</summary>
    public bool AllowAnyIp { get; set; }
}

/// <summary>Publishes the decorated <c>Handle</c> method as an HTTP GET endpoint.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class MediatorHttpGetAttribute(string uriTemplate) : MediatorHttpAttribute(uriTemplate, "GET");

/// <summary>Publishes the decorated <c>Handle</c> method as an HTTP POST endpoint.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class MediatorHttpPostAttribute(string uriTemplate) : MediatorHttpAttribute(uriTemplate, "POST");

/// <summary>Publishes the decorated <c>Handle</c> method as an HTTP PUT endpoint.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class MediatorHttpPutAttribute(string uriTemplate) : MediatorHttpAttribute(uriTemplate, "PUT");

/// <summary>Publishes the decorated <c>Handle</c> method as an HTTP PATCH endpoint.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class MediatorHttpPatchAttribute(string uriTemplate) : MediatorHttpAttribute(uriTemplate, "PATCH");

/// <summary>Publishes the decorated <c>Handle</c> method as an HTTP DELETE endpoint.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class MediatorHttpDeleteAttribute(string uriTemplate) : MediatorHttpAttribute(uriTemplate, "DELETE");

/// <summary>
/// Publishes a mediator handler's <c>Handle</c> method as an HTTP endpoint. Use the verb-specific
/// subclasses in source.
/// <para>
/// How the contract is filled in depends on the verb, and it is not a preference — it is what a
/// request of that shape actually carries. <b>GET</b> and <b>DELETE</b> bind each member of the
/// contract from a route token or the query string. <b>POST</b>, <b>PUT</b> and <b>PATCH</b> read
/// the whole contract from the JSON body, and any route token that names a member is applied over
/// the top of it, so <c>PUT /widgets/{id}</c> works the way you would expect.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public abstract class MediatorHttpAttribute(string uriTemplate, string method) : Attribute
{
    /// <summary>The route template, appended to <see cref="MediatorHttpGroupAttribute.Prefix"/>.</summary>
    public string UriTemplate { get; } = uriTemplate;

    /// <summary>The HTTP verb, as the uppercase method name.</summary>
    public string Method { get; } = method;

    /// <summary>OpenAPI operation id. Defaults to the handler and contract names.</summary>
    public string? OperationId { get; set; }

    /// <summary>Requires an authenticated, authorized caller.</summary>
    public bool RequiresAuthorization { get; set; }

    /// <summary>Named authorization policies this endpoint enforces.</summary>
    public string[]? AuthorizationPolicies { get; set; }

    /// <summary>Roles this endpoint requires.</summary>
    public string[]? Roles { get; set; }

    /// <summary>Exempts this endpoint from authorization, including from a group or fallback policy.</summary>
    public bool AllowAnonymous { get; set; }

    /// <summary>OpenAPI tags, added to any the group declared.</summary>
    public string[]? Tags { get; set; }

    /// <summary>OpenAPI summary.</summary>
    public string? Summary { get; set; }

    /// <summary>OpenAPI description.</summary>
    public string? Description { get; set; }

    /// <summary>Omits this endpoint from the OpenAPI document without unmapping it.</summary>
    public bool ExcludeFromDescription { get; set; }

    /// <summary>Named CORS policy for this endpoint.</summary>
    public string? CorsPolicy { get; set; }

    /// <summary>Exempts this endpoint from CORS, including from the default policy.</summary>
    public bool DisableCors { get; set; }

    /// <summary>Named rate limit policy for this endpoint.</summary>
    public string? RateLimitingPolicy { get; set; }

    /// <summary>Exempts this endpoint from rate limiting, including from the global policy.</summary>
    public bool DisableRateLimiting { get; set; }

    /// <summary>Named IP filter policy for this endpoint.</summary>
    public string? IpFilterPolicy { get; set; }

    /// <summary>Exempts this endpoint from the IP filter, including from the default policy.</summary>
    public bool AllowAnyIp { get; set; }

    /// <summary>
    /// The SSE <c>event:</c> name for a stream request. Ignored for anything else.
    /// <para>Left null, frames carry data only, which is what a browser's default
    /// <c>onmessage</c> handler reads.</para>
    /// </summary>
    public string? EventName { get; set; }

    /// <summary>
    /// Status code returned by a command that completes without a result. Defaults to
    /// <c>204 No Content</c>; set it to 200 if a client insists on a body-less 200.
    /// </summary>
    public int SuccessStatusCode { get; set; } = 204;
}
