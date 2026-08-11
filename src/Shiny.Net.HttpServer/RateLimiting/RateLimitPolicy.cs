using System.Collections.Concurrent;

namespace Shiny.Net.HttpServer.RateLimiting;

/// <summary>
/// A rate limit, applied per partition — per caller IP, per API key, per user, or globally.
/// <para>
/// Four are built in: <see cref="FixedWindowRateLimitPolicy"/>,
/// <see cref="SlidingWindowRateLimitPolicy"/>, <see cref="TokenBucketRateLimitPolicy"/> and
/// <see cref="ConcurrencyRateLimitPolicy"/>. Deriving from
/// <see cref="PartitionedRateLimitPolicy{TState}"/> gets partitioning, locking and eviction for free.
/// </para>
/// </summary>
public abstract class RateLimitPolicy
{
    /// <summary>
    /// Turns a request into the bucket it counts against. Returning null exempts the request
    /// entirely. Defaults to the caller's IP address.
    /// </summary>
    public Func<HttpContext, string?> Partitioner { get; set; } = RateLimitPartitioners.ByIpAddress;

    /// <summary>The permit count this policy allows, reported in the response headers.</summary>
    public abstract long PermitLimit { get; }

    /// <summary>Live partitions. Exposed because an unbounded one would be a memory leak worth watching.</summary>
    public abstract int PartitionCount { get; }

    /// <summary>Takes a permit for one request. Never blocks: it answers yes or no immediately.</summary>
    public abstract RateLimitLease Acquire(string partitionKey);
}

/// <summary>
/// The machinery every limiter shares: one state object per partition, a lock around it, and
/// eviction of partitions nobody has touched in a while.
/// <para>
/// Eviction is the part that is easy to leave out and expensive to leave out. A limiter partitioned
/// by IP accumulates an entry per address that has ever called, so a server exposed to the internet
/// would grow a dictionary the size of its attacker's address space. Idle partitions are dropped on
/// a sweep, but only while they hold no permits and no history that would let a caller reset their
/// own window by waiting.
/// </para>
/// </summary>
public abstract class PartitionedRateLimitPolicy<TState> : RateLimitPolicy where TState : class
{
    readonly ConcurrentDictionary<string, Partition> partitions = new(StringComparer.Ordinal);
    readonly TimeProvider time;
    long lastSweepTicks;

    protected PartitionedRateLimitPolicy(TimeProvider? timeProvider = null)
    {
        this.time = timeProvider ?? TimeProvider.System;
        this.lastSweepTicks = this.time.GetUtcNow().UtcTicks;
    }

    protected TimeProvider Time => this.time;

    public sealed override int PartitionCount => this.partitions.Count;

    /// <summary>How long a partition must sit untouched before it may be evicted.</summary>
    protected abstract TimeSpan IdlePeriod { get; }

    /// <summary>Fresh state for a partition being seen for the first time.</summary>
    protected abstract TState CreateState(DateTimeOffset now);

    /// <summary>
    /// Decides one request. Called under the partition's lock, so implementations need no
    /// synchronisation of their own — and must not do anything slow.
    /// </summary>
    protected abstract RateLimitLease TryAcquire(TState state, DateTimeOffset now);

    /// <summary>
    /// Whether a partition's state carries nothing worth keeping. Called under the partition's lock.
    /// A concurrency limiter says no while permits are out; a window limiter says yes once its
    /// window has expired anyway.
    /// </summary>
    protected abstract bool IsEvictable(TState state, DateTimeOffset now);

    public sealed override RateLimitLease Acquire(string partitionKey)
    {
        ArgumentNullException.ThrowIfNull(partitionKey);

        var now = this.time.GetUtcNow();
        this.SweepIfDue(now);

        while (true)
        {
            var partition = this.partitions.GetOrAdd(partitionKey, _ => new Partition(this.CreateState(now)));

            lock (partition.Gate)
            {
                // Lost a race with the sweeper: this entry is already out of the dictionary, so
                // counting against it would count against nothing. Go round and get the live one.
                if (partition.Evicted)
                    continue;

                partition.LastSeenTicks = now.UtcTicks;
                return this.TryAcquire(partition.State, now);
            }
        }
    }

    /// <summary>Drops idle partitions. Called opportunistically; safe to call at any time.</summary>
    public void Sweep()
    {
        var now = this.time.GetUtcNow();
        Interlocked.Exchange(ref this.lastSweepTicks, now.UtcTicks);
        this.SweepCore(now);
    }

    void SweepIfDue(DateTimeOffset now)
    {
        var last = Interlocked.Read(ref this.lastSweepTicks);
        var interval = this.IdlePeriod.Ticks;

        if (now.UtcTicks - last < interval)
            return;

        // One thread wins and sweeps; everyone else carries on serving requests.
        if (Interlocked.CompareExchange(ref this.lastSweepTicks, now.UtcTicks, last) != last)
            return;

        this.SweepCore(now);
    }

    void SweepCore(DateTimeOffset now)
    {
        var idleTicks = this.IdlePeriod.Ticks;

        foreach (var pair in this.partitions)
        {
            var partition = pair.Value;

            lock (partition.Gate)
            {
                if (partition.Evicted || now.UtcTicks - partition.LastSeenTicks < idleTicks)
                    continue;

                if (!this.IsEvictable(partition.State, now))
                    continue;

                partition.Evicted = true;
                this.partitions.TryRemove(new KeyValuePair<string, Partition>(pair.Key, partition));
            }
        }
    }

    sealed class Partition(TState state)
    {
        public object Gate { get; } = new();

        public TState State { get; } = state;

        public long LastSeenTicks;

        public bool Evicted;
    }
}
