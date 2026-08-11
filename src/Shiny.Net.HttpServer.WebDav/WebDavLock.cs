namespace Shiny.Net.HttpServer.WebDav;

/// <summary>Whether a write lock excludes every other lock, or only exclusive ones.</summary>
public enum WebDavLockScope
{
    /// <summary>One holder. Anything else asking for a lock on the same resource is refused.</summary>
    Exclusive,

    /// <summary>
    /// Several holders may hold one at once. It still blocks writes from anyone who does not
    /// present a token — a shared lock is an agreement between the holders, not an open door.
    /// </summary>
    Shared
}

/// <summary>A write lock held on a resource.</summary>
/// <param name="Token">
/// The lock token, an <c>opaquelocktoken:</c> URI. This is what a client puts in an <c>If</c>
/// header to prove it may write, and in <c>Lock-Token</c> to release.
/// </param>
/// <param name="Path">The locked resource's path, relative to the mount root.</param>
/// <param name="Scope">Exclusive or shared.</param>
/// <param name="IsDeep">True when the lock covers the whole subtree — <c>Depth: infinity</c>.</param>
/// <param name="Owner">
/// The raw XML the client put inside <c>&lt;DAV:owner&gt;</c>, or null when it sent none. Opaque to
/// the server and handed straight back in <c>lockdiscovery</c>, which is the only thing RFC 4918
/// asks of it.
/// </param>
/// <param name="Timeout">How long the lock was granted for, counted from the last refresh.</param>
/// <param name="ExpiresUtc">When it lapses if nothing refreshes it.</param>
public sealed record WebDavLock(
    string Token,
    string Path,
    WebDavLockScope Scope,
    bool IsDeep,
    string? Owner,
    TimeSpan Timeout,
    DateTimeOffset ExpiresUtc
)
{
    /// <summary>True once <see cref="ExpiresUtc"/> has passed.</summary>
    public bool IsExpired(DateTimeOffset now) => now >= this.ExpiresUtc;
}
