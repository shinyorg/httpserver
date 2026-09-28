namespace Shiny.Net.HttpServer.Webhooks;

/// <summary>
/// Remembers which deliveries have been handled, so a retry of one already processed is
/// acknowledged without running the handler twice.
/// <para>
/// Every major sender retries — on a timeout, on a 5xx, sometimes on nothing at all — and promises
/// at-least-once delivery, not exactly-once. A handler that charges a card or sends a push on each
/// delivery needs something like this in front of it. The built-in store is memory-only and bounded;
/// implement this over a database when duplicates must be caught across restarts.
/// </para>
/// </summary>
public interface IWebhookDeliveryStore
{
    /// <summary>
    /// Records a delivery key, returning false when it was already recorded — i.e. this is a duplicate.
    /// Must be atomic: two concurrent deliveries of the same key must not both get true.
    /// </summary>
    ValueTask<bool> TryAddAsync(string deliveryKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Forgets a key. Called when the handler failed, so the sender's retry is processed rather
    /// than acknowledged as a duplicate of a delivery that never actually succeeded.
    /// </summary>
    ValueTask RemoveAsync(string deliveryKey, CancellationToken cancellationToken = default);
}

/// <summary>
/// A bounded, in-memory <see cref="IWebhookDeliveryStore"/>: remembers up to <c>capacity</c> keys
/// for up to <c>retention</c>, dropping the oldest first.
/// <para>
/// Bounded on purpose. The keys come from the sender, and an unbounded set of them is a memory
/// leak on a server that runs for months — or on a phone, one that gets the app killed. The default
/// window of 24 hours comfortably covers the retry schedules of GitHub, Stripe and Svix for all but
/// the last few attempts.
/// </para>
/// </summary>
public sealed class InMemoryWebhookDeliveryStore : IWebhookDeliveryStore
{
    readonly Lock sync = new();
    readonly Dictionary<string, LinkedListNode<(string Key, DateTimeOffset Expires)>> index = new(StringComparer.Ordinal);
    readonly LinkedList<(string Key, DateTimeOffset Expires)> order = new();
    readonly TimeProvider timeProvider;

    public InMemoryWebhookDeliveryStore(int capacity = 10_000, TimeSpan? retention = null, TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        this.Capacity = capacity;
        this.Retention = retention ?? TimeSpan.FromHours(24);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(this.Retention, TimeSpan.Zero, nameof(retention));

        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The most keys held at once.</summary>
    public int Capacity { get; }

    /// <summary>How long a key is remembered.</summary>
    public TimeSpan Retention { get; }

    /// <summary>Keys currently held.</summary>
    public int Count
    {
        get
        {
            lock (this.sync)
                return this.index.Count;
        }
    }

    public ValueTask<bool> TryAddAsync(string deliveryKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deliveryKey);

        var now = this.timeProvider.GetUtcNow();

        lock (this.sync)
        {
            // Oldest first, so expired entries are always at the front.
            while (this.order.First is { } first && first.Value.Expires <= now)
            {
                this.index.Remove(first.Value.Key);
                this.order.RemoveFirst();
            }

            if (this.index.ContainsKey(deliveryKey))
                return ValueTask.FromResult(false);

            this.index[deliveryKey] = this.order.AddLast((deliveryKey, now + this.Retention));

            while (this.index.Count > this.Capacity && this.order.First is { } oldest)
            {
                this.index.Remove(oldest.Value.Key);
                this.order.RemoveFirst();
            }
        }

        return ValueTask.FromResult(true);
    }

    public ValueTask RemoveAsync(string deliveryKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deliveryKey);

        lock (this.sync)
        {
            if (this.index.Remove(deliveryKey, out var node))
                this.order.Remove(node);
        }

        return ValueTask.CompletedTask;
    }
}
