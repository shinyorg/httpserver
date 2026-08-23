namespace Shiny.Net.HttpServer.Timeouts;

/// <summary>How long a request gets, and what happens when it does not finish in time.</summary>
public sealed class RequestTimeoutPolicy
{
    public RequestTimeoutPolicy(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), "A request timeout must be positive.");

        this.Timeout = timeout;
    }

    public TimeSpan Timeout { get; }

    /// <summary>
    /// The status answered when the handler runs out of time. 504 by default, matching ASP.NET
    /// Core: the request was fine, the thing behind it took too long.
    /// </summary>
    public int StatusCode { get; init; } = StatusCodes.Status504GatewayTimeout;

    /// <summary>
    /// Writes the timeout response instead of the bare status code. Only called when the response
    /// has not already started — once bytes are on the wire there is no status left to choose.
    /// </summary>
    public Func<HttpContext, ValueTask>? OnTimeout { get; init; }
}

/// <summary>The default timeout and any named ones.</summary>
public sealed class RequestTimeoutOptions
{
    readonly Dictionary<string, RequestTimeoutPolicy> policies = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Applied to every endpoint that does not name a policy. Null — the default — means a handler
    /// runs until the client gives up, which is the right behaviour for SSE and WebSockets and the
    /// wrong one for everything else.
    /// </summary>
    public RequestTimeoutPolicy? DefaultPolicy { get; set; }

    /// <summary>Registers a named policy.</summary>
    public RequestTimeoutOptions AddPolicy(string name, RequestTimeoutPolicy policy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(policy);

        this.policies[name] = policy;
        return this;
    }

    /// <summary>Registers a named policy from a bare duration.</summary>
    public RequestTimeoutOptions AddPolicy(string name, TimeSpan timeout)
        => this.AddPolicy(name, new RequestTimeoutPolicy(timeout));

    public RequestTimeoutPolicy GetPolicy(string name)
        => this.policies.TryGetValue(name, out var policy)
            ? policy
            : throw new InvalidOperationException(
                $"No request timeout policy named '{name}' is registered. Add it with " +
                $"services.AddRequestTimeouts(o => o.AddPolicy(\"{name}\", TimeSpan.FromSeconds(30)))."
            );

    internal bool TryGetPolicy(string name, out RequestTimeoutPolicy policy) => this.policies.TryGetValue(name, out policy!);
}

/// <summary>What an endpoint asked for, attached to it as metadata.</summary>
public sealed class RequestTimeoutMetadata
{
    /// <summary>A registered policy's name.</summary>
    public string? PolicyName { get; set; }

    /// <summary>A duration given inline, which wins over <see cref="PolicyName"/>.</summary>
    public TimeSpan? Timeout { get; set; }

    /// <summary>True when the endpoint opted out, including out of the default policy.</summary>
    public bool Disabled { get; set; }
}
