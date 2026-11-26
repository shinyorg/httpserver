namespace Shiny.Net.HttpServer.Routing;

/// <summary>
/// The one place a convention chained onto <see cref="HttpServer"/> — <c>.RequireAuthorization()</c>,
/// <c>.RequireRateLimiting("x")</c>, <c>.WithRequestTimeout(…)</c> — finds what it applies to:
/// every route the preceding mapping call registered (<see cref="Router.LastMapped"/>), not just the
/// last route in the table.
/// </summary>
static class LastMappedConventions
{
    /// <summary>
    /// The routes the last mapping call registered. Throws when nothing has been mapped at all; empty when
    /// the last call mapped nothing (an empty group), in which case the convention has nothing to do.
    /// </summary>
    public static IReadOnlyList<RouteEndpoint> LastMapped(HttpServer server, string convention)
    {
        ArgumentNullException.ThrowIfNull(server);

        if (!server.Router.HasMapped)
            throw new InvalidOperationException(
                $"{convention} applies to the routes the preceding Map call registered, and no route has been mapped yet."
            );

        return server.Router.LastMapped;
    }

    /// <summary>
    /// Finds or attaches <typeparamref name="T"/> on each of the last mapped routes and hands it to
    /// <paramref name="apply"/> — once per metadata instance, so an instance shared by several routes is
    /// not, say, given the same policy twice.
    /// </summary>
    public static void Apply<T>(HttpServer server, string convention, Action<T> apply) where T : class, new()
    {
        ArgumentNullException.ThrowIfNull(apply);

        var seen = new HashSet<T>(ReferenceEqualityComparer.Instance);

        foreach (var endpoint in LastMapped(server, convention))
        {
            var metadata = endpoint.GetMetadata<T>();

            if (metadata is null)
            {
                metadata = new T();
                endpoint.WithMetadata(metadata);
            }

            if (seen.Add(metadata))
                apply(metadata);
        }
    }

    /// <summary>Attaches metadata made fresh for each of the last mapped routes.</summary>
    public static void Attach(HttpServer server, string convention, Func<object> create)
    {
        ArgumentNullException.ThrowIfNull(create);

        foreach (var endpoint in LastMapped(server, convention))
            endpoint.WithMetadata(create());
    }
}
