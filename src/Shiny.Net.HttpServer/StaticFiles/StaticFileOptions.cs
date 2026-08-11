using System.Globalization;
using Shiny.Net.HttpServer.Files;

namespace Shiny.Net.HttpServer.StaticFiles;

/// <summary>A file about to be served, for a last look before the response goes out.</summary>
/// <param name="HttpContext">The request being answered.</param>
/// <param name="File">The resolved file.</param>
public readonly record struct StaticFileResponseContext(HttpContext HttpContext, StaticFile File);

/// <summary>How static content is served.</summary>
public sealed class StaticFileOptions
{
    /// <summary>Where files come from. Required.</summary>
    public IStaticFileSource Source { get; set; } = null!;

    /// <summary>
    /// URL prefix these files live under — <c>/assets</c> serves <c>/assets/app.js</c> from
    /// <c>app.js</c>. Empty (the default) serves from the root.
    /// </summary>
    public string RequestPath { get; set; } = string.Empty;

    /// <summary>
    /// Files tried when a directory is requested. <c>/docs/</c> serves <c>/docs/index.html</c>.
    /// </summary>
    public IList<string> DefaultDocuments { get; } = ["index.html", "index.htm"];

    /// <summary>
    /// File served when nothing else matched — <c>index.html</c> for a single-page app, so a deep
    /// link like <c>/orders/42</c> reaches the client-side router instead of a 404.
    /// <para>
    /// Only applied to GET/HEAD requests that look like navigations (they accept HTML) and do not
    /// look like assets (no file extension), so a missing script still 404s honestly rather than
    /// returning HTML that the browser will fail to parse.
    /// </para>
    /// </summary>
    public string? FallbackFile { get; set; }

    /// <summary>
    /// Serves files whose extension has no known content type, using
    /// <see cref="DefaultContentType"/>.
    /// <para>
    /// Off by default. Guessing at an unknown extension is how a user-writable directory turns into
    /// a way to serve HTML — and therefore script — from your origin.
    /// </para>
    /// </summary>
    public bool ServeUnknownFileTypes { get; set; }

    public string DefaultContentType { get; set; } = "application/octet-stream";

    /// <summary>
    /// <c>Cache-Control</c> for served files. Null sends none, which browsers treat as
    /// "revalidate", and the ETag makes that cheap.
    /// </summary>
    public string? CacheControl { get; set; }

    /// <summary>
    /// Sets <see cref="CacheControl"/> to <c>public, max-age=…</c>, and to
    /// <c>public, max-age=…, immutable</c> when <paramref name="immutable"/> is set.
    /// <para>
    /// Only mark content immutable when its URL changes with its content — a hashed bundle name.
    /// Otherwise a browser will keep a stale copy for the whole age and no deploy will dislodge it.
    /// </para>
    /// </summary>
    public StaticFileOptions CacheFor(TimeSpan maxAge, bool immutable = false)
    {
        var seconds = (long)maxAge.TotalSeconds;

        this.CacheControl = string.Create(
            CultureInfo.InvariantCulture,
            $"public, max-age={seconds}{(immutable ? ", immutable" : string.Empty)}"
        );

        return this;
    }

    /// <summary>
    /// Serves a <c>.br</c> or <c>.gz</c> sidecar in place of the original when the client accepts
    /// it — <c>app.wasm.br</c> for <c>app.wasm</c>.
    /// <para>
    /// Off by default, because a directory containing an unrelated <c>.gz</c> would otherwise start
    /// serving it as an encoding of a file it is not. On for a build that publishes precompressed
    /// assets, where it is strictly better: those were compressed once at maximum effort, and
    /// recompressing them per request spends CPU to produce a larger result.
    /// </para>
    /// </summary>
    public bool ServePrecompressedFiles { get; set; }

    /// <summary>
    /// Runs just before the response is written — for a per-file cache policy, a security header,
    /// or a log line.
    /// </summary>
    public Action<StaticFileResponseContext>? OnPrepareResponse { get; set; }

    /// <summary>
    /// Content types by extension. Falls back to the built-in map, which covers the usual web
    /// assets; add to this for anything it does not know.
    /// </summary>
    public IDictionary<string, string> ContentTypeOverrides { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    internal string? ResolveContentType(string fileName)
    {
        var extension = Path.GetExtension(fileName);

        if (extension.Length > 0 && this.ContentTypeOverrides.TryGetValue(extension, out var overridden))
            return overridden;

        var known = ContentTypes.ForFileName(fileName);

        // The built-in map answers octet-stream for anything it does not recognise, which is
        // indistinguishable from a genuine octet-stream — so an explicit extension check decides
        // whether this counts as "unknown".
        if (!ContentTypes.IsKnownExtension(extension))
            return this.ServeUnknownFileTypes ? this.DefaultContentType : null;

        return known;
    }
}
