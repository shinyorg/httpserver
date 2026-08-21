using System.Text;
using Shiny.Net.HttpServer.Files;
using Shiny.Net.HttpServer.StaticFiles;

namespace Shiny.Net.HttpServer.WebDav.Internal;

/// <summary>A request path that has been checked and resolved onto the file system.</summary>
/// <param name="Relative">Relative to the mount root, forward slashes, empty for the root itself.</param>
/// <param name="Full">The absolute path on disk.</param>
readonly record struct DavPath(string Relative, string Full)
{
    public bool IsRoot => this.Relative.Length == 0;
}

/// <summary>
/// The verbs behind <see cref="WebDavExtensions.MapWebDav(HttpServer, string, WebDavOptions)"/>.
/// <para>
/// Every path from a request goes through <see cref="TryResolve"/> before anything touches the file
/// system, and nothing else in here builds a path by hand. That is the security model: one door,
/// checked once, rather than a check at each call site that someone will eventually forget.
/// </para>
/// </summary>
sealed partial class WebDavHandler
{
    readonly WebDavOptions options;
    readonly string root;
    readonly string basePath;
    readonly WebDavLockManager locks;
    readonly IWebDavPropertyStore properties;

    public WebDavHandler(WebDavOptions options, string root, string basePath)
    {
        this.options = options;
        this.root = root;
        this.basePath = basePath;
        this.locks = new WebDavLockManager(options);
        this.properties = options.PropertyStore ?? new InMemoryWebDavPropertyStore();
    }

    // ---- OPTIONS ----

    /// <summary>
    /// Announces the compliance classes and the methods this mount answers.
    /// <para>
    /// The one request every client makes first, and the one that decides how it will treat the
    /// mount for the rest of the session — a server that does not say <c>2</c> here gets mounted
    /// read-only by Finder and by the Windows redirector no matter what it allows afterwards.
    /// </para>
    /// </summary>
    public ValueTask OptionsAsync(HttpContext context)
    {
        var response = context.Response;

        response.Headers.Set(WebDavHeaderNames.Dav, this.options.EnableLocking ? "1, 2" : "1");

        // Without this the Windows redirector probes for FrontPage RPC before trying DAV, which
        // costs several seconds and a handful of 404s on every mount.
        response.Headers.Set(WebDavHeaderNames.MsAuthorVia, "DAV");
        response.Headers.Set(HeaderNames.Allow, this.AllowHeader());

        response.StatusCode = StatusCodes.Status200OK;
        response.ContentLength = 0;

        return response.StartAsync(context.RequestAborted);
    }

    string AllowHeader()
    {
        var builder = new StringBuilder("OPTIONS, HEAD, GET, PROPFIND");

        if (this.options.AllowWrite)
            builder.Append(", PUT, MKCOL, PROPPATCH, COPY");

        if (this.options.AllowDelete)
            builder.Append(", DELETE");

        if (this.options.AllowMove)
            builder.Append(", MOVE");

        if (this.options.EnableLocking)
            builder.Append(", LOCK, UNLOCK");

        return builder.ToString();
    }

    // ---- GET / HEAD ----

    public async ValueTask GetAsync(HttpContext context)
    {
        if (!this.TryResolve(RawPath(context), out var path))
        {
            await StatusAsync(context, StatusCodes.Status404NotFound).ConfigureAwait(false);
            return;
        }

        if (Directory.Exists(path.Full))
        {
            if (!this.options.DirectoryBrowsing)
            {
                await this.NotAllowedAsync(context).ConfigureAwait(false);
                return;
            }

            await this.WriteIndexAsync(context, path).ConfigureAwait(false);
            return;
        }

        if (!File.Exists(path.Full))
        {
            await StatusAsync(context, StatusCodes.Status404NotFound).ConfigureAwait(false);
            return;
        }

        var info = new FileInfo(path.Full);

        // Through the download result so ranges, ETags and conditional requests all work — a client
        // resuming a large file over a phone's connection is exactly the case that needs them. No
        // download name, though: this is a mount, and a Content-Disposition would turn every open
        // into a save.
        await FileDownloadResult
            .FromOpener(
                _ => new ValueTask<Stream>(OpenRead(path.Full)),
                info.Length,
                this.ContentTypeFor(info.Name),
                downloadName: null,
                ETagFor(info),
                info.LastWriteTimeUtc
            )
            .ExecuteAsync(context)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The file manager a browser gets on a collection: the listing, plus whatever verbs this mount
    /// allows. WebDAV itself has nothing to say about <c>GET</c> on a collection.
    /// </summary>
    async ValueTask WriteIndexAsync(HttpContext context, DavPath path)
    {
        var entries = new List<WebDavDirectoryPage.Entry>();

        foreach (var child in this.Children(path))
        {
            var isCollection = child is DirectoryInfo;

            entries.Add(new WebDavDirectoryPage.Entry(
                child.Name,
                this.HrefFor(Join(path.Relative, child.Name), isCollection),
                isCollection,
                child is FileInfo file ? file.Length : 0,
                child.LastWriteTimeUtc
            ));
        }

        var model = new WebDavDirectoryPage.Model(
            path.IsRoot ? this.RootDisplayName : path.Relative,
            this.HrefFor(path, isCollection: true),
            this.ParentHref(path),
            this.Trail(path),
            entries,
            new WebDavDirectoryPage.Capabilities(
                this.options.AllowWrite,
                this.options.AllowDelete,
                this.options.AllowMove,
                this.options.MaxUploadBytes
            )
        );

        // A listing that a delete or an upload has already invalidated is exactly what the back
        // button would otherwise serve from cache.
        context.Response.Headers.Set(HeaderNames.CacheControl, "no-store");

        await context.Response
            .WriteTextAsync(WebDavDirectoryPage.Render(model), "text/html; charset=utf-8", context.RequestAborted)
            .ConfigureAwait(false);
    }


    /// <summary>
    /// The collection above this one, or null at the mount root - which has no parent this mount is
    /// willing to name.
    /// </summary>
    /// <remarks>
    /// Absolute, like every href here: a browser that arrived without the trailing slash - which is
    /// how anyone types a mount URL - resolves a relative one against the *parent*, so every link in
    /// the listing would land a directory short.
    /// </remarks>
    string? ParentHref(DavPath path)
    {
        if (path.IsRoot)
            return null;

        var cut = path.Relative.LastIndexOf('/');

        return this.HrefFor(cut < 0 ? string.Empty : path.Relative[..cut], isCollection: true);
    }


    /// <summary>The walk back to the mount root, root first, this collection last.</summary>
    IReadOnlyList<WebDavDirectoryPage.Crumb> Trail(DavPath path)
    {
        var trail = new List<WebDavDirectoryPage.Crumb>
        {
            new(this.RootDisplayName, this.HrefFor(string.Empty, isCollection: true))
        };

        if (path.IsRoot)
            return trail;

        var walked = string.Empty;

        foreach (var segment in path.Relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            walked = Join(walked, segment);
            trail.Add(new WebDavDirectoryPage.Crumb(segment, this.HrefFor(walked, isCollection: true)));
        }

        return trail;
    }


    // ---- PUT ----

    public async ValueTask PutAsync(HttpContext context)
    {
        if (!this.options.AllowWrite)
        {
            await this.NotAllowedAsync(context).ConfigureAwait(false);
            return;
        }

        if (!this.TryResolve(RawPath(context), out var path) || path.IsRoot)
        {
            await StatusAsync(context, StatusCodes.Status403Forbidden).ConfigureAwait(false);
            return;
        }

        // RFC 4918 §9.7.1: a partial PUT has no defined meaning. Refusing beats writing the bytes
        // at offset zero, which is what ignoring the header would do.
        if (context.Request.Headers.GetFirst(HeaderNames.ContentRange) is not null)
        {
            await StatusAsync(context, StatusCodes.Status400BadRequest).ConfigureAwait(false);
            return;
        }

        if (Directory.Exists(path.Full))
        {
            await this.NotAllowedAsync(context).ConfigureAwait(false);
            return;
        }

        // A PUT does not create intermediate collections; RFC 4918 §9.7.1 makes a missing parent a
        // conflict, and a client that meant to make one will MKCOL it.
        var parent = Path.GetDirectoryName(path.Full);
        if (parent is null || !Directory.Exists(parent))
        {
            await StatusAsync(context, StatusCodes.Status409Conflict).ConfigureAwait(false);
            return;
        }

        if (await this.AuthorizeAsync(context, path, subtree: false).ConfigureAwait(false) is null)
            return;

        if (context.Request.ContentLength is { } declared && declared > this.options.MaxUploadBytes)
        {
            await StatusAsync(context, StatusCodes.Status413PayloadTooLarge).ConfigureAwait(false);
            return;
        }

        var existed = File.Exists(path.Full);

        // Written to a temporary file and moved into place, so a failed or abandoned upload cannot
        // leave a half-written file where a whole one used to be.
        var staging = path.Full + ".webdav-" + Guid.NewGuid().ToString("n")[..8];

        try
        {
            if (!await this.CopyBodyAsync(context, staging).ConfigureAwait(false))
            {
                await StatusAsync(context, StatusCodes.Status413PayloadTooLarge).ConfigureAwait(false);
                return;
            }

            File.Move(staging, path.Full, overwrite: true);
        }
        finally
        {
            if (File.Exists(staging))
                File.Delete(staging);
        }

        // The new tag, so a client can make its next write conditional without a round trip.
        context.Response.Headers.Set(HeaderNames.ETag, ETagFor(new FileInfo(path.Full)));

        await StatusAsync(
            context,
            existed ? StatusCodes.Status204NoContent : StatusCodes.Status201Created
        ).ConfigureAwait(false);
    }

    // ---- DELETE ----

    public async ValueTask DeleteAsync(HttpContext context)
    {
        if (!this.options.AllowDelete)
        {
            await this.NotAllowedAsync(context).ConfigureAwait(false);
            return;
        }

        // Deleting the mount root would take the directory the mount is defined by with it.
        if (!this.TryResolve(RawPath(context), out var path) || path.IsRoot)
        {
            await StatusAsync(context, StatusCodes.Status403Forbidden).ConfigureAwait(false);
            return;
        }

        var isCollection = Directory.Exists(path.Full);

        if (!isCollection && !File.Exists(path.Full))
        {
            await StatusAsync(context, StatusCodes.Status404NotFound).ConfigureAwait(false);
            return;
        }

        // RFC 4918 §9.6: DELETE on a collection is always Depth: infinity, and anything else is
        // malformed rather than a narrower request.
        if (isCollection &&
            context.Request.Headers.GetFirst(WebDavHeaderNames.Depth) is { } depth &&
            !depth.Trim().Equals("infinity", StringComparison.OrdinalIgnoreCase))
        {
            await StatusAsync(context, StatusCodes.Status400BadRequest).ConfigureAwait(false);
            return;
        }

        if (await this.AuthorizeAsync(context, path, subtree: isCollection).ConfigureAwait(false) is null)
            return;

        if (isCollection)
            Directory.Delete(path.Full, recursive: true);
        else
            File.Delete(path.Full);

        this.locks.ReleaseTree(path.Relative);

        await this.properties
            .DeleteAsync(path.Relative, isCollection, context.RequestAborted)
            .ConfigureAwait(false);

        await StatusAsync(context, StatusCodes.Status204NoContent).ConfigureAwait(false);
    }

    // ---- MKCOL ----

    public async ValueTask MkColAsync(HttpContext context)
    {
        if (!this.options.AllowWrite)
        {
            await this.NotAllowedAsync(context).ConfigureAwait(false);
            return;
        }

        // RFC 4918 §9.3.1: a body on MKCOL is a request this server does not understand, and the
        // answer is 415 rather than a silently ignored payload.
        if (context.Request.HasBody)
        {
            await StatusAsync(context, StatusCodes.Status415UnsupportedMediaType).ConfigureAwait(false);
            return;
        }

        if (!this.TryResolve(RawPath(context), out var path) || path.IsRoot)
        {
            await StatusAsync(context, StatusCodes.Status403Forbidden).ConfigureAwait(false);
            return;
        }

        if (Directory.Exists(path.Full) || File.Exists(path.Full))
        {
            await this.NotAllowedAsync(context).ConfigureAwait(false);
            return;
        }

        var parent = Path.GetDirectoryName(path.Full);
        if (parent is null || !Directory.Exists(parent))
        {
            await StatusAsync(context, StatusCodes.Status409Conflict).ConfigureAwait(false);
            return;
        }

        if (await this.AuthorizeAsync(context, path, subtree: false).ConfigureAwait(false) is null)
            return;

        Directory.CreateDirectory(path.Full);

        await StatusAsync(context, StatusCodes.Status201Created).ConfigureAwait(false);
    }

    // ---- shared plumbing ----

    string RootDisplayName => this.options.DisplayName
        ?? Path.GetFileName(this.root)
        ?? "/";

    /// <summary>
    /// The catch-all the route captured, which is empty for a request to the mount root — that
    /// route has no <c>{*path}</c> to fill.
    /// </summary>
    static string RawPath(HttpContext context)
        => context.Request.RouteValues.TryGetValue("path", out var value)
            ? value?.ToString() ?? string.Empty
            : string.Empty;

    /// <summary>
    /// Turns a request path into a real one inside the root.
    /// <para>
    /// The path arrives already percent-decoded, so <c>%2e%2e%2f</c> is a plain <c>../</c> by the
    /// time it gets here — which is why the segment check happens on this value and not on the raw
    /// target. Resolution does not require the path to exist: a <c>PUT</c> or a <c>Destination</c>
    /// names something that is about to.
    /// </para>
    /// </summary>
    bool TryResolve(string raw, out DavPath path)
    {
        path = new DavPath(string.Empty, this.root);

        if (raw.Length == 0)
            return true;

        if (!StaticFilePath.TryNormalize(raw, this.options.ServeHiddenFiles, out var segments))
            return false;

        var candidate = Path.GetFullPath(Path.Combine(this.root, segments));

        // Belt and braces after the segment check, and again after following links — a symlink is
        // the one way a path that looks contained can leave.
        if (!this.IsInsideRoot(candidate))
            return false;

        var relative = segments.Replace(Path.DirectorySeparatorChar, '/');

        if (!this.PassesFilter(relative))
            return false;

        var link = File.Exists(candidate) || Directory.Exists(candidate)
            ? new FileInfo(candidate).ResolveLinkTarget(returnFinalTarget: true)
            : null;

        if (link is not null && !this.IsInsideRoot(link.FullName))
            return false;

        path = new DavPath(relative, candidate);
        return true;
    }

    /// <summary>Maps a URL — absolute or origin-relative — onto a path inside this mount.</summary>
    bool TryResolveUrl(string url, out DavPath path)
    {
        path = default;

        return TryGetUrlPath(url, out var encodedPath, out _)
            && this.TrySplitPrefix(encodedPath, out var relative)
            && this.TryResolve(relative, out path);
    }

    /// <summary>
    /// Takes the path out of a URL, absolute or origin-relative, still percent-encoded.
    /// <paramref name="authority"/> is null when the URL named no host.
    /// </summary>
    static bool TryGetUrlPath(string url, out string encodedPath, out string? authority)
    {
        encodedPath = string.Empty;
        authority = null;

        // Tested before Uri.TryCreate, not after. On Unix an absolute file-system path *is* a valid
        // absolute URI — "/dav/notes.txt" parses as file:///dav/notes.txt — so letting the parser
        // look first turns every origin-relative Destination into a foreign one.
        if (url.StartsWith('/'))
        {
            var cut = url.AsSpan().IndexOfAny('?', '#');
            encodedPath = cut < 0 ? url : url[..cut];

            return true;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var absolute))
            return false;

        if (!absolute.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) &&
            !absolute.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            return false;

        encodedPath = absolute.AbsolutePath;
        authority = absolute.Authority;

        return true;
    }

    /// <summary>
    /// Strips the mount's own prefix off a URL path, leaving the part this mount is about.
    /// <para>
    /// Decoded segment by segment rather than all at once: an encoded <c>%2F</c> inside a name is
    /// part of that name, and decoding the whole path first would promote it to a separator — which
    /// is one of the ways a containment check gets walked past.
    /// </para>
    /// </summary>
    bool TrySplitPrefix(string encodedPath, out string relative)
    {
        relative = string.Empty;

        var segments = encodedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var prefix = this.basePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length < prefix.Length)
            return false;

        for (var i = 0; i < prefix.Length; i++)
        {
            if (!Uri.UnescapeDataString(segments[i]).Equals(prefix[i], StringComparison.OrdinalIgnoreCase))
                return false;
        }

        var tail = new string[segments.Length - prefix.Length];

        for (var i = 0; i < tail.Length; i++)
            tail[i] = Uri.UnescapeDataString(segments[prefix.Length + i]);

        relative = string.Join('/', tail);
        return true;
    }

    /// <summary>The href a response reports for a path.</summary>
    string HrefFor(DavPath path, bool isCollection) => WebDavXml.Href(this.basePath, path.Relative, isCollection);

    string HrefFor(string relative, bool isCollection) => WebDavXml.Href(this.basePath, relative, isCollection);

    /// <summary>The members of a collection that this mount is willing to show.</summary>
    IEnumerable<FileSystemInfo> Children(DavPath path)
    {
        var directory = new DirectoryInfo(path.Full);

        foreach (var child in directory.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (this.IsVisible(child.Name, child.Attributes, path.Relative))
                yield return child;
        }

        foreach (var child in directory.EnumerateFiles().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (this.IsVisible(child.Name, child.Attributes, path.Relative))
                yield return child;
        }
    }

    bool IsVisible(string name, FileAttributes attributes, string parentRelative)
    {
        if (!this.options.ServeHiddenFiles && (name.StartsWith('.') || attributes.HasFlag(FileAttributes.Hidden)))
            return false;

        return this.PassesFilter(Join(parentRelative, name));
    }

    bool PassesFilter(string relative) => this.options.Filter is null || this.options.Filter(relative);

    static string Join(string parent, string name) => parent.Length == 0 ? name : parent + "/" + name;

    bool IsInsideRoot(string fullPath)
        => fullPath.StartsWith(this.root + Path.DirectorySeparatorChar, StaticFilePath.PathComparison)
            || string.Equals(fullPath, this.root, StaticFilePath.PathComparison);

    string ContentTypeFor(string name)
    {
        var extension = Path.GetExtension(name);

        return ContentTypes.IsKnownExtension(extension)
            ? ContentTypes.ForFileName(name)
            : this.options.DefaultContentType;
    }

    /// <summary>
    /// Length plus modification time, quoted. The same shape the static file handler uses, so a
    /// client that fetched a file over one and writes it back over the other sees one entity.
    /// </summary>
    static string ETagFor(FileInfo info) => $"\"{info.LastWriteTimeUtc.Ticks:x}-{info.Length:x}\"";

    static FileStream OpenRead(string path) => new(
        path,
        new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        }
    );

    /// <summary>Copies the body to a file, or returns false once it passes the upload limit.</summary>
    async Task<bool> CopyBodyAsync(HttpContext context, string destination)
    {
        var buffer = new byte[64 * 1024];
        long total = 0;

        var file = new FileStream(
            destination,
            new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            }
        );

        await using (file.ConfigureAwait(false))
        {
            int read;
            while ((read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted).ConfigureAwait(false)) > 0)
            {
                total += read;

                // Counted as it copies rather than trusting Content-Length, which a client is free
                // to understate or omit entirely.
                if (total > this.options.MaxUploadBytes)
                    return false;

                await file.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted).ConfigureAwait(false);
            }
        }

        return true;
    }

    /// <summary>
    /// 405, with the <c>Allow</c> header this mount publishes.
    /// <para>
    /// The answer to a verb the options turn off, rather than 403. A read-only mount does not
    /// support <c>PUT</c> — that is what the <c>Allow</c> it sent with <c>OPTIONS</c> said, and
    /// answering with the same list here keeps the two from disagreeing.
    /// </para>
    /// </summary>
    ValueTask NotAllowedAsync(HttpContext context)
    {
        context.Response.Headers.Set(HeaderNames.Allow, this.AllowHeader());

        return StatusAsync(context, StatusCodes.Status405MethodNotAllowed);
    }

    /// <summary>A bare status, which is what a WebDAV client wants when there is nothing to say.</summary>
    static ValueTask StatusAsync(HttpContext context, int statusCode)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentLength = 0;

        return context.Response.StartAsync(context.RequestAborted);
    }
}
