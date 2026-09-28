using System.Net;
using System.Text;
using Shiny.Net.HttpServer.CalDav;
using Shiny.Net.HttpServer.CalDav.Internal;
using Shiny.Net.HttpServer.Testing;

namespace Shiny.Net.HttpServer.Tests;

/// <summary>
/// The iCalendar/vCard content-line parser and the recurrence expander, on their own — the parts of
/// the CalDAV package whose mistakes would show up as an event quietly missing from a query.
/// </summary>
public class CalDavParserTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void Unfolds_lines_and_reads_parameters_and_escapes()
    {
        const string text = "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:1\r\n"
            + "SUMMARY:Planning\\, with notes\\; and a\\nnewline and a long line that\r\n  is folded\r\n"
            + "ATTENDEE;CN=\"Lovelace, Ada\";ROLE=REQ-PARTICIPANT:mailto:ada@example.com\r\n"
            + "X-CAT;X-TAGS=a,b,\"c;d\":value\n"
            + "END:VEVENT\r\nEND:VCALENDAR";

        var calendar = ContentComponent.Parse(text);
        var evt = calendar.GetComponents("vevent").Single();

        Assert.Equal("Planning, with notes; and a\nnewline and a long line that is folded", evt.GetProperty("summary")!.GetText());

        var attendee = evt.GetProperty("ATTENDEE")!;
        Assert.Equal("Lovelace, Ada", attendee.GetParameter("cn"));
        Assert.Equal("mailto:ada@example.com", attendee.Value);

        Assert.Equal(["a", "b", "c;d"], evt.GetProperty("X-CAT")!.Parameters.Single().Values);
    }

    [Fact]
    public void Reads_vcard_groups_and_bare_21_parameters()
    {
        var card = ContentComponent.Parse("BEGIN:VCARD\r\nVERSION:2.1\r\nitem1.EMAIL;HOME:a@example.com\r\nFN:A\r\nEND:VCARD\r\n");
        var email = card.GetProperty("EMAIL")!;

        Assert.Equal("item1", email.Group);
        Assert.Equal("HOME", email.GetParameter("TYPE"));
    }

    [Theory]
    [InlineData("BEGIN:VCALENDAR\r\nEND:VEVENT\r\n")]
    [InlineData("BEGIN:VCALENDAR\r\n")]
    [InlineData("SUMMARY:outside\r\n")]
    [InlineData("BEGIN:VCARD\r\nEND:VCARD\r\nBEGIN:VCARD\r\nEND:VCARD\r\n")]
    [InlineData("BEGIN:VCARD\r\nno colon here\r\nEND:VCARD\r\n")]
    [InlineData("")]
    public void Refuses_what_is_not_one_well_formed_object(string text)
        => Assert.False(ContentComponent.TryParse(text, out _, out _));

    [Fact]
    public void Writes_back_folded_at_75_octets_without_splitting_a_character()
    {
        var component = new ContentComponent("VEVENT");
        component.Properties.Add(new ContentProperty("SUMMARY", ContentProperty.EscapeText(new string('é', 100) + ", done")));

        var text = component.ToString();

        foreach (var line in text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
            Assert.True(Encoding.UTF8.GetByteCount(line) <= 75, $"'{line}' is longer than 75 octets");

        Assert.Equal(new string('é', 100) + ", done", ContentComponent.Parse(text).GetProperty("SUMMARY")!.GetText());
    }

    static List<DateTime> Expand(string rule, string start = "20260105T090000Z", string until = "20270101T000000Z")
    {
        var evt = ContentComponent.Parse($"BEGIN:VEVENT\r\nUID:x\r\nDTSTART:{start}\r\nRRULE:{rule}\r\nEND:VEVENT\r\n");
        ICalTime.TryParseUtc(until, out var end);

        var occurrences = Recurrence.Expand(evt, null, end, 1000);
        Assert.False(occurrences.Approximate);

        return [.. occurrences.StartsUtc];
    }

    static DateTime Utc(int year, int month, int day, int hour = 9) => new(year, month, day, hour, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Expands_the_last_friday_of_each_month()
    {
        var starts = Expand("FREQ=MONTHLY;BYDAY=-1FR;COUNT=4", "20260130T090000Z");

        Assert.Equal([Utc(2026, 1, 30), Utc(2026, 2, 27), Utc(2026, 3, 27), Utc(2026, 4, 24)], starts);
    }

    [Fact]
    public void Skips_months_without_the_day_rather_than_clamping()
    {
        var starts = Expand("FREQ=MONTHLY;COUNT=3", "20260131T090000Z");

        Assert.Equal([Utc(2026, 1, 31), Utc(2026, 3, 31), Utc(2026, 5, 31)], starts);
    }

    [Fact]
    public void Expands_a_yearly_rule_and_a_negative_month_day()
    {
        Assert.Equal(
            [Utc(2026, 3, 15), Utc(2027, 3, 15), Utc(2028, 3, 15)],
            Expand("FREQ=YEARLY;COUNT=3", "20260315T090000Z", "20300101T000000Z")
        );

        Assert.Equal(
            [Utc(2026, 1, 31), Utc(2026, 2, 28), Utc(2026, 3, 31)],
            Expand("FREQ=MONTHLY;BYMONTHDAY=-1;COUNT=3", "20260131T090000Z")
        );
    }

    [Fact]
    public void Expands_a_fortnightly_rule_on_two_days()
    {
        var starts = Expand("FREQ=WEEKLY;INTERVAL=2;BYDAY=TU,TH;COUNT=4", "20260106T090000Z");

        Assert.Equal([Utc(2026, 1, 6), Utc(2026, 1, 8), Utc(2026, 1, 20), Utc(2026, 1, 22)], starts);
    }

    [Fact]
    public void A_rule_it_cannot_expand_is_approximate_rather_than_empty()
    {
        var evt = ContentComponent.Parse("BEGIN:VEVENT\r\nUID:x\r\nDTSTART:20260105T090000Z\r\nRRULE:FREQ=MONTHLY;BYDAY=MO,TU;BYSETPOS=-1\r\nEND:VEVENT\r\n");
        var occurrences = Recurrence.Expand(evt, null, new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc), 1000);

        Assert.True(occurrences.Approximate);

        // Approximate means "might be anywhere after the start", so a range in it matches.
        var evaluator = new CalendarFilterEvaluator(1000);
        var calendar = new ContentComponent("VCALENDAR");
        calendar.Components.Add(evt);

        Assert.True(evaluator.InRange(evt, calendar, new TimeRange(Utc(2026, 6, 1, 0), Utc(2026, 6, 2, 0))));
        Assert.False(evaluator.InRange(evt, calendar, new TimeRange(Utc(2025, 6, 1, 0), Utc(2025, 6, 2, 0))));
    }

    [Theory]
    [InlineData("PT1H30M", 90)]
    [InlineData("P1D", 1440)]
    [InlineData("P1W", 10080)]
    [InlineData("-PT15M", -15)]
    [InlineData("P1DT1H", 1500)]
    public void Reads_durations(string text, int minutes)
    {
        Assert.True(ICalTime.TryParseDuration(text, out var duration));
        Assert.Equal(TimeSpan.FromMinutes(minutes), duration);
    }

    [Theory]
    [InlineData("P")]
    [InlineData("PT")]
    [InlineData("1H")]
    [InlineData("PT1D")]
    public void Refuses_malformed_durations(string text)
        => Assert.False(ICalTime.TryParseDuration(text, out _));

    [Fact]
    public async Task Serves_the_same_protocol_over_the_in_memory_path_tunnels_use()
    {
        // TestHttpServer hands connections to ServeAsync exactly as every ITunnelProvider does, so
        // this is the tunneled path without standing a tunnel up.
        using var root = new ContentRoot();

        await using var app = TestHttpServer.Create(server => server.MapCalDav("/dav", o =>
        {
            o.CalendarStore = new FileCalendarStore(root.Path);
            o.DefaultPrincipal = "me";
        }));

        var put = new HttpRequestMessage(HttpMethod.Put, "/dav/calendars/me/default/a.ics")
        {
            Content = new StringContent(
                "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VEVENT\r\nUID:a\r\nDTSTART:20260105T090000Z\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n",
                Encoding.UTF8,
                "text/calendar"
            )
        };

        Assert.Equal(HttpStatusCode.Created, (await app.Client.SendAsync(put, Token)).StatusCode);

        var sync = new HttpRequestMessage(new HttpMethod("REPORT"), "/dav/calendars/me/default/")
        {
            Content = new StringContent(
                """<D:sync-collection xmlns:D="DAV:"><D:sync-token/><D:prop><D:getetag/></D:prop></D:sync-collection>""",
                Encoding.UTF8,
                "application/xml"
            )
        };

        var response = await app.Client.SendAsync(sync, Token);

        Assert.Equal(207, (int)response.StatusCode);
        Assert.Contains("/dav/calendars/me/default/a.ics", await response.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
    }
}
