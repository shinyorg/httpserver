namespace Shiny.Net.HttpServer.RateLimiting;

/// <summary>
/// The rate limits for the whole app.
/// <code>
/// builder.Services.AddRateLimiter(o =>
/// {
///     o.GlobalPolicy = new FixedWindowRateLimitPolicy(300, TimeSpan.FromMinutes(1));
///     o.AddTokenBucket("uploads", capacity: 5, tokensPerPeriod: 1, period: TimeSpan.FromSeconds(10));
///     o.AddConcurrency("thumbnails", 4);
/// });
///
/// app.UseRateLimiter();
/// app.OnPost("/upload", Handler).RequireRateLimiting("uploads");
/// </code>
/// </summary>
public sealed class RateLimitOptions
{
    readonly Dictionary<string, RateLimitPolicy> policies = new(StringComparer.Ordinal);

    /// <summary>
    /// Applied to every request that does not name a policy — including ones that match no route.
    /// Null leaves everything unlimited except the endpoints that asked for a policy.
    /// </summary>
    public RateLimitPolicy? GlobalPolicy { get; set; }

    /// <summary>What a throttled caller gets. 429, and there is no good reason to change it.</summary>
    public int RejectionStatusCode { get; set; } = StatusCodes.Status429TooManyRequests;

    /// <summary>
    /// Whether a rejection carries <c>Retry-After</c>. On by default: a client that is told when to
    /// come back stops hammering, which is the entire point.
    /// </summary>
    public bool IncludeRetryAfterHeader { get; set; } = true;

    /// <summary>
    /// Whether successful responses carry <c>X-RateLimit-Limit</c> and <c>X-RateLimit-Remaining</c>.
    /// Useful to a client pacing itself; turn it off to keep the limits to yourself.
    /// </summary>
    public bool IncludeRateLimitHeaders { get; set; } = true;

    /// <summary>
    /// Called instead of the plain 429, for an app that wants to write a body or emit a metric.
    /// Whatever it writes is the response.
    /// </summary>
    public Func<HttpContext, RateLimitLease, ValueTask>? OnRejected { get; set; }

    public IReadOnlyDictionary<string, RateLimitPolicy> Policies => this.policies;

    public RateLimitOptions AddPolicy(string name, RateLimitPolicy policy)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(policy);

        this.policies[name] = policy;
        return this;
    }

    /// <summary>Adds a <see cref="FixedWindowRateLimitPolicy"/>.</summary>
    public RateLimitOptions AddFixedWindow(
        string name,
        int permitLimit,
        TimeSpan window,
        Func<HttpContext, string?>? partitioner = null
    ) => this.AddPolicy(name, Partition(new FixedWindowRateLimitPolicy(permitLimit, window), partitioner));

    /// <summary>Adds a <see cref="SlidingWindowRateLimitPolicy"/>.</summary>
    public RateLimitOptions AddSlidingWindow(
        string name,
        int permitLimit,
        TimeSpan window,
        int segments = 8,
        Func<HttpContext, string?>? partitioner = null
    ) => this.AddPolicy(name, Partition(new SlidingWindowRateLimitPolicy(permitLimit, window, segments), partitioner));

    /// <summary>Adds a <see cref="TokenBucketRateLimitPolicy"/>.</summary>
    public RateLimitOptions AddTokenBucket(
        string name,
        int capacity,
        int tokensPerPeriod,
        TimeSpan period,
        Func<HttpContext, string?>? partitioner = null
    ) => this.AddPolicy(name, Partition(new TokenBucketRateLimitPolicy(capacity, tokensPerPeriod, period), partitioner));

    /// <summary>Adds a <see cref="ConcurrencyRateLimitPolicy"/>.</summary>
    public RateLimitOptions AddConcurrency(
        string name,
        int permitLimit,
        Func<HttpContext, string?>? partitioner = null
    ) => this.AddPolicy(name, Partition(new ConcurrencyRateLimitPolicy(permitLimit), partitioner));

    /// <summary>
    /// Resolves a named policy, throwing when it was never registered — at the first request that
    /// names it, rather than quietly running unlimited.
    /// </summary>
    public RateLimitPolicy GetPolicy(string name)
        => this.policies.TryGetValue(name, out var policy)
            ? policy
            : throw new InvalidOperationException(
                $"No rate limit policy named '{name}' is registered. " +
                $"Add it with services.AddRateLimiter(o => o.AddFixedWindow(\"{name}\", 100, TimeSpan.FromMinutes(1)))."
            );

    static TPolicy Partition<TPolicy>(TPolicy policy, Func<HttpContext, string?>? partitioner)
        where TPolicy : RateLimitPolicy
    {
        if (partitioner is not null)
            policy.Partitioner = partitioner;

        return policy;
    }
}

/// <summary>What an endpoint asks of the rate limiter, attached to it as metadata.</summary>
public sealed class RateLimitMetadata
{
    /// <summary>The named policy to apply, or null for <see cref="RateLimitOptions.GlobalPolicy"/>.</summary>
    public string? PolicyName { get; set; }

    /// <summary>True when the endpoint opted out, including out of the global policy.</summary>
    public bool Disabled { get; set; }
}
