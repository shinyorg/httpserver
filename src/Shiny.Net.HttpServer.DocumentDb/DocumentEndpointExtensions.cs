using Microsoft.Extensions.DependencyInjection;
using Shiny.DocumentDb;
using Shiny.Net.HttpServer.DocumentDb.Internal;

namespace Shiny.Net.HttpServer.DocumentDb;

/// <summary>
/// Turns a document type into a complete HTTP resource in one line: list, by-id, count, create, replace,
/// merge-patch, delete and a live SSE tail.
/// </summary>
/// <remarks>
/// This is the same shape as <c>Shiny.DocumentDb.AspNetCore</c>, on a server that runs where ASP.NET Core
/// cannot — a .NET MAUI app, a trimmed host, an embedded appliance. The document engine underneath is
/// identical, so a filter, a scope or a cursor means the same thing on either.
/// </remarks>
public static class DocumentEndpointExtensions
{
    /// <summary>
    /// Maps <typeparamref name="T"/> as a REST resource under <paramref name="prefix"/>. Returns the resource,
    /// so authorization, rate limiting, CORS and IP filtering compose across every route it registered.
    /// </summary>
    /// <example>
    /// <code>
    /// app.MapDocuments&lt;Order&gt;("/orders", o =>
    /// {
    ///     o.Operations = DocumentEndpoints.All;
    ///     o.AllowFilterOn(x => x.Status, x => x.Total);
    ///     o.TypeInfo = AppJsonContext.Default.Order;
    ///     o.Scope&lt;ITenantContext&gt;((tenant, _) => x => x.TenantId == tenant.TenantId);
    /// })
    /// .RequireAuthorization("orders");
    /// </code>
    /// </example>
    public static DocumentResourceBuilder MapDocuments<T>(
        this HttpServer server,
        string prefix,
        Action<DocumentEndpointOptions<T>>? configure = null
    ) where T : class
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        var options = new DocumentEndpointOptions<T>();
        configure?.Invoke(options);

        Validate(server.Services, options.Operations, options.RequiredServices, options.StoreName, typeof(T).Name);

        List<RouteEndpointBuilder>? routes = null;
        server.MapGroup(prefix, group => routes = DocumentEndpointHandlers<T>.Map(group, options));

        return new DocumentResourceBuilder(routes!);
    }

    /// <summary>Maps <typeparamref name="T"/> as a REST resource onto an existing route builder.</summary>
    public static DocumentResourceBuilder MapDocuments<T>(
        this IEndpointRouteBuilder endpoints,
        string prefix,
        Action<DocumentEndpointOptions<T>>? configure = null
    ) where T : class
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        var options = new DocumentEndpointOptions<T>();
        configure?.Invoke(options);

        Validate(endpoints.Services, options.Operations, options.RequiredServices, options.StoreName, typeof(T).Name);

        var group = endpoints.MapGroup(prefix);
        var routes = DocumentEndpointHandlers<T>.Map(group, options);

        return new DocumentResourceBuilder(routes);
    }

    /// <summary>
    /// Maps a <b>schema-free</b> JSON collection (<c>store.Collection(name)</c>) as a REST resource. Documents
    /// are plain JSON with no CLR type, filtered with the string grammar. Relational providers only.
    /// </summary>
    public static DocumentResourceBuilder MapDocumentCollection(
        this HttpServer server,
        string prefix,
        string collectionName,
        Action<DocumentCollectionEndpointOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);

        var options = new DocumentCollectionEndpointOptions();
        configure?.Invoke(options);

        Validate(server.Services, options.Operations, options.RequiredServices, options.StoreName, collectionName);

        List<RouteEndpointBuilder>? routes = null;
        server.MapGroup(prefix, group => routes = DocumentCollectionEndpointHandlers.Map(group, collectionName, options));

        return new DocumentResourceBuilder(routes!);
    }

    /// <summary>Maps a schema-free JSON collection onto an existing route builder.</summary>
    public static DocumentResourceBuilder MapDocumentCollection(
        this IEndpointRouteBuilder endpoints,
        string prefix,
        string collectionName,
        Action<DocumentCollectionEndpointOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);

        var options = new DocumentCollectionEndpointOptions();
        configure?.Invoke(options);

        Validate(endpoints.Services, options.Operations, options.RequiredServices, options.StoreName, collectionName);

        var group = endpoints.MapGroup(prefix);
        var routes = DocumentCollectionEndpointHandlers.Map(group, collectionName, options);

        return new DocumentResourceBuilder(routes);
    }

    /// <summary>
    /// Startup checks, so the failure is at boot rather than on the first request that needs the thing:
    /// every <c>Scope&lt;TService&gt;</c> service must be registered, and <c>/stream</c> is refused outright on
    /// a provider without change monitoring.
    /// </summary>
    static void Validate(
        IServiceProvider? services,
        DocumentEndpoints operations,
        IReadOnlyList<Type> requiredServices,
        string? storeName,
        string resource
    )
    {
        // Nothing can be checked before the container exists. A server configured through the builder has one
        // by the time routes are mapped; one built by hand may not, and silently skipping is better than
        // refusing to map at all.
        if (services is null)
            return;

        foreach (var serviceType in requiredServices)
        {
            if (services.GetService(serviceType) is null)
                throw new InvalidOperationException(
                    $"'{resource}' registers a Scope<{serviceType.Name}>(…) but no '{serviceType}' is registered. "
                    + "A scope that cannot resolve its service refuses every request, so this is a startup error."
                );
        }

        if (operations.HasFlag(DocumentEndpoints.Stream))
        {
            var store = storeName is null
                ? services.GetService<IDocumentStore>()
                : services.GetKeyedService<IDocumentStore>(storeName);

            if (store is not null and not IObservableDocumentStore)
                throw new InvalidOperationException(
                    $"'{resource}' maps DocumentEndpoints.Stream, but '{store.GetType().Name}' does not support "
                    + "change monitoring (IObservableDocumentStore). Drop the Stream flag or move to a provider "
                    + "that does — a /stream route that can only ever return 501 is worse than no route."
                );
        }
    }
}
