using Shiny.Net.HttpServer.WebDav;

namespace Shiny.Net.HttpServer.CalDav;

/// <summary>
/// Where a principal's calendars or address books, and the objects in them, actually live.
/// <para>
/// The mount answers the protocol — discovery, <c>PROPFIND</c>, the <c>REPORT</c>s, sync tokens,
/// ETag preconditions, validation of what a client uploads — and a store answers what is there. It
/// is shaped as collections of text objects, each addressed by a name and versioned by an ETag,
/// because that is what CalDAV and CardDAV are underneath: <see cref="FileCalendarStore"/> keeps
/// them as <c>.ics</c> files in a directory, and an app that already has its own data (the device
/// calendar, a contacts database, a table in SQLite) adapts it by generating the text on the way out
/// and reading it on the way in.
/// </para>
/// <para>
/// <b>Names.</b> <c>principal</c> is the authenticated user the request is for,
/// <c>collection</c> is one path segment, and an object's <c>name</c> is one path segment too —
/// usually a UUID with <c>.ics</c> or <c>.vcf</c> on the end, chosen by the client. None of them
/// contains a <c>/</c>, is empty, or is <c>.</c> or <c>..</c>; the mount checks before a store is
/// asked.
/// </para>
/// <para>
/// <b>ETags</b> are strong, quoted entity tags (<c>"abc123"</c>) and must change whenever the
/// object's text does. They are what every client syncs by, and what <c>sync-collection</c>
/// compares — so a store that can hand back the same tag for different text will lose edits.
/// </para>
/// <para>
/// <b>Refusing.</b> Throw <see cref="WebDavException"/> with the status the client should see: 403
/// for a calendar the platform will not let the app create, 507 when storage is full. The mount
/// serialises writes to one collection, so a store does not have to guard its own read-then-write
/// against a second request from this server.
/// </para>
/// </summary>
/// <typeparam name="TCollection">The collection type — a calendar or an address book.</typeparam>
public interface IDavCollectionStore<TCollection> where TCollection : DavCollection
{
    /// <summary>The principal's collections. Empty — not an error — for a principal with none.</summary>
    ValueTask<IReadOnlyList<TCollection>> GetCollectionsAsync(string principal, CancellationToken cancellationToken);

    /// <summary>One collection, or null when the principal has none by that id.</summary>
    ValueTask<TCollection?> GetCollectionAsync(string principal, string collection, CancellationToken cancellationToken);

    /// <summary>
    /// Creates a collection for <c>MKCALENDAR</c> or an extended <c>MKCOL</c>. The id is known to be
    /// free. Returns the collection as stored, which may differ from what was asked for.
    /// </summary>
    ValueTask<TCollection> CreateCollectionAsync(string principal, TCollection collection, CancellationToken cancellationToken);

    /// <summary>
    /// Saves a changed collection — a new display name, colour or description from a
    /// <c>PROPPATCH</c>. The id is unchanged and known to exist.
    /// </summary>
    ValueTask UpdateCollectionAsync(string principal, TCollection collection, CancellationToken cancellationToken);

    /// <summary>Removes a collection and every object in it.</summary>
    ValueTask DeleteCollectionAsync(string principal, string collection, CancellationToken cancellationToken);

    /// <summary>
    /// What is in a collection: every object's name and ETag, without its text. This is asked on
    /// every sync, so it should not read every object to answer.
    /// </summary>
    ValueTask<IReadOnlyList<DavObjectInfo>> GetObjectsAsync(string principal, string collection, CancellationToken cancellationToken);

    /// <summary>One object with its text, or null when there is none by that name.</summary>
    ValueTask<DavObject?> GetObjectAsync(string principal, string collection, string name, CancellationToken cancellationToken);

    /// <summary>
    /// Creates or replaces an object. By the time this is called the text has been parsed and
    /// validated, the preconditions (<c>If-Match</c>, <c>If-None-Match</c>, UID uniqueness) have
    /// passed, and the collection is known to exist.
    /// </summary>
    /// <returns>
    /// The new ETag — or null when the store did not keep the text byte for byte (a device calendar
    /// that re-generates it, say). RFC 4791 §5.3.4 forbids sending an ETag in that case, because a
    /// client that has one assumes its own copy is current; without one, it fetches the stored text.
    /// </returns>
    ValueTask<string?> PutObjectAsync(string principal, string collection, DavObjectWrite write, CancellationToken cancellationToken);

    /// <summary>Removes an object. It is known to exist.</summary>
    ValueTask DeleteObjectAsync(string principal, string collection, string name, CancellationToken cancellationToken);
}

/// <summary>
/// The calendars a CalDAV mount serves. See <see cref="IDavCollectionStore{TCollection}"/> for the
/// contract, and <see cref="FileCalendarStore"/> for the one that keeps <c>.ics</c> files in a
/// directory.
/// </summary>
public interface ICalendarStore : IDavCollectionStore<CalendarCollection>;

/// <summary>
/// The address books a CardDAV mount serves. See <see cref="IDavCollectionStore{TCollection}"/> for
/// the contract, and <see cref="FileAddressBookStore"/> for the one that keeps <c>.vcf</c> files in
/// a directory.
/// </summary>
public interface IAddressBookStore : IDavCollectionStore<AddressBookCollection>;

/// <summary>What calendars and address books have in common.</summary>
/// <param name="Id">The path segment the collection is addressed by. Stable: clients key on it.</param>
public abstract record DavCollection(string Id)
{
    /// <summary>What a client labels it with — the <c>DAV:displayname</c>. Null shows <see cref="Id"/>.</summary>
    public string? DisplayName { get; init; }

    /// <summary>The CalDAV <c>calendar-description</c> or CardDAV <c>addressbook-description</c>.</summary>
    public string? Description { get; init; }

    /// <summary>
    /// A version for the whole collection — <c>CS:getctag</c>, which older iOS and macOS clients
    /// poll to decide whether to sync at all. Null has the mount derive one from the names and
    /// ETags of what is in it, which is always right and costs a <see cref="IDavCollectionStore{TCollection}.GetObjectsAsync"/>.
    /// </summary>
    public string? CTag { get; init; }

    /// <summary>
    /// Advertised to clients as read-only, and refused writes: a holiday calendar, or a device
    /// account the platform does not let the app change.
    /// </summary>
    public bool IsReadOnly { get; init; }
}

/// <summary>A calendar collection (RFC 4791 §4.2).</summary>
public sealed record CalendarCollection(string Id) : DavCollection(Id)
{
    /// <summary>
    /// The component types it holds — <c>supported-calendar-component-set</c>. A <c>PUT</c> of any
    /// other type is refused with <c>supported-calendar-component</c>. Both events and tasks by
    /// default; iOS creates separate calendars for reminders and asks for <c>VTODO</c> alone.
    /// </summary>
    public IReadOnlyList<string> SupportedComponents { get; init; } = ["VEVENT", "VTODO"];

    /// <summary>The Apple <c>calendar-color</c>, as <c>#RRGGBB</c> or <c>#RRGGBBAA</c>.</summary>
    public string? Color { get; init; }

    /// <summary>
    /// The <c>calendar-timezone</c>: a whole <c>VCALENDAR</c> holding one <c>VTIMEZONE</c>, as a
    /// client sends it. Kept for clients that read it back; not used to interpret floating times.
    /// </summary>
    public string? TimeZone { get; init; }
}

/// <summary>An address book collection (RFC 6352 §5.2).</summary>
public sealed record AddressBookCollection(string Id) : DavCollection(Id);

/// <summary>One object in a collection, without its text — what a listing is made of.</summary>
/// <param name="Name">The path segment it is addressed by, e.g. <c>1D4C…9A.ics</c>.</param>
/// <param name="ETag">Its quoted, strong entity tag.</param>
/// <param name="LastModifiedUtc">Reported as <c>getlastmodified</c>.</param>
public sealed record DavObjectInfo(string Name, string ETag, DateTimeOffset LastModifiedUtc)
{
    /// <summary>
    /// Its UID, when the store knows it without reading the text. Used to enforce
    /// <c>no-uid-conflict</c>; when null the mount reads the object to find out, and remembers the
    /// answer for as long as the ETag does not change.
    /// </summary>
    public string? Uid { get; init; }

    /// <summary>Its size in bytes, for <c>getcontentlength</c>. Left out when null.</summary>
    public long? Length { get; init; }
}

/// <summary>One object with its text.</summary>
/// <param name="Name">The path segment it is addressed by.</param>
/// <param name="ETag">Its quoted, strong entity tag.</param>
/// <param name="Data">The iCalendar or vCard text.</param>
/// <param name="LastModifiedUtc">Reported as <c>getlastmodified</c>.</param>
public sealed record DavObject(string Name, string ETag, string Data, DateTimeOffset LastModifiedUtc);

/// <summary>An object a client is writing, already parsed and validated.</summary>
/// <param name="Name">The path segment it is addressed by.</param>
/// <param name="Data">The text exactly as the client sent it.</param>
/// <param name="Uid">The UID every component in it shares.</param>
/// <param name="Component">
/// The parsed <c>VCALENDAR</c> or <c>VCARD</c>, so a store that maps it onto its own model does not
/// parse it a second time.
/// </param>
/// <param name="IsNew">False when it replaces an existing object.</param>
public sealed record DavObjectWrite(string Name, string Data, string Uid, ContentComponent Component, bool IsNew);
