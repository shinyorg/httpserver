using System.Reflection;
using Shiny.Net.HttpServer.Files;

namespace Shiny.Net.HttpServer.StaticFiles;

/// <summary>
/// Serving a published Blazor WebAssembly app.
/// <para>
/// A Blazor app is a folder of static files, so nothing here is special-cased for its sake — it is
/// the general static file handler with the three settings a Blazor publish needs, and getting any
/// of them wrong produces a blank page and a stack trace from inside the runtime rather than an
/// obvious error.
/// </para>
/// </summary>
public static class BlazorWebAssemblyExtensions
{
    /// <summary>
    /// Serves a published Blazor WebAssembly app from <paramref name="rootPath"/> — the
    /// <c>wwwroot</c> directory produced by <c>dotnet publish</c>.
    /// <code>
    /// app.UseBlazorWebAssembly("./wwwroot");
    /// app.MapGet("/api/readings", …);   // the app's own API, alongside it
    /// </code>
    /// <para>
    /// What this arranges beyond plain static files:
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// <b>The single-page fallback.</b> A Blazor route like <c>/counter</c> exists only in the
    /// client-side router, so a reload of that URL has to return <c>index.html</c>.
    /// </item>
    /// <item>
    /// <b>Precompressed assets.</b> A publish emits <c>.br</c> and <c>.gz</c> beside every file,
    /// compressed once at maximum effort. Serving those beats recompressing 20-odd megabytes per
    /// request at a level chosen for speed.
    /// </item>
    /// <item>
    /// <b>Cache policy.</b> Everything under <c>_framework</c> is content-fingerprinted by the
    /// publish, so it can be cached indefinitely; the entry document must not be, or a deploy would
    /// never reach anyone.
    /// </item>
    /// </list>
    /// </summary>
    public static HttpServer UseBlazorWebAssembly(
        this HttpServer server,
        string rootPath,
        Action<StaticFileOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        return server.UseBlazorWebAssembly(
            new PhysicalFileSource(rootPath) { PrecompressedEncodings = ["br", "gzip"] },
            configure
        );
    }

    /// <summary>
    /// Serves a Blazor app embedded in an assembly — the packaged case, where a MAUI or single-file
    /// build has no <c>wwwroot</c> on disk to point at.
    /// <code>
    /// app.UseBlazorWebAssembly(typeof(App).Assembly, "MyApp.wwwroot");
    /// </code>
    /// </summary>
    public static HttpServer UseBlazorWebAssembly(
        this HttpServer server,
        Assembly assembly,
        string? baseNamespace = null,
        Action<StaticFileOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(assembly);

        return server.UseBlazorWebAssembly(new EmbeddedFileSource(assembly, baseNamespace), configure);
    }

    /// <summary>Serves a Blazor app from any source.</summary>
    public static HttpServer UseBlazorWebAssembly(
        this HttpServer server,
        IStaticFileSource source,
        Action<StaticFileOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(source);

        var options = new StaticFileOptions
        {
            Source = source,
            FallbackFile = "index.html",
            ServePrecompressedFiles = true,
            OnPrepareResponse = ApplyCachePolicy
        };

        configure?.Invoke(options);

        return server.Use(new StaticFileMiddleware(options));
    }

    /// <summary>
    /// Caches fingerprinted assets forever and the entry document never.
    /// <para>
    /// A publish names framework files after their content hash, so a change is a new URL and the
    /// old one can never go stale. <c>index.html</c> keeps the same URL by definition, and caching
    /// it is how a deploy silently fails to reach anyone.
    /// </para>
    /// </summary>
    static void ApplyCachePolicy(StaticFileResponseContext context)
    {
        var path = context.HttpContext.Request.Path;
        var headers = context.HttpContext.Response.Headers;

        if (path.StartsWith("/_framework/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/_content/", StringComparison.OrdinalIgnoreCase))
        {
            headers[HeaderNames.CacheControl] = "public, max-age=31536000, immutable";
            return;
        }

        if (IsEntryDocument(context.File.Name))
            headers[HeaderNames.CacheControl] = "no-cache";
    }

    /// <summary>
    /// "no-cache" rather than "no-store": the browser may keep the copy, it just has to revalidate.
    /// With an ETag that is a conditional request answered by a 304, which costs nothing.
    /// </summary>
    static bool IsEntryDocument(string name) =>
        ContentTypes.ForFileName(name).StartsWith("text/html", StringComparison.OrdinalIgnoreCase);
}
