namespace Shiny.Net.HttpServer.RateLimiting;

/// <summary>
/// The answer to one "may this request proceed?", and — for the limiters that count requests in
/// flight rather than requests over time — the thing that gives the permit back.
/// <para>
/// Always dispose it. A window or bucket limiter does not care, but a concurrency limiter leaks a
/// permit per undisposed lease and eventually refuses everything.
/// </para>
/// </summary>
public sealed class RateLimitLease : IDisposable
{
    Action? release;

    RateLimitLease(bool acquired, long? limit, long? remaining, TimeSpan? retryAfter, Action? release)
    {
        this.IsAcquired = acquired;
        this.Limit = limit;
        this.Remaining = remaining;
        this.RetryAfter = retryAfter;
        this.release = release;
    }

    /// <summary>True when the request may proceed.</summary>
    public bool IsAcquired { get; }

    /// <summary>The policy's permit limit, for the response headers. Null when it has no single number.</summary>
    public long? Limit { get; }

    /// <summary>Permits left in this partition after this request. Null when unknown.</summary>
    public long? Remaining { get; }

    /// <summary>
    /// How long until a retry could succeed, for <c>Retry-After</c>. Null when the limiter cannot
    /// say — a concurrency limiter has no idea when a permit will come back.
    /// </summary>
    public TimeSpan? RetryAfter { get; }

    public static RateLimitLease Acquired(long? limit = null, long? remaining = null, Action? release = null)
        => new(true, limit, remaining, null, release);

    public static RateLimitLease Rejected(TimeSpan? retryAfter = null, long? limit = null)
        => new(false, limit, 0, retryAfter, null);

    /// <summary>Returns the permit, if this kind of limiter holds one. Safe to call more than once.</summary>
    public void Dispose()
    {
        // Swapped out rather than flagged: a double dispose must not return the permit twice, and
        // over-releasing a concurrency limiter is worse than leaking — it raises the real limit.
        var callback = Interlocked.Exchange(ref this.release, null);
        callback?.Invoke();
    }
}
