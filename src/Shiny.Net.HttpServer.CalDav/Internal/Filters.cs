namespace Shiny.Net.HttpServer.CalDav.Internal;

/// <summary>A <c>CALDAV:time-range</c>: UTC, either end open.</summary>
sealed record TimeRange(DateTime? Start, DateTime? End)
{
    DateTime From => this.Start ?? DateTime.MinValue;

    DateTime To => this.End ?? DateTime.MaxValue;

    /// <summary>
    /// RFC 4791 §9.9: a span with length overlaps when it starts before the range ends and ends after
    /// it starts; a zero-length one when it falls in <c>[start, end)</c>.
    /// </summary>
    public bool Overlaps(DateTime start, DateTime end)
        => end > start
            ? this.From < end && this.To > start
            : this.From <= start && this.To > start;

    /// <summary>A DATE-TIME property value, tested as an instant.</summary>
    public bool Contains(DateTime instant) => this.From <= instant && this.To > instant;
}

/// <summary>
/// A <c>text-match</c>, from either protocol. CalDAV's is always a substring match; CardDAV adds
/// <c>match-type</c>, which is accepted from both for leniency.
/// </summary>
sealed class TextMatch
{
    public const string AsciiCasemap = "i;ascii-casemap";
    public const string Octet = "i;octet";
    public const string UnicodeCasemap = "i;unicode-casemap";

    public string Text { get; init; } = string.Empty;

    public string Collation { get; init; } = AsciiCasemap;

    public string MatchType { get; init; } = "contains";

    public bool Negate { get; init; }

    public static bool IsSupportedCollation(string collation)
        => collation is AsciiCasemap or Octet or UnicodeCasemap;

    public static bool IsSupportedMatchType(string matchType)
        => matchType is "equals" or "contains" or "starts-with" or "ends-with";

    public bool Matches(string value)
    {
        string haystack = value, needle = this.Text;
        var comparison = StringComparison.Ordinal;

        switch (this.Collation)
        {
            case AsciiCasemap:
                // Only A–Z fold under i;ascii-casemap (RFC 4790 §9.2); OrdinalIgnoreCase would also
                // fold letters outside ASCII, which this collation must not.
                haystack = FoldAscii(haystack);
                needle = FoldAscii(needle);
                break;

            case UnicodeCasemap:
                comparison = StringComparison.OrdinalIgnoreCase;
                break;
        }

        var matched = this.MatchType switch
        {
            "equals" => string.Equals(haystack, needle, comparison),
            "starts-with" => haystack.StartsWith(needle, comparison),
            "ends-with" => haystack.EndsWith(needle, comparison),
            _ => haystack.Contains(needle, comparison)
        };

        return matched != this.Negate;
    }

    static string FoldAscii(string value)
    {
        var chars = value.ToCharArray();

        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] is >= 'A' and <= 'Z')
                chars[i] = (char)(chars[i] + 32);
        }

        return new string(chars);
    }
}

sealed class ParamFilter
{
    public string Name { get; init; } = string.Empty;

    public bool IsNotDefined { get; set; }

    public TextMatch? TextMatch { get; set; }

    public bool Matches(ContentProperty property)
    {
        var parameters = property.Parameters.Where(p => p.Name.Equals(this.Name, StringComparison.OrdinalIgnoreCase)).ToList();

        if (this.IsNotDefined)
            return parameters.Count == 0;

        if (parameters.Count == 0)
            return false;

        return this.TextMatch is not { } match || parameters.Any(p => p.Values.Any(match.Matches));
    }
}

sealed class PropFilter
{
    public string Name { get; init; } = string.Empty;

    public bool IsNotDefined { get; set; }

    public TimeRange? TimeRange { get; set; }

    public List<TextMatch> TextMatches { get; } = [];

    public List<ParamFilter> ParamFilters { get; } = [];

    /// <summary>
    /// How the tests inside combine. Always true for CalDAV, where they are a conjunction; CardDAV's
    /// <c>test</c> attribute defaults it to false, "anyof".
    /// </summary>
    public bool AllOf { get; set; } = true;

    public bool Matches(ContentComponent component)
    {
        var properties = component.GetProperties(this.Name).ToList();

        if (this.IsNotDefined)
            return properties.Count == 0;

        return properties.Any(this.MatchesProperty);
    }

    bool MatchesProperty(ContentProperty property)
    {
        var tests = new List<Func<bool>>();

        if (this.TimeRange is { } range)
            tests.Add(() => ICalTime.TryRead(property, out var value) && range.Contains(value.ToUtc()));

        foreach (var match in this.TextMatches)
            tests.Add(() => match.Matches(property.GetText()));

        foreach (var parameter in this.ParamFilters)
            tests.Add(() => parameter.Matches(property));

        if (tests.Count == 0)
            return true;

        return this.AllOf ? tests.All(t => t()) : tests.Any(t => t());
    }
}

sealed class CompFilter
{
    public string Name { get; init; } = string.Empty;

    public bool IsNotDefined { get; set; }

    public TimeRange? TimeRange { get; set; }

    public List<CompFilter> CompFilters { get; } = [];

    public List<PropFilter> PropFilters { get; } = [];
}

/// <summary>
/// A <c>CARDDAV:filter</c>: prop-filters combined by its <c>test</c> attribute (RFC 6352 §10.5).
/// </summary>
sealed class AddressBookFilter
{
    public bool AllOf { get; set; }

    public List<PropFilter> PropFilters { get; } = [];

    public bool Matches(ContentComponent card)
    {
        if (this.PropFilters.Count == 0)
            return true;

        return this.AllOf
            ? this.PropFilters.All(p => p.Matches(card))
            : this.PropFilters.Any(p => p.Matches(card));
    }
}

/// <summary>Evaluating a <c>CALDAV:filter</c> (RFC 4791 §9.7) against one calendar object.</summary>
sealed class CalendarFilterEvaluator(int maxInstances)
{
    public bool Matches(CompFilter root, ContentComponent calendar)
    {
        if (!root.Name.Equals(calendar.Name, StringComparison.OrdinalIgnoreCase))
            return root.IsNotDefined;

        return !root.IsNotDefined && this.Evaluate(root, calendar, calendar);
    }

    bool Evaluate(CompFilter filter, ContentComponent component, ContentComponent calendar)
    {
        if (filter.TimeRange is { } range && !this.InRange(component, calendar, range))
            return false;

        foreach (var child in filter.CompFilters)
        {
            var candidates = component.GetComponents(child.Name).ToList();

            if (child.IsNotDefined)
            {
                if (candidates.Count > 0)
                    return false;

                continue;
            }

            if (!candidates.Any(c => this.Evaluate(child, c, calendar)))
                return false;
        }

        foreach (var property in filter.PropFilters)
        {
            if (!property.Matches(component))
                return false;
        }

        return true;
    }

    /// <summary>Whether a component overlaps a time range, recurrence included (RFC 4791 §9.9).</summary>
    public bool InRange(ContentComponent component, ContentComponent calendar, TimeRange range)
        => component.Name switch
        {
            "VEVENT" => this.EventInRange(component, calendar, range),
            "VTODO" => this.TodoInRange(component, calendar, range),
            "VJOURNAL" => this.JournalInRange(component, calendar, range),
            "VFREEBUSY" => FreeBusyInRange(component, range),

            // An alarm's time-range is its trigger, relative to an instance it belongs to. Not
            // implemented; see the docs page's list of limits.
            _ => false
        };

    /// <summary>
    /// The periods an event occupies inside a range: its instances, each with the event's length.
    /// Empty when it has no DTSTART.
    /// </summary>
    public IEnumerable<(DateTime Start, DateTime End)> EventPeriods(ContentComponent component, ContentComponent calendar, TimeRange range)
    {
        if (!ICalTime.TryRead(component.GetProperty("DTSTART"), out var start))
            yield break;

        var duration = EventDuration(component, start);
        var occurrences = Recurrence.Expand(component, calendar, range.End ?? FarFuture, maxInstances);

        foreach (var instance in occurrences.StartsUtc)
        {
            if (range.Overlaps(instance, instance + duration))
                yield return (instance, instance + duration);
        }
    }

    static readonly DateTime FarFuture = new(9000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    bool EventInRange(ContentComponent component, ContentComponent calendar, TimeRange range)
    {
        if (!ICalTime.TryRead(component.GetProperty("DTSTART"), out var start))
            return false;

        var duration = EventDuration(component, start);
        var occurrences = Recurrence.Expand(component, calendar, range.End ?? FarFuture, maxInstances);

        if (occurrences.Approximate && ApproximatelyInRange(occurrences, start.ToUtc(), duration, range))
            return true;

        foreach (var instance in occurrences.StartsUtc)
        {
            if (range.Overlaps(instance, instance + duration))
                return true;
        }

        return false;
    }

    /// <summary>
    /// RFC 4791 §9.9's VEVENT rows: DTEND, else DURATION, else a day for an all-day event and
    /// nothing at all for a timed one.
    /// </summary>
    static TimeSpan EventDuration(ContentComponent component, ICalDateTime start)
    {
        if (ICalTime.TryRead(component.GetProperty("DTEND"), out var end))
        {
            var length = end.ToUtc() - start.ToUtc();
            return length > TimeSpan.Zero ? length : TimeSpan.Zero;
        }

        if (ICalTime.TryParseDuration(component.GetProperty("DURATION")?.Value, out var duration))
            return duration > TimeSpan.Zero ? duration : TimeSpan.Zero;

        return start.IsDate ? TimeSpan.FromDays(1) : TimeSpan.Zero;
    }

    bool TodoInRange(ContentComponent component, ContentComponent calendar, TimeRange range)
    {
        var hasStart = ICalTime.TryRead(component.GetProperty("DTSTART"), out var dtStart);
        var hasDue = ICalTime.TryRead(component.GetProperty("DUE"), out var due);
        var hasDuration = ICalTime.TryParseDuration(component.GetProperty("DURATION")?.Value, out var duration);
        var hasCompleted = ICalTime.TryRead(component.GetProperty("COMPLETED"), out var completed);
        var hasCreated = ICalTime.TryRead(component.GetProperty("CREATED"), out var created);

        var from = range.Start ?? DateTime.MinValue;
        var to = range.End ?? DateTime.MaxValue;

        bool Test(TimeSpan shift)
        {
            // The rows of RFC 4791 §9.9's VTODO table, in its order.
            if (hasStart && hasDuration)
            {
                var d = dtStart.ToUtc() + shift;
                return from <= d + duration && (to > d || to >= d + duration);
            }

            if (hasStart && hasDue)
            {
                var d = dtStart.ToUtc() + shift;
                var u = due.ToUtc() + shift;
                return (from < u || from <= d) && (to > d || to >= u);
            }

            if (hasStart)
            {
                var d = dtStart.ToUtc() + shift;
                return from <= d && to > d;
            }

            if (hasDue)
            {
                var u = due.ToUtc() + shift;
                return from < u && to >= u;
            }

            if (hasCompleted && hasCreated)
            {
                var c = completed.ToUtc();
                var r = created.ToUtc();
                return (from <= r || from <= c) && (to >= r || to >= c);
            }

            if (hasCompleted)
                return from <= completed.ToUtc() && to >= completed.ToUtc();

            if (hasCreated)
                return to > created.ToUtc();

            return true;
        }

        if (!hasStart)
            return Test(TimeSpan.Zero);

        var startUtc = dtStart.ToUtc();
        var occurrences = Recurrence.Expand(component, calendar, range.End ?? FarFuture, maxInstances);
        var span = hasDuration ? duration : hasDue ? due.ToUtc() - startUtc : TimeSpan.Zero;

        if (occurrences.Approximate && ApproximatelyInRange(occurrences, startUtc, span, range))
            return true;

        return occurrences.StartsUtc.Any(instance => Test(instance - startUtc));
    }

    bool JournalInRange(ContentComponent component, ContentComponent calendar, TimeRange range)
    {
        if (!ICalTime.TryRead(component.GetProperty("DTSTART"), out var start))
            return false;

        var length = start.IsDate ? TimeSpan.FromDays(1) : TimeSpan.Zero;
        var occurrences = Recurrence.Expand(component, calendar, range.End ?? FarFuture, maxInstances);

        if (occurrences.Approximate && ApproximatelyInRange(occurrences, start.ToUtc(), length, range))
            return true;

        return occurrences.StartsUtc.Any(instance => range.Overlaps(instance, instance + length));
    }

    static bool FreeBusyInRange(ContentComponent component, TimeRange range)
    {
        foreach (var property in component.GetProperties("FREEBUSY"))
        {
            foreach (var period in property.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (ICalTime.TryParsePeriod(period, null, out var start, out var end) && range.Overlaps(start, end))
                    return true;
            }
        }

        if (ICalTime.TryRead(component.GetProperty("DTSTART"), out var dtStart) &&
            ICalTime.TryRead(component.GetProperty("DTEND"), out var dtEnd))
            return range.Overlaps(dtStart.ToUtc(), dtEnd.ToUtc());

        return false;
    }

    /// <summary>
    /// For a series that could not be expanded: could any instance, from the first start up to the
    /// last one possible, fall in the range?
    /// </summary>
    static bool ApproximatelyInRange(Occurrences occurrences, DateTime firstStart, TimeSpan length, TimeRange range)
    {
        if (range.End is { } end && firstStart >= end)
            return false;

        if (range.Start is { } start && occurrences.LastPossibleUtc is { } last && last + length < start)
            return false;

        return true;
    }
}
