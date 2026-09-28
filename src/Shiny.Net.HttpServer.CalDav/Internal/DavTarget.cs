using System.Text;
using Shiny.Net.HttpServer.WebDav;

namespace Shiny.Net.HttpServer.CalDav.Internal;

enum TargetKind
{
    /// <summary>The mount itself — where discovery starts.</summary>
    Root,

    /// <summary><c>principals/</c>.</summary>
    PrincipalsRoot,

    /// <summary><c>principals/{user}/</c>.</summary>
    Principal,

    /// <summary><c>calendars/</c> or <c>addressbooks/</c>.</summary>
    HomesRoot,

    /// <summary><c>calendars/{user}/</c> — the calendar-home-set.</summary>
    Home,

    /// <summary><c>calendars/{user}/{calendar}/</c>.</summary>
    Collection,

    /// <summary><c>calendars/{user}/{calendar}/{object}</c>.</summary>
    Object
}

enum Flavor
{
    None,
    Calendar,
    AddressBook
}

/// <summary>
/// Where a request points, in the mount's fixed layout:
/// <code>
/// {prefix}/
/// {prefix}/principals/{user}/
/// {prefix}/calendars/{user}/{calendar}/{object}.ics
/// {prefix}/addressbooks/{user}/{book}/{object}.vcf
/// </code>
/// The layout is the server's to choose — clients discover every URL from the one before it, starting
/// at <c>current-user-principal</c> — and a fixed one means there is nothing to configure.
/// </summary>
readonly record struct DavTarget(TargetKind Kind, Flavor Flavor, string? Principal, string? Collection, string? Name)
{
    public const string PrincipalsSegment = "principals";
    public const string CalendarsSegment = "calendars";
    public const string AddressBooksSegment = "addressbooks";

    public bool IsCollection => this.Kind != TargetKind.Object;

    /// <summary>The segments below the mount, unescaped.</summary>
    public IEnumerable<string> Segments()
    {
        switch (this.Kind)
        {
            case TargetKind.Root:
                yield break;

            case TargetKind.PrincipalsRoot:
                yield return PrincipalsSegment;
                yield break;

            case TargetKind.Principal:
                yield return PrincipalsSegment;
                yield return this.Principal!;
                yield break;
        }

        yield return this.Flavor == Flavor.Calendar ? CalendarsSegment : AddressBooksSegment;

        if (this.Kind == TargetKind.HomesRoot)
            yield break;

        yield return this.Principal!;

        if (this.Kind == TargetKind.Home)
            yield break;

        yield return this.Collection!;

        if (this.Kind == TargetKind.Object)
            yield return this.Name!;
    }

    /// <summary>The key dead properties are stored under — the path below the mount.</summary>
    public string PropertyKey => string.Join('/', this.Segments());

    /// <summary>The key a collection's sync state and write lock are held under.</summary>
    public string CollectionKey => $"{(int)this.Flavor}/{this.Principal}/{this.Collection}";

    public DavTarget WithCollection(string collection) => this with { Kind = TargetKind.Collection, Collection = collection, Name = null };

    public DavTarget WithObject(string name) => this with { Kind = TargetKind.Object, Name = name };

    public static DavTarget Root => new(TargetKind.Root, Flavor.None, null, null, null);

    public static DavTarget PrincipalOf(string principal) => new(TargetKind.Principal, Flavor.None, principal, null, null);

    public static DavTarget HomeOf(Flavor flavor, string principal) => new(TargetKind.Home, flavor, principal, null, null);

    /// <summary>
    /// Reads a path below the mount. False for anything outside the layout, or a segment that could
    /// not be a name — empty, <c>.</c>, <c>..</c>, or (for a collection or an object) starting with a
    /// dot, which is where a store keeps what is not an object.
    /// </summary>
    public static bool TryParse(IReadOnlyList<string> segments, out DavTarget target)
    {
        target = Root;

        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment is "." or ".." || segment.Contains('/') || segment.Contains('\\'))
                return false;
        }

        if (segments.Count == 0)
            return true;

        if (segments[0] == PrincipalsSegment)
        {
            switch (segments.Count)
            {
                case 1:
                    target = new DavTarget(TargetKind.PrincipalsRoot, Flavor.None, null, null, null);
                    return true;

                case 2:
                    target = PrincipalOf(segments[1]);
                    return true;

                default:
                    return false;
            }
        }

        var flavor = segments[0] switch
        {
            CalendarsSegment => Flavor.Calendar,
            AddressBooksSegment => Flavor.AddressBook,
            _ => Flavor.None
        };

        if (flavor == Flavor.None)
            return false;

        for (var i = 2; i < segments.Count; i++)
        {
            if (segments[i].StartsWith('.'))
                return false;
        }

        target = segments.Count switch
        {
            1 => new DavTarget(TargetKind.HomesRoot, flavor, null, null, null),
            2 => new DavTarget(TargetKind.Home, flavor, segments[1], null, null),
            3 => new DavTarget(TargetKind.Collection, flavor, segments[1], segments[2], null),
            4 => new DavTarget(TargetKind.Object, flavor, segments[1], segments[2], segments[3]),
            _ => default
        };

        return segments.Count <= 4;
    }
}

/// <summary>Turning targets into hrefs and hrefs back into targets.</summary>
sealed class DavPaths(string basePath)
{
    public string BasePath => basePath;

    /// <summary>Percent-encoded segment by segment; a collection's href ends in a slash.</summary>
    public string Href(DavTarget target)
    {
        var builder = new StringBuilder(basePath);

        foreach (var segment in target.Segments())
            builder.Append('/').Append(Uri.EscapeDataString(segment));

        if (target.IsCollection)
            builder.Append('/');

        return builder.ToString();
    }

    /// <summary>
    /// A request's route capture — already percent-decoded by the router — as a target.
    /// </summary>
    public static bool TryParseRoute(string raw, out DavTarget target)
        => DavTarget.TryParse(raw.Split('/', StringSplitOptions.RemoveEmptyEntries), out target);

    /// <summary>
    /// An href from a request body — absolute or origin-relative, still percent-encoded — as a
    /// target. Segments are decoded one at a time, so an encoded <c>%2F</c> inside a name stays
    /// part of that name instead of becoming a separator.
    /// </summary>
    public bool TryParseHref(string href, out DavTarget target)
    {
        target = default;

        string path;

        if (href.StartsWith('/'))
        {
            var cut = href.AsSpan().IndexOfAny('?', '#');
            path = cut < 0 ? href : href[..cut];
        }
        else if (Uri.TryCreate(href, UriKind.Absolute, out var absolute) &&
                 (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
        {
            path = absolute.AbsolutePath;
        }
        else
        {
            return false;
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var prefix = basePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length < prefix.Length)
            return false;

        for (var i = 0; i < prefix.Length; i++)
        {
            if (!Uri.UnescapeDataString(segments[i]).Equals(prefix[i], StringComparison.OrdinalIgnoreCase))
                return false;
        }

        var tail = new string[segments.Length - prefix.Length];

        for (var i = 0; i < tail.Length; i++)
            tail[i] = Uri.UnescapeDataString(segments[prefix.Length + i]);

        return DavTarget.TryParse(tail, out target);
    }
}

/// <summary>
/// One resource being described in a multistatus: where it is, and whatever has been loaded about it.
/// </summary>
sealed class DavNode(DavTarget target, string href)
{
    public DavTarget Target { get; } = target;

    public string Href { get; } = href;

    public DavCollection? Collection { get; init; }

    public DavObjectInfo? Info { get; init; }

    /// <summary>The object's text, when a property that returns it was asked for.</summary>
    public string? Data { get; set; }

    /// <summary>A collection's members, once something has needed them.</summary>
    public IReadOnlyList<DavObjectInfo>? Listing { get; set; }

    public string? SyncToken { get; set; }

    public string? CTag { get; set; }

    public IReadOnlyList<WebDavProperty>? Dead { get; set; }
}
