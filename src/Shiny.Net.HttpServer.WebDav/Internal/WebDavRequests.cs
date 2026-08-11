using System.Globalization;
using System.Xml;

namespace Shiny.Net.HttpServer.WebDav.Internal;

/// <summary>How far into a collection an operation reaches.</summary>
enum Depth
{
    Zero,
    One,
    Infinity
}

/// <summary>Which properties a <c>PROPFIND</c> asked for.</summary>
enum PropFindKind
{
    /// <summary>Everything the server volunteers, plus anything in <c>&lt;DAV:include&gt;</c>.</summary>
    AllProp,

    /// <summary>The names only, with no values.</summary>
    PropName,

    /// <summary>Exactly the listed names.</summary>
    Prop
}

sealed class PropFindRequest
{
    public PropFindKind Kind { get; set; } = PropFindKind.AllProp;

    /// <summary>The names asked for, or the <c>&lt;DAV:include&gt;</c> extras under allprop.</summary>
    public List<WebDavPropertyName> Names { get; } = [];
}

/// <summary>One <c>&lt;DAV:set&gt;</c> or <c>&lt;DAV:remove&gt;</c> from a <c>PROPPATCH</c>.</summary>
/// <param name="Name">The property being changed.</param>
/// <param name="Xml">Its new inner XML, or null when this is a removal.</param>
sealed record PropPatchInstruction(WebDavPropertyName Name, string? Xml)
{
    public bool IsRemove => this.Xml is null;
}

sealed class LockInfoRequest
{
    public WebDavLockScope Scope { get; set; } = WebDavLockScope.Exclusive;

    /// <summary>The raw XML inside <c>&lt;DAV:owner&gt;</c>. Opaque, and handed straight back.</summary>
    public string? Owner { get; set; }
}

/// <summary>Parsing the bodies and headers RFC 4918 defines.</summary>
static class WebDavRequests
{
    /// <summary>
    /// Reads the <c>Depth</c> header. RFC 4918 makes a missing one mean infinity, which is worth
    /// knowing when a mount refuses that: the client gets a 403 telling it to ask again with a
    /// depth it can bound, rather than a silent reinterpretation of what it meant.
    /// </summary>
    public static bool TryParseDepth(string? header, Depth fallback, out Depth depth)
    {
        depth = fallback;

        if (header is null)
            return true;

        var value = header.Trim();

        switch (value.Length)
        {
            case 0:
                return true;

            case 1 when value[0] == '0':
                depth = Depth.Zero;
                return true;

            case 1 when value[0] == '1':
                depth = Depth.One;
                return true;
        }

        if (value.Equals("infinity", StringComparison.OrdinalIgnoreCase))
        {
            depth = Depth.Infinity;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Reads the <c>Overwrite</c> header. <c>T</c> replaces an existing destination, <c>F</c> fails
    /// instead; absent means <c>T</c>.
    /// </summary>
    public static bool TryParseOverwrite(string? header, out bool overwrite)
    {
        overwrite = true;

        if (header is null)
            return true;

        var value = header.Trim();

        if (value.Equals("T", StringComparison.OrdinalIgnoreCase))
            return true;

        if (value.Equals("F", StringComparison.OrdinalIgnoreCase))
        {
            overwrite = false;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Reads the <c>Timeout</c> header — <c>Second-600</c>, <c>Infinite</c>, or a comma-separated
    /// list of preferences, best first. Null when the client expressed none.
    /// </summary>
    public static TimeSpan? ParseTimeout(string? header)
    {
        if (header is null)
            return null;

        foreach (var candidate in header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Infinite is a request, not a promise. It comes back clamped to MaxLockTimeout, which
            // is the whole reason a server gets to choose.
            if (candidate.Equals("Infinite", StringComparison.OrdinalIgnoreCase))
                return TimeSpan.MaxValue;

            if (!candidate.StartsWith("Second-", StringComparison.OrdinalIgnoreCase))
                continue;

            if (long.TryParse(candidate.AsSpan(7), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) &&
                seconds is > 0 and < 100_000_000)
                return TimeSpan.FromSeconds(seconds);
        }

        return null;
    }

    /// <summary>Strips the angle brackets a <c>Lock-Token</c> header wraps its token in.</summary>
    public static string? ParseLockToken(string? header)
    {
        if (header is null)
            return null;

        var value = header.Trim();

        if (value.Length >= 2 && value[0] == '<' && value[^1] == '>')
            value = value[1..^1];

        return value.Length == 0 ? null : value;
    }

    /// <summary>
    /// Reads a <c>PROPFIND</c> body. An absent or empty one means allprop, which is what a client
    /// that just wants a listing sends.
    /// </summary>
    public static bool TryParsePropFind(Stream body, out PropFindRequest request)
    {
        var parsed = new PropFindRequest();
        request = parsed;

        if (body.Length == 0)
            return true;

        try
        {
            using var reader = XmlReader.Create(body, WebDavXml.ReaderSettings);

            if (!MoveToRoot(reader, "propfind"))
                return false;

            ForEachChild(reader, child =>
            {
                if (WebDavXml.IsDav(child, "allprop"))
                {
                    parsed.Kind = PropFindKind.AllProp;
                }
                else if (WebDavXml.IsDav(child, "propname"))
                {
                    parsed.Kind = PropFindKind.PropName;
                }
                else if (WebDavXml.IsDav(child, "prop"))
                {
                    parsed.Kind = PropFindKind.Prop;
                    ReadNames(child, parsed.Names);
                }
                else if (WebDavXml.IsDav(child, "include"))
                {
                    // <include> only adds to allprop, and nothing guarantees it arrives after the
                    // <allprop> it belongs to, so the kind is left alone here.
                    ReadNames(child, parsed.Names);
                }

                return false;
            });

            return true;
        }
        catch (XmlException)
        {
            return false;
        }
    }

    /// <summary>Reads a <c>PROPPATCH</c> body, keeping the instructions in document order.</summary>
    public static bool TryParsePropPatch(Stream body, out List<PropPatchInstruction> instructions)
    {
        var parsed = new List<PropPatchInstruction>();
        instructions = parsed;

        if (body.Length == 0)
            return false;

        try
        {
            using var reader = XmlReader.Create(body, WebDavXml.ReaderSettings);

            if (!MoveToRoot(reader, "propertyupdate"))
                return false;

            ForEachChild(reader, action =>
            {
                var isRemove = WebDavXml.IsDav(action, "remove");

                if (!isRemove && !WebDavXml.IsDav(action, "set"))
                    return false;

                ForEachChild(action, container =>
                {
                    if (!WebDavXml.IsDav(container, "prop"))
                        return false;

                    ForEachChild(container, property =>
                    {
                        // The name has to come out before anything moves the reader: ReadInnerXml
                        // advances past the element, and asking afterwards gives the next one.
                        var name = WebDavXml.NameOf(property);

                        if (property.IsEmptyElement)
                        {
                            parsed.Add(new PropPatchInstruction(name, isRemove ? null : string.Empty));
                            return false;
                        }

                        if (isRemove)
                        {
                            parsed.Add(new PropPatchInstruction(name, null));
                            property.Skip();
                        }
                        else
                        {
                            parsed.Add(new PropPatchInstruction(name, property.ReadInnerXml()));
                        }

                        return true;
                    });

                    return false;
                });

                return false;
            });

            return true;
        }
        catch (XmlException)
        {
            instructions = [];
            return false;
        }
    }

    /// <summary>
    /// Reads a <c>LOCK</c> body. An empty one is not malformed — it is how a client asks to refresh
    /// a lock it already holds, so this reports success with a null request.
    /// </summary>
    public static bool TryParseLockInfo(Stream body, out LockInfoRequest? request)
    {
        request = null;

        if (body.Length == 0)
            return true;

        try
        {
            using var reader = XmlReader.Create(body, WebDavXml.ReaderSettings);

            if (!MoveToRoot(reader, "lockinfo"))
                return false;

            var parsed = new LockInfoRequest();

            ForEachChild(reader, child =>
            {
                if (WebDavXml.IsDav(child, "lockscope"))
                {
                    parsed.Scope = ReadScope(child);
                    return false;
                }

                if (WebDavXml.IsDav(child, "owner"))
                {
                    if (child.IsEmptyElement)
                        return false;

                    parsed.Owner = child.ReadInnerXml();
                    return true;
                }

                // <write> is the only lock type RFC 4918 defines and the only one supportedlock
                // advertises, so there is nothing to decide about locktype.
                return false;
            });

            request = parsed;
            return true;
        }
        catch (XmlException)
        {
            return false;
        }
    }

    static WebDavLockScope ReadScope(XmlReader reader)
    {
        var scope = WebDavLockScope.Exclusive;

        ForEachChild(reader, child =>
        {
            if (WebDavXml.IsDav(child, "shared"))
                scope = WebDavLockScope.Shared;

            return false;
        });

        return scope;
    }

    static void ReadNames(XmlReader reader, List<WebDavPropertyName> names)
        => ForEachChild(reader, child =>
        {
            var name = WebDavXml.NameOf(child);

            if (!names.Contains(name))
                names.Add(name);

            return false;
        });

    /// <summary>
    /// Walks the element children of the node the reader is on.
    /// <para>
    /// The callback returns true when it consumed its element — <c>ReadInnerXml</c> and <c>Skip</c>
    /// both leave the reader on the *next* node, and a walk that read again from there would step
    /// straight over a sibling. That one detail is why this is a helper rather than a loop written
    /// out three times.
    /// </para>
    /// </summary>
    static void ForEachChild(XmlReader reader, Func<XmlReader, bool> onElement)
    {
        if (reader.IsEmptyElement)
            return;

        var depth = reader.Depth;
        var consumed = false;

        while (consumed || reader.Read())
        {
            consumed = false;

            if (reader.Depth <= depth)
                return;

            if (reader.NodeType == XmlNodeType.Element && reader.Depth == depth + 1)
                consumed = onElement(reader);
        }
    }

    static bool MoveToRoot(XmlReader reader, string localName)
    {
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element)
                continue;

            return WebDavXml.IsDav(reader, localName);
        }

        return false;
    }
}
