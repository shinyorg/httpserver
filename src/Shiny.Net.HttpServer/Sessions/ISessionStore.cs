using System.Collections.Concurrent;

namespace Shiny.Net.HttpServer.Sessions;

/// <summary>
/// One session's contents, as the store hands them over.
/// <para>
/// Values are byte arrays rather than objects on purpose: a store that survives a restart or spans
/// two machines has to serialize anyway, and pretending otherwise produces an in-memory API that
/// cannot be swapped for a real one later.
/// </para>
/// </summary>
public sealed class SessionData
{
    public SessionData()
    {
    }

    public SessionData(IDictionary<string, byte[]> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        foreach (var (key, value) in values)
            this.Values[key] = value;
    }

    public IDictionary<string, byte[]> Values { get; } = new Dictionary<string, byte[]>(StringComparer.Ordinal);
}

/// <summary>
/// Where session state lives between requests.
/// <para>
/// An interface so a fleet can move it somewhere shared, but the default is in-memory — an embedded
/// server is one process and usually one device, and a distributed cache would be infrastructure
/// bought for nothing.
/// </para>
/// </summary>
public interface ISessionStore
{
    /// <summary>Loads a session, or null when it does not exist or has expired.</summary>
    ValueTask<SessionData?> LoadAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary>Saves a session and (re)starts its idle timeout.</summary>
    ValueTask SaveAsync(string sessionId, SessionData data, TimeSpan idleTimeout, CancellationToken cancellationToken);

    /// <summary>Extends a session's idle timeout without rewriting its contents.</summary>
    ValueTask RefreshAsync(string sessionId, TimeSpan idleTimeout, CancellationToken cancellationToken);

    ValueTask RemoveAsync(string sessionId, CancellationToken cancellationToken);
}

/// <summary>
/// Sessions held in memory, with idle expiry.
/// <para>
/// Sessions are lost on restart, which is the honest trade for having no dependency. Anything that
/// must survive one belongs in a database, not a session.
/// </para>
/// </summary>
public sealed class InMemorySessionStore(TimeProvider? timeProvider = null) : ISessionStore
{
    readonly ConcurrentDictionary<string, Entry> sessions = new(StringComparer.Ordinal);
    readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Sessions to keep before the oldest are dropped. A cap rather than unbounded growth: a session
    /// id comes from a cookie, and anyone can present a new one as often as they like.
    /// </summary>
    public int Capacity { get; init; } = 10_000;

    /// <summary>Live sessions, for diagnostics.</summary>
    public int Count => this.sessions.Count;

    public ValueTask<SessionData?> LoadAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (!this.sessions.TryGetValue(sessionId, out var entry))
            return ValueTask.FromResult<SessionData?>(null);

        if (entry.ExpiresAt <= this.clock.GetUtcNow())
        {
            this.sessions.TryRemove(sessionId, out _);
            return ValueTask.FromResult<SessionData?>(null);
        }

        return ValueTask.FromResult<SessionData?>(entry.Data);
    }

    public ValueTask SaveAsync(string sessionId, SessionData data, TimeSpan idleTimeout, CancellationToken cancellationToken)
    {
        this.sessions[sessionId] = new Entry(data, this.clock.GetUtcNow() + idleTimeout);
        this.EvictIfOverCapacity();

        return ValueTask.CompletedTask;
    }

    public ValueTask RefreshAsync(string sessionId, TimeSpan idleTimeout, CancellationToken cancellationToken)
    {
        if (this.sessions.TryGetValue(sessionId, out var entry))
            this.sessions[sessionId] = entry with { ExpiresAt = this.clock.GetUtcNow() + idleTimeout };

        return ValueTask.CompletedTask;
    }

    public ValueTask RemoveAsync(string sessionId, CancellationToken cancellationToken)
    {
        this.sessions.TryRemove(sessionId, out _);
        return ValueTask.CompletedTask;
    }

    /// <summary>Drops expired sessions. Returns how many went.</summary>
    public int Prune()
    {
        var now = this.clock.GetUtcNow();
        var removed = 0;

        foreach (var (id, entry) in this.sessions)
        {
            if (entry.ExpiresAt <= now && this.sessions.TryRemove(id, out _))
                removed++;
        }

        return removed;
    }

    void EvictIfOverCapacity()
    {
        if (this.sessions.Count <= this.Capacity)
            return;

        // Expired ones first — usually enough, and it costs nothing to prefer them.
        if (this.Prune() > 0 && this.sessions.Count <= this.Capacity)
            return;

        foreach (var id in this.sessions.OrderBy(x => x.Value.ExpiresAt).Take(this.sessions.Count - this.Capacity).Select(x => x.Key))
            this.sessions.TryRemove(id, out _);
    }

    readonly record struct Entry(SessionData Data, DateTimeOffset ExpiresAt);
}
