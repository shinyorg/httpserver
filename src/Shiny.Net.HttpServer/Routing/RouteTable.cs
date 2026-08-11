namespace Shiny.Net.HttpServer.Routing;

/// <summary>
/// An immutable prefix trie over a fixed set of endpoints.
/// <para>
/// Immutability is what makes routes changeable at runtime. Matching never takes a lock and never
/// sees a half-built trie, because it only ever reads a table that was finished before it was
/// published; adding or removing a route builds a new table and swaps it in. Rebuilding is O(n) in
/// the number of routes, which is the right trade — route tables are small and change rarely, and
/// requests are neither.
/// </para>
/// </summary>
sealed class RouteTable
{
    public static readonly RouteTable Empty = new([]);

    readonly Node root = new();

    public RouteTable(IReadOnlyList<RouteEndpoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        this.Endpoints = endpoints;

        foreach (var endpoint in endpoints)
            this.Insert(endpoint);
    }

    /// <summary>Every endpoint in this table, in registration order.</summary>
    public IReadOnlyList<RouteEndpoint> Endpoints { get; }

    public bool IsEmpty => this.Endpoints.Count == 0;

    void Insert(RouteEndpoint endpoint)
    {
        var node = this.root;
        var segments = endpoint.Template.Segments;

        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            switch (segment.Kind)
            {
                case RouteSegmentKind.Literal:
                    node.Literals ??= new(StringComparer.OrdinalIgnoreCase);
                    if (!node.Literals.TryGetValue(segment.Text, out var literal))
                        node.Literals[segment.Text] = literal = new Node();
                    node = literal;
                    break;

                case RouteSegmentKind.Parameter:
                    // An optional trailing parameter also has to match with the segment absent,
                    // so the endpoint is registered on the parent as well as the child.
                    if (segment.IsOptional)
                        node.AddEndpoint(endpoint);

                    node = node.GetOrAddParameter(segment);
                    break;

                case RouteSegmentKind.CatchAll:
                    node.CatchAll ??= new Node { ParameterName = segment.Text };
                    node = node.CatchAll;
                    node.IsCatchAll = true;
                    break;
            }
        }

        node.AddEndpoint(endpoint);
    }

    /// <summary>
    /// Matches a request. On success <paramref name="routeValues"/> holds the captured parameters;
    /// on failure it is left truncated back to empty.
    /// </summary>
    public RouteMatch Match(string method, string path, RouteValueDictionary routeValues)
    {
        var state = new MatchState(method, routeValues);
        this.Walk(this.root, path, SkipSlash(path, 0), ref state);

        if (state.Endpoint is not null)
            return new RouteMatch(state.Endpoint, null);

        routeValues.Reset();
        return new RouteMatch(null, state.BuildAllowHeader());
    }

    bool Walk(Node node, string path, int index, ref MatchState state)
    {
        if (index >= path.Length)
            return node.TrySelect(ref state);

        var slash = path.IndexOf('/', index);
        var end = slash < 0 ? path.Length : slash;
        var segment = path.AsSpan(index, end - index);
        var next = SkipSlash(path, end);

        // A trailing slash ("/users/") leaves an empty final segment. Treat it as the end of the
        // path so "/users" and "/users/" select the same endpoint.
        if (segment.IsEmpty && next >= path.Length)
            return node.TrySelect(ref state);

        var mark = state.RouteValues.Count;

        // Alternate lookup so a segment can be compared without materialising a string. Matching
        // runs on every request; allocating per segment to look up a literal would be waste.
        if (node.Literals is not null &&
            node.Literals.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(segment, out var literal) &&
            this.Walk(literal, path, next, ref state))
            return true;

        // A parameter has to capture something. Without this, "/users//orders" would bind an empty
        // string to {id} and reach a handler that has no way to tell it apart from a real value.
        if (node.Parameters is not null && !segment.IsEmpty)
        {
            foreach (var parameter in node.Parameters)
            {
                if (!parameter.Constraint.Matches(segment))
                    continue;

                state.RouteValues.Add(parameter.ParameterName!, new string(segment));

                if (this.Walk(parameter, path, next, ref state))
                    return true;

                state.RouteValues.TruncateTo(mark);
            }
        }

        if (node.CatchAll is not null)
        {
            state.RouteValues.Add(node.CatchAll.ParameterName!, path[index..]);

            if (node.CatchAll.TrySelect(ref state))
                return true;

            state.RouteValues.TruncateTo(mark);
        }

        return false;
    }

    static int SkipSlash(string path, int index)
        => index < path.Length && path[index] == '/' ? index + 1 : index;

    /// <summary>
    /// Carries the search forward and accumulates the methods seen on paths that matched by URL,
    /// which is what turns a would-be 404 into a 405.
    /// </summary>
    ref struct MatchState(string method, RouteValueDictionary routeValues)
    {
        public readonly string Method = method;
        public readonly RouteValueDictionary RouteValues = routeValues;
        public Endpoint? Endpoint;
        public List<string>? Allowed;

        public void RecordAllowed(string candidate)
        {
            this.Allowed ??= [];
            if (!this.Allowed.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                this.Allowed.Add(candidate);
        }

        public readonly string? BuildAllowHeader()
            => this.Allowed is null ? null : string.Join(", ", this.Allowed);
    }

    sealed class Node
    {
        public Dictionary<string, Node>? Literals;
        public List<Node>? Parameters;
        public Node? CatchAll;

        public string? ParameterName;
        public RouteConstraint Constraint = RouteConstraint.None;
        public bool IsCatchAll;

        Dictionary<string, RouteEndpoint>? endpoints;

        public Node GetOrAddParameter(RouteSegment segment)
        {
            this.Parameters ??= [];

            foreach (var existing in this.Parameters)
            {
                if (existing.ParameterName == segment.Text &&
                    existing.Constraint.ToString() == segment.Constraint.ToString())
                    return existing;
            }

            var node = new Node { ParameterName = segment.Text, Constraint = segment.Constraint };

            // Constrained parameters are tried first: {id:int} should win over {slug} for "/42"
            // no matter which was registered first.
            if (segment.Constraint.IsUnconstrained)
                this.Parameters.Add(node);
            else
                this.Parameters.Insert(0, node);

            return node;
        }

        public void AddEndpoint(RouteEndpoint endpoint)
        {
            this.endpoints ??= new(StringComparer.OrdinalIgnoreCase);

            if (this.endpoints.TryGetValue(endpoint.Method, out var existing))
                throw new InvalidOperationException(
                    $"Cannot register '{endpoint.DisplayName}': the route '{existing.DisplayName}' already handles it."
                );

            this.endpoints[endpoint.Method] = endpoint;
        }

        /// <summary>
        /// Attempts to select an endpoint on this node for the request's method. Returns false —
        /// letting the caller keep backtracking — while still recording which methods this path
        /// does support.
        /// </summary>
        public bool TrySelect(ref MatchState state)
        {
            if (this.endpoints is null)
                return false;

            if (this.endpoints.TryGetValue(state.Method, out var endpoint))
            {
                state.Endpoint = endpoint;
                return true;
            }

            // A HEAD request should be served by the GET handler; the output producer drops the
            // body. Doing it here means every route gets HEAD for free.
            if (HttpMethods.IsHead(state.Method) &&
                this.endpoints.TryGetValue(HttpMethods.Get, out var get))
            {
                state.Endpoint = get;
                return true;
            }

            foreach (var method in this.endpoints.Keys)
                state.RecordAllowed(method);

            return false;
        }
    }
}
