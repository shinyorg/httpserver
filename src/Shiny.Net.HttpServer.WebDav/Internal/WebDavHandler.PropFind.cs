using System.Globalization;
using System.Xml;

namespace Shiny.Net.HttpServer.WebDav.Internal;

/// <summary>One resource, reduced to the facts the property writer needs.</summary>
readonly record struct DavResource(
    string Relative,
    string Name,
    bool IsCollection,
    long Length,
    DateTimeOffset CreatedUtc,
    DateTimeOffset LastModifiedUtc,
    string? ContentType,
    string? ETag
);

/// <summary>Reading and writing properties: <c>PROPFIND</c> and <c>PROPPATCH</c>.</summary>
partial class WebDavHandler
{
    const string Dav = WebDavXml.Ns;

    /// <summary>
    /// The live properties this server volunteers under <c>allprop</c>.
    /// <para>
    /// Quota is deliberately not among them. RFC 4331 says so, and it is also the only one whose
    /// value costs a syscall against the file system rather than a field off a
    /// <see cref="FileSystemInfo"/> — a listing of a thousand files should not make a thousand of
    /// them.
    /// </para>
    /// </summary>
    static readonly string[] VolunteeredNames =
    [
        "resourcetype",
        "displayname",
        "getcontentlength",
        "getcontenttype",
        "getetag",
        "getlastmodified",
        "creationdate",
        "supportedlock",
        "lockdiscovery"
    ];

    // ---- PROPFIND ----

    public async ValueTask PropFindAsync(HttpContext context)
    {
        if (!this.TryResolve(RawPath(context), out var path))
        {
            await StatusAsync(context, StatusCodes.Status404NotFound).ConfigureAwait(false);
            return;
        }

        var isCollection = Directory.Exists(path.Full);

        if (!isCollection && !File.Exists(path.Full))
        {
            await StatusAsync(context, StatusCodes.Status404NotFound).ConfigureAwait(false);
            return;
        }

        if (!WebDavRequests.TryParseDepth(
                context.Request.Headers.GetFirst(WebDavHeaderNames.Depth),
                Depth.Infinity,
                out var depth
            ))
        {
            await StatusAsync(context, StatusCodes.Status400BadRequest).ConfigureAwait(false);
            return;
        }

        if (depth == Depth.Infinity && !this.options.AllowInfiniteDepth)
        {
            // RFC 4918 §9.1 defines this exact refusal, and clients know how to read it: ask again
            // with a depth you can bound. Answering 403 with no body would just look broken.
            await WebDavXml.WriteErrorAsync(
                context,
                StatusCodes.Status403Forbidden,
                "propfind-finite-depth",
                href: null,
                context.RequestAborted
            ).ConfigureAwait(false);

            return;
        }

        using var body = await WebDavXml
            .ReadBodyAsync(context, this.options.MaxXmlBodyBytes, context.RequestAborted)
            .ConfigureAwait(false);

        if (body is null)
        {
            await StatusAsync(context, StatusCodes.Status413PayloadTooLarge).ConfigureAwait(false);
            return;
        }

        if (!WebDavRequests.TryParsePropFind(body, out var request))
        {
            await StatusAsync(context, StatusCodes.Status400BadRequest).ConfigureAwait(false);
            return;
        }

        var resources = new List<DavResource>();

        if (!this.Collect(path, isCollection, depth, resources))
        {
            await StatusAsync(context, StatusCodes.Status507InsufficientStorage).ConfigureAwait(false);
            return;
        }

        // Dead properties are read up front: the writer below is synchronous, and the store is not.
        var dead = new Dictionary<string, IReadOnlyList<WebDavProperty>>(StringComparer.Ordinal);

        if (request.Kind != PropFindKind.Prop || request.Names.Any(n => !n.IsDav))
        {
            foreach (var resource in resources)
            {
                var held = await this.properties
                    .GetAsync(resource.Relative, context.RequestAborted)
                    .ConfigureAwait(false);

                if (held.Count > 0)
                    dead[resource.Relative] = held;
            }
        }

        await WebDavXml.WriteAsync(context, StatusCodes.Status207MultiStatus, writer =>
        {
            writer.WriteStartElement(WebDavXml.Prefix, "multistatus", Dav);

            foreach (var resource in resources)
            {
                dead.TryGetValue(resource.Relative, out var held);
                this.WriteResourceResponse(writer, resource, request, held);
            }

            writer.WriteEndElement();
        }, context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>
    /// Gathers the resources a <c>PROPFIND</c> covers. False once it passes
    /// <see cref="WebDavOptions.MaxPropFindResults"/>, which is the answer to a request that would
    /// otherwise walk a whole device into memory.
    /// </summary>
    bool Collect(DavPath path, bool isCollection, Depth depth, List<DavResource> resources)
    {
        resources.Add(this.Describe(
            path.Relative,
            isCollection ? new DirectoryInfo(path.Full) : (FileSystemInfo)new FileInfo(path.Full)
        ));

        if (depth == Depth.Zero || !isCollection)
            return true;

        return this.CollectMembers(path, depth == Depth.Infinity, resources);
    }

    bool CollectMembers(DavPath path, bool recurse, List<DavResource> resources)
    {
        foreach (var child in this.Children(path))
        {
            if (resources.Count >= this.options.MaxPropFindResults)
                return false;

            var relative = Join(path.Relative, child.Name);
            resources.Add(this.Describe(relative, child));

            if (!recurse || child is not DirectoryInfo)
                continue;

            // A link inside the root that points at one of its own ancestors is a cycle, and an
            // infinite-depth walk would follow it until the cap stopped it. Not descending into
            // reparse points ends that without a visited-set.
            if (child.Attributes.HasFlag(FileAttributes.ReparsePoint))
                continue;

            if (!this.CollectMembers(new DavPath(relative, child.FullName), recurse: true, resources))
                return false;
        }

        return true;
    }

    DavResource Describe(string relative, FileSystemInfo info)
    {
        var isCollection = info is DirectoryInfo;
        var file = info as FileInfo;

        return new DavResource(
            relative,
            relative.Length == 0 ? this.RootDisplayName : info.Name,
            isCollection,
            file?.Length ?? 0,
            info.CreationTimeUtc,
            info.LastWriteTimeUtc,
            isCollection ? null : this.ContentTypeFor(info.Name),
            file is null ? null : ETagFor(file)
        );
    }

    void WriteResourceResponse(
        XmlWriter writer,
        in DavResource resource,
        PropFindRequest request,
        IReadOnlyList<WebDavProperty>? dead
    )
    {
        writer.WriteStartElement(WebDavXml.Prefix, "response", Dav);
        writer.WriteElementString(WebDavXml.Prefix, "href", Dav, this.HrefFor(resource.Relative, resource.IsCollection));

        switch (request.Kind)
        {
            case PropFindKind.PropName:
                this.WriteNamesOnly(writer, resource, dead);
                break;

            case PropFindKind.Prop:
                this.WriteRequested(writer, resource, request.Names, dead);
                break;

            default:
                this.WriteEverything(writer, resource, request.Names, dead);
                break;
        }

        writer.WriteEndElement();
    }

    void WriteNamesOnly(XmlWriter writer, in DavResource resource, IReadOnlyList<WebDavProperty>? dead)
    {
        var captured = resource;

        this.WritePropStat(writer, StatusCodes.Status200OK, w =>
        {
            foreach (var name in VolunteeredNames)
            {
                if (this.Applies(name, captured))
                    w.WriteElementString(WebDavXml.Prefix, name, Dav, null);
            }

            if (dead is null)
                return;

            foreach (var property in dead)
                w.WriteElementString(null, property.Name.Name, property.Name.Namespace, null);
        });
    }

    void WriteEverything(
        XmlWriter writer,
        in DavResource resource,
        IReadOnlyList<WebDavPropertyName> include,
        IReadOnlyList<WebDavProperty>? dead
    )
    {
        var captured = resource;
        var extras = include;

        this.WritePropStat(writer, StatusCodes.Status200OK, w =>
        {
            foreach (var name in VolunteeredNames)
            {
                if (this.Applies(name, captured))
                    this.WriteLive(w, new WebDavPropertyName(Dav, name), captured);
            }

            // <DAV:include> is how a client asks for something allprop does not volunteer —
            // quota, in practice.
            foreach (var name in extras)
            {
                if (name.IsDav && Array.IndexOf(VolunteeredNames, name.Name) < 0)
                    this.WriteLive(w, name, captured);
            }

            if (dead is null)
                return;

            foreach (var property in dead)
                WriteDead(w, property);
        });
    }

    void WriteRequested(
        XmlWriter writer,
        in DavResource resource,
        IReadOnlyList<WebDavPropertyName> names,
        IReadOnlyList<WebDavProperty>? dead
    )
    {
        var found = new List<WebDavPropertyName>();
        var missing = new List<WebDavPropertyName>();
        var captured = resource;

        foreach (var name in names)
        {
            var exists = name.IsDav
                ? this.Applies(name.Name, captured) || this.IsQuota(name.Name, captured)
                : dead is not null && dead.Any(p => p.Name == name);

            (exists ? found : missing).Add(name);
        }

        if (found.Count > 0)
        {
            this.WritePropStat(writer, StatusCodes.Status200OK, w =>
            {
                foreach (var name in found)
                {
                    if (name.IsDav)
                        this.WriteLive(w, name, captured);
                    else
                        WriteDead(w, dead!.First(p => p.Name == name));
                }
            });
        }

        // Named but absent is a 404 against that property, not against the resource — a client
        // reading a mixed response needs to see which half it got.
        if (missing.Count > 0)
        {
            this.WritePropStat(writer, StatusCodes.Status404NotFound, w =>
            {
                foreach (var name in missing)
                    w.WriteElementString(null, name.Name, name.Namespace, null);
            });
        }
    }

    void WritePropStat(XmlWriter writer, int statusCode, Action<XmlWriter> writeProperties)
    {
        writer.WriteStartElement(WebDavXml.Prefix, "propstat", Dav);
        writer.WriteStartElement(WebDavXml.Prefix, "prop", Dav);

        writeProperties(writer);

        writer.WriteEndElement();
        writer.WriteElementString(WebDavXml.Prefix, "status", Dav, WebDavXml.StatusLine(statusCode));
        writer.WriteEndElement();
    }

    /// <summary>Whether a volunteered property means anything for this resource.</summary>
    bool Applies(string localName, in DavResource resource) => localName switch
    {
        "getcontentlength" or "getcontenttype" or "getetag" => !resource.IsCollection,
        "supportedlock" or "lockdiscovery" => this.options.EnableLocking,
        _ => Array.IndexOf(VolunteeredNames, localName) >= 0
    };

    bool IsQuota(string localName, in DavResource resource)
        => resource.IsCollection && localName is "quota-available-bytes" or "quota-used-bytes";

    void WriteLive(XmlWriter writer, WebDavPropertyName name, in DavResource resource)
    {
        switch (name.Name)
        {
            case "resourcetype":
                writer.WriteStartElement(WebDavXml.Prefix, "resourcetype", Dav);

                if (resource.IsCollection)
                    writer.WriteElementString(WebDavXml.Prefix, "collection", Dav, null);

                writer.WriteEndElement();
                break;

            case "displayname":
                writer.WriteElementString(WebDavXml.Prefix, "displayname", Dav, resource.Name);
                break;

            case "getcontentlength":
                writer.WriteElementString(
                    WebDavXml.Prefix,
                    "getcontentlength",
                    Dav,
                    resource.Length.ToString(CultureInfo.InvariantCulture)
                );
                break;

            case "getcontenttype":
                writer.WriteElementString(WebDavXml.Prefix, "getcontenttype", Dav, resource.ContentType);
                break;

            case "getetag":
                writer.WriteElementString(WebDavXml.Prefix, "getetag", Dav, resource.ETag);
                break;

            case "getlastmodified":
                // RFC 1123, not ISO 8601. getlastmodified is defined as an HTTP-date, and
                // creationdate right below it is not — a mismatch clients do notice.
                writer.WriteElementString(
                    WebDavXml.Prefix,
                    "getlastmodified",
                    Dav,
                    resource.LastModifiedUtc.ToString("R", CultureInfo.InvariantCulture)
                );
                break;

            case "creationdate":
                writer.WriteElementString(
                    WebDavXml.Prefix,
                    "creationdate",
                    Dav,
                    resource.CreatedUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)
                );
                break;

            case "supportedlock":
                WriteSupportedLock(writer);
                break;

            case "lockdiscovery":
                writer.WriteStartElement(WebDavXml.Prefix, "lockdiscovery", Dav);

                foreach (var held in this.locks.Discover(resource.Relative))
                    this.WriteActiveLock(writer, held);

                writer.WriteEndElement();
                break;

            case "quota-available-bytes":
            case "quota-used-bytes":
                this.WriteQuota(writer, name.Name);
                break;
        }
    }

    static void WriteSupportedLock(XmlWriter writer)
    {
        writer.WriteStartElement(WebDavXml.Prefix, "supportedlock", Dav);

        foreach (var scope in (ReadOnlySpan<string>)["exclusive", "shared"])
        {
            writer.WriteStartElement(WebDavXml.Prefix, "lockentry", Dav);

            writer.WriteStartElement(WebDavXml.Prefix, "lockscope", Dav);
            writer.WriteElementString(WebDavXml.Prefix, scope, Dav, null);
            writer.WriteEndElement();

            writer.WriteStartElement(WebDavXml.Prefix, "locktype", Dav);
            writer.WriteElementString(WebDavXml.Prefix, "write", Dav, null);
            writer.WriteEndElement();

            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    /// <summary>
    /// RFC 4331 quota, reported from the volume the mount lives on.
    /// <para>
    /// Not the size of the subtree, which is what the RFC's wording suggests and what walking it
    /// would cost. Clients ask for this to draw a "space free" figure, and the volume's is both the
    /// number they mean and the one that is true.
    /// </para>
    /// </summary>
    void WriteQuota(XmlWriter writer, string localName)
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(this.root) ?? this.root);

            var value = localName == "quota-available-bytes"
                ? drive.AvailableFreeSpace
                : drive.TotalSize - drive.AvailableFreeSpace;

            writer.WriteElementString(
                WebDavXml.Prefix,
                localName,
                Dav,
                value.ToString(CultureInfo.InvariantCulture)
            );
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // A volume that will not answer is not a failed request. The property is simply absent,
            // and a client that wanted a number falls back to not showing one.
        }
    }

    static void WriteDead(XmlWriter writer, WebDavProperty property)
    {
        writer.WriteStartElement(null, property.Name.Name, property.Name.Namespace);

        if (property.Xml.Length > 0)
            writer.WriteRaw(property.Xml);

        writer.WriteEndElement();
    }

    // ---- PROPPATCH ----

    public async ValueTask PropPatchAsync(HttpContext context)
    {
        if (!this.options.AllowWrite)
        {
            await this.NotAllowedAsync(context).ConfigureAwait(false);
            return;
        }

        if (!this.TryResolve(RawPath(context), out var path))
        {
            await StatusAsync(context, StatusCodes.Status403Forbidden).ConfigureAwait(false);
            return;
        }

        var isCollection = Directory.Exists(path.Full);

        if (!isCollection && !File.Exists(path.Full))
        {
            await StatusAsync(context, StatusCodes.Status404NotFound).ConfigureAwait(false);
            return;
        }

        if (await this.AuthorizeAsync(context, path, subtree: false).ConfigureAwait(false) is null)
            return;

        using var body = await WebDavXml
            .ReadBodyAsync(context, this.options.MaxXmlBodyBytes, context.RequestAborted)
            .ConfigureAwait(false);

        if (body is null)
        {
            await StatusAsync(context, StatusCodes.Status413PayloadTooLarge).ConfigureAwait(false);
            return;
        }

        if (!WebDavRequests.TryParsePropPatch(body, out var instructions))
        {
            await StatusAsync(context, StatusCodes.Status400BadRequest).ConfigureAwait(false);
            return;
        }

        // Two passes, because RFC 4918 §9.2 makes a PROPPATCH atomic: either every instruction is
        // applied or none is, and the ones that would have worked report 424 rather than 200.
        var statuses = new int[instructions.Count];
        var refused = false;

        for (var i = 0; i < instructions.Count; i++)
        {
            var protectedProperty = instructions[i].Name.IsDav;

            statuses[i] = protectedProperty ? StatusCodes.Status403Forbidden : StatusCodes.Status200OK;
            refused |= protectedProperty;
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
            foreach (var instruction in instructions)
            {
                if (instruction.IsRemove)
                {
                    await this.properties
                        .RemoveAsync(path.Relative, instruction.Name, context.RequestAborted)
                        .ConfigureAwait(false);
                }
                else
                {
                    await this.properties
                        .SetAsync(path.Relative, new WebDavProperty(instruction.Name, instruction.Xml!), context.RequestAborted)
                        .ConfigureAwait(false);
                }
            }
        }

        var href = this.HrefFor(path, isCollection);

        await WebDavXml.WriteAsync(context, StatusCodes.Status207MultiStatus, writer =>
        {
            writer.WriteStartElement(WebDavXml.Prefix, "multistatus", Dav);
            writer.WriteStartElement(WebDavXml.Prefix, "response", Dav);
            writer.WriteElementString(WebDavXml.Prefix, "href", Dav, href);

            // One propstat per distinct status, which is the shape RFC 4918 asks for and what a
            // client parses to find out which of its instructions survived.
            foreach (var status in statuses.Distinct())
            {
                var group = status;

                this.WritePropStat(writer, group, w =>
                {
                    for (var i = 0; i < instructions.Count; i++)
                    {
                        if (statuses[i] == group)
                            w.WriteElementString(null, instructions[i].Name.Name, instructions[i].Name.Namespace, null);
                    }
                });
            }

            writer.WriteEndElement();
            writer.WriteEndElement();
        }, context.RequestAborted).ConfigureAwait(false);
    }
}
