using System.Collections.Concurrent;
using System.Globalization;

namespace Shiny.Net.HttpServer.CalDav.Internal;

/// <summary>A DATE or DATE-TIME value as written: wall-clock time plus how to read it.</summary>
/// <param name="Local">The value as written — wall-clock for a zoned or floating time, UTC for one ending in Z.</param>
/// <param name="IsDate">A DATE: an all-day value with no time part.</param>
/// <param name="IsUtc">Ended in <c>Z</c>.</param>
/// <param name="Zone">The zone its TZID named, when that zone is one this platform knows.</param>
readonly record struct ICalDateTime(DateTime Local, bool IsDate, bool IsUtc, TimeZoneInfo? Zone)
{
    /// <summary>
    /// The instant it names. A DATE, or a floating time with no zone, is read as UTC — see
    /// <see cref="ICalTime"/> for why that is the pragmatic reading.
    /// </summary>
    public DateTime ToUtc() => ICalTime.ToUtc(this.Local, this.IsUtc, this.Zone);
}

/// <summary>
/// Reading iCalendar times (RFC 5545 §3.3.4, §3.3.5, §3.3.6) — enough for time-range filters.
/// <para>
/// <b>Zones.</b> A <c>TZID</c> is looked up as a system time zone, which accepts IANA ids
/// (<c>Europe/Berlin</c>, which is what Apple, Google and Thunderbird write) and, since .NET 6,
/// Windows ids (<c>W. Europe Standard Time</c>, which Outlook writes) on every platform. A TZID the
/// platform does not know — a custom <c>VTIMEZONE</c> — is read as UTC rather than the object
/// being refused: the filter is off by the zone's offset at worst, never wrong about which day.
/// Floating times and DATE values are read as UTC for the same reason; RFC 4791 would have them in
/// the calendar's own zone, which a store is not required to have.
/// </para>
/// </summary>
static class ICalTime
{
    static readonly ConcurrentDictionary<string, TimeZoneInfo?> Zones = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Reads a DATE or DATE-TIME property, honouring its TZID.</summary>
    public static bool TryRead(ContentProperty? property, out ICalDateTime value)
    {
        value = default;

        if (property is null)
            return false;

        // A multi-valued RDATE/EXDATE is read one value at a time by the caller; this is the first.
        var text = property.Value;
        var comma = text.IndexOf(',');

        if (comma >= 0)
            text = text[..comma];

        return TryParse(text, property.GetParameter("TZID"), out value);
    }

    /// <summary>Parses <c>20260105</c>, <c>20260105T090000</c> or <c>20260105T090000Z</c>.</summary>
    public static bool TryParse(string text, string? tzid, out ICalDateTime value)
    {
        value = default;
        text = text.Trim();

        if (text.Length == 8)
        {
            if (!DateTime.TryParseExact(text, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                return false;

            value = new ICalDateTime(date, IsDate: true, IsUtc: false, Zone: null);
            return true;
        }

        var utc = text.EndsWith('Z') || text.EndsWith('z');
        var body = utc ? text[..^1] : text;

        if (body.Length != 15 ||
            !DateTime.TryParseExact(body, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            return false;

        value = new ICalDateTime(local, IsDate: false, IsUtc: utc, Zone: utc ? null : FindZone(tzid));
        return true;
    }

    /// <summary>A CalDAV <c>time-range</c> attribute, which RFC 4791 §9.9 requires to be UTC.</summary>
    public static bool TryParseUtc(string? text, out DateTime utc)
    {
        utc = default;

        if (text is null || !TryParse(text, null, out var value))
            return false;

        utc = DateTime.SpecifyKind(value.Local, DateTimeKind.Utc);
        return true;
    }

    public static DateTime ToUtc(DateTime local, bool isUtc, TimeZoneInfo? zone)
    {
        if (isUtc || zone is null)
            return DateTime.SpecifyKind(local, DateTimeKind.Utc);

        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);

        // A wall-clock time inside a spring-forward gap does not exist. RFC 5545 §3.3.5 says to read
        // it with the offset from before the gap, which lands it an hour later.
        if (zone.IsInvalidTime(unspecified))
            unspecified = unspecified.AddHours(1);

        return TimeZoneInfo.ConvertTimeToUtc(unspecified, zone);
    }

    /// <summary>
    /// A duration: <c>P1D</c>, <c>PT1H30M</c>, <c>-PT15M</c>, <c>P2W</c>.
    /// </summary>
    public static bool TryParseDuration(string? text, out TimeSpan duration)
    {
        duration = default;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        var span = text.Trim().AsSpan();
        var negative = false;

        if (span[0] is '+' or '-')
        {
            negative = span[0] == '-';
            span = span[1..];
        }

        if (span.Length < 2 || span[0] is not ('P' or 'p'))
            return false;

        span = span[1..];

        var inTime = false;
        var total = TimeSpan.Zero;
        var number = 0L;
        var hasNumber = false;
        var parts = 0;

        foreach (var c in span)
        {
            if (char.IsAsciiDigit(c))
            {
                number = checked(number * 10 + (c - '0'));
                hasNumber = true;

                if (number > 1_000_000)
                    return false;

                continue;
            }

            var unit = char.ToUpperInvariant(c);

            if (unit == 'T')
            {
                inTime = true;
                continue;
            }

            if (!hasNumber)
                return false;

            TimeSpan? part = (unit, inTime) switch
            {
                ('W', false) => TimeSpan.FromDays(7 * number),
                ('D', false) => TimeSpan.FromDays(number),
                ('H', true) => TimeSpan.FromHours(number),
                ('M', true) => TimeSpan.FromMinutes(number),
                ('S', true) => TimeSpan.FromSeconds(number),
                _ => null
            };

            if (part is null)
                return false;

            total += part.Value;
            parts++;

            number = 0;
            hasNumber = false;
        }

        if (hasNumber || parts == 0)
            return false;

        duration = negative ? -total : total;
        return true;
    }

    /// <summary>
    /// A PERIOD (<c>start/end</c> or <c>start/duration</c>), as <c>FREEBUSY</c> and <c>RDATE</c>
    /// carry them.
    /// </summary>
    public static bool TryParsePeriod(string text, string? tzid, out DateTime startUtc, out DateTime endUtc)
    {
        startUtc = endUtc = default;

        var slash = text.IndexOf('/');

        if (slash <= 0 || !TryParse(text[..slash], tzid, out var start))
            return false;

        startUtc = start.ToUtc();
        var rest = text[(slash + 1)..];

        if (rest.StartsWith('P') || rest.StartsWith('+') || rest.StartsWith('-'))
        {
            if (!TryParseDuration(rest, out var duration))
                return false;

            endUtc = startUtc + duration;
            return true;
        }

        if (!TryParse(rest, tzid, out var end))
            return false;

        endUtc = end.ToUtc();
        return true;
    }

    public static string FormatUtc(DateTime utc)
        => utc.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// A TZID as a system zone. Also tries the tail of a path-shaped id, which older Thunderbird and
    /// Lightning builds wrote (<c>/mozilla.org/20050126_1/America/New_York</c>).
    /// </summary>
    public static TimeZoneInfo? FindZone(string? tzid)
    {
        if (string.IsNullOrWhiteSpace(tzid))
            return null;

        return Zones.GetOrAdd(tzid, static id =>
        {
            if (TryFind(id, out var zone))
                return zone;

            var segments = id.Split('/', StringSplitOptions.RemoveEmptyEntries);

            for (var take = Math.Min(3, segments.Length); take >= 1; take--)
            {
                if (TryFind(string.Join('/', segments[^take..]), out zone))
                    return zone;
            }

            return null;
        });
    }

    static bool TryFind(string id, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out TimeZoneInfo? zone)
    {
        zone = null;

        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            return false;
        }
    }
}
