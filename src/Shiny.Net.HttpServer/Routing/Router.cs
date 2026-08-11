namespace Shiny.Net.HttpServer.Routing;

/// <summary>The outcome of matching one request against the route table.</summary>
public readonly struct RouteMatch
{
    internal RouteMatch(Endpoint? endpoint, string? allowedMethods)
    {
        this.Endpoint = endpoint;
        this.AllowedMethods = allowedMethods;
    }

    /// <summary>The selected endpoint, or null when nothing matched.</summary>
    public Endpoint? Endpoint { get; }

    /// <summary>
    /// Comma-separated methods registered for a path that matched by URL but not by method.
    /// Non-null exactly when the correct response is 405 with an <c>Allow</c> header.
    /// </summary>
    public string? AllowedMethods { get; }

    public bool IsMatch => this.Endpoint is not null;

    /// <summary>True when the path exists but the method does not — a 405, not a 404.</summary>
    public bool IsMethodNotAllowed => this.Endpoint is null && this.AllowedMethods is not null;
}

/// <summary>
/// The route table, mutable at any time.
/// <para>
/// Routes can be added and removed while the server is running. Each change builds a fresh
/// <see cref="RouteTable"/> and publishes it with a single volatile write, so a request in flight
/// either sees the whole change or none of it — never a trie mid-edit. A change that would produce
/// a duplicate route throws and leaves the live table exactly as it was.
/// </para>
/// </summary>
public sealed class Router
{
    readonly object gate = new();
    volatile RouteTable table = RouteTable.Empty;

    /// <summary>Every endpoint registered, in registration order. A snapshot; safe to enumerate.</summary>
    public IReadOnlyList<RouteEndpoint> Endpoints => this.table.Endpoints;

    /// <summary>Raised after any change, with the new endpoint count. Useful for logging and tests.</summary>
    public event EventHandler<int>? Changed;

    public void Add(RouteEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        lock (this.gate)
        {
            List<RouteEndpoint> next = [.. this.table.Endpoints, endpoint];

            // Built before it is published: a duplicate throws out of the constructor and the live
            // table is untouched, so a rejected registration cannot leave the server half-changed.
            this.Publish(new RouteTable(next));
        }
    }

    /// <summary>Adds several routes as one change, so no request sees a partial batch.</summary>
    public void AddRange(IEnumerable<RouteEndpoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        lock (this.gate)
        {
            List<RouteEndpoint> next = [.. this.table.Endpoints, .. endpoints];
            this.Publish(new RouteTable(next));
        }
    }

    /// <summary>Removes a specific endpoint. Returns false when it was not registered.</summary>
    public bool Remove(RouteEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return this.RemoveAll(e => ReferenceEquals(e, endpoint)) > 0;
    }

    /// <summary>Removes the endpoint for a method and template. Returns false when there is none.</summary>
    public bool Remove(string method, string pattern)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(pattern);

        var template = RouteTemplate.Parse(pattern);
        return this.RemoveAll(
            e => HttpMethods.Equals(e.Method, method) && e.Template.IsEquivalentTo(template)
        ) > 0;
    }

    /// <summary>Removes every endpoint matching <paramref name="predicate"/>. Returns how many went.</summary>
    public int RemoveAll(Func<RouteEndpoint, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        lock (this.gate)
        {
            var remaining = this.table.Endpoints.Where(e => !predicate(e)).ToList();
            var removed = this.table.Endpoints.Count - remaining.Count;

            if (removed > 0)
                this.Publish(new RouteTable(remaining));

            return removed;
        }
    }

    /// <summary>Removes every route.</summary>
    public void Clear()
    {
        lock (this.gate)
        {
            if (!this.table.IsEmpty)
                this.Publish(RouteTable.Empty);
        }
    }

    /// <summary>Finds the endpoint registered for a method and template, if any.</summary>
    public RouteEndpoint? Find(string method, string pattern)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(pattern);

        var template = RouteTemplate.Parse(pattern);
        return this.table.Endpoints.FirstOrDefault(
            e => HttpMethods.Equals(e.Method, method) && e.Template.IsEquivalentTo(template)
        );
    }

    /// <summary>
    /// Matches a request. On success <paramref name="routeValues"/> holds the captured parameters;
    /// on failure it is left truncated back to empty.
    /// </summary>
    public RouteMatch Match(string method, string path, RouteValueDictionary routeValues)
        // One volatile read, then no synchronisation at all for the rest of the walk.
        => this.table.Match(method, path, routeValues);

    void Publish(RouteTable next)
    {
        this.table = next;
        this.Changed?.Invoke(this, next.Endpoints.Count);
    }
}
