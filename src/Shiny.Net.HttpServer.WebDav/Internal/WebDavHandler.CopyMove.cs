using Shiny.Net.HttpServer.StaticFiles;

namespace Shiny.Net.HttpServer.WebDav.Internal;

/// <summary>Where a <c>Destination</c> header pointed.</summary>
enum DestinationKind
{
    /// <summary>Inside this mount, and resolvable.</summary>
    Ok,

    /// <summary>Somewhere this mount does not serve — another host, or another prefix.</summary>
    Foreign,

    /// <summary>Inside the mount's namespace, but a path it will not resolve.</summary>
    Refused
}

/// <summary><c>COPY</c> and <c>MOVE</c>.</summary>
partial class WebDavHandler
{
    public ValueTask CopyAsync(HttpContext context) => this.CopyOrMoveAsync(context, move: false);

    public ValueTask MoveAsync(HttpContext context) => this.CopyOrMoveAsync(context, move: true);

    async ValueTask CopyOrMoveAsync(HttpContext context, bool move)
    {
        // MOVE writes at one end and deletes at the other, so it needs both permissions.
        if (!(move ? this.options.AllowMove : this.options.AllowWrite))
        {
            await this.NotAllowedAsync(context).ConfigureAwait(false);
            return;
        }

        if (!this.TryResolve(RawPath(context), out var source))
        {
            await StatusAsync(context, StatusCodes.Status403Forbidden).ConfigureAwait(false);
            return;
        }

        var isCollection = Directory.Exists(source.Full);

        if (!isCollection && !File.Exists(source.Full))
        {
            await StatusAsync(context, StatusCodes.Status404NotFound).ConfigureAwait(false);
            return;
        }

        // Moving the root would take the directory the mount is defined by with it.
        if (move && source.IsRoot)
        {
            await StatusAsync(context, StatusCodes.Status403Forbidden).ConfigureAwait(false);
            return;
        }

        if (context.Request.Headers.GetFirst(WebDavHeaderNames.Destination) is not { Length: > 0 } header)
        {
            await StatusAsync(context, StatusCodes.Status400BadRequest).ConfigureAwait(false);
            return;
        }

        switch (this.ResolveDestination(context, header, out var destination))
        {
            // RFC 4918 §9.8.5: a destination this server cannot write to is a gateway problem, not
            // a bad request — the client's syntax was fine.
            case DestinationKind.Foreign:
                await StatusAsync(context, StatusCodes.Status502BadGateway).ConfigureAwait(false);
                return;

            case DestinationKind.Refused:
                await StatusAsync(context, StatusCodes.Status403Forbidden).ConfigureAwait(false);
                return;
        }

        if (!WebDavRequests.TryParseOverwrite(context.Request.Headers.GetFirst(WebDavHeaderNames.Overwrite), out var overwrite))
        {
            await StatusAsync(context, StatusCodes.Status400BadRequest).ConfigureAwait(false);
            return;
        }

        var depthHeader = context.Request.Headers.GetFirst(WebDavHeaderNames.Depth);
        var shallow = false;

        if (move)
        {
            // RFC 4918 §9.9.2: a MOVE is the whole subtree, and a Depth saying otherwise is
            // malformed rather than a narrower request.
            if (depthHeader is not null && !depthHeader.Trim().Equals("infinity", StringComparison.OrdinalIgnoreCase))
            {
                await StatusAsync(context, StatusCodes.Status400BadRequest).ConfigureAwait(false);
                return;
            }
        }
        else
        {
            if (!WebDavRequests.TryParseDepth(depthHeader, Depth.Infinity, out var depth) || depth == Depth.One)
            {
                await StatusAsync(context, StatusCodes.Status400BadRequest).ConfigureAwait(false);
                return;
            }

            // Depth: 0 on a collection copies the collection and its properties, not its members.
            shallow = depth == Depth.Zero;
        }

        if (string.Equals(source.Relative, destination.Relative, StaticFilePath.PathComparison))
        {
            await StatusAsync(context, StatusCodes.Status403Forbidden).ConfigureAwait(false);
            return;
        }

        // A collection cannot be copied or moved inside itself: the walk would keep finding the
        // output it had just written.
        if (isCollection && IsUnder(destination.Relative, source.Relative))
        {
            await StatusAsync(context, StatusCodes.Status403Forbidden).ConfigureAwait(false);
            return;
        }

        if (destination.IsRoot)
        {
            await StatusAsync(context, StatusCodes.Status403Forbidden).ConfigureAwait(false);
            return;
        }

        var destinationParent = Path.GetDirectoryName(destination.Full);

        if (destinationParent is null || !Directory.Exists(destinationParent))
        {
            await StatusAsync(context, StatusCodes.Status409Conflict).ConfigureAwait(false);
            return;
        }

        var tokens = await this.ReadIfAsync(context, source).ConfigureAwait(false);

        if (tokens is null)
            return;

        // A MOVE removes the source, so the source's locks matter too. A COPY only reads it.
        if (move && !await this.CheckLockAsync(context, source, tokens, subtree: isCollection).ConfigureAwait(false))
            return;

        var destinationExisted = File.Exists(destination.Full) || Directory.Exists(destination.Full);

        if (!await this.CheckLockAsync(context, destination, tokens, subtree: destinationExisted).ConfigureAwait(false))
            return;

        if (destinationExisted)
        {
            if (!overwrite)
            {
                await StatusAsync(context, StatusCodes.Status412PreconditionFailed).ConfigureAwait(false);
                return;
            }

            // RFC 4918 §9.8.4: an overwriting COPY behaves as if the destination had been DELETEd
            // first. Doing it literally is also the only way a collection's stale members go.
            if (Directory.Exists(destination.Full))
                Directory.Delete(destination.Full, recursive: true);
            else
                File.Delete(destination.Full);

            this.locks.ReleaseTree(destination.Relative);

            await this.properties
                .DeleteAsync(destination.Relative, recursive: true, context.RequestAborted)
                .ConfigureAwait(false);
        }

        if (move)
        {
            if (isCollection)
                Directory.Move(source.Full, destination.Full);
            else
                File.Move(source.Full, destination.Full);

            // A lock does not travel with the resource — RFC 4918 §9.9.1.
            this.locks.ReleaseTree(source.Relative);
        }
        else if (isCollection)
        {
            CopyTree(source.Full, destination.Full, shallow);
        }
        else
        {
            File.Copy(source.Full, destination.Full, overwrite: false);
        }

        await this.properties
            .CopyAsync(source.Relative, destination.Relative, move, context.RequestAborted)
            .ConfigureAwait(false);

        // 204 says "the destination you named is now what you asked for"; 201 says "and there was
        // nothing there before". Clients use the difference to decide whether to refresh a listing.
        await StatusAsync(
            context,
            destinationExisted ? StatusCodes.Status204NoContent : StatusCodes.Status201Created
        ).ConfigureAwait(false);
    }

    static void CopyTree(string source, string destination, bool shallow)
    {
        Directory.CreateDirectory(destination);

        if (shallow)
            return;

        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            // Not descending into a link keeps a cycle inside the root from turning a copy into an
            // unbounded one.
            if (new DirectoryInfo(directory).Attributes.HasFlag(FileAttributes.ReparsePoint))
                continue;

            CopyTree(directory, Path.Combine(destination, Path.GetFileName(directory)), shallow: false);
        }
    }

    /// <summary>Maps a <c>Destination</c> header onto a path in this mount.</summary>
    DestinationKind ResolveDestination(HttpContext context, string header, out DavPath path)
    {
        path = default;

        if (!TryGetUrlPath(header, out var encodedPath, out var authority))
            return DestinationKind.Foreign;

        // Copying between servers is something this one cannot do, and treating the path after a
        // foreign authority as local would write to the wrong place.
        if (authority is { Length: > 0 } &&
            context.Request.Host is { Length: > 0 } host &&
            !authority.Equals(host, StringComparison.OrdinalIgnoreCase))
            return DestinationKind.Foreign;

        if (!this.TrySplitPrefix(encodedPath, out var relative))
            return DestinationKind.Foreign;

        return this.TryResolve(relative, out path) ? DestinationKind.Ok : DestinationKind.Refused;
    }

    static bool IsUnder(string candidate, string ancestor)
        => ancestor.Length == 0
            ? candidate.Length > 0
            : candidate.Length > ancestor.Length
                && candidate.StartsWith(ancestor, StaticFilePath.PathComparison)
                && candidate[ancestor.Length] == '/';
}
