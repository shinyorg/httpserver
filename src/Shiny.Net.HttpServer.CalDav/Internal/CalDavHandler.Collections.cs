using Shiny.Net.HttpServer.WebDav;

namespace Shiny.Net.HttpServer.CalDav.Internal;

/// <summary>Creating collections: <c>MKCALENDAR</c> (RFC 4791 §5.3.1) and extended <c>MKCOL</c> (RFC 5689).</summary>
partial class CalDavHandler
{
    public ValueTask MkCalendarAsync(HttpContext context) => this.CreateCollectionAsync(context, isMkCalendar: true);

    public ValueTask MkColAsync(HttpContext context) => this.CreateCollectionAsync(context, isMkCalendar: false);

    /// <summary>
    /// Both verbs, since they differ only in their body's root element and in which home they may
    /// be used in. A plain <c>MKCOL</c> with no body in a home creates that home's kind of
    /// collection; an extended one names its resourcetype and must name the right one.
    /// </summary>
    async ValueTask CreateCollectionAsync(HttpContext context, bool isMkCalendar)
    {
        if (await this.BeginAsync(context).ConfigureAwait(false) is not { } request)
            return;

        var target = request.Target;

        // Only directly inside a home: a calendar inside a calendar is not a thing either RFC allows.
        if (target.Kind != TargetKind.Collection || (isMkCalendar && target.Flavor != Flavor.Calendar))
        {
            if (isMkCalendar)
                await DavXml.WriteErrorAsync(context, StatusCodes.Status403Forbidden, Ns.CalDav, "calendar-collection-location-ok").ConfigureAwait(false);
            else
                await StatusAsync(context, StatusCodes.Status403Forbidden).ConfigureAwait(false);

            return;
        }

        if (this.options.ReadOnly)
        {
            await WriteNeedPrivilegesAsync(context).ConfigureAwait(false);
            return;
        }

        var store = this.StoreFor(target)!;

        if (await store.GetCollectionAsync(request.Principal, target.Collection!, context.RequestAborted).ConfigureAwait(false) is not null)
        {
            // RFC 4918 §9.3.1 / RFC 4791 §5.3.1: the URL is taken.
            if (isMkCalendar)
                await DavXml.WriteErrorAsync(context, StatusCodes.Status403Forbidden, Ns.Dav, "resource-must-be-null").ConfigureAwait(false);
            else
                await this.NotAllowedAsync(context).ConfigureAwait(false);

            return;
        }

        await using var body = await DavXml.ReadBodyAsync(context, this.options.MaxXmlBodyBytes).ConfigureAwait(false);

        if (body is null)
        {
            await StatusAsync(context, StatusCodes.Status413PayloadTooLarge).ConfigureAwait(false);
            return;
        }

        var updates = new List<PropertyUpdate>();

        if (body.Length > 0 &&
            !DavRequests.TryParseUpdates(body, isMkCalendar ? Ns.CalDav : Ns.Dav, isMkCalendar ? "mkcalendar" : "mkcol", out updates))
        {
            // RFC 5689 §3: an MKCOL body this server does not understand is 415, not 400.
            await StatusAsync(context, isMkCalendar ? StatusCodes.Status400BadRequest : StatusCodes.Status415UnsupportedMediaType).ConfigureAwait(false);
            return;
        }

        var collection = store.NewCollection(target.Collection!);
        var statuses = new int[updates.Count];
        var refused = false;

        for (var i = 0; i < updates.Count; i++)
        {
            var update = updates[i];

            if (update.IsRemove)
            {
                statuses[i] = StatusCodes.Status403Forbidden;
                refused = true;
                continue;
            }

            if (update.Name == new WebDavPropertyName(Ns.Dav, "resourcetype"))
            {
                // Extended MKCOL says what it is making. It has to be this home's kind.
                var matches = target.Flavor == Flavor.Calendar
                    ? DavRequests.ContainsElement(update.Xml!, Ns.CalDav, "calendar")
                    : DavRequests.ContainsElement(update.Xml!, Ns.CardDav, "addressbook");

                statuses[i] = matches ? StatusCodes.Status200OK : StatusCodes.Status403Forbidden;
                refused |= !matches;
                continue;
            }

            // Settable at creation, and only then (RFC 4791 §5.2.3).
            if (update.Name == new WebDavPropertyName(Ns.CalDav, "supported-calendar-component-set") && collection is CalendarCollection calendar)
            {
                var names = DavRequests.ComponentNames(update.Xml!);

                if (names.Count == 0 || names.Any(n => n is not ("VEVENT" or "VTODO" or "VJOURNAL" or "VFREEBUSY")))
                {
                    statuses[i] = StatusCodes.Status403Forbidden;
                    refused = true;
                    continue;
                }

                collection = calendar with { SupportedComponents = names };
                statuses[i] = StatusCodes.Status200OK;
                continue;
            }

            if (TryApplyToCollection(collection, update, out var changed))
            {
                collection = changed;
                statuses[i] = StatusCodes.Status200OK;
                continue;
            }

            var isProtected = Computed.Contains(update.Name) || update.Name.IsDav;
            statuses[i] = isProtected ? StatusCodes.Status403Forbidden : StatusCodes.Status200OK;
            refused |= isProtected;
        }

        if (refused)
        {
            for (var i = 0; i < statuses.Length; i++)
            {
                if (statuses[i] == StatusCodes.Status200OK)
                    statuses[i] = StatusCodes.Status424FailedDependency;
            }

            // The body says which property sank it (RFC 4791 §5.3.1.2, RFC 5689 §3.3).
            await DavXml.WriteAsync(context, StatusCodes.Status403Forbidden, writer =>
            {
                DavXml.StartRoot(writer, isMkCalendar ? Ns.CalDav : Ns.Dav, isMkCalendar ? "mkcalendar-response" : "mkcol-response");

                foreach (var status in statuses.Distinct())
                {
                    var group = status;

                    WritePropStat(writer, group, w =>
                    {
                        for (var i = 0; i < updates.Count; i++)
                        {
                            if (statuses[i] == group)
                                WriteEmptyName(w, updates[i].Name);
                        }
                    });
                }

                writer.WriteEndElement();
            }).ConfigureAwait(false);

            return;
        }

        using var held = await this.LockAsync(target, context.RequestAborted).ConfigureAwait(false);

        await store.CreateCollectionAsync(request.Principal, collection, context.RequestAborted).ConfigureAwait(false);

        // A collection made again at the URL of one deleted earlier starts with no history.
        this.sync.Forget(target.CollectionKey);

        foreach (var update in updates)
        {
            if (update.Name == new WebDavPropertyName(Ns.Dav, "resourcetype") ||
                update.Name == new WebDavPropertyName(Ns.CalDav, "supported-calendar-component-set") ||
                TryApplyToCollection(collection, update, out _))
                continue;

            await this.properties
                .SetAsync(target.PropertyKey, new WebDavProperty(update.Name, update.Xml!), context.RequestAborted)
                .ConfigureAwait(false);
        }

        context.Response.Headers.Set(HeaderNames.CacheControl, "no-cache");
        await StatusAsync(context, StatusCodes.Status201Created).ConfigureAwait(false);
    }
}
