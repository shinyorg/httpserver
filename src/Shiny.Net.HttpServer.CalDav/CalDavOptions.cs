using Shiny.Net.HttpServer.WebDav;

namespace Shiny.Net.HttpServer.CalDav;

/// <summary>What a CalDAV/CardDAV mount serves, and for whom.</summary>
public sealed class CalDavOptions
{
    /// <summary>
    /// The calendars — CalDAV. Null leaves CalDAV off: no <c>calendar-home-set</c>, no
    /// <c>/.well-known/caldav</c>, and <c>calendar-access</c> is not advertised.
    /// </summary>
    public ICalendarStore? CalendarStore { get; set; }

    /// <summary>
    /// The address books — CardDAV. Null leaves CardDAV off, on the same terms as
    /// <see cref="CalendarStore"/>. At least one of the two is required.
    /// </summary>
    public IAddressBookStore? AddressBookStore { get; set; }

    /// <summary>
    /// Who a request is for — the principal whose calendars and contacts it may see. Null uses the
    /// authenticated user's name (<c>HttpContext.User.Identity.Name</c>) and falls back to
    /// <see cref="DefaultPrincipal"/>.
    /// <para>
    /// A principal is a path segment in every URL the mount hands out, and a request for another
    /// principal's URL is refused with 403 — so this is where one user is kept out of another's
    /// data.
    /// </para>
    /// </summary>
    public Func<HttpContext, string?>? PrincipalResolver { get; set; }

    /// <summary>
    /// The principal an unauthenticated request is served as, or null (the default) to answer 401.
    /// <para>
    /// Set it for a single-user app with no authentication in front of the mount — and understand
    /// that anyone who can reach the port then has that user's calendars. The mount's
    /// <c>.RequireAuthorization()</c> with Basic authentication is what every CalDAV client
    /// expects.
    /// </para>
    /// </summary>
    public string? DefaultPrincipal { get; set; }

    /// <summary>
    /// Maps <c>/.well-known/caldav</c> and <c>/.well-known/carddav</c> (RFC 6764) as redirects to
    /// the mount. On by default: it is how iOS, macOS and DAVx⁵ find a server when the user types
    /// only a host name. Only the <see cref="HttpServer"/> overload of <c>MapCalDav</c> can map
    /// them, since they sit at the root of the server rather than under a prefix.
    /// </summary>
    public bool MapWellKnown { get; set; } = true;

    /// <summary>
    /// Refuses every change: <c>PUT</c>, <c>DELETE</c>, <c>PROPPATCH</c>, <c>MKCALENDAR</c> and
    /// <c>MKCOL</c>. Clients are told so up front through <c>current-user-privilege-set</c>, which
    /// is what makes iOS show the calendars as read-only instead of letting a user edit an event and
    /// then failing to save it.
    /// </summary>
    public bool ReadOnly { get; set; }

    /// <summary>
    /// Largest calendar or contact object a <c>PUT</c> may carry — advertised as
    /// <c>max-resource-size</c>. 10 MB by default: an event is a few hundred bytes, but a contact
    /// with a photo in it is not.
    /// </summary>
    public long MaxResourceSize { get; set; } = 10 * 1024 * 1024;

    /// <summary>
    /// Largest XML body this will read on a <c>PROPFIND</c>, <c>PROPPATCH</c>, <c>REPORT</c>,
    /// <c>MKCALENDAR</c> or <c>MKCOL</c>. A multiget of a thousand hrefs is well under it.
    /// </summary>
    public long MaxXmlBodyBytes { get; set; } = 1024 * 1024;

    /// <summary>
    /// How many past sync tokens are remembered per collection. A client presenting a token older
    /// than that — or any token after a restart — is told it is no longer valid and does a full
    /// resync, which RFC 6578 requires every client to handle.
    /// </summary>
    public int MaxSyncTokensPerCollection { get; set; } = 32;

    /// <summary>
    /// Most occurrences of one recurring event a time-range filter will generate before giving up
    /// and treating the event as matching. A rule with no end that fires every day is fine; this
    /// bounds the one built to spin.
    /// </summary>
    public int MaxRecurrenceInstances { get; set; } = 10_000;

    /// <summary>
    /// Where dead properties — the ones clients set with <c>PROPPATCH</c> that the mount does not
    /// interpret, such as Apple's <c>calendar-order</c> — are kept. In memory when null, so they
    /// are lost on restart; the ones that matter (name, colour, description) go to the store.
    /// </summary>
    public IWebDavPropertyStore? PropertyStore { get; set; }

    /// <summary>
    /// The <c>calendar-user-address-set</c> for a principal — normally <c>mailto:</c> URIs. iOS
    /// uses it to recognise the user among an event's attendees. Null leaves it out.
    /// </summary>
    public Func<string, IReadOnlyList<string>>? CalendarUserAddresses { get; set; }

    internal void Validate()
    {
        if (this.CalendarStore is null && this.AddressBookStore is null)
        {
            throw new InvalidOperationException(
                $"{nameof(CalDavOptions)} needs a {nameof(this.CalendarStore)}, an {nameof(this.AddressBookStore)}, or both."
            );
        }

        if (this.MaxSyncTokensPerCollection < 1)
            throw new InvalidOperationException($"{nameof(this.MaxSyncTokensPerCollection)} must be at least 1.");
    }
}
