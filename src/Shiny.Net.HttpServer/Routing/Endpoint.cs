using Shiny.Net.HttpServer.Security;

namespace Shiny.Net.HttpServer.Routing;

/// <summary>
/// A handler the router can select, plus whatever metadata was attached when it was registered.
/// Shaped after ASP.NET Core's <c>Endpoint</c> so the mental model carries over.
/// </summary>
public class Endpoint
{
    readonly List<object> metadata;

    public Endpoint(RequestDelegate requestDelegate, string displayName, params object[]? metadata)
    {
        ArgumentNullException.ThrowIfNull(requestDelegate);

        this.RequestDelegate = requestDelegate;
        this.DisplayName = displayName;
        this.metadata = [];

        if (metadata is not null)
        {
            foreach (var item in metadata)
                this.Add(item);
        }
    }

    public RequestDelegate RequestDelegate { get; }

    /// <summary>Human-readable name used in logs and diagnostics, e.g. <c>GET /users/{id}</c>.</summary>
    public string DisplayName { get; }

    public IReadOnlyList<object> Metadata => this.metadata;

    /// <summary>
    /// Attaches metadata after registration. This is what lets a raw route be described for
    /// OpenAPI without every <c>Map</c> overload growing a parameter for it.
    /// </summary>
    public Endpoint WithMetadata(object item)
    {
        ArgumentNullException.ThrowIfNull(item);

        this.Add(item);
        return this;
    }

    /// <summary>
    /// Adds an item, folding <see cref="AuthorizeAttribute"/> and <see cref="AllowAnonymousAttribute"/> into
    /// <see cref="AuthorizationMetadata"/> as it goes.
    /// <para>
    /// Those attributes are the obvious thing to hand a raw route — <c>Map(…, new AuthorizeAttribute("admin"))</c>
    /// — but everything that enforces or documents authorization reads <see cref="AuthorizationMetadata"/>. Left
    /// as they were, the attribute was accepted and did nothing: an endpoint that looked protected answered
    /// anyone. Folding them in here, once, means the middleware, the OpenAPI document and anything else see the
    /// same requirements, the same way the generator merges a class's attributes with a method's — policies all
    /// required, roles any one of them, <c>[AllowAnonymous]</c> above everything.
    /// </para>
    /// </summary>
    void Add(object item)
    {
        this.metadata.Add(item);

        switch (item)
        {
            case AuthorizeAttribute authorize:
            {
                var authorization = this.Authorization();
                authorization.Required = true;

                if (!String.IsNullOrWhiteSpace(authorize.Policy))
                    authorization.Policies.Add(authorize.Policy);

                if (authorize.Roles is { } roles)
                {
                    foreach (var role in roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        authorization.Roles.Add(role);
                }

                break;
            }

            case AllowAnonymousAttribute:
                this.Authorization().AllowAnonymous = true;
                break;
        }
    }

    AuthorizationMetadata Authorization()
    {
        if (this.GetMetadata<AuthorizationMetadata>() is { } existing)
            return existing;

        var created = new AuthorizationMetadata();
        this.metadata.Add(created);

        return created;
    }

    /// <summary>
    /// Returns the last metadata item assignable to <typeparamref name="T"/>, or null. Last wins so
    /// that metadata added closer to the endpoint overrides a convention applied to the whole group.
    /// </summary>
    public T? GetMetadata<T>() where T : class
    {
        for (var i = this.metadata.Count - 1; i >= 0; i--)
        {
            if (this.metadata[i] is T match)
                return match;
        }
        return null;
    }

    public override string ToString() => this.DisplayName;
}

/// <summary>An <see cref="Endpoint"/> selected by matching a route template and an HTTP method.</summary>
public sealed class RouteEndpoint : Endpoint
{
    public RouteEndpoint(
        RequestDelegate requestDelegate,
        string method,
        RouteTemplate template,
        params object[]? metadata
    ) : base(requestDelegate, $"{method} {template.RawText}", metadata)
    {
        this.Method = method;
        this.Template = template;
    }

    /// <summary>The HTTP method this endpoint answers, uppercased.</summary>
    public string Method { get; }

    public RouteTemplate Template { get; }
}
