using System.Text;
using System.Xml;
using Shiny.Net.HttpServer.WebDav;

namespace Shiny.Net.HttpServer.CalDav.Internal;

/// <summary>
/// <c>REPORT</c>: the two multigets, the two queries, <c>free-busy-query</c> and RFC 6578's
/// <c>sync-collection</c>.
/// </summary>
partial class CalDavHandler
{
    public async ValueTask ReportAsync(HttpContext context)
    {
        if (await this.BeginAsync(context).ConfigureAwait(false) is not { } request)
            return;

        // RFC 3253 §3.6: a REPORT's Depth defaults to 0.
        if (!DavRequests.TryParseDepth(context.Request.Headers.GetFirst(WebDavHeaderNames.Depth), 0, out var depth))
        {
            await StatusAsync(context, StatusCodes.Status400BadRequest).ConfigureAwait(false);
            return;
        }

        await using var body = await DavXml.ReadBodyAsync(context, this.options.MaxXmlBodyBytes).ConfigureAwait(false);

        if (body is null)
        {
            await StatusAsync(context, StatusCodes.Status413PayloadTooLarge).ConfigureAwait(false);
            return;
        }

        if (!DavRequests.TryParseReport(body, out var report))
        {
            await StatusAsync(context, StatusCodes.Status400BadRequest).ConfigureAwait(false);
            return;
        }

        var target = request.Target;
        var supported = report.Kind switch
        {
            ReportKind.CalendarQuery or ReportKind.CalendarMultiget
                => target.Flavor == Flavor.Calendar && target.Kind is TargetKind.Collection or TargetKind.Object,
            ReportKind.AddressBookQuery or ReportKind.AddressBookMultiget
                => target.Flavor == Flavor.AddressBook && target.Kind is TargetKind.Collection or TargetKind.Object,
            ReportKind.FreeBusyQuery => target.Flavor == Flavor.Calendar && target.Kind == TargetKind.Collection,
            ReportKind.SyncCollection => target.Kind == TargetKind.Collection,
            _ => false
        };

        // RFC 3253 §3.6: a report the resource does not support is 403 with this condition, which is
        // how a client learns to fall back — PROPFIND and multiget instead of sync-collection, say.
        if (!supported)
        {
            await DavXml.WriteErrorAsync(context, StatusCodes.Status403Forbidden, Ns.Dav, "supported-report").ConfigureAwait(false);
            return;
        }

        if (report.Unsupported is { } unsupported)
        {
            var bar = unsupported.IndexOf('|');
            await DavXml.WriteErrorAsync(context, StatusCodes.Status403Forbidden, unsupported[..bar], unsupported[(bar + 1)..]).ConfigureAwait(false);
            return;
        }

        if (await this.NodeAsync(request, target, context.RequestAborted).ConfigureAwait(false) is not { } node)
        {
            await StatusAsync(context, StatusCodes.Status404NotFound).ConfigureAwait(false);
            return;
        }

        switch (report.Kind)
        {
            case ReportKind.CalendarMultiget:
            case ReportKind.AddressBookMultiget:
                await this.MultigetAsync(context, request, report).ConfigureAwait(false);
                break;

            case ReportKind.CalendarQuery:
            case ReportKind.AddressBookQuery:
                await this.QueryAsync(context, request, node, report, depth).ConfigureAwait(false);
                break;

            case ReportKind.FreeBusyQuery:
                await this.FreeBusyAsync(context, request, report).ConfigureAwait(false);
                break;

            case ReportKind.SyncCollection:
                await this.SyncCollectionAsync(context, request, node, report).ConfigureAwait(false);
                break;
        }
    }

    // ---- multiget ----

    /// <summary>
    /// The objects a client names by href, with the properties it asks for — in practice the ETag
    /// and the text. Each href gets its own response, and one that is not there is a 404 inside the
    /// 207, not a failure of the report.
    /// </summary>
    async ValueTask MultigetAsync(HttpContext context, DavRequest request, ReportRequest report)
    {
        var found = new List<DavNode>();
        var missing = new List<string>();

        foreach (var href in report.Hrefs.Distinct(StringComparer.Ordinal))
        {
            DavNode? node = null;

            if (this.paths.TryParseHref(href, out var target) &&
                target.Kind == TargetKind.Object &&
                target.Flavor == request.Target.Flavor &&
                target.Principal == request.Principal)
            {
                node = await this.NodeAsync(request, target, context.RequestAborted).ConfigureAwait(false);
            }

            if (node is null)
                missing.Add(href);
            else
                found.Add(node);
        }

        await this.PrepareAsync(request, found, report.Props, context.RequestAborted).ConfigureAwait(false);

        await DavXml.WriteAsync(context, StatusCodes.Status207MultiStatus, writer =>
        {
            DavXml.StartRoot(writer, Ns.Dav, "multistatus");

            foreach (var node in found)
                this.WriteResponse(writer, request, node, report.Props);

            foreach (var href in missing)
                WriteStatusResponse(writer, href, StatusCodes.Status404NotFound);

            writer.WriteEndElement();
        }).ConfigureAwait(false);
    }

    // ---- calendar-query / addressbook-query ----

    async ValueTask QueryAsync(HttpContext context, DavRequest request, DavNode node, ReportRequest report, int depth)
    {
        var target = request.Target;
        var candidates = new List<DavNode>();

        if (target.Kind == TargetKind.Object)
        {
            candidates.Add(node);
        }
        else if (depth > 0)
        {
            foreach (var child in await this.ChildrenAsync(request, node, context.RequestAborted).ConfigureAwait(false))
            {
                var loaded = await this.StoreFor(target)!
                    .GetObjectAsync(request.Principal, target.Collection!, child.Target.Name!, context.RequestAborted)
                    .ConfigureAwait(false);

                if (loaded is not null)
                    candidates.Add(ObjectNode(child.Target, child.Href, node.Collection!, loaded));
            }
        }

        // Every object is read and parsed to be filtered. That is the honest cost of a store that is
        // a list of text objects; see the docs page for when it starts to matter.
        var matches = new List<DavNode>();

        foreach (var candidate in candidates)
        {
            if (candidate.Data is null || !ContentComponent.TryParse(candidate.Data, out var parsed, out _))
                continue;

            var include = report.Kind == ReportKind.CalendarQuery
                ? report.CalendarFilter is not { } filter || this.evaluator.Matches(filter, parsed)
                : report.AddressBookFilter is not { } cardFilter || cardFilter.Matches(parsed);

            if (include)
                matches.Add(candidate);
        }

        // CardDAV's limit (RFC 6352 §8.6.1): the first n, and a 507 against the request-URI telling
        // the client the list was cut short.
        var truncated = false;

        if (report.Limit is { } limit && matches.Count > limit)
        {
            matches.RemoveRange(limit, matches.Count - limit);
            truncated = true;
        }

        await this.PrepareAsync(request, matches, report.Props, context.RequestAborted).ConfigureAwait(false);

        await DavXml.WriteAsync(context, StatusCodes.Status207MultiStatus, writer =>
        {
            DavXml.StartRoot(writer, Ns.Dav, "multistatus");

            foreach (var match in matches)
                this.WriteResponse(writer, request, match, report.Props);

            if (truncated)
            {
                DavXml.Element(writer, Ns.Dav, "response");
                DavXml.Text(writer, Ns.Dav, "href", node.Href);
                DavXml.Text(writer, Ns.Dav, "status", DavXml.StatusLine(StatusCodes.Status507InsufficientStorage));
                DavXml.Element(writer, Ns.Dav, "error");
                DavXml.Empty(writer, Ns.Dav, "number-of-matches-within-limits");
                writer.WriteEndElement();
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        }).ConfigureAwait(false);
    }

    // ---- sync-collection (RFC 6578) ----

    async ValueTask SyncCollectionAsync(HttpContext context, DavRequest request, DavNode node, ReportRequest report)
    {
        var target = request.Target;
        var listing = await this.StoreFor(target)!
            .GetObjectsAsync(request.Principal, target.Collection!, context.RequestAborted)
            .ConfigureAwait(false);

        var changed = new List<DavObjectInfo>();
        var removed = new List<string>();

        if (string.IsNullOrEmpty(report.SyncToken))
        {
            // The initial sync: everything is new.
            changed.AddRange(listing);
        }
        else if (!SyncTracker.IsOurs(report.SyncToken))
        {
            await DavXml.WriteErrorAsync(context, StatusCodes.Status403Forbidden, Ns.Dav, "valid-sync-token").ConfigureAwait(false);
            return;
        }
        else if (report.SyncToken != SyncTracker.TokenFor(target.CollectionKey, listing))
        {
            if (!this.sync.TryGetSnapshot(target.CollectionKey, report.SyncToken, out var snapshot))
            {
                // Aged out, or from before a restart. RFC 6578 §3.2: the client drops its token and
                // syncs from scratch, which every client implements because every server does this.
                await DavXml.WriteErrorAsync(context, StatusCodes.Status403Forbidden, Ns.Dav, "valid-sync-token").ConfigureAwait(false);
                return;
            }

            var current = new HashSet<string>(StringComparer.Ordinal);

            foreach (var info in listing)
            {
                current.Add(info.Name);

                if (!snapshot.TryGetValue(info.Name, out var etag) || etag != info.ETag)
                    changed.Add(info);
            }

            foreach (var name in snapshot.Keys)
            {
                if (!current.Contains(name))
                    removed.Add(name);
            }
        }

        var token = this.sync.Register(target.CollectionKey, listing);

        var nodes = changed
            .OrderBy(i => i.Name, StringComparer.Ordinal)
            .Select(info =>
            {
                var child = target.WithObject(info.Name);
                return new DavNode(child, this.paths.Href(child)) { Collection = node.Collection, Info = info };
            })
            .ToList();

        await this.PrepareAsync(request, nodes, report.Props, context.RequestAborted).ConfigureAwait(false);

        await DavXml.WriteAsync(context, StatusCodes.Status207MultiStatus, writer =>
        {
            DavXml.StartRoot(writer, Ns.Dav, "multistatus");

            foreach (var changedNode in nodes)
                this.WriteResponse(writer, request, changedNode, report.Props);

            // A removed member is a response with a bare 404 and no propstat (RFC 6578 §3.5.2).
            foreach (var name in removed.Order(StringComparer.Ordinal))
                WriteStatusResponse(writer, this.paths.Href(target.WithObject(name)), StatusCodes.Status404NotFound);

            DavXml.Text(writer, Ns.Dav, "sync-token", token);

            writer.WriteEndElement();
        }).ConfigureAwait(false);
    }

    // ---- free-busy-query ----

    /// <summary>
    /// RFC 4791 §7.10: when this calendar is busy in a range, as one <c>VFREEBUSY</c>. Transparent
    /// and cancelled events do not count; tentative ones are <c>BUSY-TENTATIVE</c>.
    /// </summary>
    async ValueTask FreeBusyAsync(HttpContext context, DavRequest request, ReportRequest report)
    {
        if (report.TimeRange is not { Start: { } start } range)
        {
            await StatusAsync(context, StatusCodes.Status400BadRequest).ConfigureAwait(false);
            return;
        }

        var target = request.Target;
        var store = this.StoreFor(target)!;
        var listing = await store.GetObjectsAsync(request.Principal, target.Collection!, context.RequestAborted).ConfigureAwait(false);
        var periods = new List<(DateTime Start, DateTime End, string Type)>();

        foreach (var info in listing)
        {
            var found = await store.GetObjectAsync(request.Principal, target.Collection!, info.Name, context.RequestAborted).ConfigureAwait(false);

            if (found is null || !ContentComponent.TryParse(found.Data, out var calendar, out _))
                continue;

            foreach (var evt in calendar.GetComponents("VEVENT"))
            {
                if (evt.GetProperty("TRANSP")?.Value.Trim().Equals("TRANSPARENT", StringComparison.OrdinalIgnoreCase) == true)
                    continue;

                var status = evt.GetProperty("STATUS")?.Value.Trim().ToUpperInvariant();

                if (status == "CANCELLED")
                    continue;

                var type = status == "TENTATIVE" ? "BUSY-TENTATIVE" : "BUSY";

                foreach (var (from, to) in this.evaluator.EventPeriods(evt, calendar, range))
                {
                    // Clipped to the range: a client asked about a week, not about the series.
                    var clippedStart = from < start ? start : from;
                    var clippedEnd = range.End is { } end && to > end ? end : to;

                    if (clippedEnd > clippedStart)
                        periods.Add((clippedStart, clippedEnd, type));
                }
            }
        }

        var freeBusy = new ContentComponent("VFREEBUSY");
        freeBusy.Properties.Add(new ContentProperty("DTSTAMP", ICalTime.FormatUtc(DateTime.UtcNow)));
        freeBusy.Properties.Add(new ContentProperty("DTSTART", ICalTime.FormatUtc(start)));

        if (range.End is { } rangeEnd)
            freeBusy.Properties.Add(new ContentProperty("DTEND", ICalTime.FormatUtc(rangeEnd)));

        foreach (var group in periods.OrderBy(p => p.Start).GroupBy(p => p.Type))
        {
            freeBusy.Properties.Add(new ContentProperty(
                "FREEBUSY",
                string.Join(',', group.Select(p => ICalTime.FormatUtc(p.Start) + "/" + ICalTime.FormatUtc(p.End))),
                [new ContentParameter("FBTYPE", [group.Key])]
            ));
        }

        var result = new ContentComponent("VCALENDAR");
        result.Properties.Add(new ContentProperty("VERSION", "2.0"));
        result.Properties.Add(new ContentProperty("PRODID", "-//Shiny//Shiny.Net.HttpServer.CalDav//EN"));
        result.Components.Add(freeBusy);

        await context.Response
            .WriteTextAsync(result.ToString(), ContentTypeOf(Flavor.Calendar), context.RequestAborted)
            .ConfigureAwait(false);
    }

    static void WriteStatusResponse(XmlWriter writer, string href, int status)
    {
        DavXml.Element(writer, Ns.Dav, "response");
        DavXml.Text(writer, Ns.Dav, "href", href);
        DavXml.Text(writer, Ns.Dav, "status", DavXml.StatusLine(status));
        writer.WriteEndElement();
    }
}
