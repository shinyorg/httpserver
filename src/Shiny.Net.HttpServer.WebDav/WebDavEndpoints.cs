using Shiny.Net.HttpServer.WebDav.Internal;

namespace Shiny.Net.HttpServer.WebDav;

/// <summary>
/// The routes one <c>MapWebDav</c> call just registered, so a policy can be stated once for the
/// whole mount.
/// <para>
/// This server attaches metadata per route, because a route is what its middleware looks at. Rather
/// than make every caller loop over twenty-odd routes, this holds the ones the call produced and
/// fans each method out across them — so <c>.RequireAuthorization()</c> covers every verb, and a
/// verb added in a later version cannot quietly arrive unprotected.
/// </para>
/// <code>
/// app.MapWebDav("/dav", o =>
/// {
///     o.RootPath = FileSystem.AppDataDirectory;
///     o.AllowWrite = true;
///     o.AllowDelete = true;
/// })
/// .RequireAuthorization();
/// </code>
/// </summary>
public sealed class WebDavMountBuilder
{
    readonly List<RouteEndpointBuilder> read;
    readonly List<RouteEndpointBuilder> write;
    readonly List<RouteEndpointBuilder> delete;
    readonly List<RouteEndpointBuilder> locking;

    internal WebDavMountBuilder(
        List<RouteEndpointBuilder> read,
        List<RouteEndpointBuilder> write,
        List<RouteEndpointBuilder> delete,
        List<RouteEndpointBuilder> locking
    )
    {
        this.read = read;
        this.write = write;
        this.delete = delete;
        this.locking = locking;
    }

    /// <summary><c>OPTIONS</c>, <c>GET</c>/<c>HEAD</c> and <c>PROPFIND</c>.</summary>
    public IReadOnlyList<RouteEndpointBuilder> ReadRoutes => this.read;

    /// <summary><c>PUT</c>, <c>MKCOL</c>, <c>PROPPATCH</c>, <c>COPY</c> and <c>MOVE</c>.</summary>
    public IReadOnlyList<RouteEndpointBuilder> WriteRoutes => this.write;

    /// <summary><c>DELETE</c>.</summary>
    public IReadOnlyList<RouteEndpointBuilder> DeleteRoutes => this.delete;

    /// <summary><c>LOCK</c> and <c>UNLOCK</c>.</summary>
    public IReadOnlyList<RouteEndpointBuilder> LockRoutes => this.locking;

    /// <summary>Every route this mount registered.</summary>
    public IEnumerable<RouteEndpointBuilder> Routes
        => this.read.Concat(this.write).Concat(this.delete).Concat(this.locking);

    /// <summary>Requires authorization on every route, optionally against named policies.</summary>
    public WebDavMountBuilder RequireAuthorization(params string[] policies)
        => this.ForEach(route => route.RequireAuthorization(policies));

    /// <summary>
    /// Requires authorization on the routes that change something, leaving reads open.
    /// <para>
    /// Locking counts as a change: a lock reserves the right to write, and one taken anonymously is
    /// a way to stop everyone else writing.
    /// </para>
    /// </summary>
    public WebDavMountBuilder RequireAuthorizationForChanges(params string[] policies)
    {
        foreach (var route in this.write.Concat(this.delete).Concat(this.locking))
            route.RequireAuthorization(policies);

        return this;
    }

    /// <summary>Exempts every route from authorization, including from a fallback policy.</summary>
    public WebDavMountBuilder AllowAnonymous()
        => this.ForEach(route => route.AllowAnonymous());

    /// <summary>Applies a named CORS policy to every route.</summary>
    public WebDavMountBuilder RequireCors(string policyName)
        => this.ForEach(route => route.RequireCors(policyName));

    /// <summary>Exempts every route from CORS.</summary>
    public WebDavMountBuilder DisableCors()
        => this.ForEach(route => route.DisableCors());

    /// <summary>Applies a named rate limit policy to every route.</summary>
    public WebDavMountBuilder RequireRateLimiting(string policyName)
        => this.ForEach(route => route.RequireRateLimiting(policyName));

    /// <summary>Exempts every route from rate limiting.</summary>
    public WebDavMountBuilder DisableRateLimiting()
        => this.ForEach(route => route.DisableRateLimiting());

    /// <summary>Applies a named IP filter policy to every route.</summary>
    public WebDavMountBuilder RequireIpFilter(string policyName)
        => this.ForEach(route => route.RequireIpFilter(policyName));

    /// <summary>Exempts every route from the IP filter.</summary>
    public WebDavMountBuilder AllowAnyIp()
        => this.ForEach(route => route.AllowAnyIp());

    /// <summary>
    /// Omits the whole mount from the OpenAPI document without unmapping it. On by default —
    /// see <see cref="WebDavExtensions.MapWebDav(HttpServer, string, WebDavOptions)"/>.
    /// </summary>
    public WebDavMountBuilder ExcludeFromDescription()
        => this.ForEach(route => route.ExcludeFromDescription());

    /// <summary>Attaches arbitrary metadata to every route.</summary>
    public WebDavMountBuilder WithMetadata(object metadata)
        => this.ForEach(route => route.WithMetadata(metadata));

    /// <summary>Runs <paramref name="configure"/> against every route, for anything not covered above.</summary>
    public WebDavMountBuilder ForEach(Action<RouteEndpointBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        foreach (var route in this.Routes)
            configure(route);

        return this;
    }
}

/// <summary>
/// A directory served over WebDAV (RFC 4918): browsable, readable and writable by Finder, Windows
/// Explorer, the GNOME and KDE file managers, and every WebDAV client library there is.
/// <para>
/// The file browser next door is a plain-HTTP JSON API you drive with curl or a script. This is the
/// protocol an operating system already speaks — mount it and the app's storage shows up as a drive,
/// with no client to write at all. On a phone, behind a tunnel, that is a fairly short path from "an
/// app has files" to "those files are on my desktop".
/// </para>
/// <code>
/// app.MapWebDav("/dav", o =>
/// {
///     o.RootPath = FileSystem.AppDataDirectory;
///     o.AllowWrite = true;
///     o.AllowDelete = true;
/// })
/// .RequireAuthorization();
/// </code>
/// <para>
/// Serve it over TLS and put authentication in front of it. WebDAV clients send Basic credentials
/// on every request, and a mount without either is a directory anyone who finds the URL can write
/// to.
/// </para>
/// </summary>
public static class WebDavExtensions
{
    /// <summary>Maps a WebDAV mount at <paramref name="prefix"/>.</summary>
    public static WebDavMountBuilder MapWebDav(
        this HttpServer server,
        string prefix,
        Action<WebDavOptions> configure
    )
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new WebDavOptions();
        configure(options);

        return server.MapWebDav(prefix, options);
    }

    /// <summary>Maps a WebDAV mount from options built elsewhere.</summary>
    public static WebDavMountBuilder MapWebDav(this HttpServer server, string prefix, WebDavOptions options)
    {
        ArgumentNullException.ThrowIfNull(server);

        WebDavMountBuilder? builder = null;
        server.MapGroup(NormalizePrefix(prefix), group => builder = Map(group, options));

        return builder!;
    }

    /// <summary>Maps a WebDAV mount onto an existing route builder.</summary>
    public static WebDavMountBuilder MapWebDav(
        this IEndpointRouteBuilder endpoints,
        string prefix,
        WebDavOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        return Map(endpoints.MapGroup(NormalizePrefix(prefix)), options);
    }

    static WebDavMountBuilder Map(IEndpointRouteBuilder group, WebDavOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var root = options.ResolvedRoot;

        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"The WebDAV root '{root}' does not exist.");

        // Taken from the group rather than from the caller's argument, so a mount nested inside
        // another group reports hrefs that include the outer prefix — a client resolves every
        // member URL against them, so one that is short by a segment breaks the whole mount.
        var basePath = group.Prefix.Trim('/') is { Length: > 0 } trimmed ? "/" + trimmed : string.Empty;
        var handler = new WebDavHandler(options, root, basePath);

        var read = new List<RouteEndpointBuilder>();
        var write = new List<RouteEndpointBuilder>();
        var delete = new List<RouteEndpointBuilder>();
        var locking = new List<RouteEndpointBuilder>();

        // Two routes per verb: one for the mount root, one for everything under it. A catch-all
        // alone would not match the prefix on its own.
        void MapVerb(List<RouteEndpointBuilder> routes, string method, RequestDelegate handle)
        {
            routes.Add(group.Map(method, "/", handle));
            routes.Add(group.Map(method, "/{*path}", handle));
        }

        MapVerb(read, HttpMethods.Options, handler.OptionsAsync);
        MapVerb(read, HttpMethods.Get, handler.GetAsync);
        MapVerb(read, WebDavMethods.PropFind, handler.PropFindAsync);

        // Every verb is mapped whatever the options say, and the handler answers 403 for the ones
        // this mount does not allow. Leaving them unmapped would make the router's own 405 the
        // answer, with an Allow header assembled from the route table rather than from the options
        // — two sources of truth for the same question, and the wrong one is the one clients read.
        MapVerb(write, HttpMethods.Put, handler.PutAsync);
        MapVerb(write, WebDavMethods.MkCol, handler.MkColAsync);
        MapVerb(write, WebDavMethods.PropPatch, handler.PropPatchAsync);
        MapVerb(write, WebDavMethods.Copy, handler.CopyAsync);
        MapVerb(write, WebDavMethods.Move, handler.MoveAsync);

        MapVerb(delete, HttpMethods.Delete, handler.DeleteAsync);

        MapVerb(locking, WebDavMethods.Lock, handler.LockAsync);
        MapVerb(locking, WebDavMethods.Unlock, handler.UnlockAsync);

        var builder = new WebDavMountBuilder(read, write, delete, locking);

        // A WebDAV mount is not an API surface anyone wants described in an OpenAPI document: it is
        // one protocol behind twenty-two routes, and RFC 4918 already documents it.
        builder.ExcludeFromDescription();

        return builder;
    }

    static string NormalizePrefix(string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        return "/" + prefix.Trim().Trim('/');
    }
}
