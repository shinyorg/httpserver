using System.Globalization;
using System.Text;
using System.Xml;
using Shiny.Net.HttpServer.WebDav;

namespace Shiny.Net.HttpServer.CalDav.Internal;

/// <summary>Describing resources: <c>PROPFIND</c>, <c>PROPPATCH</c>, and the property writer the reports share.</summary>
partial class CalDavHandler
{
    /// <summary>What <c>allprop</c> volunteers. The CalDAV and CardDAV properties are never in it.</summary>
    static readonly WebDavPropertyName[] Volunteered =
    [
        new(Ns.Dav, "resourcetype"),
        new(Ns.Dav, "displayname"),
        new(Ns.Dav, "getetag"),
        new(Ns.Dav, "getcontenttype"),
        new(Ns.Dav, "getcontentlength"),
        new(Ns.Dav, "getlastmodified")
    ];

    /// <summary>
    /// Every property this mount computes. A client cannot set one with <c>PROPPATCH</c> — except the
    /// few that <see cref="TryApplyToCollection"/> maps onto the store — and a <c>PROPFIND</c> for one
    /// that does not apply is a 404 rather than a dead-property lookup.
    /// </summary>
    static readonly HashSet<WebDavPropertyName> Computed =
    [
        .. Volunteered,
        new(Ns.Dav, "current-user-principal"),
        new(Ns.Dav, "principal-URL"),
        new(Ns.Dav, "principal-collection-set"),
        new(Ns.Dav, "owner"),
        new(Ns.Dav, "supported-report-set"),
        new(Ns.Dav, "current-user-privilege-set"),
        new(Ns.Dav, "sync-token"),
        new(Ns.Dav, "alternate-URI-set"),
        new(Ns.Dav, "group-membership"),
        new(Ns.Dav, "creationdate"),
        new(Ns.Dav, "lockdiscovery"),
        new(Ns.Dav, "supportedlock"),
        new(Ns.CalDav, "calendar-home-set"),
        new(Ns.CalDav, "calendar-user-address-set"),
        new(Ns.CalDav, "supported-calendar-component-set"),
        new(Ns.CalDav, "supported-calendar-data"),
        new(Ns.CalDav, "calendar-description"),
        new(Ns.CalDav, "calendar-timezone"),
        new(Ns.CalDav, "max-resource-size"),
        new(Ns.CalDav, "calendar-data"),
        new(Ns.CardDav, "addressbook-home-set"),
        new(Ns.CardDav, "addressbook-description"),
        new(Ns.CardDav, "supported-address-data"),
        new(Ns.CardDav, "max-resource-size"),
        new(Ns.CardDav, "address-data"),
        new(Ns.CalendarServer, "getctag"),
        new(Ns.AppleICal, "calendar-color")
    ];

    // ---- building nodes ----

    /// <summary>The node for a target, or null when nothing is there.</summary>
    async ValueTask<DavNode?> NodeAsync(DavRequest request, DavTarget target, CancellationToken cancellationToken)
    {
        var store = this.StoreFor(target);

        switch (target.Kind)
        {
            case TargetKind.Collection:
            {
                var collection = await store!.GetCollectionAsync(request.Principal, target.Collection!, cancellationToken).ConfigureAwait(false);

                return collection is null ? null : new DavNode(target, this.paths.Href(target)) { Collection = collection };
            }

            case TargetKind.Object:
            {
                var collection = await store!.GetCollectionAsync(request.Principal, target.Collection!, cancellationToken).ConfigureAwait(false);

                if (collection is null)
                    return null;

                var found = await store.GetObjectAsync(request.Principal, target.Collection!, target.Name!, cancellationToken).ConfigureAwait(false);

                return found is null ? null : ObjectNode(target, this.paths.Href(target), collection, found);
            }

            default:
                return new DavNode(target, this.paths.Href(target));
        }
    }

    static DavNode ObjectNode(DavTarget target, string href, DavCollection collection, DavObject found)
        => new(target, href)
        {
            Collection = collection,
            Info = new DavObjectInfo(found.Name, found.ETag, found.LastModifiedUtc) { Length = Encoding.UTF8.GetByteCount(found.Data) },
            Data = found.Data
        };

    /// <summary>The members of a node, one level down.</summary>
    async ValueTask<List<DavNode>> ChildrenAsync(DavRequest request, DavNode node, CancellationToken cancellationToken)
    {
        var target = node.Target;
        var children = new List<DavNode>();

        void Add(DavTarget child) => children.Add(new DavNode(child, this.paths.Href(child)));

        switch (target.Kind)
        {
            case TargetKind.Root:
                Add(target with { Kind = TargetKind.PrincipalsRoot });

                if (this.calendars is not null)
                    Add(new DavTarget(TargetKind.HomesRoot, Flavor.Calendar, null, null, null));

                if (this.addressBooks is not null)
                    Add(new DavTarget(TargetKind.HomesRoot, Flavor.AddressBook, null, null, null));

                break;

            case TargetKind.PrincipalsRoot:
                Add(DavTarget.PrincipalOf(request.Principal));
                break;

            case TargetKind.HomesRoot:
                Add(DavTarget.HomeOf(target.Flavor, request.Principal));
                break;

            case TargetKind.Home:
            {
                var collections = await this.StoreFor(target)!.GetCollectionsAsync(request.Principal, cancellationToken).ConfigureAwait(false);

                foreach (var collection in collections)
                {
                    var child = target.WithCollection(collection.Id);
                    children.Add(new DavNode(child, this.paths.Href(child)) { Collection = collection });
                }

                break;
            }

            case TargetKind.Collection:
            {
                node.Listing ??= await this.StoreFor(target)!
                    .GetObjectsAsync(request.Principal, target.Collection!, cancellationToken)
                    .ConfigureAwait(false);

                foreach (var info in node.Listing.OrderBy(i => i.Name, StringComparer.Ordinal))
                {
                    var child = target.WithObject(info.Name);
                    children.Add(new DavNode(child, this.paths.Href(child)) { Collection = node.Collection, Info = info });
                }

                break;
            }
        }

        return children;
    }

    /// <summary>
    /// Loads what the properties about to be written need and the writer cannot fetch itself, since
    /// it is synchronous: a collection's listing for its sync token, an object's text, dead
    /// properties.
    /// </summary>
    async ValueTask PrepareAsync(DavRequest request, IReadOnlyList<DavNode> nodes, PropRequest props, CancellationToken cancellationToken)
    {
        var wantsToken = props.Wants(Ns.Dav, "sync-token") || props.Wants(Ns.CalendarServer, "getctag");
        var wantsData = props.Wants(Ns.CalDav, "calendar-data") || props.Wants(Ns.CardDav, "address-data");
        var wantsDead = props.Kind != PropKind.Prop || props.Names.Any(n => !Computed.Contains(n));

        foreach (var node in nodes)
        {
            var target = node.Target;

            if (wantsToken && target.Kind == TargetKind.Collection)
                await this.EnsureTokenAsync(request, node, cancellationToken).ConfigureAwait(false);

            if (wantsData && target.Kind == TargetKind.Object && node.Data is null)
            {
                var found = await this.StoreFor(target)!
                    .GetObjectAsync(request.Principal, target.Collection!, target.Name!, cancellationToken)
                    .ConfigureAwait(false);

                node.Data = found?.Data;
            }

            if (wantsDead)
                node.Dead = await this.properties.GetAsync(target.PropertyKey, cancellationToken).ConfigureAwait(false);
        }
    }

    async ValueTask EnsureTokenAsync(DavRequest request, DavNode node, CancellationToken cancellationToken)
    {
        var target = node.Target;

        node.Listing ??= await this.StoreFor(target)!
            .GetObjectsAsync(request.Principal, target.Collection!, cancellationToken)
            .ConfigureAwait(false);

        // Registered as it is handed out: a token a client holds is one it can sync from.
        node.SyncToken = this.sync.Register(target.CollectionKey, node.Listing);
        node.CTag = node.Collection?.CTag ?? SyncTracker.Hash(target.CollectionKey, node.Listing);
    }

    // ---- PROPFIND ----

    public async ValueTask PropFindAsync(HttpContext context)
    {
        if (await this.BeginAsync(context).ConfigureAwait(false) is not { } request)
            return;

        if (!DavRequests.TryParseDepth(context.Request.Headers.GetFirst(WebDavHeaderNames.Depth), int.MaxValue, out var depth))
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

        if (!DavRequests.TryParsePropFind(body, out var props))
        {
            await StatusAsync(context, StatusCodes.Status400BadRequest).ConfigureAwait(false);
            return;
        }

        if (await this.NodeAsync(request, request.Target, context.RequestAborted).ConfigureAwait(false) is not { } root)
        {
            await StatusAsync(context, StatusCodes.Status404NotFound).ConfigureAwait(false);
            return;
        }

        // The layout is four levels deep at most, so even Depth: infinity is bounded — by the size
        // of the principal's collections, which is theirs to have.
        var nodes = new List<DavNode> { root };
        var level = new List<DavNode> { root };

        for (var d = 0; d < depth && level.Count > 0; d++)
        {
            var next = new List<DavNode>();

            foreach (var node in level)
                next.AddRange(await this.ChildrenAsync(request, node, context.RequestAborted).ConfigureAwait(false));

            nodes.AddRange(next);
            level = next;
        }

        await this.PrepareAsync(request, nodes, props, context.RequestAborted).ConfigureAwait(false);

        await DavXml.WriteAsync(context, StatusCodes.Status207MultiStatus, writer =>
        {
            DavXml.StartRoot(writer, Ns.Dav, "multistatus");

            foreach (var node in nodes)
                this.WriteResponse(writer, request, node, props);

            writer.WriteEndElement();
        }).ConfigureAwait(false);
    }

    // ---- the property writer ----

    /// <summary>One <c>&lt;D:response&gt;</c>: the href, then a propstat per status.</summary>
    void WriteResponse(XmlWriter writer, DavRequest request, DavNode node, PropRequest props)
    {
        DavXml.Element(writer, Ns.Dav, "response");
        DavXml.Text(writer, Ns.Dav, "href", node.Href);

        switch (props.Kind)
        {
            case PropKind.PropName:
            {
                var names = Volunteered.Where(n => this.Live(request, node, n) is not null).ToList();

                if (node.Dead is { } dead)
                    names.AddRange(dead.Select(p => p.Name));

                WritePropStat(writer, StatusCodes.Status200OK, w =>
                {
                    foreach (var name in names)
                        WriteEmptyName(w, name);
                });

                break;
            }

            case PropKind.AllProp:
            {
                var found = new List<Action<XmlWriter>>();

                foreach (var name in Volunteered.Concat(props.Names).Distinct())
                {
                    if (this.Live(request, node, name) is { } write)
                        found.Add(write);
                }

                if (node.Dead is { } dead)
                {
                    foreach (var property in dead)
                        found.Add(w => WriteDead(w, property));
                }

                WritePropStat(writer, StatusCodes.Status200OK, w =>
                {
                    foreach (var write in found)
                        write(w);
                });

                break;
            }

            default:
            {
                var found = new List<Action<XmlWriter>>();
                var missing = new List<WebDavPropertyName>();

                foreach (var name in props.Names)
                {
                    if (this.Live(request, node, name) is { } write)
                    {
                        found.Add(write);
                    }
                    else if (!Computed.Contains(name) && node.Dead?.FirstOrDefault(p => p.Name == name) is { } dead)
                    {
                        found.Add(w => WriteDead(w, dead));
                    }
                    else
                    {
                        missing.Add(name);
                    }
                }

                if (found.Count > 0)
                {
                    WritePropStat(writer, StatusCodes.Status200OK, w =>
                    {
                        foreach (var write in found)
                            write(w);
                    });
                }

                // Named but absent is a 404 against that property, not the resource. iOS asks every
                // principal for a dozen Apple properties this server has no reason to have.
                if (missing.Count > 0)
                {
                    WritePropStat(writer, StatusCodes.Status404NotFound, w =>
                    {
                        foreach (var name in missing)
                            WriteEmptyName(w, name);
                    });
                }

                break;
            }
        }

        writer.WriteEndElement();
    }

    static void WritePropStat(XmlWriter writer, int statusCode, Action<XmlWriter> writeProperties)
    {
        DavXml.Element(writer, Ns.Dav, "propstat");
        DavXml.Element(writer, Ns.Dav, "prop");

        writeProperties(writer);

        writer.WriteEndElement();
        DavXml.Text(writer, Ns.Dav, "status", DavXml.StatusLine(statusCode));
        writer.WriteEndElement();
    }

    static void WriteEmptyName(XmlWriter writer, WebDavPropertyName name)
    {
        if (Ns.PrefixOf(name.Namespace) is { } prefix)
            writer.WriteElementString(prefix, name.Name, name.Namespace, null);
        else
            writer.WriteElementString(name.Name, name.Namespace, null);
    }

    static void WriteDead(XmlWriter writer, WebDavProperty property)
    {
        writer.WriteStartElement(null, property.Name.Name, property.Name.Namespace);

        if (property.Xml.Length > 0)
            writer.WriteRaw(property.Xml);

        writer.WriteEndElement();
    }

    /// <summary>
    /// The writer for a live property on a node, or null when the property does not apply to it.
    /// </summary>
    Action<XmlWriter>? Live(DavRequest request, DavNode node, WebDavPropertyName name)
    {
        var target = node.Target;
        var kind = target.Kind;
        var isCalendar = target.Flavor == Flavor.Calendar;
        var principalHref = this.paths.Href(DavTarget.PrincipalOf(request.Principal));

        switch (name.Namespace)
        {
            case Ns.Dav:
                switch (name.Name)
                {
                    case "resourcetype":
                        return w => this.WriteResourceType(w, target);

                    case "displayname":
                        return this.DisplayNameOf(request, node) is { } display
                            ? w => DavXml.Text(w, Ns.Dav, "displayname", display)
                            : null;

                    case "getetag" when kind == TargetKind.Object:
                        return w => DavXml.Text(w, Ns.Dav, "getetag", node.Info!.ETag);

                    case "getcontenttype" when kind == TargetKind.Object:
                        return w => DavXml.Text(w, Ns.Dav, "getcontenttype", ContentTypeOf(target.Flavor));

                    case "getcontentlength" when kind == TargetKind.Object && node.Info!.Length is { } length:
                        return w => DavXml.Text(w, Ns.Dav, "getcontentlength", length.ToString(CultureInfo.InvariantCulture));

                    case "getlastmodified" when kind == TargetKind.Object:
                        return w => DavXml.Text(w, Ns.Dav, "getlastmodified", node.Info!.LastModifiedUtc.ToString("R", CultureInfo.InvariantCulture));

                    // RFC 5397. Answered everywhere, because a client asks for it wherever it happens
                    // to start — the context path the well-known redirect named, usually.
                    case "current-user-principal":
                        return w => DavXml.HrefProperty(w, Ns.Dav, "current-user-principal", principalHref);

                    case "principal-URL" when kind == TargetKind.Principal:
                        return w => DavXml.HrefProperty(w, Ns.Dav, "principal-URL", principalHref);

                    case "principal-collection-set":
                        return w => DavXml.HrefProperty(
                            w,
                            Ns.Dav,
                            "principal-collection-set",
                            this.paths.Href(new DavTarget(TargetKind.PrincipalsRoot, Flavor.None, null, null, null))
                        );

                    case "owner" when kind is TargetKind.Home or TargetKind.Collection or TargetKind.Object:
                        return w => DavXml.HrefProperty(w, Ns.Dav, "owner", principalHref);

                    case "alternate-URI-set" when kind == TargetKind.Principal:
                    case "group-membership" when kind == TargetKind.Principal:
                        return w => DavXml.Empty(w, Ns.Dav, name.Name);

                    case "supported-report-set":
                        return w => WriteSupportedReports(w, target);

                    case "current-user-privilege-set":
                        return w => this.WritePrivileges(w, node);

                    case "sync-token" when kind == TargetKind.Collection && node.SyncToken is { } token:
                        return w => DavXml.Text(w, Ns.Dav, "sync-token", token);
                }

                return null;

            case Ns.CalDav:
                switch (name.Name)
                {
                    case "calendar-home-set" when kind == TargetKind.Principal && this.calendars is not null:
                        return w => DavXml.HrefProperty(w, Ns.CalDav, "calendar-home-set", this.paths.Href(DavTarget.HomeOf(Flavor.Calendar, request.Principal)));

                    case "calendar-user-address-set" when kind == TargetKind.Principal && this.options.CalendarUserAddresses is { } addresses:
                    {
                        var list = addresses(request.Principal);

                        return w =>
                        {
                            DavXml.Element(w, Ns.CalDav, "calendar-user-address-set");

                            foreach (var address in list)
                                DavXml.Text(w, Ns.Dav, "href", address);

                            w.WriteEndElement();
                        };
                    }

                    case "supported-calendar-component-set" when kind == TargetKind.Collection && node.Collection is CalendarCollection calendar:
                        return w =>
                        {
                            DavXml.Element(w, Ns.CalDav, "supported-calendar-component-set");

                            foreach (var component in calendar.SupportedComponents)
                            {
                                DavXml.Element(w, Ns.CalDav, "comp");
                                w.WriteAttributeString("name", component);
                                w.WriteEndElement();
                            }

                            w.WriteEndElement();
                        };

                    case "supported-calendar-data" when kind == TargetKind.Collection && isCalendar:
                        return w =>
                        {
                            DavXml.Element(w, Ns.CalDav, "supported-calendar-data");
                            DavXml.Element(w, Ns.CalDav, "calendar-data");
                            w.WriteAttributeString("content-type", "text/calendar");
                            w.WriteAttributeString("version", "2.0");
                            w.WriteEndElement();
                            w.WriteEndElement();
                        };

                    case "calendar-description" when kind == TargetKind.Collection && isCalendar && node.Collection!.Description is { } description:
                        return w => DavXml.Text(w, Ns.CalDav, "calendar-description", description);

                    case "calendar-timezone" when kind == TargetKind.Collection && node.Collection is CalendarCollection { TimeZone: { } zone }:
                        return w => DavXml.Text(w, Ns.CalDav, "calendar-timezone", zone);

                    case "max-resource-size" when kind == TargetKind.Collection && isCalendar:
                        return w => DavXml.Text(w, Ns.CalDav, "max-resource-size", this.options.MaxResourceSize.ToString(CultureInfo.InvariantCulture));

                    case "calendar-data" when kind == TargetKind.Object && isCalendar && node.Data is { } data:
                        return w => DavXml.Text(w, Ns.CalDav, "calendar-data", data);
                }

                return null;

            case Ns.CardDav:
                switch (name.Name)
                {
                    case "addressbook-home-set" when kind == TargetKind.Principal && this.addressBooks is not null:
                        return w => DavXml.HrefProperty(w, Ns.CardDav, "addressbook-home-set", this.paths.Href(DavTarget.HomeOf(Flavor.AddressBook, request.Principal)));

                    case "addressbook-description" when kind == TargetKind.Collection && !isCalendar && node.Collection!.Description is { } description:
                        return w => DavXml.Text(w, Ns.CardDav, "addressbook-description", description);

                    case "supported-address-data" when kind == TargetKind.Collection && !isCalendar:
                        return w =>
                        {
                            DavXml.Element(w, Ns.CardDav, "supported-address-data");

                            foreach (var version in (ReadOnlySpan<string>)["3.0", "4.0"])
                            {
                                DavXml.Element(w, Ns.CardDav, "address-data-type");
                                w.WriteAttributeString("content-type", "text/vcard");
                                w.WriteAttributeString("version", version);
                                w.WriteEndElement();
                            }

                            w.WriteEndElement();
                        };

                    case "max-resource-size" when kind == TargetKind.Collection && !isCalendar:
                        return w => DavXml.Text(w, Ns.CardDav, "max-resource-size", this.options.MaxResourceSize.ToString(CultureInfo.InvariantCulture));

                    case "address-data" when kind == TargetKind.Object && !isCalendar && node.Data is { } data:
                        return w => DavXml.Text(w, Ns.CardDav, "address-data", data);
                }

                return null;

            case Ns.CalendarServer when name.Name == "getctag" && kind == TargetKind.Collection && node.CTag is { } ctag:
                return w => DavXml.Text(w, Ns.CalendarServer, "getctag", ctag);

            case Ns.AppleICal when name.Name == "calendar-color" && node.Collection is CalendarCollection { Color: { } color } && kind == TargetKind.Collection:
                return w => DavXml.Text(w, Ns.AppleICal, "calendar-color", color);
        }

        return null;
    }

    string? DisplayNameOf(DavRequest request, DavNode node) => node.Target.Kind switch
    {
        TargetKind.Principal => request.Principal,
        TargetKind.Home => node.Target.Flavor == Flavor.Calendar ? "Calendars" : "Address Books",
        TargetKind.Collection => node.Collection!.DisplayName ?? node.Collection.Id,
        _ => null
    };

    void WriteResourceType(XmlWriter writer, DavTarget target)
    {
        DavXml.Element(writer, Ns.Dav, "resourcetype");

        if (target.IsCollection)
            DavXml.Empty(writer, Ns.Dav, "collection");

        if (target.Kind == TargetKind.Principal)
            DavXml.Empty(writer, Ns.Dav, "principal");

        if (target.Kind == TargetKind.Collection)
        {
            if (target.Flavor == Flavor.Calendar)
                DavXml.Empty(writer, Ns.CalDav, "calendar");
            else
                DavXml.Empty(writer, Ns.CardDav, "addressbook");
        }

        writer.WriteEndElement();
    }

    static void WriteSupportedReports(XmlWriter writer, DavTarget target)
    {
        DavXml.Element(writer, Ns.Dav, "supported-report-set");

        void Report(string ns, string name)
        {
            DavXml.Element(writer, Ns.Dav, "supported-report");
            DavXml.Element(writer, Ns.Dav, "report");
            DavXml.Empty(writer, ns, name);
            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        if (target.Kind is TargetKind.Collection or TargetKind.Object)
        {
            if (target.Flavor == Flavor.Calendar)
            {
                Report(Ns.CalDav, "calendar-query");
                Report(Ns.CalDav, "calendar-multiget");

                if (target.Kind == TargetKind.Collection)
                    Report(Ns.CalDav, "free-busy-query");
            }
            else
            {
                Report(Ns.CardDav, "addressbook-query");
                Report(Ns.CardDav, "addressbook-multiget");
            }

            if (target.Kind == TargetKind.Collection)
                Report(Ns.Dav, "sync-collection");
        }

        writer.WriteEndElement();
    }

    /// <summary>
    /// RFC 3744's <c>current-user-privilege-set</c>, without the rest of ACL. iOS reads it to decide
    /// whether a calendar is editable — a read-only calendar that did not say so would let a user
    /// make a change that could only fail.
    /// </summary>
    void WritePrivileges(XmlWriter writer, DavNode node)
    {
        var kind = node.Target.Kind;
        var writable = kind switch
        {
            TargetKind.Home => !this.options.ReadOnly,
            TargetKind.Collection or TargetKind.Object => this.IsWritable(node.Collection),
            _ => false
        };

        DavXml.Element(writer, Ns.Dav, "current-user-privilege-set");

        void Privilege(string privilege)
        {
            DavXml.Element(writer, Ns.Dav, "privilege");
            DavXml.Empty(writer, Ns.Dav, privilege);
            writer.WriteEndElement();
        }

        Privilege("read");
        Privilege("read-current-user-privilege-set");

        if (writable)
        {
            Privilege("write");
            Privilege("write-properties");
            Privilege("write-content");
            Privilege("bind");
            Privilege("unbind");
        }

        writer.WriteEndElement();
    }

    // ---- PROPPATCH ----

    public async ValueTask PropPatchAsync(HttpContext context)
    {
        if (await this.BeginAsync(context).ConfigureAwait(false) is not { } request)
            return;

        var target = request.Target;

        if (await this.NodeAsync(request, target, context.RequestAborted).ConfigureAwait(false) is not { } node)
        {
            await StatusAsync(context, StatusCodes.Status404NotFound).ConfigureAwait(false);
            return;
        }

        var writable = target.Kind is TargetKind.Collection or TargetKind.Object
            ? this.IsWritable(node.Collection)
            : !this.options.ReadOnly;

        if (!writable)
        {
            await WriteNeedPrivilegesAsync(context).ConfigureAwait(false);
            return;
        }

        await using var body = await DavXml.ReadBodyAsync(context, this.options.MaxXmlBodyBytes).ConfigureAwait(false);

        if (body is null)
        {
            await StatusAsync(context, StatusCodes.Status413PayloadTooLarge).ConfigureAwait(false);
            return;
        }

        if (!DavRequests.TryParseUpdates(body, Ns.Dav, "propertyupdate", out var updates))
        {
            await StatusAsync(context, StatusCodes.Status400BadRequest).ConfigureAwait(false);
            return;
        }

        using var held = target.Kind is TargetKind.Collection or TargetKind.Object
            ? await this.LockAsync(target, context.RequestAborted).ConfigureAwait(false)
            : null;

        // RFC 4918 §9.2: all or nothing. Every instruction is judged before any is applied, and the
        // ones that would have worked report 424 when another did not.
        var collection = node.Collection;
        var statuses = new int[updates.Count];
        var refused = false;

        for (var i = 0; i < updates.Count; i++)
        {
            var update = updates[i];

            if (target.Kind == TargetKind.Collection && TryApplyToCollection(collection!, update, out var changed))
            {
                collection = changed;
                statuses[i] = StatusCodes.Status200OK;
                continue;
            }

            // A display name on anything but a collection is kept as a dead property — iOS names
            // its principal that way — and every other DAV: property is the server's own.
            var isDisplayName = update.Name == new WebDavPropertyName(Ns.Dav, "displayname");
            var isProtected = !isDisplayName && (Computed.Contains(update.Name) || update.Name.IsDav);

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
        }
        else
        {
            if (collection is not null && !ReferenceEquals(collection, node.Collection))
            {
                await this.StoreFor(target)!
                    .UpdateCollectionAsync(request.Principal, collection, context.RequestAborted)
                    .ConfigureAwait(false);
            }

            foreach (var update in updates)
            {
                if (target.Kind == TargetKind.Collection && TryApplyToCollection(collection!, update, out _))
                    continue;

                if (update.IsRemove)
                {
                    await this.properties.RemoveAsync(target.PropertyKey, update.Name, context.RequestAborted).ConfigureAwait(false);
                }
                else
                {
                    await this.properties
                        .SetAsync(target.PropertyKey, new WebDavProperty(update.Name, update.Xml!), context.RequestAborted)
                        .ConfigureAwait(false);
                }
            }
        }

        await DavXml.WriteAsync(context, StatusCodes.Status207MultiStatus, writer =>
        {
            DavXml.StartRoot(writer, Ns.Dav, "multistatus");
            DavXml.Element(writer, Ns.Dav, "response");
            DavXml.Text(writer, Ns.Dav, "href", node.Href);

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
            writer.WriteEndElement();
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// The collection properties that belong in the store rather than the dead-property store —
    /// the ones an app showing its own calendar list would want to see a client's change to.
    /// True, with the updated collection, when <paramref name="update"/> is one of them.
    /// </summary>
    static bool TryApplyToCollection(DavCollection collection, PropertyUpdate update, out DavCollection changed)
    {
        changed = collection;
        var text = update.Xml is null ? null : DavRequests.InnerText(update.Xml);
        var name = update.Name;

        if (name == new WebDavPropertyName(Ns.Dav, "displayname"))
        {
            changed = collection with { DisplayName = string.IsNullOrEmpty(text) ? null : text };
            return true;
        }

        if (name == new WebDavPropertyName(Ns.CalDav, "calendar-description") && collection is CalendarCollection ||
            name == new WebDavPropertyName(Ns.CardDav, "addressbook-description") && collection is AddressBookCollection)
        {
            changed = collection with { Description = string.IsNullOrEmpty(text) ? null : text };
            return true;
        }

        if (collection is CalendarCollection calendar)
        {
            if (name == new WebDavPropertyName(Ns.AppleICal, "calendar-color"))
            {
                changed = calendar with { Color = string.IsNullOrWhiteSpace(text) ? null : text.Trim() };
                return true;
            }

            if (name == new WebDavPropertyName(Ns.CalDav, "calendar-timezone"))
            {
                changed = calendar with { TimeZone = string.IsNullOrWhiteSpace(text) ? null : text };
                return true;
            }
        }

        return false;
    }
}
