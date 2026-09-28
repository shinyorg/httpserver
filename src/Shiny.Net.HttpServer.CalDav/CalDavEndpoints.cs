using Shiny.Net.HttpServer.CalDav.Internal;
using Shiny.Net.HttpServer.WebDav;

namespace Shiny.Net.HttpServer.CalDav;

/// <summary>The methods CalDAV (RFC 4791) and extended MKCOL (RFC 5689) use on top of WebDAV.</summary>
public static class CalDavMethods
{
    /// <summary>Creates a calendar collection (RFC 4791 §5.3.1).</summary>
    public const string MkCalendar = "MKCALENDAR";

    /// <summary>Asks for a report — every query, multiget and sync. Same as <see cref="WebDavMethods.Report"/>.</summary>
    public const string Report = WebDavMethods.Report;
}

/// <summary>
/// The routes one <c>MapCalDav</c> call registered, so a policy can be stated once for the whole
/// mount — the counterpart of <see cref="WebDavMountBuilder"/>.
/// <code>
/// app.MapCalDav("/dav", o =>
/// {
///     o.CalendarStore = new FileCalendarStore(Path.Combine(FileSystem.AppDataDirectory, "calendars"));
///     o.AddressBookStore = new FileAddressBookStore(Path.Combine(FileSystem.AppDataDirectory, "contacts"));
/// })
/// .RequireAuthorization();
/// </code>
/// </summary>
public sealed class CalDavMountBuilder
{
    readonly List<RouteEndpointBuilder> routes;
    readonly List<RouteEndpointBuilder> wellKnown;

    internal CalDavMountBuilder(List<RouteEndpointBuilder> routes, List<RouteEndpointBuilder> wellKnown)
    {
        this.routes = routes;
        this.wellKnown = wellKnown;
    }

    /// <summary>Every route under the mount's prefix.</summary>
    public IReadOnlyList<RouteEndpointBuilder> Routes => this.routes;

    /// <summary>
    /// The <c>/.well-known/caldav</c> and <c>/.well-known/carddav</c> redirects, when they were
    /// mapped. Not covered by the methods below: they reveal nothing but where the mount is, and a
    /// client usually asks them before it has sent any credentials.
    /// </summary>
    public IReadOnlyList<RouteEndpointBuilder> WellKnownRoutes => this.wellKnown;

    /// <summary>
    /// Requires authorization on every route, optionally against named policies. The authenticated
    /// user's name becomes the principal — see <see cref="CalDavOptions.PrincipalResolver"/>.
    /// </summary>
    public CalDavMountBuilder RequireAuthorization(params string[] policies)
        => this.ForEach(route => route.RequireAuthorization(policies));

    /// <summary>Exempts every route from authorization, including from a fallback policy.</summary>
    public CalDavMountBuilder AllowAnonymous()
        => this.ForEach(route => route.AllowAnonymous());

    /// <summary>Applies a named CORS policy to every route.</summary>
    public CalDavMountBuilder RequireCors(string policyName)
        => this.ForEach(route => route.RequireCors(policyName));

    /// <summary>Applies a named rate limit policy to every route.</summary>
    public CalDavMountBuilder RequireRateLimiting(string policyName)
        => this.ForEach(route => route.RequireRateLimiting(policyName));

    /// <summary>Exempts every route from rate limiting.</summary>
    public CalDavMountBuilder DisableRateLimiting()
        => this.ForEach(route => route.DisableRateLimiting());

    /// <summary>Applies a named IP filter policy to every route.</summary>
    public CalDavMountBuilder RequireIpFilter(string policyName)
        => this.ForEach(route => route.RequireIpFilter(policyName));

    /// <summary>Attaches arbitrary metadata to every route.</summary>
    public CalDavMountBuilder WithMetadata(object metadata)
        => this.ForEach(route => route.WithMetadata(metadata));

    /// <summary>Runs <paramref name="configure"/> against every route under the prefix.</summary>
    public CalDavMountBuilder ForEach(Action<RouteEndpointBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        foreach (var route in this.routes)
            configure(route);

        return this;
    }
}

/// <summary>
/// Calendars and contacts served over CalDAV (RFC 4791) and CardDAV (RFC 6352): subscribed to and
/// synced by iOS and macOS Calendar and Contacts, Thunderbird, DAVx⁵ on Android, and every other
/// client that speaks them.
/// <para>
/// One mount serves both, because the two share almost everything — a principal, discovery, sync
/// tokens, multiget, the ETag rules — and a client configured with one account URL expects to find
/// both home sets on the same principal.
/// </para>
/// <code>
/// app.MapCalDav("/dav", o =>
/// {
///     o.CalendarStore = new FileCalendarStore(Path.Combine(FileSystem.AppDataDirectory, "calendars"));
///     o.AddressBookStore = new FileAddressBookStore(Path.Combine(FileSystem.AppDataDirectory, "contacts"));
/// })
/// .RequireAuthorization();
/// </code>
/// <para>
/// On iOS: <b>Settings → Calendar → Accounts → Add Account → Other → Add CalDAV Account</b>, with the
/// device's address as the server and the Basic credentials. Serve it over TLS: every one of those
/// clients sends the password on every request.
/// </para>
/// </summary>
public static class CalDavExtensions
{
    /// <summary>Maps a CalDAV/CardDAV mount at <paramref name="prefix"/>, and the <c>/.well-known</c> redirects to it.</summary>
    public static CalDavMountBuilder MapCalDav(this HttpServer server, string prefix, Action<CalDavOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new CalDavOptions();
        configure(options);

        return server.MapCalDav(prefix, options);
    }

    /// <summary>Maps a CalDAV/CardDAV mount from options built elsewhere.</summary>
    public static CalDavMountBuilder MapCalDav(this HttpServer server, string prefix, CalDavOptions options)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(options);

        var normalized = NormalizePrefix(prefix);
        List<RouteEndpointBuilder>? routes = null;

        server.MapGroup(normalized, group => routes = Map(group, options));

        var wellKnown = new List<RouteEndpointBuilder>();

        if (options.MapWellKnown)
        {
            server.MapGroup("/", root =>
            {
                if (options.CalendarStore is not null)
                    MapWellKnown(root, "/.well-known/caldav", normalized, wellKnown);

                if (options.AddressBookStore is not null)
                    MapWellKnown(root, "/.well-known/carddav", normalized, wellKnown);
            });
        }

        return new CalDavMountBuilder(routes!, wellKnown);
    }

    /// <summary>
    /// Maps a mount onto an existing route builder. The <c>/.well-known</c> redirects are not mapped
    /// — they belong at the server's root, which a group cannot reach — so map them with
    /// <see cref="MapCalDavWellKnown"/> if a client needs them.
    /// </summary>
    public static CalDavMountBuilder MapCalDav(this IEndpointRouteBuilder endpoints, string prefix, CalDavOptions options)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(options);

        return new CalDavMountBuilder(Map(endpoints.MapGroup(NormalizePrefix(prefix)), options), []);
    }

    /// <summary>
    /// Maps <c>/.well-known/caldav</c> and <c>/.well-known/carddav</c> as redirects to
    /// <paramref name="mountPath"/> — for a mount placed with the group overload, or behind a
    /// reverse proxy that changes its path.
    /// </summary>
    public static IReadOnlyList<RouteEndpointBuilder> MapCalDavWellKnown(this HttpServer server, string mountPath, bool calDav = true, bool cardDav = true)
    {
        ArgumentNullException.ThrowIfNull(server);

        var normalized = NormalizePrefix(mountPath);
        var routes = new List<RouteEndpointBuilder>();

        server.MapGroup("/", root =>
        {
            if (calDav)
                MapWellKnown(root, "/.well-known/caldav", normalized, routes);

            if (cardDav)
                MapWellKnown(root, "/.well-known/carddav", normalized, routes);
        });

        return routes;
    }

    static List<RouteEndpointBuilder> Map(IEndpointRouteBuilder group, CalDavOptions options)
    {
        options.Validate();

        // From the group rather than the argument, so a mount nested in another group hands out
        // hrefs that include the outer prefix.
        var basePath = group.Prefix.Trim('/') is { Length: > 0 } trimmed ? "/" + trimmed : string.Empty;
        var handler = new CalDavHandler(options, basePath);
        var routes = new List<RouteEndpointBuilder>();

        void MapVerb(string method, RequestDelegate handle)
        {
            var guarded = handler.Guard(handle);

            routes.Add(group.Map(method, "/", guarded));
            routes.Add(group.Map(method, "/{*path}", guarded));
        }

        MapVerb(HttpMethods.Options, handler.OptionsAsync);
        MapVerb(HttpMethods.Get, handler.GetAsync);
        MapVerb(HttpMethods.Put, handler.PutAsync);
        MapVerb(HttpMethods.Delete, handler.DeleteAsync);
        MapVerb(WebDavMethods.PropFind, handler.PropFindAsync);
        MapVerb(WebDavMethods.PropPatch, handler.PropPatchAsync);
        MapVerb(WebDavMethods.Report, handler.ReportAsync);
        MapVerb(WebDavMethods.MkCol, handler.MkColAsync);
        MapVerb(CalDavMethods.MkCalendar, handler.MkCalendarAsync);

        // Not verbs this mount has any use for, but a client that tries one should get the Allow
        // list the mount publishes rather than the router's.
        MapVerb(WebDavMethods.Copy, handler.NotAllowedAsync);
        MapVerb(WebDavMethods.Move, handler.NotAllowedAsync);
        MapVerb(WebDavMethods.Lock, handler.NotAllowedAsync);
        MapVerb(WebDavMethods.Unlock, handler.NotAllowedAsync);

        // A CalDAV mount is one protocol behind two dozen routes, and RFC 4791 documents it.
        foreach (var route in routes)
            route.ExcludeFromDescription();

        return routes;
    }

    static void MapWellKnown(IEndpointRouteBuilder root, string path, string target, List<RouteEndpointBuilder> routes)
    {
        var location = target.EndsWith('/') ? target : target + "/";

        // RFC 6764 §5: a redirect to the context path. A 301 rather than a 307, because clients
        // (iOS among them) remember a permanent one and stop asking.
        ValueTask Redirect(HttpContext context)
        {
            context.Response.StatusCode = StatusCodes.Status301MovedPermanently;
            context.Response.Headers.Set(HeaderNames.Location, location);
            context.Response.ContentLength = 0;

            return context.Response.StartAsync(context.RequestAborted);
        }

        foreach (var method in (ReadOnlySpan<string>)[HttpMethods.Get, WebDavMethods.PropFind, HttpMethods.Options])
        {
            var route = root.Map(method, path, Redirect);
            route.ExcludeFromDescription();
            routes.Add(route);
        }
    }

    static string NormalizePrefix(string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        return "/" + prefix.Trim().Trim('/');
    }
}
