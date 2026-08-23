using System.Collections.Concurrent;

namespace Shiny.Net.HttpServer.Caching;

/// <summary>A stored response, ready to be replayed without the handler running.</summary>
/// <param name="StatusCode">The status the handler produced.</param>
/// <param name="Headers">The headers worth replaying, hop-by-hop and per-connection ones already dropped.</param>
/// <param name="Body">The response body.</param>
/// <param name="Created">When the entry was stored, which is what <c>Age</c> is measured from.</param>
/// <param name="Expires">When it stops being served.</param>
public sealed record OutputCacheEntry(
    int StatusCode,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    byte[] Body,
    DateTimeOffset Created,
    DateTimeOffset Expires
)
{
    public bool IsExpired(DateTimeOffset now) => now >= this.Expires;

    /// <summary>The stored <c>ETag</c>, so a revalidating client can be answered 304 from the cache.</summary>
    public string? ETag
    {
        get
        {
            foreach (var header in this.Headers)
            {
                if (string.Equals(header.Key, HeaderNames.ETag, StringComparison.OrdinalIgnoreCase))
                    return header.Value;
            }

            return null;
        }
    }
}

/// <summary>Where cached responses live. Replace it to cache somewhere other than this process.</summary>
public interface IOutputCacheStore
{
    ValueTask<OutputCacheEntry?> GetAsync(string key, CancellationToken cancellationToken);

    ValueTask SetAsync(string key, OutputCacheEntry entry, CancellationToken cancellationToken);

    /// <summary>Drops one entry. Returns false when there was nothing under the key.</summary>
    ValueTask<bool> RemoveAsync(string key, CancellationToken cancellationToken);

    ValueTask ClearAsync(CancellationToken cancellationToken);
}

/// <summary>
/// An in-process store with a byte budget.
/// <para>
/// Bounded rather than unbounded because the usual host is a phone: a cache that grows until the
/// OS notices is the fastest way to turn a working app into a terminated one. When the budget is
/// exceeded, expired entries go first and then the oldest, until it fits.
/// </para>
/// </summary>
public sealed class MemoryOutputCacheStore(long maxBytes = 8 * 1024 * 1024) : IOutputCacheStore
{
    readonly ConcurrentDictionary<string, OutputCacheEntry> entries = new(StringComparer.Ordinal);
    long size;

    /// <summary>Bytes of response body currently held.</summary>
    public long SizeInBytes => Interlocked.Read(ref this.size);

    public int Count => this.entries.Count;

    public ValueTask<OutputCacheEntry?> GetAsync(string key, CancellationToken cancellationToken)
    {
        if (!this.entries.TryGetValue(key, out var entry))
            return new ValueTask<OutputCacheEntry?>((OutputCacheEntry?)null);

        if (!entry.IsExpired(DateTimeOffset.UtcNow))
            return new ValueTask<OutputCacheEntry?>(entry);

        this.Drop(key);
        return new ValueTask<OutputCacheEntry?>((OutputCacheEntry?)null);
    }

    public ValueTask SetAsync(string key, OutputCacheEntry entry, CancellationToken cancellationToken)
    {
        this.Drop(key);

        this.entries[key] = entry;
        Interlocked.Add(ref this.size, entry.Body.Length);

        this.Trim();

        return default;
    }

    public ValueTask<bool> RemoveAsync(string key, CancellationToken cancellationToken) => new(this.Drop(key));

    public ValueTask ClearAsync(CancellationToken cancellationToken)
    {
        foreach (var key in this.entries.Keys)
            this.Drop(key);

        return default;
    }

    bool Drop(string key)
    {
        if (!this.entries.TryRemove(key, out var removed))
            return false;

        Interlocked.Add(ref this.size, -removed.Body.Length);
        return true;
    }

    void Trim()
    {
        if (this.SizeInBytes <= maxBytes)
            return;

        var now = DateTimeOffset.UtcNow;

        foreach (var pair in this.entries)
        {
            if (pair.Value.IsExpired(now))
                this.Drop(pair.Key);
        }

        while (this.SizeInBytes > maxBytes)
        {
            var oldest = default(KeyValuePair<string, OutputCacheEntry>);

            foreach (var pair in this.entries)
            {
                if (oldest.Value is null || pair.Value.Created < oldest.Value.Created)
                    oldest = pair;
            }

            if (oldest.Value is null || !this.Drop(oldest.Key))
                return;
        }
    }
}

/// <summary>How long a response is kept, and what makes two requests the same request.</summary>
public sealed class OutputCachePolicy
{
    public OutputCachePolicy(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration), "A cache duration must be positive.");

        this.Duration = duration;
    }

    public TimeSpan Duration { get; }

    /// <summary>
    /// Whether the query string is part of the key. On by default — two different queries are two
    /// different responses, and getting this wrong serves one caller's filtered list to another.
    /// </summary>
    public bool VaryByQuery { get; init; } = true;

    /// <summary>
    /// The only query keys that matter. Empty (the default) means the whole query string, which is
    /// safe but lets a cache-busting parameter miss every time.
    /// </summary>
    public IReadOnlyList<string> VaryByQueryKeys { get; init; } = [];

    /// <summary>Request headers that select between variants — <c>Accept</c>, <c>Accept-Language</c>.</summary>
    public IReadOnlyList<string> VaryByHeaders { get; init; } = [];

    /// <summary>
    /// Caches responses to authenticated requests.
    /// <para>
    /// Off, and worth leaving off. The key does not include the caller, so turning this on without
    /// adding the identity to <see cref="VaryByHeaders"/> serves one user's data to the next one.
    /// </para>
    /// </summary>
    public bool AllowAuthenticated { get; init; }

    /// <summary>The last word on whether a particular response is stored.</summary>
    public Func<HttpContext, bool>? ShouldCache { get; init; }
}

/// <summary>The default policy, the named ones, and the limits that apply to all of them.</summary>
public sealed class OutputCacheOptions
{
    readonly Dictionary<string, OutputCachePolicy> policies = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Applied to endpoints that did not ask for a policy by name. Null caches nothing by default.</summary>
    public OutputCachePolicy? DefaultPolicy { get; set; }

    /// <summary>
    /// Responses larger than this are served but not stored. A cache is for the responses that are
    /// asked for repeatedly, and those are rarely the big ones.
    /// </summary>
    public int MaxBodyBytes { get; set; } = 512 * 1024;

    public OutputCacheOptions AddPolicy(string name, OutputCachePolicy policy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(policy);

        this.policies[name] = policy;
        return this;
    }

    public OutputCacheOptions AddPolicy(string name, TimeSpan duration) => this.AddPolicy(name, new OutputCachePolicy(duration));

    public OutputCachePolicy GetPolicy(string name)
        => this.policies.TryGetValue(name, out var policy)
            ? policy
            : throw new InvalidOperationException(
                $"No output cache policy named '{name}' is registered. Add it with " +
                $"services.AddOutputCache(o => o.AddPolicy(\"{name}\", TimeSpan.FromMinutes(1)))."
            );
}

/// <summary>What an endpoint asked for, attached to it as metadata.</summary>
public sealed class OutputCacheMetadata
{
    public string? PolicyName { get; set; }

    /// <summary>A duration given inline, which wins over <see cref="PolicyName"/>.</summary>
    public TimeSpan? Duration { get; set; }

    public bool Disabled { get; set; }
}
