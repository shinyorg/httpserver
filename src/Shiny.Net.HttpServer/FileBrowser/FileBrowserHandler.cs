using System.Globalization;
using Shiny.Net.HttpServer.Files;
using Shiny.Net.HttpServer.StaticFiles;

namespace Shiny.Net.HttpServer.FileBrowser;

/// <summary>
/// The verbs behind <see cref="FileBrowserExtensions.MapFileBrowser(HttpServer, string, FileBrowserOptions)"/>.
/// <para>
/// Every path from a request goes through <see cref="TryResolve"/> before anything touches the file
/// system, and nothing else in here builds a path by hand. That is the whole security model: one
/// door, checked once, rather than a check at each call site that someone will eventually forget.
/// </para>
/// </summary>
sealed class FileBrowserHandler(FileBrowserOptions options, string root)
{
    public async ValueTask GetAsync(HttpContext context)
    {
        if (!this.TryResolve(context, out var fullPath, out var relativePath))
        {
            await RejectPathAsync(context).ConfigureAwait(false);
            return;
        }

        if (Directory.Exists(fullPath))
        {
            await Results.Ok(this.List(fullPath, relativePath), FileBrowserJsonContext.Default.DirectoryListing)
                .ExecuteAsync(context)
                .ConfigureAwait(false);

            return;
        }

        if (!File.Exists(fullPath))
        {
            await NotFoundAsync(context).ConfigureAwait(false);
            return;
        }

        // Handed to the download result so ranges, ETags and conditional requests all work — a
        // large file over a phone's connection is exactly the case that needs resuming.
        var info = new FileInfo(fullPath);

        await FileDownloadResult
            .FromFile(fullPath, this.ContentTypeFor(info.Name), downloadName: null)
            .ExecuteAsync(context)
            .ConfigureAwait(false);
    }

    public async ValueTask PutAsync(HttpContext context)
    {
        if (!this.TryResolve(context, out var fullPath, out var relativePath))
        {
            await RejectPathAsync(context).ConfigureAwait(false);
            return;
        }

        // A trailing slash means a directory. It is the one piece of syntax here, and it exists
        // because "create an empty file" and "create a directory" are otherwise the same request.
        if (context.Request.Path.EndsWith('/'))
        {
            if (!options.AllowCreateDirectories)
            {
                await ProblemAsync(context, StatusCodes.Status403Forbidden, "Creating directories is not allowed.")
                    .ConfigureAwait(false);

                return;
            }

            Directory.CreateDirectory(fullPath);

            // The status goes through the result rather than being set beforehand: a result carries
            // its own status and would otherwise overwrite this one with 200.
            await Results.Json(
                Describe(new DirectoryInfo(fullPath), relativePath, null),
                FileBrowserJsonContext.Default.FileEntry,
                StatusCodes.Status201Created
            ).ExecuteAsync(context).ConfigureAwait(false);

            return;
        }

        if (Directory.Exists(fullPath))
        {
            await ProblemAsync(context, StatusCodes.Status409Conflict, "A directory already exists at that path.")
                .ConfigureAwait(false);

            return;
        }

        if (context.Request.ContentLength is { } declared && declared > options.MaxUploadBytes)
        {
            await ProblemAsync(
                context,
                StatusCodes.Status413PayloadTooLarge,
                $"The body is larger than the {options.MaxUploadBytes} byte limit."
            ).ConfigureAwait(false);

            return;
        }

        var existed = File.Exists(fullPath);
        var parent = Path.GetDirectoryName(fullPath);

        if (parent is { Length: > 0 })
            Directory.CreateDirectory(parent);

        // Written to a temporary file and moved into place, so a failed or abandoned upload cannot
        // leave a half-written file where a whole one used to be.
        var staging = fullPath + ".uploading-" + Guid.NewGuid().ToString("n")[..8];

        try
        {
            var written = await this.CopyBodyAsync(context, staging).ConfigureAwait(false);

            if (written < 0)
            {
                await ProblemAsync(
                    context,
                    StatusCodes.Status413PayloadTooLarge,
                    $"The body is larger than the {options.MaxUploadBytes} byte limit."
                ).ConfigureAwait(false);

                return;
            }

            File.Move(staging, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(staging))
                File.Delete(staging);
        }

        // 201 only when something was created; overwriting an existing file created nothing.
        await Results.Json(
            Describe(new FileInfo(fullPath), relativePath, this.ContentTypeFor(fullPath)),
            FileBrowserJsonContext.Default.FileEntry,
            existed ? StatusCodes.Status200OK : StatusCodes.Status201Created
        ).ExecuteAsync(context).ConfigureAwait(false);
    }

    public async ValueTask DeleteAsync(HttpContext context)
    {
        if (!this.TryResolve(context, out var fullPath, out _))
        {
            await RejectPathAsync(context).ConfigureAwait(false);
            return;
        }

        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);

            context.Response.StatusCode = StatusCodes.Status204NoContent;
            context.Response.ContentLength = 0;

            await context.Response.StartAsync(context.RequestAborted).ConfigureAwait(false);

            return;
        }

        if (Directory.Exists(fullPath))
        {
            // Only an empty one. Recursive delete behind a URL is a single mistyped path away from
            // taking everything with it, and there is no undo on a phone.
            if (Directory.EnumerateFileSystemEntries(fullPath).Any())
            {
                await ProblemAsync(context, StatusCodes.Status409Conflict, "The directory is not empty.")
                    .ConfigureAwait(false);

                return;
            }

            Directory.Delete(fullPath);

            context.Response.StatusCode = StatusCodes.Status204NoContent;
            context.Response.ContentLength = 0;

            await context.Response.StartAsync(context.RequestAborted).ConfigureAwait(false);

            return;
        }

        await NotFoundAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Turns the request's path into a real one inside the root, or writes the refusal and returns
    /// false.
    /// <para>
    /// The path arrives already percent-decoded, so <c>%2e%2e%2f</c> is a plain <c>../</c> by the
    /// time it gets here — which is why the segment check happens on this value and not on the raw
    /// target.
    /// </para>
    /// </summary>
    bool TryResolve(HttpContext context, out string fullPath, out string relativePath)
    {
        fullPath = root;
        relativePath = string.Empty;

        var raw = context.Request.RouteValues.TryGetValue("path", out var value)
            ? value?.ToString() ?? string.Empty
            : string.Empty;

        if (raw.Length == 0)
            return true;

        if (!StaticFilePath.TryNormalize(raw, options.ServeHiddenFiles, out var segments))
            return false;

        var candidate = Path.GetFullPath(Path.Combine(root, segments));

        // Belt and braces after the segment check, and again after following links — a symlink is
        // the one way a path that looks contained can leave.
        if (!this.IsInsideRoot(candidate) || !this.PassesFilter(segments))
            return false;

        var link = File.Exists(candidate) || Directory.Exists(candidate)
            ? new FileInfo(candidate).ResolveLinkTarget(returnFinalTarget: true)
            : null;

        if (link is not null && !this.IsInsideRoot(link.FullName))
            return false;

        fullPath = candidate;
        relativePath = segments.Replace(Path.DirectorySeparatorChar, '/');

        return true;
    }

    /// <summary>The refusal for a path that did not resolve. Awaited, unlike the first draft of this.</summary>
    static ValueTask RejectPathAsync(HttpContext context)
        => ProblemAsync(context, StatusCodes.Status400BadRequest, "That path is not allowed.");

    DirectoryListing List(string fullPath, string relativePath)
    {
        var entries = new List<FileEntry>();

        foreach (var directory in Directory.EnumerateDirectories(fullPath).Order(StringComparer.OrdinalIgnoreCase))
        {
            var info = new DirectoryInfo(directory);

            if (this.IsVisible(info.Name, info.Attributes, relativePath))
                entries.Add(Describe(info, Join(relativePath, info.Name), null));
        }

        foreach (var file in Directory.EnumerateFiles(fullPath).Order(StringComparer.OrdinalIgnoreCase))
        {
            var info = new FileInfo(file);

            if (this.IsVisible(info.Name, info.Attributes, relativePath))
                entries.Add(Describe(info, Join(relativePath, info.Name), this.ContentTypeFor(info.Name)));
        }

        return new DirectoryListing(relativePath, entries);
    }

    bool IsVisible(string name, FileAttributes attributes, string parentRelativePath)
    {
        if (!options.ServeHiddenFiles && (name.StartsWith('.') || attributes.HasFlag(FileAttributes.Hidden)))
            return false;

        return this.PassesFilter(Join(parentRelativePath, name));
    }

    bool PassesFilter(string relativePath)
        => options.Filter is null || options.Filter(relativePath.Replace(Path.DirectorySeparatorChar, '/'));

    static FileEntry Describe(FileSystemInfo info, string relativePath, string? contentType) => new(
        info.Name,
        relativePath.Replace(Path.DirectorySeparatorChar, '/'),
        info is DirectoryInfo,
        info is FileInfo file ? file.Length : 0,
        info.LastWriteTimeUtc,
        contentType
    );

    static string Join(string parent, string name) => parent.Length == 0 ? name : parent + "/" + name;

    string ContentTypeFor(string name)
    {
        var extension = Path.GetExtension(name);

        return ContentTypes.IsKnownExtension(extension)
            ? ContentTypes.ForFileName(name)
            : options.DefaultContentType;
    }

    bool IsInsideRoot(string fullPath) =>
        fullPath.StartsWith(root + Path.DirectorySeparatorChar, StaticFilePath.PathComparison)
        || string.Equals(fullPath, root, StaticFilePath.PathComparison);

    /// <summary>
    /// Copies the body to <paramref name="destination"/>, or returns -1 once it exceeds the limit.
    /// <para>
    /// Counted as it copies rather than trusting Content-Length, which a client is free to
    /// understate or omit entirely.
    /// </para>
    /// </summary>
    async Task<long> CopyBodyAsync(HttpContext context, string destination)
    {
        var buffer = new byte[64 * 1024];
        long total = 0;

        await using var file = new FileStream(
            destination,
            new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            }
        );

        int read;
        while ((read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted).ConfigureAwait(false)) > 0)
        {
            total += read;

            if (total > options.MaxUploadBytes)
                return -1;

            await file.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted).ConfigureAwait(false);
        }

        return total;
    }

    static ValueTask NotFoundAsync(HttpContext context)
        => ProblemAsync(context, StatusCodes.Status404NotFound, "No such file or directory.");

    static ValueTask ProblemAsync(HttpContext context, int statusCode, string detail)
    {
        var problem = new ProblemDetails { Status = statusCode, Detail = detail };
        ProblemDetailsDefaults.ApplyDefaults(problem, context, statusCode);

        return ProblemDetailsWriter.WriteResponseAsync(context, problem, context.RequestAborted);
    }
}
