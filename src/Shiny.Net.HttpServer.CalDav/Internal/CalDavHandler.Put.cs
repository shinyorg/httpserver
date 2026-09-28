using System.Text;

namespace Shiny.Net.HttpServer.CalDav.Internal;

/// <summary>
/// Writing objects: <c>PUT</c>, with the preconditions RFC 4791 §5.3.2.1 and RFC 6352 §6.3.2.1 put
/// on it.
/// </summary>
partial class CalDavHandler
{
    public async ValueTask PutAsync(HttpContext context)
    {
        if (await this.BeginAsync(context).ConfigureAwait(false) is not { } request)
            return;

        var target = request.Target;

        if (target.Kind != TargetKind.Object)
        {
            await this.NotAllowedAsync(context).ConfigureAwait(false);
            return;
        }

        var store = this.StoreFor(target)!;
        var isCalendar = target.Flavor == Flavor.Calendar;
        var ns = isCalendar ? Ns.CalDav : Ns.CardDav;

        var collection = await store.GetCollectionAsync(request.Principal, target.Collection!, context.RequestAborted).ConfigureAwait(false);

        // A PUT does not create the collection it lands in; RFC 4918 §9.7.1 makes that a conflict.
        if (collection is null)
        {
            await StatusAsync(context, StatusCodes.Status409Conflict).ConfigureAwait(false);
            return;
        }

        if (!this.IsWritable(collection))
        {
            await WriteNeedPrivilegesAsync(context).ConfigureAwait(false);
            return;
        }

        if (!IsAcceptableMediaType(context.Request.ContentType, isCalendar))
        {
            await DavXml.WriteErrorAsync(context, StatusCodes.Status403Forbidden, ns, isCalendar ? "supported-calendar-data" : "supported-address-data").ConfigureAwait(false);
            return;
        }

        await using var body = await DavXml.ReadBodyAsync(context, this.options.MaxResourceSize).ConfigureAwait(false);

        if (body is null)
        {
            await DavXml.WriteErrorAsync(context, StatusCodes.Status403Forbidden, ns, "max-resource-size").ConfigureAwait(false);
            return;
        }

        var text = Encoding.UTF8.GetString(body.GetBuffer(), 0, (int)body.Length).TrimStart('﻿');

        if (!ContentComponent.TryParse(text, out var root, out _))
        {
            await DavXml.WriteErrorAsync(context, StatusCodes.Status403Forbidden, ns, isCalendar ? "valid-calendar-data" : "valid-address-data").ConfigureAwait(false);
            return;
        }

        var (uid, condition) = isCalendar
            ? ValidateCalendarObject(root, (CalendarCollection)collection)
            : ValidateAddressObject(root);

        if (condition is not null)
        {
            await DavXml.WriteErrorAsync(context, StatusCodes.Status403Forbidden, ns, condition).ConfigureAwait(false);
            return;
        }

        using var held = await this.LockAsync(target, context.RequestAborted).ConfigureAwait(false);

        var existing = await store.GetObjectAsync(request.Principal, target.Collection!, target.Name!, context.RequestAborted).ConfigureAwait(false);

        // If-None-Match: * is how a client says "create, do not replace" — two devices adding the
        // same new event at once must not both succeed. If-Match is the other half: replace only the
        // version I last saw. Either failing is 412, and the client re-fetches and merges.
        if (context.Request.Headers.GetFirst(HeaderNames.IfNoneMatch) is { } ifNoneMatch &&
            existing is not null &&
            ETagMatches(ifNoneMatch, existing.ETag))
        {
            await StatusAsync(context, StatusCodes.Status412PreconditionFailed).ConfigureAwait(false);
            return;
        }

        if (context.Request.Headers.GetFirst(HeaderNames.IfMatch) is { } ifMatch &&
            (existing is null || !ETagMatches(ifMatch, existing.ETag)))
        {
            await StatusAsync(context, StatusCodes.Status412PreconditionFailed).ConfigureAwait(false);
            return;
        }

        // One UID per collection: a second object claiming it is a second copy of the same event,
        // which every client would then show twice.
        var listing = await store.GetObjectsAsync(request.Principal, target.Collection!, context.RequestAborted).ConfigureAwait(false);

        foreach (var info in listing)
        {
            if (info.Name == target.Name)
                continue;

            if (!string.Equals(await this.UidOfAsync(request, target, info, context.RequestAborted).ConfigureAwait(false), uid, StringComparison.Ordinal))
                continue;

            var href = this.paths.Href(target.WithObject(info.Name));

            await DavXml.WriteErrorAsync(
                context,
                StatusCodes.Status403Forbidden,
                ns,
                "no-uid-conflict",
                w => DavXml.Text(w, Ns.Dav, "href", href)
            ).ConfigureAwait(false);

            return;
        }

        var etag = await store
            .PutObjectAsync(request.Principal, target.Collection!, new DavObjectWrite(target.Name!, text, uid!, root, existing is null), context.RequestAborted)
            .ConfigureAwait(false);

        if (etag is not null)
        {
            this.uids[(target.CollectionKey, target.Name!)] = (etag, uid);
            context.Response.Headers.Set(HeaderNames.ETag, etag);
        }
        else
        {
            this.uids.TryRemove((target.CollectionKey, target.Name!), out _);
        }

        await StatusAsync(context, existing is null ? StatusCodes.Status201Created : StatusCodes.Status204NoContent).ConfigureAwait(false);
    }

    /// <summary>
    /// A calendar object resource (RFC 4791 §4.1): a VCALENDAR with no METHOD, whose components —
    /// apart from VTIMEZONEs — are all one type and all share one UID, of a type the calendar holds.
    /// Returns the UID, or the precondition that failed.
    /// </summary>
    static (string? Uid, string? Condition) ValidateCalendarObject(ContentComponent root, CalendarCollection collection)
    {
        if (root.Name != "VCALENDAR")
            return (null, "valid-calendar-data");

        // A METHOD makes it an iTIP message (a scheduling request), which belongs in an inbox this
        // server does not have, not in a calendar.
        if (root.GetProperty("METHOD") is not null)
            return (null, "valid-calendar-object-resource");

        string? type = null;
        string? uid = null;

        foreach (var component in root.Components)
        {
            if (component.Name == "VTIMEZONE")
                continue;

            if (type is null)
                type = component.Name;
            else if (type != component.Name)
                return (null, "valid-calendar-object-resource");

            var componentUid = component.GetProperty("UID")?.Value.Trim();

            if (string.IsNullOrEmpty(componentUid))
                return (null, "valid-calendar-object-resource");

            if (uid is null)
                uid = componentUid;
            else if (uid != componentUid)
                return (null, "valid-calendar-object-resource");
        }

        if (type is null)
            return (null, "valid-calendar-object-resource");

        if (!collection.SupportedComponents.Contains(type, StringComparer.OrdinalIgnoreCase))
            return (null, "supported-calendar-component");

        return (uid, null);
    }

    /// <summary>
    /// An address object resource (RFC 6352 §5.1): exactly one VCARD, with a UID to keep it unique
    /// by.
    /// </summary>
    static (string? Uid, string? Condition) ValidateAddressObject(ContentComponent root)
    {
        if (root.Name != "VCARD")
            return (null, "valid-address-data");

        var uid = root.GetProperty("UID")?.Value.Trim();

        return string.IsNullOrEmpty(uid) ? (null, "valid-address-data") : (uid, null);
    }

    /// <summary>
    /// <c>text/calendar</c> for a calendar, <c>text/vcard</c> (or the older <c>text/x-vcard</c>)
    /// for an address book. A missing Content-Type is let through — the body is validated anyway,
    /// and refusing it would only break the odd client that leaves the header off.
    /// </summary>
    static bool IsAcceptableMediaType(string? contentType, bool isCalendar)
    {
        if (string.IsNullOrWhiteSpace(contentType))
            return true;

        var semicolon = contentType.IndexOf(';');
        var media = (semicolon < 0 ? contentType : contentType[..semicolon]).Trim();

        return isCalendar
            ? media.Equals("text/calendar", StringComparison.OrdinalIgnoreCase)
            : media.Equals("text/vcard", StringComparison.OrdinalIgnoreCase) || media.Equals("text/x-vcard", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// An object's UID: remembered against its ETag, else what the store reported, else read from
    /// the object itself.
    /// </summary>
    async ValueTask<string?> UidOfAsync(DavRequest request, DavTarget target, DavObjectInfo info, CancellationToken cancellationToken)
    {
        var key = (target.CollectionKey, info.Name);

        if (this.uids.TryGetValue(key, out var cached) && cached.ETag == info.ETag)
            return cached.Uid;

        var uid = info.Uid;

        if (uid is null)
        {
            var found = await this.StoreFor(target)!
                .GetObjectAsync(request.Principal, target.Collection!, info.Name, cancellationToken)
                .ConfigureAwait(false);

            if (found is not null && ContentComponent.TryParse(found.Data, out var parsed, out _))
            {
                uid = parsed.Name == "VCARD"
                    ? parsed.GetProperty("UID")?.Value.Trim()
                    : parsed.Components.FirstOrDefault(c => c.Name != "VTIMEZONE")?.GetProperty("UID")?.Value.Trim();
            }
        }

        this.uids[key] = (info.ETag, uid);
        return uid;
    }
}
