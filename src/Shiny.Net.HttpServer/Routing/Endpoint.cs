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
        this.metadata = metadata is { Length: > 0 } ? [.. metadata] : [];
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

        this.metadata.Add(item);
        return this;
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
