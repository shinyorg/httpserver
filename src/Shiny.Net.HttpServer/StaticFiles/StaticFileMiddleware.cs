using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Shiny.Net.HttpServer.Files;

namespace Shiny.Net.HttpServer.StaticFiles;

/// <summary>
/// Serves files from an <see cref="IStaticFileSource"/>.
/// <para>
/// Everything hard about this — ranges, <c>If-None-Match</c>, 304, 416 — already exists in
/// <see cref="FileDownloadResult"/>, so this resolves a path and hands over. What is left is the
/// part that has to be right: deciding which paths are allowed to resolve at all.
/// </para>
/// </summary>
public sealed class StaticFileMiddleware(StaticFileOptions options) : IHttpMiddleware
{
    readonly StaticFileOptions options = ValidateOptions(options);
    readonly string prefix = NormalizePrefix(options.RequestPath);

    public async ValueTask InvokeAsync(HttpContext context, RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        // Anything else is a route's business. A POST to a static path is not a static file
        // request, and answering 405 here would shadow a handler that does accept it.
        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        if (!this.TryMatchPrefix(context.Request.Path, out var relative))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var accepted = this.AcceptedEncodings(context.Request);

        if (this.TryResolve(relative, accepted, out var file, out var contentType))
        {
            await this.ServeAsync(context, file, contentType).ConfigureAwait(false);
            return;
        }

        // The fallback runs *after* the pipeline, not instead of it: a real route for this path
        // must win over the single-page app's catch-all.
        await next(context).ConfigureAwait(false);

        if (this.ShouldServeFallback(context))
            await this.TryServeFallbackAsync(context).ConfigureAwait(false);
    }

    bool TryMatchPrefix(string path, out string relative)
    {
        if (this.prefix.Length == 0)
        {
            relative = path;
            return true;
        }

        if (!path.StartsWith(this.prefix, StaticFilePath.PathComparison))
        {
            relative = string.Empty;
            return false;
        }

        var rest = path[this.prefix.Length..];

        // "/assetsomething" starts with "/assets" but is not inside it.
        if (rest.Length > 0 && rest[0] != '/')
        {
            relative = string.Empty;
            return false;
        }

        relative = rest;
        return true;
    }

    /// <summary>
    /// Codings the client will accept, or null when there is nothing to prefer. Only consulted for
    /// sidecar lookup, so the full q-value machinery would be more precision than the question needs.
    /// </summary>
    IReadOnlyList<string>? AcceptedEncodings(HttpRequest request)
    {
        if (!this.options.ServePrecompressedFiles)
            return null;

        if (request.Headers.GetFirst(HeaderNames.AcceptEncoding) is not { Length: > 0 } header)
            return null;

        var accepted = new List<string>(3);

        foreach (var part in header.Split(','))
        {
            var entry = part.AsSpan().Trim();
            var semicolon = entry.IndexOf(';');

            // "br;q=0" is a refusal, and treating it as acceptance would send bytes the client
            // has told us it will not decode.
            if (semicolon >= 0)
            {
                if (entry[(semicolon + 1)..].Trim().StartsWith("q=0", StringComparison.OrdinalIgnoreCase)
                    && !entry[(semicolon + 1)..].Contains("q=0.", StringComparison.OrdinalIgnoreCase))
                    continue;

                entry = entry[..semicolon].Trim();
            }

            if (!entry.IsEmpty)
                accepted.Add(entry.ToString());
        }

        return accepted.Count == 0 ? null : accepted;
    }

    bool TryResolve(string relativePath, IReadOnlyList<string>? accepted, out StaticFile file, out string contentType)
    {
        contentType = string.Empty;

        // A directory request tries its default documents; "/" itself only ever means that.
        if (relativePath.Length == 0 || relativePath.EndsWith('/'))
        {
            foreach (var document in this.options.DefaultDocuments)
            {
                if (this.TryResolveFile(relativePath + document, accepted, out file, out contentType))
                    return true;
            }

            file = default;
            return false;
        }

        return this.TryResolveFile(relativePath, accepted, out file, out contentType);
    }

    bool TryResolveFile(string path, IReadOnlyList<string>? accepted, out StaticFile file, out string contentType)
    {
        contentType = string.Empty;

        var found = accepted is { Count: > 0 } && this.options.Source is IPrecompressedFileSource precompressed
            ? precompressed.TryGetFile(path, accepted, out file)
            : this.options.Source.TryGetFile(path, out file);

        if (!found)
            return false;

        // Resolved but unservable: an unknown extension is not something to guess at.
        if (this.options.ResolveContentType(file.Name) is not { } resolved)
        {
            file = default;
            return false;
        }

        contentType = resolved;
        return true;
    }

    /// <summary>
    /// Whether an unmatched request should get the single-page app's entry document.
    /// <para>
    /// Only navigations. A request with a file extension, or one that does not accept HTML, is
    /// asking for an asset — and answering that with a page of HTML produces a broken script tag
    /// and a mystifying console error instead of an honest 404.
    /// </para>
    /// </summary>
    bool ShouldServeFallback(HttpContext context)
    {
        if (this.options.FallbackFile is null || context.Response.HasStarted)
            return false;

        if (context.Response.StatusCode != StatusCodes.Status404NotFound)
            return false;

        var path = context.Request.Path;
        if (Path.HasExtension(path))
            return false;

        var accept = context.Request.Headers.GetFirst(HeaderNames.Accept);

        return accept is null
            || accept.Contains("text/html", StringComparison.OrdinalIgnoreCase)
            || accept.Contains("*/*", StringComparison.Ordinal);
    }

    async ValueTask TryServeFallbackAsync(HttpContext context)
    {
        if (!this.TryResolveFile(this.options.FallbackFile!, this.AcceptedEncodings(context.Request), out var file, out var contentType))
            return;

        // The route was not found, but the document that handles it was — the client-side router
        // will map the URL, so this is a 200 for the app shell rather than a 404 with a body.
        context.Response.StatusCode = StatusCodes.Status200OK;

        await this.ServeAsync(context, file, contentType).ConfigureAwait(false);
    }

    async ValueTask ServeAsync(HttpContext context, StaticFile file, string contentType)
    {
        var response = context.Response;

        if (this.options.CacheControl is { Length: > 0 } cacheControl)
            response.Headers[HeaderNames.CacheControl] = cacheControl;

        if (file.ContentEncoding is { Length: > 0 } encoding)
        {
            response.Headers[HeaderNames.ContentEncoding] = encoding;

            // Which variant was served depends on the request, so a shared cache has to key on it.
            // The compression middleware also reads Content-Encoding and passes the response
            // through, so these bytes are never compressed twice.
            response.Headers.Append(HeaderNames.Vary, HeaderNames.AcceptEncoding);
        }

        this.options.OnPrepareResponse?.Invoke(new StaticFileResponseContext(context, file));

        // No download name: these are page assets, and a Content-Disposition would turn a
        // stylesheet into a save dialog.
        var result = FileDownloadResult.FromOpener(
            file.Open,
            file.Length,
            contentType,
            downloadName: null,
            eTag: file.ETag,
            lastModified: file.LastModified
        );

        await result.ExecuteAsync(context).ConfigureAwait(false);
    }

    static StaticFileOptions ValidateOptions(StaticFileOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.Source is null)
            throw new InvalidOperationException($"{nameof(StaticFileOptions)}.{nameof(StaticFileOptions.Source)} is required.");

        return options;
    }

    static string NormalizePrefix(string requestPath)
    {
        if (string.IsNullOrEmpty(requestPath) || requestPath == "/")
            return string.Empty;

        var prefix = requestPath.StartsWith('/') ? requestPath : "/" + requestPath;

        return prefix.EndsWith('/') ? prefix.TrimEnd('/') : prefix;
    }
}

/// <summary>Wiring static files into a server.</summary>
public static class StaticFileExtensions
{
    /// <summary>
    /// Serves a directory from disk.
    /// <code>
    /// app.UseStaticFiles("./wwwroot");
    /// </code>
    /// </summary>
    public static HttpServer UseStaticFiles(
        this HttpServer server,
        string rootPath,
        Action<StaticFileOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        return server.UseStaticFiles(new PhysicalFileSource(rootPath), configure);
    }

    /// <summary>
    /// Serves web assets embedded in an assembly — the packaged-app case, where there is no
    /// directory to point at.
    /// <code>
    /// app.UseEmbeddedFiles(typeof(App).Assembly, "MyApp.wwwroot", o => o.FallbackFile = "index.html");
    /// </code>
    /// </summary>
    public static HttpServer UseEmbeddedFiles(
        this HttpServer server,
        Assembly assembly,
        string? baseNamespace = null,
        Action<StaticFileOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(assembly);

        return server.UseStaticFiles(new EmbeddedFileSource(assembly, baseNamespace), configure);
    }

    /// <summary>Serves files from any source.</summary>
    public static HttpServer UseStaticFiles(
        this HttpServer server,
        IStaticFileSource source,
        Action<StaticFileOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(source);

        var options = new StaticFileOptions { Source = source };
        configure?.Invoke(options);

        return server.Use(new StaticFileMiddleware(options));
    }

    /// <summary>Serves files from fully-specified options.</summary>
    public static HttpServer UseStaticFiles(this HttpServer server, StaticFileOptions options)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(options);

        return server.Use(new StaticFileMiddleware(options));
    }

    /// <summary>
    /// Registers static file options in the container, for apps that configure everything through
    /// DI and resolve the middleware with <c>Use&lt;StaticFileMiddleware&gt;()</c>.
    /// </summary>
    public static ShinyHttpServerBuilder AddStaticFiles(
        this ShinyHttpServerBuilder builder,
        IStaticFileSource source,
        Action<StaticFileOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(source);

        builder.Services.AddSingleton(_ =>
        {
            var options = new StaticFileOptions { Source = source };
            configure?.Invoke(options);

            return options;
        });

        builder.Services.AddSingleton(sp => new StaticFileMiddleware(sp.GetRequiredService<StaticFileOptions>()));

        return builder;
    }
}
