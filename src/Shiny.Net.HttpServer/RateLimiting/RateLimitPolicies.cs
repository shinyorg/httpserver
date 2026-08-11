namespace Shiny.Net.HttpServer.RateLimiting;

/// <summary>
/// N requests per window, counted from the first request in the window.
/// <para>
/// The cheapest limiter and the easiest to reason about, with one known wart: a caller can spend
/// their whole allowance at the end of one window and again at the start of the next, so a burst of
/// up to twice the limit can land across the boundary. <see cref="SlidingWindowRateLimitPolicy"/>
/// costs a little more and does not have that edge.
/// </para>
/// </summary>
public sealed class FixedWindowRateLimitPolicy : PartitionedRateLimitPolicy<FixedWindowRateLimitPolicy.State>
{
    readonly int permitLimit;
    readonly TimeSpan window;

    public FixedWindowRateLimitPolicy(int permitLimit, TimeSpan window, TimeProvider? timeProvider = null)
        : base(timeProvider)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(permitLimit, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);

        this.permitLimit = permitLimit;
        this.window = window;
    }

    public override long PermitLimit => this.permitLimit;

    public TimeSpan Window => this.window;

    protected override TimeSpan IdlePeriod => this.window;

    protected override State CreateState(DateTimeOffset now) => new() { WindowStartTicks = now.UtcTicks };

    protected override RateLimitLease TryAcquire(State state, DateTimeOffset now)
    {
        var elapsed = now.UtcTicks - state.WindowStartTicks;

        if (elapsed >= this.window.Ticks)
        {
            state.WindowStartTicks = now.UtcTicks;
            state.Count = 0;
            elapsed = 0;
        }

        if (state.Count >= this.permitLimit)
            return RateLimitLease.Rejected(TimeSpan.FromTicks(this.window.Ticks - elapsed), this.permitLimit);

        state.Count++;
        return RateLimitLease.Acquired(this.permitLimit, this.permitLimit - state.Count);
    }

    protected override bool IsEvictable(State state, DateTimeOffset now)
        // Only once the window is over: evicting a live one would hand the caller a fresh allowance
        // for the price of going quiet for a moment.
        => now.UtcTicks - state.WindowStartTicks >= this.window.Ticks;

    public sealed class State
    {
        public long WindowStartTicks;
        public int Count;
    }
}

/// <summary>
/// N requests per window, with the window divided into segments that expire one at a time.
/// <para>
/// More faithful than a fixed window — the allowance is spread rather than refunded all at once —
/// at the cost of one small array per partition. More segments means a smoother limit and more
/// memory; eight is usually plenty.
/// </para>
/// </summary>
public sealed class SlidingWindowRateLimitPolicy : PartitionedRateLimitPolicy<SlidingWindowRateLimitPolicy.State>
{
    readonly int permitLimit;
    readonly TimeSpan window;
    readonly int segments;
    readonly long segmentTicks;

    public SlidingWindowRateLimitPolicy(
        int permitLimit,
        TimeSpan window,
        int segments = 8,
        TimeProvider? timeProvider = null
    ) : base(timeProvider)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(permitLimit, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(segments, 1);

        this.permitLimit = permitLimit;
        this.window = window;
        this.segments = segments;
        this.segmentTicks = Math.Max(1, window.Ticks / segments);
    }

    public override long PermitLimit => this.permitLimit;

    public TimeSpan Window => this.window;

    public int Segments => this.segments;

    protected override TimeSpan IdlePeriod => this.window;

    protected override State CreateState(DateTimeOffset now)
        => new(this.segments) { SegmentStartTicks = now.UtcTicks };

    protected override RateLimitLease TryAcquire(State state, DateTimeOffset now)
    {
        this.Advance(state, now);

        if (state.Total >= this.permitLimit)
        {
            // The soonest anything could free up is when the current segment rolls off the back.
            var untilNextSegment = this.segmentTicks - (now.UtcTicks - state.SegmentStartTicks);
            return RateLimitLease.Rejected(
                TimeSpan.FromTicks(Math.Max(untilNextSegment, 0)),
                this.permitLimit
            );
        }

        state.Counts[state.Index]++;
        state.Total++;

        return RateLimitLease.Acquired(this.permitLimit, this.permitLimit - state.Total);
    }

    protected override bool IsEvictable(State state, DateTimeOffset now)
    {
        this.Advance(state, now);
        return state.Total == 0;
    }

    /// <summary>Rolls expired segments off the back, zeroing each as it goes.</summary>
    void Advance(State state, DateTimeOffset now)
    {
        var elapsedSegments = (now.UtcTicks - state.SegmentStartTicks) / this.segmentTicks;
        if (elapsedSegments <= 0)
            return;

        // Past a whole window, everything has expired; clearing beats spinning through segments.
        if (elapsedSegments >= this.segments)
        {
            Array.Clear(state.Counts);
            state.Total = 0;
            state.Index = 0;
        }
        else
        {
            for (var i = 0; i < elapsedSegments; i++)
            {
                state.Index = (state.Index + 1) % this.segments;
                state.Total -= state.Counts[state.Index];
                state.Counts[state.Index] = 0;
            }
        }

        state.SegmentStartTicks += elapsedSegments * this.segmentTicks;
    }

    public sealed class State(int segments)
    {
        public int[] Counts { get; } = new int[segments];
        public long SegmentStartTicks;
        public int Index;
        public int Total;
    }
}

/// <summary>
/// A bucket of <c>capacity</c> tokens that refills at a steady rate. One token per request.
/// <para>
/// The limiter to reach for when bursts are fine but a sustained flood is not: a caller who has been
/// quiet can spend the whole bucket at once, then settles into the refill rate.
/// </para>
/// </summary>
public sealed class TokenBucketRateLimitPolicy : PartitionedRateLimitPolicy<TokenBucketRateLimitPolicy.State>
{
    readonly int capacity;
    readonly double tokensPerTick;

    public TokenBucketRateLimitPolicy(
        int capacity,
        int tokensPerPeriod,
        TimeSpan period,
        TimeProvider? timeProvider = null
    ) : base(timeProvider)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(tokensPerPeriod, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(period, TimeSpan.Zero);

        this.capacity = capacity;
        this.Period = period;
        this.TokensPerPeriod = tokensPerPeriod;
        this.tokensPerTick = (double)tokensPerPeriod / period.Ticks;
    }

    public override long PermitLimit => this.capacity;

    public int TokensPerPeriod { get; }

    public TimeSpan Period { get; }

    /// <summary>Long enough that an evicted bucket would have refilled completely anyway.</summary>
    protected override TimeSpan IdlePeriod => TimeSpan.FromTicks((long)(this.capacity / this.tokensPerTick));

    protected override State CreateState(DateTimeOffset now)
        => new() { Tokens = this.capacity, LastRefillTicks = now.UtcTicks };

    protected override RateLimitLease TryAcquire(State state, DateTimeOffset now)
    {
        this.Refill(state, now);

        if (state.Tokens < 1d)
        {
            var ticksToOneToken = (long)((1d - state.Tokens) / this.tokensPerTick);
            return RateLimitLease.Rejected(TimeSpan.FromTicks(Math.Max(ticksToOneToken, 1)), this.capacity);
        }

        state.Tokens -= 1d;
        return RateLimitLease.Acquired(this.capacity, (long)state.Tokens);
    }

    protected override bool IsEvictable(State state, DateTimeOffset now)
    {
        this.Refill(state, now);

        // A full bucket is indistinguishable from a brand new one, so nothing is lost by dropping it.
        return state.Tokens >= this.capacity;
    }

    void Refill(State state, DateTimeOffset now)
    {
        var elapsed = now.UtcTicks - state.LastRefillTicks;
        if (elapsed <= 0)
            return;

        state.Tokens = Math.Min(this.capacity, state.Tokens + (elapsed * this.tokensPerTick));
        state.LastRefillTicks = now.UtcTicks;
    }

    public sealed class State
    {
        public double Tokens;
        public long LastRefillTicks;
    }
}

/// <summary>
/// At most N requests <em>in flight</em> per partition. Not a rate at all — a queue depth.
/// <para>
/// This is the one that protects a resource rather than a quota: a phone serving thumbnails can
/// happily answer a thousand requests a minute and still fall over if fifty arrive at once. The
/// permit is held until the lease is disposed, which the middleware does when the response is
/// complete.
/// </para>
/// </summary>
public sealed class ConcurrencyRateLimitPolicy : PartitionedRateLimitPolicy<ConcurrencyRateLimitPolicy.State>
{
    readonly int permitLimit;

    public ConcurrencyRateLimitPolicy(int permitLimit, TimeProvider? timeProvider = null)
        : base(timeProvider)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(permitLimit, 1);
        this.permitLimit = permitLimit;
    }

    public override long PermitLimit => this.permitLimit;

    protected override TimeSpan IdlePeriod => TimeSpan.FromMinutes(1);

    protected override State CreateState(DateTimeOffset now) => new();

    protected override RateLimitLease TryAcquire(State state, DateTimeOffset now)
    {
        // Interlocked on both sides, not just the release: the lock serialises acquisitions, but a
        // lease being disposed on another thread is not holding it.
        if (Volatile.Read(ref state.Active) >= this.permitLimit)
            return RateLimitLease.Rejected(null, this.permitLimit);

        var active = Interlocked.Increment(ref state.Active);

        return RateLimitLease.Acquired(
            this.permitLimit,
            Math.Max(this.permitLimit - active, 0),
            () => Interlocked.Decrement(ref state.Active)
        );
    }

    protected override bool IsEvictable(State state, DateTimeOffset now)
        // Never while permits are out: the count is the only record that they are.
        => Volatile.Read(ref state.Active) == 0;

    public sealed class State
    {
        public int Active;
    }
}
