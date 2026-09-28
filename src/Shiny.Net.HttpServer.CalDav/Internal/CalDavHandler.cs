using System.Collections.Concurrent;
using System.Text;
using Shiny.Net.HttpServer.WebDav;

namespace Shiny.Net.HttpServer.CalDav.Internal;

/// <summary>A request that has been resolved to a principal and a target, and may proceed.</summary>
sealed record DavRequest(string Principal, DavTarget Target);

/// <summary>
/// The verbs behind <see cref="CalDavExtensions.MapCalDav(HttpServer, string, CalDavOptions)"/>.
/// <para>
/// Every request goes through <see cref="BeginAsync"/> first: it works out who the request is for,
/// what it points at, and refuses one that points into another principal's home. Nothing below it
/// builds a path or a principal by hand — one door, checked once.
/// </para>
/// </summary>
sealed partial class CalDavHandler
{
    readonly CalDavOptions options;
    readonly DavPaths paths;
    readonly IStoreAdapter? calendars;
    readonly IStoreAdapter? addressBooks;
    readonly IWebDavPropertyStore properties;
    readonly SyncTracker sync;
    readonly CalendarFilterEvaluator evaluator;

    // One writer at a time per collection, so the If-Match check, the UID-uniqueness check and the
    // write they guard happen as one step — a store does not have to be careful about that itself.
    readonly ConcurrentDictionary<string, SemaphoreSlim> writeLocks = new(StringComparer.Ordinal);

    // The UID of each object, remembered against its ETag, so enforcing no-uid-conflict does not
    // read every object in a collection on every PUT.
    readonly ConcurrentDictionary<(string Collection, string Name), (string ETag, string? Uid)> uids = new();

    public CalDavHandler(CalDavOptions options, string basePath)
    {
        this.options = options;
        this.paths = new DavPaths(basePath);
        this.properties = options.PropertyStore ?? new InMemoryWebDavPropertyStore();
        this.sync = new SyncTracker(options.MaxSyncTokensPerCollection);
        this.evaluator = new CalendarFilterEvaluator(options.MaxRecurrenceInstances);

        if (options.CalendarStore is { } calendarStore)
            this.calendars = new StoreAdapter<CalendarCollection>(calendarStore, Flavor.Calendar, id => new CalendarCollection(id));

        if (options.AddressBookStore is { } addressBookStore)
            this.addressBooks = new StoreAdapter<AddressBookCollection>(addressBookStore, Flavor.AddressBook, id => new AddressBookCollection(id));
    }

    /// <summary>
    /// Wraps a verb so that a store's refusal becomes the status it names — <see cref="WebDavException"/>
    /// as on a WebDAV mount, and the <c>System.IO</c> exceptions a file-backed store throws anyway.
    /// </summary>
    public RequestDelegate Guard(RequestDelegate verb) => async context =>
    {
        int status;

        try
        {
            await verb(context).ConfigureAwait(false);
            return;
        }
        catch (WebDavException ex) when (!context.Response.HasStarted)
        {
            status = ex.StatusCode;
        }
        catch (UnauthorizedAccessException) when (!context.Response.HasStarted)
        {
            status = StatusCodes.Status403Forbidden;
        }
        catch (IOException ex) when (!context.Response.HasStarted)
        {
            status = ex is FileNotFoundException or DirectoryNotFoundException
                ? StatusCodes.Status404NotFound
                : StatusCodes.Status409Conflict;
        }

        await StatusAsync(context, status).ConfigureAwait(false);
    };

    /// <summary>
    /// Resolves who the request is for and what it points at, answering the client itself when it
    /// cannot proceed: 401 with no principal, 404 outside the layout or for a protocol that is off,
    /// 403 for another principal's URL. Null means the response is written.
    /// </summary>
    async ValueTask<DavRequest?> BeginAsync(HttpContext context)
    {
        var principal = this.PrincipalOf(context);

        if (principal is null)
        {
            await StatusAsync(context, StatusCodes.Status401Unauthorized).ConfigureAwait(false);
            return null;
        }

        var raw = context.Request.RouteValues.TryGetValue("path", out var value) ? value?.ToString() ?? string.Empty : string.Empty;

        if (!DavPaths.TryParseRoute(raw, out var target) || this.StoreFor(target) is null && target.Flavor != Flavor.None)
        {
            await StatusAsync(context, StatusCodes.Status404NotFound).ConfigureAwait(false);
            return null;
        }

        // A principal is a path segment in every URL this mount hands out, and the URL is the only
        // thing a client can tamper with. Checked here, once, before any store is asked anything.
        if (target.Principal is { } owner && !string.Equals(owner, principal, StringComparison.Ordinal))
        {
            await StatusAsync(context, StatusCodes.Status403Forbidden).ConfigureAwait(false);
            return null;
        }

        return new DavRequest(principal, target);
    }

    string? PrincipalOf(HttpContext context)
    {
        var principal = this.options.PrincipalResolver is { } resolve
            ? resolve(context)
            : context.User.Identity is { IsAuthenticated: true, Name: { Length: > 0 } name } ? name : this.options.DefaultPrincipal;

        // A principal becomes a path segment; one that cannot be one is not a principal.
        if (principal is null || principal.Length == 0 || principal is "." or ".." || principal.Contains('/') || principal.Contains('\\'))
            return null;

        return principal;
    }

    IStoreAdapter? StoreFor(DavTarget target) => target.Flavor switch
    {
        Flavor.Calendar => this.calendars,
        Flavor.AddressBook => this.addressBooks,
        _ => null
    };

    bool IsWritable(DavCollection? collection) => !this.options.ReadOnly && collection is not { IsReadOnly: true };

    // ---- OPTIONS ----

    /// <summary>
    /// The compliance classes and the methods. The <c>DAV</c> header is what a client checks for
    /// <c>calendar-access</c> and <c>addressbook</c> before it will treat the server as CalDAV or
    /// CardDAV at all.
    /// </summary>
    public ValueTask OptionsAsync(HttpContext context)
    {
        var response = context.Response;

        response.Headers.Set(WebDavHeaderNames.Dav, this.DavHeader());
        response.Headers.Set(HeaderNames.Allow, this.AllowHeader());
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentLength = 0;

        return response.StartAsync(context.RequestAborted);
    }

    string DavHeader()
    {
        // Class 1 and 3 — not 2, since there is no locking, and no CalDAV or CardDAV client needs it.
        var builder = new StringBuilder("1, 3, extended-mkcol");

        if (this.calendars is not null)
            builder.Append(", calendar-access");

        if (this.addressBooks is not null)
            builder.Append(", addressbook");

        return builder.ToString();
    }

    string AllowHeader()
        => this.options.ReadOnly
            ? "OPTIONS, GET, HEAD, PROPFIND, REPORT"
            : "OPTIONS, GET, HEAD, PUT, DELETE, PROPFIND, PROPPATCH, REPORT, MKCOL, MKCALENDAR";

    /// <summary>405, with the <c>Allow</c> list OPTIONS publishes.</summary>
    public ValueTask NotAllowedAsync(HttpContext context)
    {
        context.Response.Headers.Set(HeaderNames.Allow, this.AllowHeader());

        return StatusAsync(context, StatusCodes.Status405MethodNotAllowed);
    }

    // ---- GET / HEAD ----

    public async ValueTask GetAsync(HttpContext context)
    {
        if (await this.BeginAsync(context).ConfigureAwait(false) is not { } request)
            return;

        var target = request.Target;
        var store = this.StoreFor(target);

        if (target.Kind == TargetKind.Object)
        {
            var found = await store!
                .GetObjectAsync(request.Principal, target.Collection!, target.Name!, context.RequestAborted)
                .ConfigureAwait(false);

            if (found is null)
            {
                await StatusAsync(context, StatusCodes.Status404NotFound).ConfigureAwait(false);
                return;
            }

            context.Response.Headers.Set(HeaderNames.ETag, found.ETag);
            context.Response.Headers.Set(HeaderNames.LastModified, found.LastModifiedUtc.ToString("R", System.Globalization.CultureInfo.InvariantCulture));

            if (ETagMatches(context.Request.Headers.GetFirst(HeaderNames.IfNoneMatch), found.ETag))
            {
                await StatusAsync(context, StatusCodes.Status304NotModified).ConfigureAwait(false);
                return;
            }

            await context.Response
                .WriteTextAsync(found.Data, ContentTypeOf(target.Flavor), context.RequestAborted)
                .ConfigureAwait(false);

            return;
        }

        if (target.Kind == TargetKind.Collection)
        {
            await this.ExportAsync(context, request, store!).ConfigureAwait(false);
            return;
        }

        await this.NotAllowedAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>GET</c> on a collection, which neither RFC defines: the whole calendar as one
    /// <c>VCALENDAR</c>, or every contact as one <c>.vcf</c>. That makes the same URL a read-only
    /// subscription (<c>webcal://</c>) for a client that does not speak CalDAV, and an export for
    /// a person with a browser.
    /// </summary>
    async ValueTask ExportAsync(HttpContext context, DavRequest request, IStoreAdapter store)
    {
        var target = request.Target;

        if (await store.GetCollectionAsync(request.Principal, target.Collection!, context.RequestAborted).ConfigureAwait(false) is not { } collection)
        {
            await StatusAsync(context, StatusCodes.Status404NotFound).ConfigureAwait(false);
            return;
        }

        var listing = await store.GetObjectsAsync(request.Principal, target.Collection!, context.RequestAborted).ConfigureAwait(false);
        var text = new StringBuilder();

        if (target.Flavor == Flavor.Calendar)
        {
            var merged = new ContentComponent("VCALENDAR");
            merged.Properties.Add(new ContentProperty("VERSION", "2.0"));
            merged.Properties.Add(new ContentProperty("PRODID", "-//Shiny//Shiny.Net.HttpServer.CalDav//EN"));
            merged.Properties.Add(new ContentProperty("X-WR-CALNAME", ContentProperty.EscapeText(collection.DisplayName ?? collection.Id)));

            var zones = new HashSet<string>(StringComparer.Ordinal);

            foreach (var item in listing.OrderBy(i => i.Name, StringComparer.Ordinal))
            {
                var found = await store.GetObjectAsync(request.Principal, target.Collection!, item.Name, context.RequestAborted).ConfigureAwait(false);

                if (found is null || !ContentComponent.TryParse(found.Data, out var calendar, out _))
                    continue;

                foreach (var component in calendar.Components)
                {
                    // One VTIMEZONE per zone, however many events use it.
                    if (component.Name == "VTIMEZONE" && !zones.Add(component.GetProperty("TZID")?.Value ?? string.Empty))
                        continue;

                    merged.Components.Add(component);
                }
            }

            merged.WriteTo(text);
        }
        else
        {
            foreach (var item in listing.OrderBy(i => i.Name, StringComparer.Ordinal))
            {
                var found = await store.GetObjectAsync(request.Principal, target.Collection!, item.Name, context.RequestAborted).ConfigureAwait(false);

                if (found is not null)
                    text.Append(found.Data.TrimEnd('\r', '\n')).Append("\r\n");
            }
        }

        context.Response.Headers.Set(HeaderNames.CacheControl, "no-cache");

        await context.Response
            .WriteTextAsync(text.ToString(), ContentTypeOf(target.Flavor), context.RequestAborted)
            .ConfigureAwait(false);
    }

    // ---- DELETE ----

    public async ValueTask DeleteAsync(HttpContext context)
    {
        if (await this.BeginAsync(context).ConfigureAwait(false) is not { } request)
            return;

        var target = request.Target;
        var store = this.StoreFor(target);

        if (target.Kind is not (TargetKind.Object or TargetKind.Collection))
        {
            await StatusAsync(context, StatusCodes.Status403Forbidden).ConfigureAwait(false);
            return;
        }

        var collection = await store!
            .GetCollectionAsync(request.Principal, target.Collection!, context.RequestAborted)
            .ConfigureAwait(false);

        if (collection is null)
        {
            await StatusAsync(context, StatusCodes.Status404NotFound).ConfigureAwait(false);
            return;
        }

        if (!this.IsWritable(collection))
        {
            await WriteNeedPrivilegesAsync(context).ConfigureAwait(false);
            return;
        }

        using var held = await this.LockAsync(target, context.RequestAborted).ConfigureAwait(false);

        if (target.Kind == TargetKind.Collection)
        {
            await store.DeleteCollectionAsync(request.Principal, target.Collection!, context.RequestAborted).ConfigureAwait(false);
            await this.properties.DeleteAsync(target.PropertyKey, recursive: true, context.RequestAborted).ConfigureAwait(false);

            this.sync.Forget(target.CollectionKey);

            foreach (var key in this.uids.Keys)
            {
                if (key.Collection == target.CollectionKey)
                    this.uids.TryRemove(key, out _);
            }

            await StatusAsync(context, StatusCodes.Status204NoContent).ConfigureAwait(false);
            return;
        }

        var existing = await store
            .GetObjectAsync(request.Principal, target.Collection!, target.Name!, context.RequestAborted)
            .ConfigureAwait(false);

        if (existing is null)
        {
            await StatusAsync(context, StatusCodes.Status404NotFound).ConfigureAwait(false);
            return;
        }

        // A client deletes with the ETag it last saw, so a change made elsewhere since then is not
        // thrown away by a deletion that did not know about it.
        if (context.Request.Headers.GetFirst(HeaderNames.IfMatch) is { } ifMatch && !ETagMatches(ifMatch, existing.ETag))
        {
            await StatusAsync(context, StatusCodes.Status412PreconditionFailed).ConfigureAwait(false);
            return;
        }

        await store.DeleteObjectAsync(request.Principal, target.Collection!, target.Name!, context.RequestAborted).ConfigureAwait(false);
        await this.properties.DeleteAsync(target.PropertyKey, recursive: false, context.RequestAborted).ConfigureAwait(false);

        this.uids.TryRemove((target.CollectionKey, target.Name!), out _);

        await StatusAsync(context, StatusCodes.Status204NoContent).ConfigureAwait(false);
    }

    // ---- shared plumbing ----

    async ValueTask<IDisposable> LockAsync(DavTarget target, CancellationToken cancellationToken)
    {
        var gate = this.writeLocks.GetOrAdd(target.CollectionKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        return new Releaser(gate);
    }

    sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        int released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref this.released, 1) == 0)
                gate.Release();
        }
    }

    static string ContentTypeOf(Flavor flavor)
        => flavor == Flavor.Calendar ? "text/calendar; charset=utf-8" : "text/vcard; charset=utf-8";

    /// <summary>
    /// Whether an <c>If-Match</c>/<c>If-None-Match</c> list names this tag. <c>*</c> matches any
    /// tag; weak tags compare by their opaque part, which is what RFC 9110 §13.1.2 allows for
    /// <c>If-None-Match</c> and harmless for the strong tags this mount hands out.
    /// </summary>
    static bool ETagMatches(string? header, string etag)
    {
        if (header is null)
            return false;

        var normalized = Opaque(etag);

        foreach (var candidate in header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (candidate == "*" || Opaque(candidate) == normalized)
                return true;
        }

        return false;

        static string Opaque(string tag)
        {
            var value = tag.Trim();

            if (value.StartsWith("W/", StringComparison.Ordinal))
                value = value[2..];

            return value.Trim('"');
        }
    }

    /// <summary>A bare status, which is what a DAV client wants when there is nothing to say.</summary>
    static ValueTask StatusAsync(HttpContext context, int statusCode)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentLength = 0;

        return context.Response.StartAsync(context.RequestAborted);
    }

    /// <summary>
    /// 403 with <c>DAV:need-privileges</c> (RFC 3744 §7.1.1): the collection is read-only, which the
    /// client was already told through <c>current-user-privilege-set</c>.
    /// </summary>
    static ValueTask WriteNeedPrivilegesAsync(HttpContext context)
        => DavXml.WriteErrorAsync(context, StatusCodes.Status403Forbidden, Ns.Dav, "need-privileges");
}
