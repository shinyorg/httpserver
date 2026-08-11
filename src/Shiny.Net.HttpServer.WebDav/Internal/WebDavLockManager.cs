using Shiny.Net.HttpServer.StaticFiles;

namespace Shiny.Net.HttpServer.WebDav.Internal;

/// <summary>
/// The locks a mount is holding, in memory.
/// <para>
/// A flat list under one lock rather than anything cleverer. The set is small — a client holds a
/// lock for the seconds between opening a file and saving it — and every question asked of it
/// ("does anything cover this path?") walks ancestors and descendants, which a dictionary would not
/// answer any faster.
/// </para>
/// </summary>
sealed class WebDavLockManager(WebDavOptions options)
{
    readonly List<WebDavLock> locks = [];
    readonly StringComparison comparison = StaticFilePath.PathComparison;

    /// <summary>
    /// The lock standing in the way of writing to <paramref name="path"/>, or null when nothing is.
    /// <para>
    /// A lock the caller proved it holds — its token arrived in the <c>If</c> header — is not in the
    /// way. Neither is one that has lapsed.
    /// </para>
    /// </summary>
    /// <param name="includeDescendants">
    /// True when the operation reaches into the subtree, as <c>DELETE</c> and <c>MOVE</c> of a
    /// collection do. A lock on something inside blocks those even though it does not cover the
    /// collection itself.
    /// </param>
    public WebDavLock? FindBlocking(string path, IReadOnlyCollection<string> submittedTokens, bool includeDescendants)
    {
        lock (this.locks)
        {
            this.Prune();

            foreach (var held in this.locks)
            {
                if (!this.Covers(held, path) && !(includeDescendants && this.IsUnder(held.Path, path)))
                    continue;

                if (!submittedTokens.Contains(held.Token, StringComparer.Ordinal))
                    return held;
            }

            return null;
        }
    }

    /// <summary>
    /// Takes a lock, or reports the one that prevents it.
    /// <para>
    /// A lock the caller already holds does not block it from taking another: it proved that by
    /// submitting the token, which is how a client that holds a deep lock on a collection goes on
    /// to lock a file inside it.
    /// </para>
    /// </summary>
    public bool TryAcquire(
        string path,
        WebDavLockScope scope,
        bool deep,
        string? owner,
        TimeSpan timeout,
        IReadOnlyCollection<string> submittedTokens,
        out WebDavLock result
    )
    {
        lock (this.locks)
        {
            this.Prune();

            foreach (var held in this.locks)
            {
                // Two locks meet when one covers the other's path — either direction, since a new
                // deep lock reaches down onto locks already held below it.
                var meets = this.Covers(held, path) || (deep && this.IsUnder(held.Path, path));
                if (!meets)
                    continue;

                if (submittedTokens.Contains(held.Token, StringComparer.Ordinal))
                    continue;

                // Two shared locks are allowed to coexist; that is the whole difference.
                if (held.Scope == WebDavLockScope.Shared && scope == WebDavLockScope.Shared)
                    continue;

                result = held;
                return false;
            }

            result = new WebDavLock(
                "opaquelocktoken:" + Guid.NewGuid().ToString("d"),
                path,
                scope,
                deep,
                owner,
                timeout,
                DateTimeOffset.UtcNow.Add(timeout)
            );

            this.locks.Add(result);
            return true;
        }
    }

    /// <summary>Extends a lock's life. Returns null when the token is unknown or has lapsed.</summary>
    public WebDavLock? Refresh(string token, TimeSpan timeout)
    {
        lock (this.locks)
        {
            this.Prune();

            for (var i = 0; i < this.locks.Count; i++)
            {
                if (!string.Equals(this.locks[i].Token, token, StringComparison.Ordinal))
                    continue;

                var refreshed = this.locks[i] with
                {
                    Timeout = timeout,
                    ExpiresUtc = DateTimeOffset.UtcNow.Add(timeout)
                };

                this.locks[i] = refreshed;
                return refreshed;
            }

            return null;
        }
    }

    /// <summary>The lock a token names, whatever path it is on. Null when it is unknown or lapsed.</summary>
    public WebDavLock? Find(string token)
    {
        lock (this.locks)
        {
            this.Prune();

            foreach (var held in this.locks)
            {
                if (string.Equals(held.Token, token, StringComparison.Ordinal))
                    return held;
            }

            return null;
        }
    }

    /// <summary>Releases a lock. False when no lock with that token is held on that path.</summary>
    public bool Release(string path, string token)
    {
        lock (this.locks)
        {
            this.Prune();

            for (var i = 0; i < this.locks.Count; i++)
            {
                var held = this.locks[i];

                if (!string.Equals(held.Token, token, StringComparison.Ordinal))
                    continue;

                if (!string.Equals(held.Path, path, this.comparison))
                    return false;

                this.locks.RemoveAt(i);
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Every lock in force on a path — its own, plus the deep ones held above it. This is what
    /// <c>lockdiscovery</c> reports.
    /// </summary>
    public IReadOnlyList<WebDavLock> Discover(string path)
    {
        lock (this.locks)
        {
            this.Prune();

            List<WebDavLock>? found = null;

            foreach (var held in this.locks)
            {
                if (this.Covers(held, path))
                    (found ??= []).Add(held);
            }

            return (IReadOnlyList<WebDavLock>?)found ?? Array.Empty<WebDavLock>();
        }
    }

    /// <summary>
    /// Drops the locks on a resource and everything under it. Called once it has been deleted or
    /// moved away: RFC 4918 §9.9.1 is explicit that a lock does not travel with a <c>MOVE</c>.
    /// </summary>
    public void ReleaseTree(string path)
    {
        lock (this.locks)
        {
            this.locks.RemoveAll(held =>
                string.Equals(held.Path, path, this.comparison) || this.IsUnder(held.Path, path)
            );
        }
    }

    /// <summary>True when <paramref name="held"/> is in force at <paramref name="path"/>.</summary>
    bool Covers(WebDavLock held, string path)
        => string.Equals(held.Path, path, this.comparison)
            || (held.IsDeep && this.IsUnder(path, held.Path));

    /// <summary>True when <paramref name="candidate"/> sits somewhere below <paramref name="ancestor"/>.</summary>
    bool IsUnder(string candidate, string ancestor)
        => ancestor.Length == 0
            ? candidate.Length > 0
            : candidate.Length > ancestor.Length
                && candidate.StartsWith(ancestor, this.comparison)
                && candidate[ancestor.Length] == '/';

    /// <summary>
    /// Drops lapsed locks. Done on the way into every operation rather than on a timer: a lock only
    /// matters when something asks about it, and a timer on an embedded server is a wakeup the
    /// device did not need.
    /// </summary>
    void Prune()
    {
        if (this.locks.Count == 0)
            return;

        var now = DateTimeOffset.UtcNow;
        this.locks.RemoveAll(held => held.IsExpired(now));
    }

    /// <summary>Clamps what a client asked for to what this mount is willing to grant.</summary>
    public TimeSpan ResolveTimeout(TimeSpan? requested)
    {
        var wanted = requested ?? options.DefaultLockTimeout;

        if (wanted <= TimeSpan.Zero || wanted > options.MaxLockTimeout)
            return options.MaxLockTimeout;

        return wanted;
    }
}
