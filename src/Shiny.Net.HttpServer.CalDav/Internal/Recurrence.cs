using System.Globalization;

namespace Shiny.Net.HttpServer.CalDav.Internal;

/// <summary>
/// An <c>RRULE</c> (RFC 5545 §3.3.10), reduced to the parts this server expands.
/// <para>
/// <b>Expanded:</b> <c>FREQ</c> of <c>DAILY</c>, <c>WEEKLY</c>, <c>MONTHLY</c> and <c>YEARLY</c>, with
/// <c>INTERVAL</c>, <c>COUNT</c>, <c>UNTIL</c>, <c>WKST</c>, <c>BYDAY</c> (with ordinals such as
/// <c>2TU</c> and <c>-1FR</c> for monthly and yearly rules), <c>BYMONTHDAY</c> (negative from the end
/// of the month) and <c>BYMONTH</c>. That covers what calendar apps actually write: every day, every
/// other week on Monday and Wednesday, the last Friday of the month, every year on a birthday.
/// </para>
/// <para>
/// <b>Not expanded:</b> <c>BYSETPOS</c>, <c>BYWEEKNO</c>, <c>BYYEARDAY</c>, <c>BYHOUR</c>,
/// <c>BYMINUTE</c>, <c>BYSECOND</c> and the sub-daily frequencies. A rule using them is marked
/// <see cref="Supported"/> false, and a time-range filter then treats the series as possibly
/// occurring anywhere between its start and its <c>UNTIL</c> — returning an event the client did not
/// need is harmless, since the client filters again, while leaving out one it did is a missing
/// meeting.
/// </para>
/// </summary>
sealed class RecurrenceRule
{
    public string Frequency { get; private set; } = string.Empty;

    public int Interval { get; private set; } = 1;

    public int? Count { get; private set; }

    public ICalDateTime? Until { get; private set; }

    public List<(int Ordinal, DayOfWeek Day)> ByDay { get; } = [];

    public List<int> ByMonthDay { get; } = [];

    public List<int> ByMonth { get; } = [];

    public DayOfWeek WeekStart { get; private set; } = DayOfWeek.Monday;

    public bool Supported { get; private set; } = true;

    /// <summary>Null when the rule is malformed — no FREQ, or a part that does not parse.</summary>
    public static RecurrenceRule? Parse(string value, string? tzid)
    {
        var rule = new RecurrenceRule();

        foreach (var part in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var equals = part.IndexOf('=');

            if (equals <= 0)
                return null;

            var name = part[..equals].ToUpperInvariant();
            var text = part[(equals + 1)..];

            switch (name)
            {
                case "FREQ":
                    rule.Frequency = text.ToUpperInvariant();

                    if (rule.Frequency is not ("DAILY" or "WEEKLY" or "MONTHLY" or "YEARLY"))
                        rule.Supported = false;

                    break;

                case "INTERVAL":
                    if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var interval) || interval < 1)
                        return null;

                    rule.Interval = interval;
                    break;

                case "COUNT":
                    if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count < 1)
                        return null;

                    rule.Count = count;
                    break;

                case "UNTIL":
                    // UNTIL is UTC whenever DTSTART is zoned (RFC 5545 §3.3.10); the TZID is only a
                    // fallback for the floating case.
                    if (!ICalTime.TryParse(text, tzid, out var until))
                        return null;

                    rule.Until = until;
                    break;

                case "WKST":
                    if (ParseDay(text) is not { } weekStart)
                        return null;

                    rule.WeekStart = weekStart;
                    break;

                case "BYDAY":
                    foreach (var item in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (item.Length < 2 || ParseDay(item[^2..]) is not { } day)
                            return null;

                        var ordinal = 0;

                        if (item.Length > 2 &&
                            (!int.TryParse(item[..^2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out ordinal) ||
                             ordinal is 0 or < -53 or > 53))
                            return null;

                        rule.ByDay.Add((ordinal, day));
                    }

                    break;

                case "BYMONTHDAY":
                    if (!TryParseList(text, -31, 31, rule.ByMonthDay))
                        return null;

                    break;

                case "BYMONTH":
                    if (!TryParseList(text, 1, 12, rule.ByMonth))
                        return null;

                    break;

                case "BYSETPOS":
                case "BYWEEKNO":
                case "BYYEARDAY":
                case "BYHOUR":
                case "BYMINUTE":
                case "BYSECOND":
                    rule.Supported = false;
                    break;
            }
        }

        return rule.Frequency.Length == 0 ? null : rule;
    }

    /// <summary>
    /// Every candidate start the rule describes, in order, from the period holding
    /// <paramref name="start"/> onward. Not filtered by COUNT or UNTIL — the caller does that, since
    /// both depend on the first instance, which is DTSTART whether or not the rule produces it.
    /// </summary>
    public IEnumerable<DateTime> Candidates(DateTime start)
    {
        var time = start.TimeOfDay;

        // Periods with no match at all (BYMONTHDAY=30 with BYMONTH=2) would otherwise loop forever
        // without yielding; this ends the walk well before DateTime runs out of years.
        const int maxPeriods = 200_000;

        switch (this.Frequency)
        {
            case "DAILY":
            {
                var day = start.Date;

                for (var i = 0; i < maxPeriods && day.Year < 9000; i++, day = day.AddDays(this.Interval))
                {
                    if (this.PassesMonth(day) && this.PassesMonthDay(day) && this.PassesWeekday(day))
                        yield return day + time;
                }

                break;
            }

            case "WEEKLY":
            {
                var offset = ((int)start.DayOfWeek - (int)this.WeekStart + 7) % 7;
                var week = start.Date.AddDays(-offset);

                for (var i = 0; i < maxPeriods && week.Year < 9000; i++, week = week.AddDays(7 * this.Interval))
                {
                    for (var d = 0; d < 7; d++)
                    {
                        var day = week.AddDays(d);

                        var matches = this.ByDay.Count == 0
                            ? day.DayOfWeek == start.DayOfWeek
                            : this.ByDay.Any(b => b.Day == day.DayOfWeek);

                        if (matches && this.PassesMonth(day) && this.PassesMonthDay(day))
                            yield return day + time;
                    }
                }

                break;
            }

            case "MONTHLY":
            {
                var month = new DateTime(start.Year, start.Month, 1);

                for (var i = 0; i < maxPeriods && month.Year < 9000; i++, month = month.AddMonths(this.Interval))
                {
                    if (!this.PassesMonth(month))
                        continue;

                    foreach (var day in this.DaysInMonth(month, start))
                        yield return day + time;
                }

                break;
            }

            case "YEARLY":
            {
                for (var year = start.Year; year < 9000 && year - start.Year < maxPeriods; year += this.Interval)
                {
                    if (this.ByMonth.Count == 0 && this.ByMonthDay.Count == 0 && this.ByDay.Count > 0)
                    {
                        // BYDAY with no month narrows within the whole year: 20MO is the twentieth
                        // Monday of it.
                        foreach (var day in DaysMatching(new DateTime(year, 1, 1), new DateTime(year, 12, 31), this.ByDay))
                            yield return day + time;

                        continue;
                    }

                    var months = this.ByMonth.Count > 0 ? this.ByMonth.Order().ToList() : [start.Month];

                    foreach (var m in months)
                    {
                        foreach (var day in this.DaysInMonth(new DateTime(year, m, 1), start))
                            yield return day + time;
                    }
                }

                break;
            }
        }
    }

    IEnumerable<DateTime> DaysInMonth(DateTime first, DateTime start)
    {
        var length = DateTime.DaysInMonth(first.Year, first.Month);

        if (this.ByMonthDay.Count > 0)
        {
            var days = new SortedSet<int>();

            foreach (var d in this.ByMonthDay)
            {
                var resolved = d > 0 ? d : length + 1 + d;

                if (resolved >= 1 && resolved <= length)
                    days.Add(resolved);
            }

            foreach (var d in days)
            {
                var day = first.AddDays(d - 1);

                if (this.PassesWeekday(day))
                    yield return day;
            }

            yield break;
        }

        if (this.ByDay.Count > 0)
        {
            foreach (var day in DaysMatching(first, first.AddDays(length - 1), this.ByDay))
                yield return day;

            yield break;
        }

        // The start's own day of the month. A month without one (the 31st in April) is skipped,
        // as RFC 5545 §3.3.10 requires, rather than clamped to the last day.
        if (start.Day <= length)
            yield return first.AddDays(start.Day - 1);
    }

    /// <summary>The days between two dates that a BYDAY list picks, ordinals included, in order.</summary>
    static IEnumerable<DateTime> DaysMatching(DateTime first, DateTime last, List<(int Ordinal, DayOfWeek Day)> byDay)
    {
        var picked = new SortedSet<DateTime>();

        foreach (var (ordinal, weekday) in byDay)
        {
            var all = new List<DateTime>();

            for (var day = first; day <= last; day = day.AddDays(1))
            {
                if (day.DayOfWeek == weekday)
                    all.Add(day);
            }

            if (ordinal == 0)
            {
                foreach (var day in all)
                    picked.Add(day);
            }
            else
            {
                var index = ordinal > 0 ? ordinal - 1 : all.Count + ordinal;

                if (index >= 0 && index < all.Count)
                    picked.Add(all[index]);
            }
        }

        return picked;
    }

    bool PassesMonth(DateTime day) => this.ByMonth.Count == 0 || this.ByMonth.Contains(day.Month);

    bool PassesWeekday(DateTime day) => this.ByDay.Count == 0 || this.ByDay.Any(b => b.Day == day.DayOfWeek);

    bool PassesMonthDay(DateTime day)
    {
        if (this.ByMonthDay.Count == 0)
            return true;

        var length = DateTime.DaysInMonth(day.Year, day.Month);

        return this.ByMonthDay.Any(d => (d > 0 ? d : length + 1 + d) == day.Day);
    }

    static bool TryParseList(string text, int min, int max, List<int> target)
    {
        foreach (var item in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(item, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ||
                value < min || value > max || value == 0)
                return false;

            target.Add(value);
        }

        return true;
    }

    static DayOfWeek? ParseDay(string text) => text.ToUpperInvariant() switch
    {
        "SU" => DayOfWeek.Sunday,
        "MO" => DayOfWeek.Monday,
        "TU" => DayOfWeek.Tuesday,
        "WE" => DayOfWeek.Wednesday,
        "TH" => DayOfWeek.Thursday,
        "FR" => DayOfWeek.Friday,
        "SA" => DayOfWeek.Saturday,
        _ => null
    };
}

/// <summary>
/// The instances of a component, as far as a time range needs them.
/// </summary>
/// <param name="StartsUtc">Every instance start before the window's end, ascending.</param>
/// <param name="Approximate">
/// The series could not be fully expanded — a rule part this server does not implement, or more
/// instances than <see cref="CalDavOptions.MaxRecurrenceInstances"/>. The caller then treats the
/// series as occurring anywhere from its first start up to <paramref name="LastPossibleUtc"/>.
/// </param>
/// <param name="LastPossibleUtc">The latest an approximate series can occur, or null for no end.</param>
readonly record struct Occurrences(IReadOnlyList<DateTime> StartsUtc, bool Approximate, DateTime? LastPossibleUtc);

/// <summary>Expanding a recurring component's DTSTART into its instance starts.</summary>
static class Recurrence
{
    public static bool IsRecurring(ContentComponent component)
        => component.GetProperty("RRULE") is not null || component.GetProperty("RDATE") is not null;

    /// <summary>
    /// The instance starts of <paramref name="component"/> that begin before
    /// <paramref name="windowEndUtc"/>, with EXDATEs removed and — for a master — the instances that
    /// an override elsewhere in the same object (one with a matching <c>RECURRENCE-ID</c>) replaces.
    /// </summary>
    /// <param name="component">A VEVENT, VTODO or VJOURNAL with a DTSTART.</param>
    /// <param name="calendar">The VCALENDAR it came from, where its overrides are.</param>
    /// <param name="windowEndUtc">Instances starting at or after this are not needed.</param>
    /// <param name="maxInstances">Past this many the series is reported approximate.</param>
    public static Occurrences Expand(ContentComponent component, ContentComponent? calendar, DateTime windowEndUtc, int maxInstances)
    {
        if (!ICalTime.TryRead(component.GetProperty("DTSTART"), out var start))
            return new Occurrences([], false, null);

        var startUtc = start.ToUtc();

        // An override is a single instance, however it is written.
        if (component.GetProperty("RECURRENCE-ID") is not null || !IsRecurring(component))
            return new Occurrences([startUtc], false, null);

        var starts = new SortedSet<DateTime> { startUtc };
        var approximate = false;
        DateTime? lastPossible = null;
        var anyUnbounded = false;

        foreach (var property in component.GetProperties("RRULE"))
        {
            if (RecurrenceRule.Parse(property.Value, property.GetParameter("TZID") ?? start.Zone?.Id) is not { } rule)
                continue;

            if (!rule.Supported)
            {
                approximate = true;

                if (rule.Until is { } bound)
                    lastPossible = Max(lastPossible, bound.ToUtc());
                else
                    anyUnbounded = true;

                continue;
            }

            var produced = 1;

            foreach (var local in rule.Candidates(start.Local))
            {
                if (local <= start.Local)
                    continue;

                var utc = ICalTime.ToUtc(local, start.IsUtc, start.Zone);

                if (rule.Until is { } until && IsAfter(local, utc, until))
                    break;

                if (rule.Count is { } count && produced >= count)
                    break;

                produced++;

                if (utc >= windowEndUtc)
                    break;

                starts.Add(utc);

                if (starts.Count > maxInstances)
                {
                    // Past the cap there is no telling what the rest of the series does, so the
                    // answer becomes "maybe" rather than a guess presented as fact.
                    approximate = true;
                    anyUnbounded = true;
                    break;
                }
            }
        }

        foreach (var property in component.GetProperties("RDATE"))
        {
            var tzid = property.GetParameter("TZID");

            foreach (var value in property.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (value.Contains('/'))
                {
                    if (ICalTime.TryParsePeriod(value, tzid, out var periodStart, out _))
                        starts.Add(periodStart);
                }
                else if (ICalTime.TryParse(value, tzid, out var date))
                {
                    starts.Add(date.ToUtc());
                }
            }
        }

        var excludedInstants = new HashSet<DateTime>();
        var excludedDates = new HashSet<DateTime>();

        foreach (var property in component.GetProperties("EXDATE"))
        {
            var tzid = property.GetParameter("TZID");

            foreach (var value in property.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!ICalTime.TryParse(value, tzid, out var excluded))
                    continue;

                if (excluded.IsDate)
                    excludedDates.Add(excluded.Local.Date);
                else
                    excludedInstants.Add(excluded.ToUtc());
            }
        }

        if (calendar is not null)
        {
            var uid = component.GetProperty("UID")?.Value;

            foreach (var sibling in calendar.Components)
            {
                if (ReferenceEquals(sibling, component) ||
                    !sibling.Name.Equals(component.Name, StringComparison.Ordinal) ||
                    !string.Equals(sibling.GetProperty("UID")?.Value, uid, StringComparison.Ordinal))
                    continue;

                if (ICalTime.TryRead(sibling.GetProperty("RECURRENCE-ID"), out var recurrenceId))
                    excludedInstants.Add(recurrenceId.ToUtc());
            }
        }

        var result = new List<DateTime>(starts.Count);

        foreach (var instance in starts)
        {
            if (instance >= windowEndUtc && instance != startUtc)
                continue;

            if (excludedInstants.Contains(instance) || excludedDates.Contains(instance.Date))
                continue;

            result.Add(instance);
        }

        return new Occurrences(result, approximate, anyUnbounded ? null : lastPossible);
    }

    static bool IsAfter(DateTime local, DateTime utc, ICalDateTime until)
    {
        if (until.IsDate)
            return local.Date > until.Local.Date;

        return until.IsUtc ? utc > until.Local : local > until.Local;
    }

    static DateTime? Max(DateTime? a, DateTime b) => a is { } value && value > b ? value : b;
}
