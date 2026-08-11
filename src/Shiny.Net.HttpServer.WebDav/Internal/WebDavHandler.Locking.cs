using System.Globalization;
using System.Xml;

namespace Shiny.Net.HttpServer.WebDav.Internal;

/// <summary>
/// Compliance class 2: <c>LOCK</c>, <c>UNLOCK</c>, and the <c>If</c> header that makes them mean
/// something.
/// </summary>
partial class WebDavHandler
{
    // ---- the guard every mutating verb goes through ----

    /// <summary>
    /// Decides whether this request may write to <paramref name="path"/>, answering the client
    /// itself when it may not.
    /// <para>
    /// Returns the lock tokens the request submitted, which the caller passes on to any further
    /// check it makes — a <c>MOVE</c> is authorized twice, once at each end, on the same tokens.
    /// Null means the response has already been written and the caller is finished.
    /// </para>
    /// </summary>
    /// <param name="subtree">
    /// True when the operation reaches into the collection's members, as <c>DELETE</c> and
    /// <c>MOVE</c> do. A lock on anything inside blocks those.
    /// </param>
    async ValueTask<IReadOnlyList<string>?> AuthorizeAsync(HttpContext context, DavPath path, bool subtree)
    {
        var tokens = await this.ReadIfAsync(context, path).ConfigureAwait(false);

        if (tokens is null)
            return null;

        return await this.CheckLockAsync(context, path, tokens, subtree).ConfigureAwait(false) ? tokens : null;
    }

    /// <summary>
    /// The lock half of <see cref="AuthorizeAsync"/>, for callers that have already read the
    /// <c>If</c> header and need to check a second resource against it.
    /// </summary>
    async ValueTask<bool> CheckLockAsync(
        HttpContext context,
        DavPath path,
        IReadOnlyList<string> tokens,
        bool subtree
    )
    {
        if (!this.options.EnableLocking)
            return true;

        if (this.locks.FindBlocking(path.Relative, tokens, subtree) is not { } blocking)
            return true;

        await WebDavXml.WriteErrorAsync(
            context,
            StatusCodes.Status423Locked,
            "lock-token-submitted",
            this.HrefFor(blocking.Path, Directory.Exists(this.FullOf(blocking.Path))),
            context.RequestAborted
        ).ConfigureAwait(false);

        return false;
    }

    /// <summary>
    /// Parses and evaluates the <c>If</c> header. Returns the tokens it submitted, an empty list
    /// when there was no header, or null once it has answered 400 or 412.
    /// </summary>
    async ValueTask<IReadOnlyList<string>?> ReadIfAsync(HttpContext context, DavPath path)
    {
        var header = context.Request.Headers.GetFirst(WebDavHeaderNames.If);

        if (!IfHeader.TryParse(header, out var parsed))
        {
            await StatusAsync(context, StatusCodes.Status400BadRequest).ConfigureAwait(false);
            return null;
        }

        if (parsed is null)
            return IfHeader.NoTokens;

        if (!parsed.Evaluate(path.Relative, this.ResolveTagPath, this.StateOf))
        {
            await StatusAsync(context, StatusCodes.Status412PreconditionFailed).ConfigureAwait(false);
            return null;
        }

        return parsed.Tokens;
    }

    string? ResolveTagPath(string url) => this.TryResolveUrl(url, out var path) ? path.Relative : null;

    /// <summary>What a resource currently is, as far as an <c>If</c> header is concerned.</summary>
    (IReadOnlyList<string> Tokens, string? ETag) StateOf(string relative)
    {
        var held = this.locks.Discover(relative);
        var tokens = new string[held.Count];

        for (var i = 0; i < held.Count; i++)
            tokens[i] = held[i].Token;

        var full = this.FullOf(relative);
        var info = new FileInfo(full);

        return (tokens, info.Exists ? ETagFor(info) : null);
    }

    string FullOf(string relative) => relative.Length == 0
        ? this.root
        : Path.Combine(this.root, relative.Replace('/', Path.DirectorySeparatorChar));

    // ---- LOCK ----

    public async ValueTask LockAsync(HttpContext context)
    {
        if (!this.options.EnableLocking)
        {
            await this.NotAllowedAsync(context).ConfigureAwait(false);
            return;
        }

        if (!this.options.AllowWrite)
        {
            await this.NotAllowedAsync(context).ConfigureAwait(false);
            return;
        }

        if (!this.TryResolve(RawPath(context), out var path))
        {
            await StatusAsync(context, StatusCodes.Status403Forbidden).ConfigureAwait(false);
            return;
        }

        using var body = await WebDavXml
            .ReadBodyAsync(context, this.options.MaxXmlBodyBytes, context.RequestAborted)
            .ConfigureAwait(false);

        if (body is null)
        {
            await StatusAsync(context, StatusCodes.Status413PayloadTooLarge).ConfigureAwait(false);
            return;
        }

        if (!WebDavRequests.TryParseLockInfo(body, out var info))
        {
            await StatusAsync(context, StatusCodes.Status400BadRequest).ConfigureAwait(false);
            return;
        }

        var timeout = this.locks.ResolveTimeout(
            WebDavRequests.ParseTimeout(context.Request.Headers.GetFirst(WebDavHeaderNames.Timeout))
        );

        // An empty body is not a malformed request: it is how a client asks to extend a lock it
        // already holds, naming it in the If header.
        if (info is null)
        {
            await this.RefreshLockAsync(context, path, timeout).ConfigureAwait(false);
            return;
        }

        if (!WebDavRequests.TryParseDepth(
                context.Request.Headers.GetFirst(WebDavHeaderNames.Depth),
                Depth.Infinity,
                out var depth
            ) || depth == Depth.One)
        {
            // RFC 4918 §9.10.3: a lock is taken on a resource or on a whole subtree. There is no
            // meaning for one level.
            await StatusAsync(context, StatusCodes.Status400BadRequest).ConfigureAwait(false);
            return;
        }

        var tokens = await this.ReadIfAsync(context, path).ConfigureAwait(false);

        if (tokens is null)
            return;

        var created = false;

        if (!File.Exists(path.Full) && !Directory.Exists(path.Full))
        {
            // RFC 4918 §7.3: locking an unmapped URL creates an empty resource to hold the lock.
            // Not an edge case — it is exactly what a Mac does when you save a new file, and a
            // server that answers 404 here cannot be written to from Finder at all.
            if (path.IsRoot)
            {
                await StatusAsync(context, StatusCodes.Status403Forbidden).ConfigureAwait(false);
                return;
            }

            var parent = Path.GetDirectoryName(path.Full);

            if (parent is null || !Directory.Exists(parent))
            {
                await StatusAsync(context, StatusCodes.Status409Conflict).ConfigureAwait(false);
                return;
            }

            if (!await this.CheckLockAsync(context, path, tokens, subtree: false).ConfigureAwait(false))
                return;

            await using (File.Create(path.Full).ConfigureAwait(false))
            {
            }

            created = true;
        }

        if (!this.locks.TryAcquire(path.Relative, info.Scope, depth == Depth.Infinity, info.Owner, timeout, tokens, out var held))
        {
            await WebDavXml.WriteErrorAsync(
                context,
                StatusCodes.Status423Locked,
                "no-conflicting-lock",
                this.HrefFor(held.Path, Directory.Exists(this.FullOf(held.Path))),
                context.RequestAborted
            ).ConfigureAwait(false);

            return;
        }

        await this.WriteLockResponseAsync(
            context,
            held,
            created ? StatusCodes.Status201Created : StatusCodes.Status200OK
        ).ConfigureAwait(false);
    }

    async ValueTask RefreshLockAsync(HttpContext context, DavPath path, TimeSpan timeout)
    {
        var header = context.Request.Headers.GetFirst(WebDavHeaderNames.If);

        if (!IfHeader.TryParse(header, out var parsed) || parsed is null || parsed.Tokens.Count == 0)
        {
            await StatusAsync(context, StatusCodes.Status400BadRequest).ConfigureAwait(false);
            return;
        }

        foreach (var token in parsed.Tokens)
        {
            // Only a lock that is actually in force here may be refreshed from here. A token that
            // names a lock on another resource is not this resource's to extend.
            if (this.locks.Find(token) is not { } candidate ||
                !this.locks.Discover(path.Relative).Any(l => l.Token == token))
                continue;

            if (this.locks.Refresh(candidate.Token, timeout) is { } refreshed)
            {
                await this.WriteLockResponseAsync(context, refreshed, StatusCodes.Status200OK).ConfigureAwait(false);
                return;
            }
        }

        await StatusAsync(context, StatusCodes.Status412PreconditionFailed).ConfigureAwait(false);
    }

    // ---- UNLOCK ----

    public async ValueTask UnlockAsync(HttpContext context)
    {
        if (!this.options.EnableLocking)
        {
            await this.NotAllowedAsync(context).ConfigureAwait(false);
            return;
        }

        var token = WebDavRequests.ParseLockToken(context.Request.Headers.GetFirst(WebDavHeaderNames.LockToken));

        if (token is null)
        {
            await StatusAsync(context, StatusCodes.Status400BadRequest).ConfigureAwait(false);
            return;
        }

        if (!this.TryResolve(RawPath(context), out var path))
        {
            await StatusAsync(context, StatusCodes.Status403Forbidden).ConfigureAwait(false);
            return;
        }

        // RFC 4918 §9.11.1: a token that is not held on this resource is a conflict, not a 404 —
        // the resource is fine, the client's idea of what it holds is not.
        if (!this.locks.Release(path.Relative, token))
        {
            await StatusAsync(context, StatusCodes.Status409Conflict).ConfigureAwait(false);
            return;
        }

        await StatusAsync(context, StatusCodes.Status204NoContent).ConfigureAwait(false);
    }

    // ---- the lock document, shared with PROPFIND's lockdiscovery ----

    ValueTask WriteLockResponseAsync(HttpContext context, WebDavLock held, int statusCode)
    {
        // Outside the body as well as in it: RFC 4918 §9.10.1 requires the header, and clients read
        // it from there rather than parsing the document they just asked for.
        context.Response.Headers.Set(WebDavHeaderNames.LockToken, $"<{held.Token}>");

        return WebDavXml.WriteAsync(context, statusCode, writer =>
        {
            writer.WriteStartElement(WebDavXml.Prefix, "prop", WebDavXml.Ns);
            writer.WriteStartElement(WebDavXml.Prefix, "lockdiscovery", WebDavXml.Ns);

            this.WriteActiveLock(writer, held);

            writer.WriteEndElement();
            writer.WriteEndElement();
        }, context.RequestAborted);
    }

    void WriteActiveLock(XmlWriter writer, WebDavLock held)
    {
        writer.WriteStartElement(WebDavXml.Prefix, "activelock", WebDavXml.Ns);

        writer.WriteStartElement(WebDavXml.Prefix, "locktype", WebDavXml.Ns);
        writer.WriteElementString(WebDavXml.Prefix, "write", WebDavXml.Ns, null);
        writer.WriteEndElement();

        writer.WriteStartElement(WebDavXml.Prefix, "lockscope", WebDavXml.Ns);
        writer.WriteElementString(
            WebDavXml.Prefix,
            held.Scope == WebDavLockScope.Shared ? "shared" : "exclusive",
            WebDavXml.Ns,
            null
        );
        writer.WriteEndElement();

        writer.WriteElementString(WebDavXml.Prefix, "depth", WebDavXml.Ns, held.IsDeep ? "infinity" : "0");

        if (held.Owner is { Length: > 0 } owner)
        {
            writer.WriteStartElement(WebDavXml.Prefix, "owner", WebDavXml.Ns);

            // Written back exactly as it arrived. The owner is opaque to the server, and a client
            // that put structured XML in there expects to read the same structure out.
            writer.WriteRaw(owner);
            writer.WriteEndElement();
        }

        var remaining = held.ExpiresUtc - DateTimeOffset.UtcNow;

        writer.WriteElementString(
            WebDavXml.Prefix,
            "timeout",
            WebDavXml.Ns,
            "Second-" + ((long)Math.Max(0, Math.Ceiling(remaining.TotalSeconds))).ToString(CultureInfo.InvariantCulture)
        );

        writer.WriteStartElement(WebDavXml.Prefix, "locktoken", WebDavXml.Ns);
        writer.WriteElementString(WebDavXml.Prefix, "href", WebDavXml.Ns, held.Token);
        writer.WriteEndElement();

        // Where the lock was taken, which is not where it is being reported when a deep lock is
        // discovered from a member.
        writer.WriteStartElement(WebDavXml.Prefix, "lockroot", WebDavXml.Ns);
        writer.WriteElementString(
            WebDavXml.Prefix,
            "href",
            WebDavXml.Ns,
            this.HrefFor(held.Path, Directory.Exists(this.FullOf(held.Path)))
        );
        writer.WriteEndElement();

        writer.WriteEndElement();
    }
}
