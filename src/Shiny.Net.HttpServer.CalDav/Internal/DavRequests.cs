using System.Globalization;
using System.Xml;
using Shiny.Net.HttpServer.WebDav;

namespace Shiny.Net.HttpServer.CalDav.Internal;

enum PropKind
{
    AllProp,
    PropName,
    Prop
}

/// <summary>Which properties a <c>PROPFIND</c> or a <c>REPORT</c> asked for.</summary>
sealed class PropRequest
{
    public PropKind Kind { get; set; } = PropKind.AllProp;

    public List<WebDavPropertyName> Names { get; } = [];

    public bool Wants(string ns, string localName)
        => this.Kind == PropKind.Prop && this.Names.Contains(new WebDavPropertyName(ns, localName));
}

enum ReportKind
{
    CalendarQuery,
    CalendarMultiget,
    FreeBusyQuery,
    AddressBookQuery,
    AddressBookMultiget,
    SyncCollection,
    Unsupported
}

sealed class ReportRequest
{
    public ReportKind Kind { get; set; } = ReportKind.Unsupported;

    public PropRequest Props { get; } = new() { Kind = PropKind.Prop };

    public List<string> Hrefs { get; } = [];

    public CompFilter? CalendarFilter { get; set; }

    public AddressBookFilter? AddressBookFilter { get; set; }

    /// <summary>CardDAV <c>limit/nresults</c>.</summary>
    public int? Limit { get; set; }

    public string? SyncToken { get; set; }

    public TimeRange? TimeRange { get; set; }

    /// <summary>Set when a filter named a collation or match type this server does not support.</summary>
    public string? Unsupported { get; set; }
}

/// <summary>One <c>&lt;D:set&gt;</c> or <c>&lt;D:remove&gt;</c> property.</summary>
/// <param name="Name">The property.</param>
/// <param name="Xml">Its inner XML, or null for a removal.</param>
sealed record PropertyUpdate(WebDavPropertyName Name, string? Xml)
{
    public bool IsRemove => this.Xml is null;
}

/// <summary>Parsing the request bodies CalDAV, CardDAV and RFC 6578 define.</summary>
static class DavRequests
{
    static readonly XmlReaderSettings FragmentSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersFromEntities = 0,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        ConformanceLevel = ConformanceLevel.Fragment
    };

    public static bool TryParseDepth(string? header, int fallback, out int depth)
    {
        depth = fallback;

        if (header is null || header.Trim().Length == 0)
            return true;

        switch (header.Trim())
        {
            case "0":
                depth = 0;
                return true;

            case "1":
                depth = 1;
                return true;
        }

        if (header.Trim().Equals("infinity", StringComparison.OrdinalIgnoreCase))
        {
            depth = int.MaxValue;
            return true;
        }

        return false;
    }

    public static bool TryParsePropFind(Stream body, out PropRequest request)
    {
        var parsed = new PropRequest();
        request = parsed;

        if (body.Length == 0)
            return true;

        try
        {
            using var reader = XmlReader.Create(body, DavXml.ReaderSettings);

            if (!DavXml.MoveToRoot(reader) || !DavXml.Is(reader, Ns.Dav, "propfind"))
                return false;

            DavXml.ForEachChild(reader, child => ReadPropChoice(child, parsed));
            return true;
        }
        catch (XmlException)
        {
            return false;
        }
    }

    /// <summary><c>prop</c>, <c>allprop</c> or <c>propname</c>, wherever it appears.</summary>
    static bool ReadPropChoice(XmlReader child, PropRequest request)
    {
        if (DavXml.Is(child, Ns.Dav, "allprop"))
        {
            request.Kind = PropKind.AllProp;
        }
        else if (DavXml.Is(child, Ns.Dav, "propname"))
        {
            request.Kind = PropKind.PropName;
        }
        else if (DavXml.Is(child, Ns.Dav, "prop"))
        {
            request.Kind = PropKind.Prop;
            ReadNames(child, request.Names);
        }
        else if (DavXml.Is(child, Ns.Dav, "include"))
        {
            ReadNames(child, request.Names);
        }

        return false;
    }

    static void ReadNames(XmlReader reader, List<WebDavPropertyName> names)
        => DavXml.ForEachChild(reader, child =>
        {
            var name = DavXml.NameOf(child);

            if (!names.Contains(name))
                names.Add(name);

            // calendar-data and address-data may carry children (comp, expand, prop) that narrow
            // what is returned. The whole object is returned regardless, which RFC 4791 §9.6
            // permits a server to do; the children are skipped here so the walk stays in step.
            if (!child.IsEmptyElement)
            {
                child.Skip();
                return true;
            }

            return false;
        });

    /// <summary>
    /// A <c>PROPPATCH</c> body, or the <c>set</c> half of a <c>MKCALENDAR</c> or extended
    /// <c>MKCOL</c> body. <paramref name="rootNs"/>/<paramref name="rootName"/> name the document
    /// element each expects.
    /// </summary>
    public static bool TryParseUpdates(Stream body, string rootNs, string rootName, out List<PropertyUpdate> updates)
    {
        var parsed = new List<PropertyUpdate>();
        updates = parsed;

        if (body.Length == 0)
            return false;

        try
        {
            using var reader = XmlReader.Create(body, DavXml.ReaderSettings);

            if (!DavXml.MoveToRoot(reader) || !DavXml.Is(reader, rootNs, rootName))
                return false;

            DavXml.ForEachChild(reader, action =>
            {
                var isRemove = DavXml.Is(action, Ns.Dav, "remove");

                if (!isRemove && !DavXml.Is(action, Ns.Dav, "set"))
                    return false;

                DavXml.ForEachChild(action, container =>
                {
                    if (!DavXml.Is(container, Ns.Dav, "prop"))
                        return false;

                    DavXml.ForEachChild(container, property =>
                    {
                        var name = DavXml.NameOf(property);

                        if (property.IsEmptyElement)
                        {
                            parsed.Add(new PropertyUpdate(name, isRemove ? null : string.Empty));
                            return false;
                        }

                        if (isRemove)
                        {
                            parsed.Add(new PropertyUpdate(name, null));
                            property.Skip();
                        }
                        else
                        {
                            parsed.Add(new PropertyUpdate(name, property.ReadInnerXml()));
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
            updates = [];
            return false;
        }
    }

    /// <summary>The text inside a property's raw inner XML — a display name, a colour.</summary>
    public static string InnerText(string xml)
    {
        if (xml.Length == 0 || !xml.Contains('<'))
            return xml.Contains('&') ? Decode(xml) : xml;

        return Decode(xml);

        static string Decode(string fragment)
        {
            try
            {
                using var reader = XmlReader.Create(new StringReader(fragment), FragmentSettings);
                var text = new System.Text.StringBuilder();

                while (reader.Read())
                {
                    if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace)
                        text.Append(reader.Value);
                }

                return text.ToString();
            }
            catch (XmlException)
            {
                return fragment;
            }
        }
    }

    /// <summary>The <c>name</c> of every <c>&lt;C:comp&gt;</c> in a supported-calendar-component-set.</summary>
    public static List<string> ComponentNames(string xml)
    {
        var names = new List<string>();

        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), FragmentSettings);

            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element &&
                    reader.LocalName == "comp" &&
                    reader.GetAttribute("name") is { Length: > 0 } name)
                    names.Add(name.ToUpperInvariant());
            }
        }
        catch (XmlException)
        {
            names.Clear();
        }

        return names;
    }

    /// <summary>Whether a resourcetype's inner XML names a given element.</summary>
    public static bool ContainsElement(string xml, string ns, string localName)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), FragmentSettings);

            while (reader.Read())
            {
                if (DavXml.Is(reader, ns, localName))
                    return true;
            }
        }
        catch (XmlException)
        {
        }

        return false;
    }

    public static bool TryParseReport(Stream body, out ReportRequest request)
    {
        var parsed = new ReportRequest();
        request = parsed;

        if (body.Length == 0)
            return false;

        try
        {
            using var reader = XmlReader.Create(body, DavXml.ReaderSettings);

            if (!DavXml.MoveToRoot(reader))
                return false;

            parsed.Kind = reader switch
            {
                _ when DavXml.Is(reader, Ns.CalDav, "calendar-query") => ReportKind.CalendarQuery,
                _ when DavXml.Is(reader, Ns.CalDav, "calendar-multiget") => ReportKind.CalendarMultiget,
                _ when DavXml.Is(reader, Ns.CalDav, "free-busy-query") => ReportKind.FreeBusyQuery,
                _ when DavXml.Is(reader, Ns.CardDav, "addressbook-query") => ReportKind.AddressBookQuery,
                _ when DavXml.Is(reader, Ns.CardDav, "addressbook-multiget") => ReportKind.AddressBookMultiget,
                _ when DavXml.Is(reader, Ns.Dav, "sync-collection") => ReportKind.SyncCollection,
                _ => ReportKind.Unsupported
            };

            if (parsed.Kind == ReportKind.Unsupported)
                return true;

            if (parsed.Kind == ReportKind.AddressBookQuery)
                parsed.AddressBookFilter = new AddressBookFilter();

            DavXml.ForEachChild(reader, child =>
            {
                if (DavXml.Is(child, Ns.Dav, "href"))
                {
                    var href = DavXml.ReadText(child).Trim();

                    if (href.Length > 0)
                        parsed.Hrefs.Add(href);

                    return true;
                }

                if (DavXml.Is(child, Ns.Dav, "sync-token"))
                {
                    parsed.SyncToken = DavXml.ReadText(child).Trim();
                    return true;
                }

                if (DavXml.Is(child, Ns.CalDav, "filter"))
                {
                    DavXml.ForEachChild(child, comp =>
                    {
                        if (DavXml.Is(comp, Ns.CalDav, "comp-filter"))
                        {
                            parsed.CalendarFilter = ReadCompFilter(comp, parsed);
                            return true;
                        }

                        return false;
                    });

                    return false;
                }

                if (DavXml.Is(child, Ns.CalDav, "time-range"))
                {
                    parsed.TimeRange = ReadTimeRange(child, parsed);
                    return false;
                }

                if (DavXml.Is(child, Ns.CardDav, "filter"))
                {
                    parsed.AddressBookFilter!.AllOf = IsAllOf(child);

                    DavXml.ForEachChild(child, prop =>
                    {
                        if (DavXml.Is(prop, Ns.CardDav, "prop-filter"))
                        {
                            parsed.AddressBookFilter.PropFilters.Add(ReadPropFilter(prop, Ns.CardDav, parsed));
                            return true;
                        }

                        return false;
                    });

                    return false;
                }

                if (DavXml.Is(child, Ns.CardDav, "limit"))
                {
                    DavXml.ForEachChild(child, n =>
                    {
                        if (DavXml.Is(n, Ns.CardDav, "nresults") &&
                            int.TryParse(DavXml.ReadText(n).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var limit))
                        {
                            parsed.Limit = limit;
                            return true;
                        }

                        return false;
                    });

                    return false;
                }

                return ReadPropChoice(child, parsed.Props);
            });

            return true;
        }
        catch (XmlException)
        {
            return false;
        }
    }

    static CompFilter ReadCompFilter(XmlReader reader, ReportRequest request)
    {
        var filter = new CompFilter { Name = (reader.GetAttribute("name") ?? string.Empty).ToUpperInvariant() };

        DavXml.ForEachChild(reader, child =>
        {
            if (DavXml.Is(child, Ns.CalDav, "is-not-defined"))
            {
                filter.IsNotDefined = true;
                return false;
            }

            if (DavXml.Is(child, Ns.CalDav, "time-range"))
            {
                filter.TimeRange = ReadTimeRange(child, request);
                return false;
            }

            if (DavXml.Is(child, Ns.CalDav, "comp-filter"))
            {
                filter.CompFilters.Add(ReadCompFilter(child, request));
                return true;
            }

            if (DavXml.Is(child, Ns.CalDav, "prop-filter"))
            {
                filter.PropFilters.Add(ReadPropFilter(child, Ns.CalDav, request));
                return true;
            }

            return false;
        });

        // The nested walk leaves the reader on this element's end tag; step past it so the caller's
        // walk sees the next sibling, which is what "consumed" promises.
        reader.Read();
        return filter;
    }

    static PropFilter ReadPropFilter(XmlReader reader, string ns, ReportRequest request)
    {
        var filter = new PropFilter
        {
            Name = (reader.GetAttribute("name") ?? string.Empty).ToUpperInvariant(),

            // CalDAV's children are a conjunction; CardDAV's combine by "test", anyof by default.
            AllOf = ns == Ns.CalDav || IsAllOf(reader)
        };

        DavXml.ForEachChild(reader, child =>
        {
            if (DavXml.Is(child, ns, "is-not-defined"))
            {
                filter.IsNotDefined = true;
                return false;
            }

            if (ns == Ns.CalDav && DavXml.Is(child, Ns.CalDav, "time-range"))
            {
                filter.TimeRange = ReadTimeRange(child, request);
                return false;
            }

            if (DavXml.Is(child, ns, "text-match"))
            {
                filter.TextMatches.Add(ReadTextMatch(child, ns, request));
                return true;
            }

            if (DavXml.Is(child, ns, "param-filter"))
            {
                filter.ParamFilters.Add(ReadParamFilter(child, ns, request));
                return true;
            }

            return false;
        });

        reader.Read();
        return filter;
    }

    static ParamFilter ReadParamFilter(XmlReader reader, string ns, ReportRequest request)
    {
        var filter = new ParamFilter { Name = (reader.GetAttribute("name") ?? string.Empty).ToUpperInvariant() };

        DavXml.ForEachChild(reader, child =>
        {
            if (DavXml.Is(child, ns, "is-not-defined"))
            {
                filter.IsNotDefined = true;
                return false;
            }

            if (DavXml.Is(child, ns, "text-match"))
            {
                filter.TextMatch = ReadTextMatch(child, ns, request);
                return true;
            }

            return false;
        });

        reader.Read();
        return filter;
    }

    static TextMatch ReadTextMatch(XmlReader reader, string ns, ReportRequest request)
    {
        // CardDAV's default collation is i;unicode-casemap (RFC 6352 §10.5.4); CalDAV's is
        // i;ascii-casemap (RFC 4791 §9.7.5).
        var collation = reader.GetAttribute("collation")
            ?? (ns == Ns.CardDav ? TextMatch.UnicodeCasemap : TextMatch.AsciiCasemap);
        var matchType = reader.GetAttribute("match-type") ?? "contains";
        var negate = reader.GetAttribute("negate-condition") is "yes";

        if (!TextMatch.IsSupportedCollation(collation) || !TextMatch.IsSupportedMatchType(matchType))
            request.Unsupported ??= ns + "|" + (TextMatch.IsSupportedCollation(collation) ? "supported-filter" : "supported-collation");

        var text = DavXml.ReadText(reader);

        return new TextMatch { Text = text, Collation = collation, MatchType = matchType, Negate = negate };
    }

    static TimeRange? ReadTimeRange(XmlReader reader, ReportRequest request)
    {
        var start = reader.GetAttribute("start");
        var end = reader.GetAttribute("end");

        DateTime? from = null, to = null;

        if (start is not null)
        {
            if (!ICalTime.TryParseUtc(start, out var value))
            {
                request.Unsupported ??= Ns.CalDav + "|valid-filter";
                return null;
            }

            from = value;
        }

        if (end is not null)
        {
            if (!ICalTime.TryParseUtc(end, out var value))
            {
                request.Unsupported ??= Ns.CalDav + "|valid-filter";
                return null;
            }

            to = value;
        }

        if (from is null && to is null)
        {
            request.Unsupported ??= Ns.CalDav + "|valid-filter";
            return null;
        }

        return new TimeRange(from, to);
    }

    static bool IsAllOf(XmlReader reader) => reader.GetAttribute("test") is "allof";
}
