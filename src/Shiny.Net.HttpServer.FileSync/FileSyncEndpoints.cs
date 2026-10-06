using Shiny.Net.HttpServer.FileSync.Internal;
using Shiny.Net.HttpServer.Tus;

namespace Shiny.Net.HttpServer.FileSync;

/// <summary>
/// The routes one <c>MapFileSync</c> call registered - its own and the tus upload routes - so a
/// policy is stated once for all of them.
/// <code>
/// app.MapFileSync("/sync", o => o.StoreSelector = ...).RequireAuthorization();
/// </code>
/// </summary>
public sealed class FileSyncMountBuilder
{
    readonly List<RouteEndpointBuilder> routes;
    readonly FileSyncHandler handler;
    readonly FileSyncOptions options;

    internal FileSyncMountBuilder(List<RouteEndpointBuilder> routes, FileSyncHandler handler, FileSyncOptions options)
    {
        this.routes = routes;
        this.handler = handler;
        this.options = options;
    }

    /// <summary>Every route this endpoint registered, tus included.</summary>
    public IReadOnlyList<RouteEndpointBuilder> Routes => this.routes;

    /// <summary>
    /// Deletes chunks no current file refers to and that are older than
    /// <see cref="FileSyncOptions.GarbageGracePeriod"/>: the chunks of deleted files and of old
    /// versions, and uploads that were never committed. Run it from a timer or a background job.
    /// Returns how many were removed.
    /// </summary>
    public ValueTask<int> CollectGarbageAsync(IFileSyncStore? store = null, CancellationToken cancellationToken = default)
        => this.handler.CollectGarbageAsync(
            store ?? this.options.Store ?? throw new ArgumentNullException(nameof(store), "This endpoint picks stores per request; name the one to collect."),
            cancellationToken
        );

    /// <summary>Requires authorization on every route, optionally against named policies.</summary>
    public FileSyncMountBuilder RequireAuthorization(params string[] policies)
        => this.ForEach(route => route.RequireAuthorization(policies));

    /// <summary>Exempts every route from authorization, including from a fallback policy.</summary>
    public FileSyncMountBuilder AllowAnonymous()
        => this.ForEach(route => route.AllowAnonymous());

    /// <summary>Applies a named CORS policy to every route.</summary>
    public FileSyncMountBuilder RequireCors(string policyName)
        => this.ForEach(route => route.RequireCors(policyName));

    /// <summary>Applies a named rate limit policy to every route.</summary>
    public FileSyncMountBuilder RequireRateLimiting(string policyName)
        => this.ForEach(route => route.RequireRateLimiting(policyName));

    /// <summary>Applies a named IP filter policy to every route.</summary>
    public FileSyncMountBuilder RequireIpFilter(string policyName)
        => this.ForEach(route => route.RequireIpFilter(policyName));

    /// <summary>Omits the endpoint from the OpenAPI document. On by default.</summary>
    public FileSyncMountBuilder ExcludeFromDescription()
        => this.ForEach(route => route.ExcludeFromDescription());

    /// <summary>Attaches arbitrary metadata to every route.</summary>
    public FileSyncMountBuilder WithMetadata(object metadata)
        => this.ForEach(route => route.WithMetadata(metadata));

    /// <summary>Runs <paramref name="configure"/> against every route.</summary>
    public FileSyncMountBuilder ForEach(Action<RouteEndpointBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        foreach (var route in this.routes)
            configure(route);

        return this;
    }
}

/// <summary>
/// Dropbox-style file sync. Files are kept as content-defined chunks addressed by their SHA-256, so
/// a client that edits a page of a 500 MB file sends the few chunks that changed, and a copy or an
/// earlier version of a file costs nothing to send again. Pair it with
/// <c>Shiny.Net.HttpServer.FileSync.Client</c>.
/// <code>
/// app.MapFileSync("/sync", o => o.Store = new DiskFileSyncStore(Path.Combine(dataDir, "sync")))
///    .RequireAuthorization();
/// </code>
/// <para>Routes, under the prefix:</para>
/// <code>
/// GET    changes?cursor=&amp;limit=        what changed after a cursor
/// GET    changes/wait?cursor=&amp;timeout= long-poll until something does
/// GET    files/manifest?path=           a file's current version and chunk list
/// PUT    files/manifest?path=           commit a new version (409 stale, 422 missing chunks)
/// DELETE files/manifest?path=&amp;revision= delete (leaves a tombstone)
/// GET    files/content?path=            the whole file, for anything that is not a sync client
/// POST   chunks/missing                 which of these chunks the server lacks
/// PUT    chunks/pack                    upload a pack of chunks in one request
/// GET    chunks/pack?h=a,b,c            download chunks as a pack (resumable with Range)
/// GET    chunks/{hash}                  one chunk
/// *      uploads                        tus 1.0.0, for resumable pack uploads
/// </code>
/// </summary>
public static class FileSyncExtensions
{
    /// <summary>Maps a sync endpoint at <paramref name="prefix"/>.</summary>
    public static FileSyncMountBuilder MapFileSync(this HttpServer server, string prefix, Action<FileSyncOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new FileSyncOptions();
        configure(options);

        return server.MapFileSync(prefix, options);
    }

    /// <summary>Maps a sync endpoint from options built elsewhere.</summary>
    public static FileSyncMountBuilder MapFileSync(this HttpServer server, string prefix, FileSyncOptions options)
    {
        ArgumentNullException.ThrowIfNull(server);

        FileSyncMountBuilder? builder = null;
        server.MapGroup(NormalizePrefix(prefix), group => builder = Map(group, options));

        return builder!;
    }

    /// <summary>Maps a sync endpoint onto an existing route builder, such as a group.</summary>
    public static FileSyncMountBuilder MapFileSync(this IEndpointRouteBuilder endpoints, string prefix, FileSyncOptions options)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        return Map(endpoints.MapGroup(NormalizePrefix(prefix)), options);
    }

    static FileSyncMountBuilder Map(IEndpointRouteBuilder group, FileSyncOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.Store is null && options.StoreSelector is null)
            throw new InvalidOperationException($"{nameof(FileSyncOptions)} needs a {nameof(FileSyncOptions.Store)} or a {nameof(FileSyncOptions.StoreSelector)}.");

        if (options.MaxChunkSize <= 0 || options.MaxPackSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Chunk and pack limits must be positive.");

        var handler = new FileSyncHandler(options);
        var routes = new List<RouteEndpointBuilder>
        {
            group.Map(HttpMethods.Get, "/changes", handler.Guard(handler.ChangesAsync)),
            group.Map(HttpMethods.Get, "/changes/wait", handler.Guard(handler.WaitAsync)),
            group.Map(HttpMethods.Get, "/files/manifest", handler.Guard(handler.GetManifestAsync)),
            group.Map(HttpMethods.Put, "/files/manifest", handler.Guard(handler.CommitAsync)),
            group.Map(HttpMethods.Delete, "/files/manifest", handler.Guard(handler.DeleteAsync)),
            group.Map(HttpMethods.Get, "/files/content", handler.Guard(handler.ContentAsync)),
            group.Map(HttpMethods.Post, "/chunks/missing", handler.Guard(handler.MissingAsync)),
            group.Map(HttpMethods.Put, "/chunks/pack", handler.Guard(handler.UploadPackAsync)),
            group.Map(HttpMethods.Get, "/chunks/pack", handler.Guard(handler.DownloadPackAsync)),
            group.Map(HttpMethods.Get, "/chunks/{hash}", handler.Guard(handler.DownloadChunkAsync))
        };

        if (options.EnableTusUploads)
        {
            var tus = group.MapTus("/uploads", new TusOptions
            {
                Store = options.TusStore ?? DefaultTusStore(options),
                MaxSize = options.MaxPackSize,
                Expiration = TimeSpan.FromDays(1),
                OnUploadCompleteAsync = handler.IngestTusAsync
            });

            routes.AddRange(tus.Routes);
        }

        var builder = new FileSyncMountBuilder(routes, handler, options);

        // A protocol with its own client; an OpenAPI schema of it would help nobody.
        builder.ExcludeFromDescription();

        return builder;
    }

    static DiskTusStore DefaultTusStore(FileSyncOptions options)
        => new(options.Store is DiskFileSyncStore disk
            ? Path.Combine(disk.RootPath, "uploads")
            : Path.Combine(Path.GetTempPath(), "shiny-filesync-uploads"));

    static string NormalizePrefix(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);

        var trimmed = prefix.Trim().Trim('/');
        return trimmed.Length == 0 ? "/" : "/" + trimmed;
    }
}
