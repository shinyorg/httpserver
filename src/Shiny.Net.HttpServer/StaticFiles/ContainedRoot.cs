using System.IO.Enumeration;

namespace Shiny.Net.HttpServer.StaticFiles;

/// <summary>Where a request path really leads, once every link on the way has been followed.</summary>
/// <param name="FullPath">
/// The real path: under the real root, links resolved. For a path that does not exist yet, the deepest
/// part that does, resolved, with the rest appended as given.
/// </param>
/// <param name="RelativePath">
/// The request's own path with forward slashes, spelled the way the directory entries are when names
/// were canonicalised, and the way the request spelled them otherwise. Empty at the root.
/// </param>
/// <param name="RealRelativePath">
/// Where <paramref name="FullPath"/> sits relative to the real root, with forward slashes. Differs from
/// <paramref name="RelativePath"/> only when a link inside the root was followed.
/// </param>
/// <param name="Exists">Whether the whole path exists.</param>
readonly record struct ContainedPath(string FullPath, string RelativePath, string RealRelativePath, bool Exists);

/// <summary>
/// A directory that request paths are resolved inside, following links the way the file system will.
/// <para>
/// <see cref="Path.GetFullPath(string)"/> is lexical: it collapses <c>..</c>, but has no idea that
/// <c>assets</c> is a link to <c>/etc</c>. Checking only the last component's link target misses the
/// case that matters - a linked <em>directory</em> in the middle of the path - so this walks every
/// component that exists, resolving each link as it meets it, and judges containment on where the path
/// really ends up. A link that stays inside the root still works; one that leaves it is refused, for a
/// read and for a write alike.
/// </para>
/// <para>
/// There is a window between this check and the open that follows it, in which something with write
/// access to the root could swap a directory for a link. Closing that needs <c>openat</c> with
/// <c>O_NOFOLLOW</c> on every component, which .NET does not expose; what is closed here is every path a
/// remote caller can construct on its own.
/// </para>
/// </summary>
sealed class ContainedRoot(string rootFullPath)
{
    /// <summary>Linux's own limit (MAXSYMLINKS), so a cycle of links fails rather than spins.</summary>
    const int MaxLinkHops = 40;

    readonly string root = Path.TrimEndingDirectorySeparator(rootFullPath);
    string? realRoot;

    /// <summary>
    /// The root as the file system sees it, its own links resolved - <c>/var/folders/…</c> is
    /// <c>/private/var/folders/…</c> on macOS. Resolved once; null (and retried) while it does not exist.
    /// </summary>
    public string? RealRoot
    {
        get
        {
            if (this.realRoot is not null)
                return this.realRoot;

            var pathRoot = Path.GetPathRoot(this.root);

            if (string.IsNullOrEmpty(pathRoot))
                return null;

            if (!TryWalk(pathRoot, this.root[pathRoot.Length..], canonicalNames: false, refuseHidden: false, out var walk) || !walk.Exists)
                return null;

            return this.realRoot = Path.TrimEndingDirectorySeparator(walk.FullPath);
        }
    }

    /// <summary>
    /// Resolves <paramref name="relativePath"/> - already through <c>StaticFilePath.TryNormalize</c>, so
    /// free of <c>..</c> - inside the root. False when it leaves the root by any route, passes through
    /// something hidden while hidden files are refused, or (with <paramref name="canonicalNames"/>)
    /// names an entry only by an alias such as a Windows short name.
    /// <para>
    /// <paramref name="canonicalNames"/> looks each component up in its directory and uses the
    /// entry's real spelling. It costs a directory scan per component, so it is only worth it where a
    /// decision is made on the name - a filter on a case-insensitive file system, which would
    /// otherwise see <c>PRIVATE</c> and wave it through.
    /// </para>
    /// </summary>
    public bool TryResolve(string relativePath, bool canonicalNames, bool serveHiddenFiles, out ContainedPath resolved)
    {
        resolved = default;

        if (this.RealRoot is not { } real)
            return false;

        try
        {
            if (!TryWalk(real, relativePath, canonicalNames, refuseHidden: !serveHiddenFiles, out var walk))
                return false;

            var full = Path.TrimEndingDirectorySeparator(walk.FullPath);

            if (!IsInside(full, real))
                return false;

            var realRelative = full.Length > real.Length
                ? full[(real.Length + 1)..].Replace(Path.DirectorySeparatorChar, '/')
                : string.Empty;

            // Only a followed link can make the real path differ from the one walked, and every
            // component of the walked one was already checked on the way.
            if (walk.FollowedLink && !serveHiddenFiles && IsHiddenAlong(real, realRelative))
                return false;

            resolved = new ContainedPath(full, string.Join('/', walk.RequestNames), realRelative, walk.Exists);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A component that cannot be read cannot be shown to stay inside the root.
            return false;
        }
    }

    static bool IsInside(string fullPath, string root) =>
        fullPath.StartsWith(root + Path.DirectorySeparatorChar, StaticFilePath.PathComparison)
        || string.Equals(fullPath, root, StaticFilePath.PathComparison);

    /// <summary>A dotfile or Hidden-attribute entry anywhere between the root and the real target.</summary>
    static bool IsHiddenAlong(string root, string realRelative)
    {
        var current = root;

        foreach (var name in realRelative.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (name.StartsWith('.'))
                return true;

            current = Path.Combine(current, name);
            var info = new FileInfo(current);

            if (IsHidden(info))
                return true;
        }

        return false;
    }

    static bool IsHidden(FileInfo info)
    {
        // -1 is what Attributes reports for an entry that is not there.
        var attributes = info.Attributes;

        return (int)attributes != -1 && attributes.HasFlag(FileAttributes.Hidden);
    }

    readonly record struct Walk(string FullPath, List<string> RequestNames, bool Exists, bool FollowedLink);

    /// <summary>
    /// <c>realpath(3)</c>, tolerant of a missing tail: resolves each component of
    /// <paramref name="relative"/> under <paramref name="start"/> (which must already be real),
    /// following links wherever they point, until it reaches a component that does not exist - after
    /// which the rest is appended as given, since there is nothing left to follow.
    /// </summary>
    static bool TryWalk(string start, string relative, bool canonicalNames, bool refuseHidden, out Walk walk)
    {
        walk = default;

        // Components still to visit, nearest on top. A link's target is pushed on in front of whatever
        // followed the link, which is all "following" a link means.
        var pending = new Stack<(string Name, bool FromRequest)>();
        Push(pending, relative, fromRequest: true);

        var current = start;
        var requestNames = new List<string>();
        var exists = true;
        var followed = false;
        var hops = 0;

        while (pending.Count > 0)
        {
            var (name, fromRequest) = pending.Pop();

            if (name == "..")
            {
                // Only ever from a link's target: request paths have had their dot segments refused.
                // Resolved against the real directory, which is what the kernel does.
                current = Path.GetDirectoryName(current) ?? current;
            }
            else if (name is not ("" or "."))
            {
                if (!exists)
                {
                    current = Path.Combine(current, name);

                    if (fromRequest)
                        requestNames.Add(name);
                }
                else
                {
                    if (!TryCanonicalName(current, name, canonicalNames, out var real))
                        return false;

                    var next = Path.Combine(current, real);
                    var info = new FileInfo(next);
                    var target = info.LinkTarget;

                    if (fromRequest)
                    {
                        requestNames.Add(real);

                        // The entry the request named, link or not: hidden there is hidden.
                        if (refuseHidden && IsHidden(info))
                            return false;
                    }

                    if (target is not null)
                    {
                        if (++hops > MaxLinkHops)
                            return false;

                        followed = true;

                        // An absolute target starts again from its own root; a relative one carries
                        // on from the link's directory, which is already real.
                        var targetRoot = Path.GetPathRoot(target) ?? string.Empty;

                        if (targetRoot.Length > 0)
                            current = targetRoot;

                        Push(pending, target[targetRoot.Length..], fromRequest: false);
                    }
                    else if (Directory.Exists(next))
                    {
                        current = next;
                    }
                    else
                    {
                        // A file ends the walk as surely as a missing entry does: nothing is under it,
                        // so whatever is still pending does not exist either.
                        exists = info.Exists && pending.Count == 0;
                        current = next;
                    }
                }
            }
        }

        walk = new Walk(current, requestNames, exists, followed);
        return true;
    }

    static void Push(Stack<(string Name, bool FromRequest)> pending, string path, bool fromRequest)
    {
        var names = path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);

        for (var i = names.Length - 1; i >= 0; i--)
            pending.Push((names[i], fromRequest));
    }

    /// <summary>
    /// The entry's own spelling of <paramref name="name"/> in <paramref name="directory"/>. False when
    /// the directory has no entry by that name ignoring case and yet the OS still resolves it - a
    /// Windows short name (<c>PROGRA~1</c>) or a trailing dot - since a check made on that spelling
    /// would be a check on a name the file does not have.
    /// </summary>
    static bool TryCanonicalName(string directory, string name, bool canonicalNames, out string real)
    {
        real = name;

        if (!canonicalNames)
            return true;

        string? exact = null;
        string? folded = null;

        var entries = new FileSystemEnumerable<string>(
            directory,
            (ref FileSystemEntry entry) => entry.FileName.ToString(),

            // Hidden and system entries are skipped by default, and they are exactly the ones whose
            // name matters.
            new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true, RecurseSubdirectories = false }
        )
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) => entry.FileName.Equals(name, StringComparison.OrdinalIgnoreCase)
        };

        // A case-sensitive volume can hold both "Private" and "private"; the exact one is the one the
        // OS will open.
        foreach (var found in entries)
        {
            if (string.Equals(found, name, StringComparison.Ordinal))
                exact = found;
            else
                folded ??= found;
        }

        if ((exact ?? folded) is { } match)
        {
            real = match;
            return true;
        }

        var candidate = Path.Combine(directory, name);
        var probe = new FileInfo(candidate);

        return !(probe.Exists || Directory.Exists(candidate) || probe.LinkTarget is not null);
    }
}
