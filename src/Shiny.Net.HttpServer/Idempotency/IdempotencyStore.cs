namespace Shiny.Net.HttpServer.Idempotency;

/// <summary>A stored response, replayed to a retry instead of running the handler again.</summary>
/// <param name="StatusCode">The status the first request was answered with.</param>
/// <param name="Headers">The headers worth replaying, hop-by-hop ones and <c>Date</c> already dropped.</param>
/// <param name="Body">The response body.</param>
/// <param name="Created">When the first request completed.</param>
public sealed record IdempotentResponse(
    int StatusCode,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    byte[] Body,
    DateTimeOffset Created
);

/// <summary>
/// What a store holds under one key: who claimed it, with what request, and — once the first request
/// has finished — what it was answered with.
/// </summary>
/// <param name="Fingerprint">
/// A hash of the first request's body. A retry must match it; the same key with a different payload
/// is a client bug, and replaying the first answer to it would hide one.
/// </param>
/// <param name="Response">The stored answer, or null while the first request is still running.</param>
/// <param name="Expires">When the record stops counting — the claim's lease, or the response's expiry.</param>
public sealed record IdempotencyRecord(
    string Fingerprint,
    IdempotentResponse? Response,
    DateTimeOffset Expires
)
{
    /// <summary>True while the first request holding the key has not finished.</summary>
    public bool InFlight => this.Response is null;

    public bool IsExpired(DateTimeOffset now) => now >= this.Expires;
}

/// <summary>
/// Where keys and their responses live. Replace it to share keys between processes — a retry that
/// lands on a different instance behind a load balancer only sees the first request if they share a
/// store.
/// <para>
/// The one hard requirement is that <see cref="TryReserveAsync"/> is atomic: two concurrent requests
/// with the same key must not both be told they hold it. That is the whole difference between an
/// idempotency store and a cache.
/// </para>
/// </summary>
public interface IIdempotencyStore
{
    /// <summary>
    /// Claims <paramref name="key"/> for a request with <paramref name="fingerprint"/>, unless a live
    /// record already holds it.
    /// </summary>
    /// <param name="key">The scoped key — the client's key plus method, path and caller.</param>
    /// <param name="fingerprint">The request's fingerprint, stored with the claim.</param>
    /// <param name="lease">How long the claim holds if nothing completes or releases it.</param>
    /// <param name="cancellationToken">The request's cancellation.</param>
    /// <returns>
    /// Null when the claim was made — the caller now holds the key and must complete or release it.
    /// Otherwise the record that already holds it, in flight or completed.
    /// </returns>
    ValueTask<IdempotencyRecord?> TryReserveAsync(string key, string fingerprint, TimeSpan lease, CancellationToken cancellationToken);

    /// <summary>Stores the response for a key this caller reserved, replacing the claim.</summary>
    ValueTask CompleteAsync(string key, IdempotencyRecord record, CancellationToken cancellationToken);

    /// <summary>
    /// Gives up a claim without storing anything, so a retry runs the handler again. Called when the
    /// handler threw, answered with a status that is not stored, or wrote a body too large to keep.
    /// </summary>
    ValueTask ReleaseAsync(string key, CancellationToken cancellationToken);
}

/// <summary>
/// An in-process store with a byte budget.
/// <para>
/// Bounded for the same reason the output cache is: the usual host is a phone, and a store that
/// grows until the OS notices is the fastest way to have the app terminated. Over budget, expired
/// records go first and then the oldest completed ones. A claim in flight is never evicted — dropping
/// one would let a concurrent duplicate through, which is the one thing this exists to prevent.
/// </para>
/// </summary>
/// <param name="maxBytes">The response-body budget. 4MB by default.</param>
/// <param name="maxEntries">
/// The record budget. A flood of unique keys on endpoints that answer with an empty body costs no
/// bytes, so the count is bounded as well. 10,000 by default.
/// </param>
public sealed class MemoryIdempotencyStore(long maxBytes = 4 * 1024 * 1024, int maxEntries = 10_000) : IIdempotencyStore
{
    readonly Dictionary<string, IdempotencyRecord> records = new(StringComparer.Ordinal);
    readonly Lock gate = new();
    long size;

    /// <summary>Bytes of stored response body currently held.</summary>
    public long SizeInBytes
    {
        get
        {
            lock (this.gate)
                return this.size;
        }
    }

    /// <summary>Records held, claims in flight included.</summary>
    public int Count
    {
        get
        {
            lock (this.gate)
                return this.records.Count;
        }
    }

    public ValueTask<IdempotencyRecord?> TryReserveAsync(string key, string fingerprint, TimeSpan lease, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(fingerprint);

        var now = DateTimeOffset.UtcNow;

        // A lock rather than a lock-free dictionary: the reserve has to be check-then-act across the
        // expiry test, and the byte accounting has to agree with the contents. Neither is on a path
        // hot enough for the difference to show on a device.
        lock (this.gate)
        {
            if (this.records.TryGetValue(key, out var existing))
            {
                if (!existing.IsExpired(now))
                    return new ValueTask<IdempotencyRecord?>(existing);

                this.Drop(key);
            }

            this.records[key] = new IdempotencyRecord(fingerprint, null, now + lease);
            return new ValueTask<IdempotencyRecord?>((IdempotencyRecord?)null);
        }
    }

    public ValueTask CompleteAsync(string key, IdempotencyRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(record);

        lock (this.gate)
        {
            this.Drop(key);

            this.records[key] = record;
            this.size += record.Response?.Body.Length ?? 0;

            this.Trim();
        }

        return default;
    }

    public ValueTask ReleaseAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);

        lock (this.gate)
        {
            // Only a claim is released. A completed record under the same key belongs to a request
            // that finished; releasing it would let the next retry run the handler again.
            if (this.records.TryGetValue(key, out var existing) && existing.InFlight)
                this.Drop(key);
        }

        return default;
    }

    bool OverBudget => this.size > maxBytes || this.records.Count > maxEntries;

    void Drop(string key)
    {
        if (this.records.Remove(key, out var removed))
            this.size -= removed.Response?.Body.Length ?? 0;
    }

    void Trim()
    {
        if (!this.OverBudget)
            return;

        var now = DateTimeOffset.UtcNow;

        foreach (var (key, record) in this.records.ToArray())
        {
            if (record.IsExpired(now))
                this.Drop(key);
        }

        while (this.OverBudget)
        {
            string? oldestKey = null;
            DateTimeOffset oldest = DateTimeOffset.MaxValue;

            foreach (var (key, record) in this.records)
            {
                if (record.Response is { } response && response.Created < oldest)
                {
                    oldest = response.Created;
                    oldestKey = key;
                }
            }

            if (oldestKey is null)
                return;

            this.Drop(oldestKey);
        }
    }
}
