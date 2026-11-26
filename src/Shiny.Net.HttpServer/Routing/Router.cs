using Shiny.Net.HttpServer.Versioning;

namespace Shiny.Net.HttpServer.Routing;

/// <summary>The outcome of matching one request against the route table.</summary>
public readonly struct RouteMatch
{
    internal RouteMatch(RouteEndpoint[]? candidates, string? allowedMethods)
    {
        this.candidates = candidates;
        this.AllowedMethods = allowedMethods;
    }

    readonly RouteEndpoint[]? candidates;

    /// <summary>
    /// The selected endpoint, or null when nothing matched. When the route has several API versions
    /// this is the first registered; which one a request actually gets is decided by API version
    /// selection, which can see the request — see <see cref="Candidates"/>.
    /// </summary>
    public Endpoint? Endpoint => this.candidates is { Length: > 0 } c ? c[0] : null;

    /// <summary>
    /// Every endpoint registered for the matched route and method, in registration order. One,
    /// except for a route with several API versions, where it is all of them.
    /// </summary>
    public IReadOnlyList<RouteEndpoint> Candidates => this.candidates ?? [];

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
    readonly Stack<List<RouteEndpoint>> registrations = new();
    volatile RouteTable table = RouteTable.Empty;
    RouteEndpoint[]? lastMapped;

    /// <summary>Every endpoint registered, in registration order. A snapshot; safe to enumerate.</summary>
    public IReadOnlyList<RouteEndpoint> Endpoints => this.table.Endpoints;

    /// <summary>
    /// The API versioning options in force, or null for the defaults. Set when the pipeline is
    /// composed — from <c>AddApiVersioning</c> in the container, or <c>UseApiVersioning</c> without
    /// one — and read by everything that has to pick between an endpoint's versions.
    /// </summary>
    internal ApiVersioningOptions? Versioning { get; set; }

    /// <summary>Raised after any change, with the new endpoint count. Useful for logging and tests.</summary>
    public event EventHandler<int>? Changed;

    /// <summary>
    /// The routes the most recent mapping call registered — one for <c>MapGet</c>, every route of the group
    /// for <c>MapGroup</c>, <c>MapModule</c> or a generated <c>Map{Class}()</c>. Empty until something is
    /// mapped.
    /// <para>
    /// This is what a convention chained onto the server applies to —
    /// <c>app.MapGroup("/admin", …).RequireAuthorization()</c> protects every route in the group. Only
    /// treating the last route in the table as "what was just mapped" would protect one route of the
    /// group and leave the rest open.
    /// </para>
    /// </summary>
    public IReadOnlyList<RouteEndpoint> LastMapped
    {
        get
        {
            lock (this.gate)
                return this.lastMapped ?? [];
        }
    }

    /// <summary>True once anything has been mapped, even when the last call registered nothing.</summary>
    internal bool HasMapped
    {
        get
        {
            lock (this.gate)
                return this.lastMapped is not null;
        }
    }

    /// <summary>
    /// Groups every route added until the returned scope is disposed into one registration, so
    /// <see cref="LastMapped"/> — and a convention chained after the call — covers all of them.
    /// <code>
    /// public static HttpServer MapThings(this HttpServer server)
    /// {
    ///     using (server.Router.BeginRegistration())
    ///     {
    ///         server.MapGet("/things", List);
    ///         server.MapPost("/things", Create);
    ///     }
    ///     return server;
    /// }
    /// </code>
    /// Anything that maps more than one route and hands back the <see cref="HttpServer"/> should do this.
    /// Scopes nest: an inner one rolls into the outer, and inside a scope <see cref="LastMapped"/> still
    /// tracks each individual call, so <c>server.MapGet(…).AllowAnonymous()</c> in the middle of a group
    /// touches only that route.
    /// </summary>
    public IDisposable BeginRegistration()
    {
        lock (this.gate)
            this.registrations.Push([]);

        return new RegistrationScope(this);
    }

    void EndRegistration()
    {
        lock (this.gate)
        {
            var finished = this.registrations.Pop();
            this.lastMapped = [.. finished];

            if (this.registrations.Count > 0)
                this.registrations.Peek().AddRange(finished);
        }
    }

    void Track(IReadOnlyCollection<RouteEndpoint> added)
    {
        this.lastMapped = [.. added];

        if (this.registrations.Count > 0)
            this.registrations.Peek().AddRange(added);
    }

    sealed class RegistrationScope(Router router) : IDisposable
    {
        int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref this.disposed, 1) == 0)
                router.EndRegistration();
        }
    }

    public void Add(RouteEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        lock (this.gate)
        {
            List<RouteEndpoint> next = [.. this.table.Endpoints, endpoint];

            // Built before it is published: a duplicate throws out of the constructor and the live
            // table is untouched, so a rejected registration cannot leave the server half-changed.
            this.Publish(new RouteTable(next));
            this.Track([endpoint]);
        }
    }

    /// <summary>Adds several routes as one change, so no request sees a partial batch.</summary>
    public void AddRange(IEnumerable<RouteEndpoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        lock (this.gate)
        {
            RouteEndpoint[] added = [.. endpoints];
            List<RouteEndpoint> next = [.. this.table.Endpoints, .. added];
            this.Publish(new RouteTable(next));
            this.Track(added);
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
