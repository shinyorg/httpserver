using Shiny.Net.HttpServer.Cors;
using Shiny.Net.HttpServer.Tus.Internal;

namespace Shiny.Net.HttpServer.Tus;

/// <summary>
/// The routes one <c>MapTus</c> call registered, so a policy can be stated once for the whole
/// endpoint - <c>.RequireAuthorization()</c> covers creation, every <c>PATCH</c> and every
/// <c>DELETE</c> alike, and a route added in a later version cannot arrive unprotected.
/// <code>
/// app.MapTus("/files", o => o.Store = new DiskTusStore(uploads))
///    .RequireAuthorization();
/// </code>
/// </summary>
public sealed class TusMountBuilder
{
    readonly List<RouteEndpointBuilder> routes;
    readonly TusHandler handler;

    internal TusMountBuilder(List<RouteEndpointBuilder> routes, TusHandler handler)
    {
        this.routes = routes;
        this.handler = handler;
    }

    /// <summary>Every route this endpoint registered.</summary>
    public IReadOnlyList<RouteEndpointBuilder> Routes => this.routes;

    /// <summary>
    /// Deletes every unfinished upload past its expiry now, rather than waiting for the sweep.
    /// Useful from a platform's own background scheduler, where the server may not be running
    /// when the work is allowed to happen. Returns how many were removed.
    /// </summary>
    public ValueTask<int> RemoveExpiredUploadsAsync(CancellationToken cancellationToken = default)
        => this.handler.RemoveExpiredAsync(cancellationToken);

    /// <summary>Requires authorization on every route, optionally against named policies.</summary>
    public TusMountBuilder RequireAuthorization(params string[] policies)
        => this.ForEach(route => route.RequireAuthorization(policies));

    /// <summary>Exempts every route from authorization, including from a fallback policy.</summary>
    public TusMountBuilder AllowAnonymous()
        => this.ForEach(route => route.AllowAnonymous());

    /// <summary>
    /// Applies a named CORS policy to every route. The policy has to allow tus's request headers
    /// and methods - <see cref="TusCorsExtensions.WithTusHeaders"/> adds them.
    /// </summary>
    public TusMountBuilder RequireCors(string policyName)
        => this.ForEach(route => route.RequireCors(policyName));

    /// <summary>Exempts every route from CORS.</summary>
    public TusMountBuilder DisableCors()
        => this.ForEach(route => route.DisableCors());

    /// <summary>Applies a named rate limit policy to every route.</summary>
    public TusMountBuilder RequireRateLimiting(string policyName)
        => this.ForEach(route => route.RequireRateLimiting(policyName));

    /// <summary>Exempts every route from rate limiting.</summary>
    public TusMountBuilder DisableRateLimiting()
        => this.ForEach(route => route.DisableRateLimiting());

    /// <summary>Applies a named IP filter policy to every route.</summary>
    public TusMountBuilder RequireIpFilter(string policyName)
        => this.ForEach(route => route.RequireIpFilter(policyName));

    /// <summary>Exempts every route from the IP filter.</summary>
    public TusMountBuilder AllowAnyIp()
        => this.ForEach(route => route.AllowAnyIp());

    /// <summary>Omits the endpoint from the OpenAPI document. On by default.</summary>
    public TusMountBuilder ExcludeFromDescription()
        => this.ForEach(route => route.ExcludeFromDescription());

    /// <summary>Attaches arbitrary metadata to every route.</summary>
    public TusMountBuilder WithMetadata(object metadata)
        => this.ForEach(route => route.WithMetadata(metadata));

    /// <summary>Runs <paramref name="configure"/> against every route, for anything not covered above.</summary>
    public TusMountBuilder ForEach(Action<RouteEndpointBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        foreach (var route in this.routes)
            configure(route);

        return this;
    }
}

/// <summary>
/// Resumable uploads over tus 1.0.0 (https://tus.io/protocols/resumable-upload).
/// <para>
/// A plain <c>POST</c> of a 200 MB video from a phone is all or nothing: the train goes into a
/// tunnel at 180 MB and the whole thing starts again. tus splits an upload into a create and a
/// series of <c>PATCH</c>es against a URL, and lets the client ask with <c>HEAD</c> how many bytes
/// the server has, so a dropped connection costs only what was in flight. The server keeps every
/// byte it received, including from the request that was cut off.
/// </para>
/// <code>
/// app.MapTus("/files", o =>
/// {
///     o.Store = new DiskTusStore(Path.Combine(FileSystem.AppDataDirectory, "uploads"));
///     o.MaxSize = 2L * 1024 * 1024 * 1024;
///     o.Expiration = TimeSpan.FromDays(1);
///     o.OnUploadCompleteAsync = async ctx =>
///     {
///         var name = ctx.Upload.Metadata.GetValueOrDefault("filename") ?? ctx.Upload.Id;
///         await using var file = await ctx.OpenReadAsync();
///         // ...
///     };
/// })
/// .RequireAuthorization();
/// </code>
/// <para>
/// Core protocol plus creation, creation-with-upload, creation-defer-length, termination,
/// expiration, checksum (sha1, sha256, md5) and concatenation. <c>X-HTTP-Method-Override</c> is
/// honoured on <c>POST</c>.
/// </para>
/// </summary>
public static class TusExtensions
{
    /// <summary>Maps a tus endpoint at <paramref name="prefix"/>.</summary>
    public static TusMountBuilder MapTus(this HttpServer server, string prefix, Action<TusOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new TusOptions();
        configure(options);

        return server.MapTus(prefix, options);
    }

    /// <summary>Maps a tus endpoint from options built elsewhere.</summary>
    public static TusMountBuilder MapTus(this HttpServer server, string prefix, TusOptions options)
    {
        ArgumentNullException.ThrowIfNull(server);

        TusMountBuilder? builder = null;
        TusHandler? handler = null;

        server.MapGroup(NormalizePrefix(prefix), group => (builder, handler) = Map(group, options));

        // Only here, where there is a server to follow: the expiry sweep runs on a timer while it
        // is running. A mount made through a route builder still sweeps, nudged by its own traffic.
        handler!.AttachTo(server);

        return builder!;
    }

    /// <summary>Maps a tus endpoint onto an existing route builder, such as a group.</summary>
    public static TusMountBuilder MapTus(this IEndpointRouteBuilder endpoints, string prefix, TusOptions options)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        return Map(endpoints.MapGroup(NormalizePrefix(prefix)), options).Builder;
    }

    static (TusMountBuilder Builder, TusHandler Handler) Map(IEndpointRouteBuilder group, TusOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.LockReleaseTimeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), $"{nameof(TusOptions.LockReleaseTimeout)} cannot be negative.");

        if (options.CleanupInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), $"{nameof(TusOptions.CleanupInterval)} must be positive.");

        // Taken from the group rather than the argument, so an endpoint nested in another group
        // hands out upload URLs that include the outer prefix.
        var basePath = group.Prefix.Trim('/') is { Length: > 0 } trimmed ? "/" + trimmed : string.Empty;
        var handler = new TusHandler(options, options.ResolveStore(), basePath);
        var routes = new List<RouteEndpointBuilder>
        {
            group.Map(HttpMethods.Options, "/", handler.Guard(handler.OptionsAsync, requireVersion: false)),
            group.Map(HttpMethods.Post, "/", handler.Guard(handler.CreateAsync)),
            group.Map(HttpMethods.Options, "/{id}", handler.Guard(handler.OptionsAsync, requireVersion: false)),
            group.Map(HttpMethods.Head, "/{id}", handler.Guard(handler.HeadAsync)),
            group.Map(HttpMethods.Patch, "/{id}", handler.Guard(handler.PatchAsync)),
            group.Map(HttpMethods.Delete, "/{id}", handler.Guard(handler.DeleteAsync)),
            group.Map(HttpMethods.Post, "/{id}", handler.Guard(handler.OverrideAsync))
        };

        var builder = new TusMountBuilder(routes, handler);

        // One protocol behind seven routes, documented by tus.io rather than by an OpenAPI schema
        // no tus client would read.
        builder.ExcludeFromDescription();

        return (builder, handler);
    }

    static string NormalizePrefix(string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        return "/" + prefix.Trim().Trim('/');
    }
}

/// <summary>CORS for a browser tus client.</summary>
public static class TusCorsExtensions
{
    /// <summary>
    /// Allows the methods and request headers tus sends, and exposes the response headers it reads.
    /// <code>
    /// app.UseCors(p => p.WithOrigins("https://app.example.com").WithTusHeaders());
    /// </code>
    /// <para>
    /// A policy without this fails in the browser in the least helpful way: the preflight for the
    /// first <c>POST</c> is refused because <c>Tus-Resumable</c> is not an allowed header, and the
    /// console says only that CORS blocked it.
    /// </para>
    /// </summary>
    public static CorsPolicyBuilder WithTusHeaders(this CorsPolicyBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder
            .WithMethods(HttpMethods.Post, HttpMethods.Head, HttpMethods.Patch, HttpMethods.Delete, HttpMethods.Options)
            .WithHeaders([.. TusHeaderNames.Request])
            .WithExposedHeaders([.. TusHeaderNames.Exposed]);
    }
}
