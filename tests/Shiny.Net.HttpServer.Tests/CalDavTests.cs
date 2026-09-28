using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using Shiny.Net.HttpServer.CalDav;
using Shiny.Net.HttpServer.Security;

namespace Shiny.Net.HttpServer.Tests;

/// <summary>
/// A CalDAV/CardDAV mount driven the way real clients drive it, over a real socket, on both HTTP/1.1
/// and HTTP/2: discovery from the well-known URL, the PROPFINDs iOS makes, PUT with its
/// preconditions, each REPORT, and sync-collection deltas.
/// </summary>
public class CalDavTests
{
    static readonly XNamespace D = "DAV:";
    static readonly XNamespace C = "urn:ietf:params:xml:ns:caldav";
    static readonly XNamespace CR = "urn:ietf:params:xml:ns:carddav";
    static readonly XNamespace CS = "http://calendarserver.org/ns/";
    static readonly XNamespace ICAL = "http://apple.com/ns/ical/";

    static CancellationToken Token => TestContext.Current.CancellationToken;

    const string Calendar = "/dav/calendars/ada/default/";
    const string Contacts = "/dav/addressbooks/ada/default/";

    /// <summary>A server with a Basic-protected mount over a fresh directory, and a signed-in client.</summary>
    sealed class Dav : IAsyncDisposable
    {
        readonly ContentRoot? owned;

        Dav(ContentRoot? owned, string path, TestServer server, HttpClient client)
        {
            this.owned = owned;
            this.Path = path;
            this.Server = server;
            this.Client = client;
        }

        public TestServer Server { get; }

        public HttpClient Client { get; }

        public string Path { get; }

        /// <param name="directory">An existing directory to serve, which the caller owns; null makes a fresh one.</param>
        public static async Task<Dav> StartAsync(bool http2 = false, Action<CalDavOptions>? configure = null, string? directory = null, string user = "ada")
        {
            var owned = directory is null ? new ContentRoot() : null;
            directory ??= owned!.Path;

            var server = await TestServer.StartAsync(
                app =>
                {
                    app.UseAuthentication();
                    app.UseAuthorization();

                    app.MapCalDav("/dav", o =>
                    {
                        o.CalendarStore = new FileCalendarStore(System.IO.Path.Combine(directory, "calendars"));
                        o.AddressBookStore = new FileAddressBookStore(System.IO.Path.Combine(directory, "contacts"));
                        configure?.Invoke(o);
                    })
                    .RequireAuthorization();
                },
                builder =>
                {
                    builder.AddAuthentication().AddBasic(o =>
                    {
                        o.AddUser("ada", "hunter2");
                        o.AddUser("bob", "swordfish");
                    });
                    builder.AddAuthorization();
                }
            );

            return new Dav(owned, directory, server, ClientFor(server, http2, user));
        }

        public static HttpClient ClientFor(TestServer server, bool http2, string? user = "ada")
        {
            var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
            {
                BaseAddress = new Uri($"http://127.0.0.1:{server.Port}"),
                Timeout = TimeSpan.FromSeconds(30)
            };

            if (http2)
            {
                client.DefaultRequestVersion = HttpVersion.Version20;
                client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
            }

            if (user is not null)
            {
                var password = user == "ada" ? "hunter2" : "swordfish";
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                    "Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"))
                );
            }

            return client;
        }

        public Task<HttpResponseMessage> SendAsync(string method, string path, string? body = null, string? depth = null, Action<HttpRequestMessage>? configure = null, string contentType = "application/xml")
        {
            var request = new HttpRequestMessage(new HttpMethod(method), path);

            if (body is not null)
                request.Content = new StringContent(body, Encoding.UTF8, contentType);

            if (depth is not null)
                request.Headers.Add("Depth", depth);

            configure?.Invoke(request);

            return this.Client.SendAsync(request, Token);
        }

        public async Task<XDocument> XmlAsync(string method, string path, string? body = null, string? depth = null, int expected = 207)
        {
            var response = await this.SendAsync(method, path, body, depth);
            var text = await response.Content.ReadAsStringAsync(Token);

            Assert.True((int)response.StatusCode == expected, $"{method} {path} answered {(int)response.StatusCode}: {text}");

            return XDocument.Parse(text);
        }

        public Task<HttpResponseMessage> PutAsync(string path, string body, string contentType = "text/calendar", string? ifMatch = null, bool ifNoneMatchAny = false)
            => this.SendAsync("PUT", path, body, configure: r =>
            {
                if (ifMatch is not null)
                    r.Headers.TryAddWithoutValidation("If-Match", ifMatch);

                if (ifNoneMatchAny)
                    r.Headers.TryAddWithoutValidation("If-None-Match", "*");
            }, contentType: contentType);

        public async ValueTask DisposeAsync()
        {
            this.Client.Dispose();
            await this.Server.DisposeAsync();
            this.owned?.Dispose();
        }
    }

    static string Event(string uid, string start, string end, string summary = "Meeting", string? extra = null, string? tzid = null)
    {
        var zone = tzid is null ? "" : $";TZID={tzid}";
        return $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Test//EN\r\nBEGIN:VEVENT\r\nUID:{uid}\r\nDTSTAMP:20260101T000000Z\r\n"
            + $"DTSTART{zone}:{start}\r\nDTEND{zone}:{end}\r\nSUMMARY:{summary}\r\n{extra}END:VEVENT\r\nEND:VCALENDAR\r\n";
    }

    static string Card(string uid, string name, string email)
        => $"BEGIN:VCARD\r\nVERSION:3.0\r\nUID:{uid}\r\nFN:{name}\r\nN:{name};;;;\r\nEMAIL;TYPE=work:{email}\r\nEND:VCARD\r\n";

    static IEnumerable<XElement> Responses(XDocument document) => document.Root!.Elements(D + "response");

    static XElement ResponseFor(XDocument document, string href)
        => Responses(document).Single(r => r.Element(D + "href")!.Value == href);

    static XElement? Found(XElement response, XName property)
        => response
            .Elements(D + "propstat")
            .Where(p => p.Element(D + "status")!.Value.Contains("200"))
            .Select(p => p.Element(D + "prop")!.Element(property))
            .FirstOrDefault(e => e is not null);

    static XElement? Missing(XElement response, XName property)
        => response
            .Elements(D + "propstat")
            .Where(p => p.Element(D + "status")!.Value.Contains("404"))
            .Select(p => p.Element(D + "prop")!.Element(property))
            .FirstOrDefault(e => e is not null);

    static string Prop(params string[] elements)
        => $"""<?xml version="1.0" encoding="utf-8"?><D:propfind xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav" xmlns:CR="urn:ietf:params:xml:ns:carddav" xmlns:CS="http://calendarserver.org/ns/" xmlns:ICAL="http://apple.com/ns/ical/" xmlns:A="http://apple.com/ns/foo/"><D:prop>{string.Concat(elements.Select(e => $"<{e}/>"))}</D:prop></D:propfind>""";

    static IEnumerable<string> Hrefs(XDocument document)
        => Responses(document).Select(r => r.Element(D + "href")!.Value);

    // ---- discovery ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Discovery_flow_as_ios_does_it(bool http2)
    {
        await using var dav = await Dav.StartAsync(http2);

        // 1. The user typed a host name. iOS asks the well-known URL, and follows the redirect.
        var wellKnown = await dav.SendAsync("PROPFIND", "/.well-known/caldav", Prop("D:current-user-principal"), "0");

        Assert.Equal(HttpStatusCode.MovedPermanently, wellKnown.StatusCode);
        Assert.Equal("/dav/", wellKnown.Headers.Location!.OriginalString);

        // 2. The context path names the principal.
        var context = await dav.XmlAsync("PROPFIND", "/dav/", Prop("D:current-user-principal", "D:resourcetype"), "0");
        var principal = Found(ResponseFor(context, "/dav/"), D + "current-user-principal")!.Element(D + "href")!.Value;

        Assert.Equal("/dav/principals/ada/", principal);

        // 3. The principal names the homes. iOS asks for Apple properties no server is obliged to
        //    have, and those come back as a 404 propstat rather than failing the request.
        var principalProps = await dav.XmlAsync(
            "PROPFIND",
            principal,
            Prop("C:calendar-home-set", "CR:addressbook-home-set", "D:displayname", "D:principal-URL", "D:resourcetype", "A:email-address-set"),
            "0"
        );

        var self = ResponseFor(principalProps, principal);

        Assert.Equal("/dav/calendars/ada/", Found(self, C + "calendar-home-set")!.Element(D + "href")!.Value);
        Assert.Equal("/dav/addressbooks/ada/", Found(self, CR + "addressbook-home-set")!.Element(D + "href")!.Value);
        Assert.Equal("ada", Found(self, D + "displayname")!.Value);
        Assert.NotNull(Found(self, D + "resourcetype")!.Element(D + "principal"));
        Assert.NotNull(Missing(self, (XNamespace)"http://apple.com/ns/foo/" + "email-address-set"));

        // 4. The home lists the calendars, with what the calendar list needs to draw them.
        var home = await dav.XmlAsync(
            "PROPFIND",
            "/dav/calendars/ada/",
            Prop("D:resourcetype", "D:displayname", "CS:getctag", "D:sync-token", "C:supported-calendar-component-set", "D:current-user-privilege-set", "ICAL:calendar-color"),
            "1"
        );

        var calendar = ResponseFor(home, Calendar);

        Assert.NotNull(Found(calendar, D + "resourcetype")!.Element(C + "calendar"));
        Assert.Equal("Calendar", Found(calendar, D + "displayname")!.Value);
        Assert.NotEmpty(Found(calendar, CS + "getctag")!.Value);
        Assert.StartsWith("http", Found(calendar, D + "sync-token")!.Value);
        Assert.Contains(
            Found(calendar, C + "supported-calendar-component-set")!.Elements(C + "comp"),
            c => c.Attribute("name")!.Value == "VEVENT"
        );
        Assert.Contains(
            Found(calendar, D + "current-user-privilege-set")!.Descendants(D + "write"),
            _ => true
        );

        // 5. OPTIONS is where a client checks it is talking CalDAV at all.
        var options = await dav.SendAsync("OPTIONS", principal);
        var davHeader = string.Join(", ", options.Headers.GetValues("DAV"));

        Assert.Contains("calendar-access", davHeader, StringComparison.Ordinal);
        Assert.Contains("addressbook", davHeader, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Well_known_redirects_need_no_credentials()
    {
        await using var dav = await Dav.StartAsync();
        using var anonymous = Dav.ClientFor(dav.Server, http2: false, user: null);

        var caldav = await anonymous.SendAsync(new HttpRequestMessage(new HttpMethod("PROPFIND"), "/.well-known/caldav"), Token);
        var carddav = await anonymous.GetAsync("/.well-known/carddav", Token);

        Assert.Equal(HttpStatusCode.MovedPermanently, caldav.StatusCode);
        Assert.Equal(HttpStatusCode.MovedPermanently, carddav.StatusCode);
        Assert.Equal("/dav/", carddav.Headers.Location!.OriginalString);

        // The mount itself is protected.
        var mount = await anonymous.SendAsync(new HttpRequestMessage(new HttpMethod("PROPFIND"), "/dav/"), Token);
        Assert.Equal(HttpStatusCode.Unauthorized, mount.StatusCode);
    }

    [Fact]
    public async Task Another_principals_urls_are_forbidden()
    {
        await using var dav = await Dav.StartAsync();
        using var bob = Dav.ClientFor(dav.Server, http2: false, user: "bob");

        var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), "/dav/calendars/ada/");
        request.Headers.Add("Depth", "1");

        Assert.Equal(HttpStatusCode.Forbidden, (await bob.SendAsync(request, Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.GetAsync("/dav/principals/ada/", Token)).StatusCode);

        // Bob's own principal is where his discovery lands.
        var own = new HttpRequestMessage(new HttpMethod("PROPFIND"), "/dav/");
        own.Headers.Add("Depth", "0");
        own.Content = new StringContent(Prop("D:current-user-principal"), Encoding.UTF8, "application/xml");

        var text = await (await bob.SendAsync(own, Token)).Content.ReadAsStringAsync(Token);
        Assert.Contains("/dav/principals/bob/", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_default_principal_serves_an_unauthenticated_mount()
    {
        using var root = new ContentRoot();

        await using var server = await TestServer.StartAsync(app => app.MapCalDav("/cal", o =>
        {
            o.CalendarStore = new FileCalendarStore(root.Path);
            o.DefaultPrincipal = "me";
        }));

        var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), "/cal/");
        request.Headers.Add("Depth", "0");
        request.Content = new StringContent(Prop("D:current-user-principal"), Encoding.UTF8, "application/xml");

        var text = await (await server.Client.SendAsync(request, Token)).Content.ReadAsStringAsync(Token);
        Assert.Contains("/cal/principals/me/", text, StringComparison.Ordinal);

        // CardDAV is off, so its well-known URL and its home are not there.
        Assert.Equal(HttpStatusCode.NotFound, (await server.Client.GetAsync("/.well-known/carddav", Token)).StatusCode);
    }

    // ---- PUT, GET, DELETE ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Create_update_and_delete_an_event_with_etag_preconditions(bool http2)
    {
        await using var dav = await Dav.StartAsync(http2);
        var path = Calendar + "evt-1.ics";

        var created = await dav.PutAsync(path, Event("uid-1", "20260105T090000Z", "20260105T100000Z"), ifNoneMatchAny: true);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var etag = created.Headers.ETag!.Tag;

        // If-None-Match: * — create, never replace.
        var again = await dav.PutAsync(path, Event("uid-1", "20260105T090000Z", "20260105T100000Z"), ifNoneMatchAny: true);
        Assert.Equal(HttpStatusCode.PreconditionFailed, again.StatusCode);

        var fetched = await dav.Client.GetAsync(path, Token);
        Assert.Equal(etag, fetched.Headers.ETag!.Tag);
        Assert.Equal("text/calendar", fetched.Content.Headers.ContentType!.MediaType);
        Assert.Contains("UID:uid-1", await fetched.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);

        var updated = await dav.PutAsync(path, Event("uid-1", "20260105T090000Z", "20260105T110000Z", "Longer"), ifMatch: etag);
        Assert.Equal(HttpStatusCode.NoContent, updated.StatusCode);
        var newEtag = updated.Headers.ETag!.Tag;
        Assert.NotEqual(etag, newEtag);

        // The old tag is stale now: an edit made from it would overwrite the one just made.
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await dav.PutAsync(path, Event("uid-1", "20260105T090000Z", "20260105T100000Z"), ifMatch: etag)).StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await dav.SendAsync("DELETE", path, configure: r => r.Headers.TryAddWithoutValidation("If-Match", etag))).StatusCode);

        var deleted = await dav.SendAsync("DELETE", path, configure: r => r.Headers.TryAddWithoutValidation("If-Match", newEtag));
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await dav.Client.GetAsync(path, Token)).StatusCode);

        // If-Match on something that does not exist fails, rather than creating it.
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await dav.PutAsync(path, Event("uid-1", "20260105T090000Z", "20260105T100000Z"), ifMatch: newEtag)).StatusCode);
    }

    [Fact]
    public async Task A_second_object_with_the_same_uid_is_refused_with_no_uid_conflict()
    {
        await using var dav = await Dav.StartAsync();

        Assert.Equal(HttpStatusCode.Created, (await dav.PutAsync(Calendar + "a.ics", Event("shared", "20260105T090000Z", "20260105T100000Z"))).StatusCode);

        var conflict = await dav.PutAsync(Calendar + "b.ics", Event("shared", "20260106T090000Z", "20260106T100000Z"));
        var body = XDocument.Parse(await conflict.Content.ReadAsStringAsync(Token));

        Assert.Equal(HttpStatusCode.Forbidden, conflict.StatusCode);
        Assert.Equal(Calendar + "a.ics", body.Root!.Element(C + "no-uid-conflict")!.Element(D + "href")!.Value);

        // Replacing the object that owns the UID is not a conflict with itself.
        Assert.Equal(HttpStatusCode.NoContent, (await dav.PutAsync(Calendar + "a.ics", Event("shared", "20260107T090000Z", "20260107T100000Z"))).StatusCode);
    }

    [Theory]
    [InlineData("BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:x\r\nEND:VCALENDAR\r\n", "valid-calendar-data")]
    [InlineData("not a calendar", "valid-calendar-data")]
    [InlineData("BEGIN:VCALENDAR\r\nMETHOD:REQUEST\r\nBEGIN:VEVENT\r\nUID:x\r\nDTSTART:20260105T090000Z\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n", "valid-calendar-object-resource")]
    [InlineData("BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:x\r\nDTSTART:20260105T090000Z\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nUID:y\r\nDTSTART:20260105T090000Z\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n", "valid-calendar-object-resource")]
    [InlineData("BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nDTSTART:20260105T090000Z\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n", "valid-calendar-object-resource")]
    [InlineData("BEGIN:VCALENDAR\r\nBEGIN:VJOURNAL\r\nUID:x\r\nDTSTART:20260105T090000Z\r\nEND:VJOURNAL\r\nEND:VCALENDAR\r\n", "supported-calendar-component")]
    public async Task Invalid_calendar_objects_are_refused_with_the_precondition_that_failed(string body, string condition)
    {
        await using var dav = await Dav.StartAsync();

        var response = await dav.PutAsync(Calendar + "bad.ics", body);
        var error = XDocument.Parse(await response.Content.ReadAsStringAsync(Token));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotNull(error.Root!.Element(C + condition));
    }

    [Fact]
    public async Task A_put_with_the_wrong_media_type_or_too_large_is_refused()
    {
        await using var dav = await Dav.StartAsync(configure: o => o.MaxResourceSize = 512);

        var wrongType = await dav.PutAsync(Calendar + "x.ics", Event("x", "20260105T090000Z", "20260105T100000Z"), contentType: "text/plain");
        Assert.Equal(HttpStatusCode.Forbidden, wrongType.StatusCode);
        Assert.Contains("supported-calendar-data", await wrongType.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);

        var large = await dav.PutAsync(Calendar + "x.ics", Event("x", "20260105T090000Z", "20260105T100000Z", new string('a', 1000)));
        Assert.Equal(HttpStatusCode.Forbidden, large.StatusCode);
        Assert.Contains("max-resource-size", await large.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.Conflict, (await dav.PutAsync("/dav/calendars/ada/nope/x.ics", Event("x", "20260105T090000Z", "20260105T100000Z"))).StatusCode);
    }

    [Fact]
    public async Task Get_on_a_calendar_exports_every_event_as_one_calendar()
    {
        await using var dav = await Dav.StartAsync();

        await dav.PutAsync(Calendar + "a.ics", Event("a", "20260105T090000Z", "20260105T100000Z", "First"));
        await dav.PutAsync(Calendar + "b.ics", Event("b", "20260106T090000Z", "20260106T100000Z", "Second"));

        var response = await dav.Client.GetAsync(Calendar, Token);
        var parsed = ContentComponent.Parse(await response.Content.ReadAsStringAsync(Token));

        Assert.Equal("VCALENDAR", parsed.Name);
        Assert.Equal(["First", "Second"], parsed.GetComponents("VEVENT").Select(e => e.GetProperty("SUMMARY")!.GetText()).Order());
    }

    // ---- collections ----

    [Fact]
    public async Task Mkcalendar_creates_a_calendar_with_the_properties_it_was_given()
    {
        await using var dav = await Dav.StartAsync();

        const string body = """
            <?xml version="1.0" encoding="utf-8"?>
            <C:mkcalendar xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav" xmlns:ICAL="http://apple.com/ns/ical/">
              <D:set><D:prop>
                <D:displayname>Reminders</D:displayname>
                <C:calendar-description>Things to do</C:calendar-description>
                <ICAL:calendar-color>#FF2968FF</ICAL:calendar-color>
                <ICAL:calendar-order>3</ICAL:calendar-order>
                <C:supported-calendar-component-set><C:comp name="VTODO"/></C:supported-calendar-component-set>
              </D:prop></D:set>
            </C:mkcalendar>
            """;

        Assert.Equal(HttpStatusCode.Created, (await dav.SendAsync("MKCALENDAR", "/dav/calendars/ada/tasks/", body)).StatusCode);

        var props = await dav.XmlAsync(
            "PROPFIND",
            "/dav/calendars/ada/tasks/",
            Prop("D:displayname", "C:calendar-description", "ICAL:calendar-color", "ICAL:calendar-order", "C:supported-calendar-component-set", "D:resourcetype"),
            "0"
        );

        var tasks = ResponseFor(props, "/dav/calendars/ada/tasks/");

        Assert.Equal("Reminders", Found(tasks, D + "displayname")!.Value);
        Assert.Equal("Things to do", Found(tasks, C + "calendar-description")!.Value);
        Assert.Equal("#FF2968FF", Found(tasks, ICAL + "calendar-color")!.Value);
        Assert.Equal("3", Found(tasks, ICAL + "calendar-order")!.Value);
        Assert.Equal(["VTODO"], Found(tasks, C + "supported-calendar-component-set")!.Elements(C + "comp").Select(c => c.Attribute("name")!.Value));

        // The URL is taken now, and a calendar cannot be made in an address book home.
        var again = await dav.SendAsync("MKCALENDAR", "/dav/calendars/ada/tasks/");
        Assert.Equal(HttpStatusCode.Forbidden, again.StatusCode);

        var misplaced = await dav.SendAsync("MKCALENDAR", "/dav/addressbooks/ada/cal/");
        Assert.Equal(HttpStatusCode.Forbidden, misplaced.StatusCode);
        Assert.Contains("calendar-collection-location-ok", await misplaced.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);

        // An event does not go into a calendar that only holds tasks.
        var evt = await dav.PutAsync("/dav/calendars/ada/tasks/e.ics", Event("e", "20260105T090000Z", "20260105T100000Z"));
        Assert.Equal(HttpStatusCode.Forbidden, evt.StatusCode);

        // And the calendar can be deleted.
        Assert.Equal(HttpStatusCode.NoContent, (await dav.SendAsync("DELETE", "/dav/calendars/ada/tasks/")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await dav.SendAsync("PROPFIND", "/dav/calendars/ada/tasks/", depth: "0")).StatusCode);
    }

    [Fact]
    public async Task Extended_mkcol_creates_an_address_book_and_refuses_the_wrong_resourcetype()
    {
        await using var dav = await Dav.StartAsync();

        const string book = """
            <D:mkcol xmlns:D="DAV:" xmlns:CR="urn:ietf:params:xml:ns:carddav">
              <D:set><D:prop>
                <D:resourcetype><D:collection/><CR:addressbook/></D:resourcetype>
                <D:displayname>Work</D:displayname>
                <CR:addressbook-description>Colleagues</CR:addressbook-description>
              </D:prop></D:set>
            </D:mkcol>
            """;

        Assert.Equal(HttpStatusCode.Created, (await dav.SendAsync("MKCOL", "/dav/addressbooks/ada/work/", book)).StatusCode);

        var props = await dav.XmlAsync("PROPFIND", "/dav/addressbooks/ada/", Prop("D:displayname", "D:resourcetype", "CR:addressbook-description", "CR:supported-address-data"), "1");
        var work = ResponseFor(props, "/dav/addressbooks/ada/work/");

        Assert.Equal("Work", Found(work, D + "displayname")!.Value);
        Assert.Equal("Colleagues", Found(work, CR + "addressbook-description")!.Value);
        Assert.NotNull(Found(work, D + "resourcetype")!.Element(CR + "addressbook"));
        Assert.NotNull(Found(work, CR + "supported-address-data"));

        const string calendarInBooks = """
            <D:mkcol xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav">
              <D:set><D:prop><D:resourcetype><D:collection/><C:calendar/></D:resourcetype></D:prop></D:set>
            </D:mkcol>
            """;

        var wrong = await dav.SendAsync("MKCOL", "/dav/addressbooks/ada/cal/", calendarInBooks);
        var response = XDocument.Parse(await wrong.Content.ReadAsStringAsync(Token));

        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        Assert.Equal(D + "mkcol-response", response.Root!.Name);
    }

    [Fact]
    public async Task Proppatch_renames_and_recolours_and_is_atomic()
    {
        await using var dav = await Dav.StartAsync();

        // A home listing is what creates the default calendar on first sight.
        await dav.SendAsync("PROPFIND", "/dav/calendars/ada/", Prop("D:displayname"), "1");

        const string patch = """
            <D:propertyupdate xmlns:D="DAV:" xmlns:ICAL="http://apple.com/ns/ical/">
              <D:set><D:prop>
                <D:displayname>Home</D:displayname>
                <ICAL:calendar-color>#00FF00</ICAL:calendar-color>
                <ICAL:calendar-order>1</ICAL:calendar-order>
              </D:prop></D:set>
            </D:propertyupdate>
            """;

        var result = await dav.XmlAsync("PROPPATCH", Calendar, patch);
        Assert.All(result.Descendants(D + "status"), s => Assert.Contains("200", s.Value, StringComparison.Ordinal));

        var props = await dav.XmlAsync("PROPFIND", Calendar, Prop("D:displayname", "ICAL:calendar-color", "ICAL:calendar-order"), "0");
        var calendar = ResponseFor(props, Calendar);

        Assert.Equal("Home", Found(calendar, D + "displayname")!.Value);
        Assert.Equal("#00FF00", Found(calendar, ICAL + "calendar-color")!.Value);
        Assert.Equal("1", Found(calendar, ICAL + "calendar-order")!.Value);

        // The name and colour reached the store, so they survive a new server over the same files.
        var store = new FileCalendarStore(Path.Combine(dav.Path, "calendars"));
        var stored = await store.GetCollectionAsync("ada", "default", Token);
        Assert.Equal("Home", stored!.DisplayName);
        Assert.Equal("#00FF00", stored.Color);

        const string protectedPatch = """
            <D:propertyupdate xmlns:D="DAV:">
              <D:set><D:prop><D:displayname>Nope</D:displayname><D:getetag>"x"</D:getetag></D:prop></D:set>
            </D:propertyupdate>
            """;

        var refused = await dav.XmlAsync("PROPPATCH", Calendar, protectedPatch);
        var statuses = refused.Descendants(D + "status").Select(s => s.Value).ToList();

        Assert.Contains(statuses, s => s.Contains("403"));
        Assert.Contains(statuses, s => s.Contains("424"));

        var after = await dav.XmlAsync("PROPFIND", Calendar, Prop("D:displayname"), "0");
        Assert.Equal("Home", Found(ResponseFor(after, Calendar), D + "displayname")!.Value);
    }

    [Fact]
    public async Task A_read_only_mount_says_so_and_refuses_writes()
    {
        await using var dav = await Dav.StartAsync(configure: o => o.ReadOnly = true);

        var home = await dav.XmlAsync("PROPFIND", "/dav/calendars/ada/", Prop("D:current-user-privilege-set"), "1");
        var privileges = Found(ResponseFor(home, Calendar), D + "current-user-privilege-set")!;

        Assert.NotEmpty(privileges.Descendants(D + "read"));
        Assert.Empty(privileges.Descendants(D + "write"));

        var put = await dav.PutAsync(Calendar + "x.ics", Event("x", "20260105T090000Z", "20260105T100000Z"));

        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
        Assert.Contains("need-privileges", await put.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
    }

    // ---- reports ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Calendar_multiget_returns_each_named_object_and_a_404_for_the_rest(bool http2)
    {
        await using var dav = await Dav.StartAsync(http2);

        await dav.PutAsync(Calendar + "a.ics", Event("a", "20260105T090000Z", "20260105T100000Z"));
        await dav.PutAsync(Calendar + "b.ics", Event("b", "20260106T090000Z", "20260106T100000Z"));

        var body = $"""
            <C:calendar-multiget xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav">
              <D:prop><D:getetag/><C:calendar-data/></D:prop>
              <D:href>{Calendar}a.ics</D:href>
              <D:href>http://127.0.0.1:{dav.Server.Port}{Calendar}b.ics</D:href>
              <D:href>{Calendar}missing.ics</D:href>
            </C:calendar-multiget>
            """;

        var result = await dav.XmlAsync("REPORT", Calendar, body, "1");

        Assert.Contains("UID:a", Found(ResponseFor(result, Calendar + "a.ics"), C + "calendar-data")!.Value, StringComparison.Ordinal);
        Assert.Contains("UID:b", Found(ResponseFor(result, Calendar + "b.ics"), C + "calendar-data")!.Value, StringComparison.Ordinal);
        Assert.NotEmpty(Found(ResponseFor(result, Calendar + "a.ics"), D + "getetag")!.Value);
        Assert.Contains("404", ResponseFor(result, Calendar + "missing.ics").Element(D + "status")!.Value, StringComparison.Ordinal);
    }

    static string TimeRangeQuery(string start, string end, string component = "VEVENT")
        => $"""
            <C:calendar-query xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav">
              <D:prop><D:getetag/></D:prop>
              <C:filter>
                <C:comp-filter name="VCALENDAR">
                  <C:comp-filter name="{component}">
                    <C:time-range start="{start}" end="{end}"/>
                  </C:comp-filter>
                </C:comp-filter>
              </C:filter>
            </C:calendar-query>
            """;

    async Task<List<string>> QueryAsync(Dav dav, string body)
        => Hrefs(await dav.XmlAsync("REPORT", Calendar, body, "1")).Select(h => h[Calendar.Length..]).Order().ToList();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Calendar_query_time_range_expands_a_weekly_rrule(bool http2)
    {
        await using var dav = await Dav.StartAsync(http2);

        // Mondays and Wednesdays from 5 January 2026, ten times: the last is Wednesday 4 February.
        await dav.PutAsync(Calendar + "weekly.ics", Event("weekly", "20260105T090000Z", "20260105T100000Z", "Standup", "RRULE:FREQ=WEEKLY;BYDAY=MO,WE;COUNT=10\r\n"));

        // Every other Friday until the end of January.
        await dav.PutAsync(Calendar + "fortnight.ics", Event("fortnight", "20260102T150000Z", "20260102T160000Z", "Review", "RRULE:FREQ=WEEKLY;INTERVAL=2;UNTIL=20260131T000000Z\r\n"));

        await dav.PutAsync(Calendar + "single.ics", Event("single", "20260301T120000Z", "20260301T130000Z"));

        Assert.Equal(["weekly.ics"], await QueryAsync(dav, TimeRangeQuery("20260202T000000Z", "20260203T000000Z")));

        // The ninth instance is 2 February; after the tenth, the series is over.
        Assert.Empty(await QueryAsync(dav, TimeRangeQuery("20260209T000000Z", "20260210T000000Z")));

        // 16 January is a fortnight after 2 January; 9 January is not.
        Assert.Equal(["fortnight.ics"], await QueryAsync(dav, TimeRangeQuery("20260116T000000Z", "20260117T000000Z")));
        Assert.Empty(await QueryAsync(dav, TimeRangeQuery("20260109T000000Z", "20260110T000000Z")));

        // Past UNTIL.
        Assert.Empty(await QueryAsync(dav, TimeRangeQuery("20260213T000000Z", "20260214T000000Z")));

        Assert.Equal(["single.ics"], await QueryAsync(dav, TimeRangeQuery("20260301T000000Z", "20260302T000000Z")));

        // A range that contains an instance's end but not its start still overlaps it.
        Assert.Equal(["weekly.ics"], await QueryAsync(dav, TimeRangeQuery("20260105T093000Z", "20260105T094500Z")));
    }

    [Fact]
    public async Task Calendar_query_honours_exdate_and_overridden_instances()
    {
        await using var dav = await Dav.StartAsync();

        // Daily from 5 January. The 7th is cancelled; the 8th is moved to the 20th.
        const string series = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Test//EN\r\n"
            + "BEGIN:VEVENT\r\nUID:daily\r\nDTSTAMP:20260101T000000Z\r\nDTSTART:20260105T090000Z\r\nDTEND:20260105T093000Z\r\n"
            + "RRULE:FREQ=DAILY;COUNT=5\r\nEXDATE:20260107T090000Z\r\nSUMMARY:Daily\r\nEND:VEVENT\r\n"
            + "BEGIN:VEVENT\r\nUID:daily\r\nDTSTAMP:20260101T000000Z\r\nRECURRENCE-ID:20260108T090000Z\r\nDTSTART:20260120T090000Z\r\nDTEND:20260120T093000Z\r\nSUMMARY:Moved\r\nEND:VEVENT\r\n"
            + "END:VCALENDAR\r\n";

        Assert.Equal(HttpStatusCode.Created, (await dav.PutAsync(Calendar + "daily.ics", series)).StatusCode);

        Assert.Equal(["daily.ics"], await QueryAsync(dav, TimeRangeQuery("20260106T000000Z", "20260107T000000Z")));
        Assert.Empty(await QueryAsync(dav, TimeRangeQuery("20260107T000000Z", "20260108T000000Z")));
        Assert.Empty(await QueryAsync(dav, TimeRangeQuery("20260108T000000Z", "20260109T000000Z")));
        Assert.Equal(["daily.ics"], await QueryAsync(dav, TimeRangeQuery("20260120T000000Z", "20260121T000000Z")));
    }

    [Fact]
    public async Task Calendar_query_expands_a_zoned_series_across_a_daylight_saving_change()
    {
        await using var dav = await Dav.StartAsync();

        // 09:00 in New York every Monday: 14:00Z in winter, 13:00Z once DST starts on 8 March.
        await dav.PutAsync(
            Calendar + "zoned.ics",
            Event("zoned", "20260105T090000", "20260105T093000", "NY", "RRULE:FREQ=WEEKLY\r\n", tzid: "America/New_York")
        );

        Assert.Equal(["zoned.ics"], await QueryAsync(dav, TimeRangeQuery("20260112T140000Z", "20260112T141500Z")));
        Assert.Equal(["zoned.ics"], await QueryAsync(dav, TimeRangeQuery("20260309T130000Z", "20260309T131500Z")));
        Assert.Empty(await QueryAsync(dav, TimeRangeQuery("20260309T140000Z", "20260309T141500Z")));
    }

    [Fact]
    public async Task Calendar_query_filters_by_component_and_text()
    {
        await using var dav = await Dav.StartAsync();

        await dav.PutAsync(Calendar + "standup.ics", Event("s", "20260105T090000Z", "20260105T100000Z", "Daily Standup"));
        await dav.PutAsync(Calendar + "lunch.ics", Event("l", "20260105T120000Z", "20260105T130000Z", "Lunch"));
        await dav.PutAsync(
            Calendar + "todo.ics",
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VTODO\r\nUID:t\r\nDTSTAMP:20260101T000000Z\r\nSUMMARY:Buy milk\r\nDUE:20260110T000000Z\r\nEND:VTODO\r\nEND:VCALENDAR\r\n"
        );

        const string todos = """
            <C:calendar-query xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav">
              <D:prop><D:getetag/></D:prop>
              <C:filter><C:comp-filter name="VCALENDAR"><C:comp-filter name="VTODO"/></C:comp-filter></C:filter>
            </C:calendar-query>
            """;

        Assert.Equal(["todo.ics"], await QueryAsync(dav, todos));

        static string Text(string match, bool negate = false) => $"""
            <C:calendar-query xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav">
              <D:prop><D:getetag/><C:calendar-data/></D:prop>
              <C:filter><C:comp-filter name="VCALENDAR"><C:comp-filter name="VEVENT">
                <C:prop-filter name="SUMMARY"><C:text-match collation="i;ascii-casemap"{(negate ? " negate-condition=\"yes\"" : "")}>{match}</C:text-match></C:prop-filter>
              </C:comp-filter></C:comp-filter></C:filter>
            </C:calendar-query>
            """;

        Assert.Equal(["standup.ics"], await QueryAsync(dav, Text("STANDUP")));
        Assert.Equal(["lunch.ics"], await QueryAsync(dav, Text("standup", negate: true)));

        // A collation the server does not have is a precondition failure, not an empty result.
        var unsupported = await dav.SendAsync("REPORT", Calendar, Text("x").Replace("i;ascii-casemap", "i;klingon", StringComparison.Ordinal), "1");
        Assert.Equal(HttpStatusCode.Forbidden, unsupported.StatusCode);
        Assert.Contains("supported-collation", await unsupported.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);

        // A todo's time range is its DUE when it has no DTSTART.
        Assert.Equal(["todo.ics"], await QueryAsync(dav, TimeRangeQuery("20260109T000000Z", "20260111T000000Z", "VTODO")));
    }

    [Fact]
    public async Task Free_busy_query_reports_busy_periods_and_skips_transparent_events()
    {
        await using var dav = await Dav.StartAsync();

        await dav.PutAsync(Calendar + "busy.ics", Event("busy", "20260105T090000Z", "20260105T100000Z", "Busy", "RRULE:FREQ=DAILY;COUNT=3\r\n"));
        await dav.PutAsync(Calendar + "free.ics", Event("free", "20260105T120000Z", "20260105T130000Z", "Free", "TRANSP:TRANSPARENT\r\n"));
        await dav.PutAsync(Calendar + "maybe.ics", Event("maybe", "20260106T150000Z", "20260106T160000Z", "Maybe", "STATUS:TENTATIVE\r\n"));

        const string body = """
            <C:free-busy-query xmlns:C="urn:ietf:params:xml:ns:caldav">
              <C:time-range start="20260105T000000Z" end="20260107T000000Z"/>
            </C:free-busy-query>
            """;

        var response = await dav.SendAsync("REPORT", Calendar, body, "1");
        var freeBusy = ContentComponent.Parse(await response.Content.ReadAsStringAsync(Token)).GetComponents("VFREEBUSY").Single();
        var periods = freeBusy.GetProperties("FREEBUSY").ToDictionary(p => p.GetParameter("FBTYPE")!, p => p.Value);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("20260105T090000Z/20260105T100000Z,20260106T090000Z/20260106T100000Z", periods["BUSY"]);
        Assert.Equal("20260106T150000Z/20260106T160000Z", periods["BUSY-TENTATIVE"]);
    }

    [Fact]
    public async Task An_unsupported_report_is_refused_with_supported_report()
    {
        await using var dav = await Dav.StartAsync();

        var expand = await dav.SendAsync("REPORT", "/dav/principals/ada/", """<D:expand-property xmlns:D="DAV:"/>""", "0");
        Assert.Equal(HttpStatusCode.Forbidden, expand.StatusCode);
        Assert.Contains("supported-report", await expand.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);

        // A calendar query against an address book is the same refusal.
        var wrongKind = await dav.SendAsync("REPORT", Contacts, TimeRangeQuery("20260101T000000Z", "20260102T000000Z"), "1");
        Assert.Equal(HttpStatusCode.Forbidden, wrongKind.StatusCode);
    }

    // ---- sync-collection ----

    static string Sync(string token) => $"""
        <D:sync-collection xmlns:D="DAV:">
          <D:sync-token>{token}</D:sync-token>
          <D:sync-level>1</D:sync-level>
          <D:prop><D:getetag/></D:prop>
        </D:sync-collection>
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sync_collection_reports_what_changed_since_a_token(bool http2)
    {
        await using var dav = await Dav.StartAsync(http2);

        await dav.PutAsync(Calendar + "a.ics", Event("a", "20260105T090000Z", "20260105T100000Z"));
        await dav.PutAsync(Calendar + "b.ics", Event("b", "20260106T090000Z", "20260106T100000Z"));

        var initial = await dav.XmlAsync("REPORT", Calendar, Sync(string.Empty));
        var token1 = initial.Root!.Element(D + "sync-token")!.Value;

        Assert.Equal([Calendar + "a.ics", Calendar + "b.ics"], Hrefs(initial).Order());

        // Nothing changed: an empty delta, and the same token back.
        var quiet = await dav.XmlAsync("REPORT", Calendar, Sync(token1));
        Assert.Empty(Responses(quiet));
        Assert.Equal(token1, quiet.Root!.Element(D + "sync-token")!.Value);

        // Add one, change one, delete one.
        await dav.PutAsync(Calendar + "c.ics", Event("c", "20260107T090000Z", "20260107T100000Z"));
        await dav.PutAsync(Calendar + "a.ics", Event("a", "20260105T090000Z", "20260105T110000Z", "Changed"));
        await dav.SendAsync("DELETE", Calendar + "b.ics");

        var delta = await dav.XmlAsync("REPORT", Calendar, Sync(token1));
        var token2 = delta.Root!.Element(D + "sync-token")!.Value;

        Assert.NotEqual(token1, token2);
        Assert.NotNull(Found(ResponseFor(delta, Calendar + "a.ics"), D + "getetag"));
        Assert.NotNull(Found(ResponseFor(delta, Calendar + "c.ics"), D + "getetag"));
        Assert.Contains("404", ResponseFor(delta, Calendar + "b.ics").Element(D + "status")!.Value, StringComparison.Ordinal);
        Assert.Equal(3, Responses(delta).Count());

        Assert.Empty(Responses(await dav.XmlAsync("REPORT", Calendar, Sync(token2))));

        // A token this server never issued.
        var bogus = await dav.SendAsync("REPORT", Calendar, Sync("http://example.com/not-ours"));
        Assert.Equal(HttpStatusCode.Forbidden, bogus.StatusCode);
        Assert.Contains("valid-sync-token", await bogus.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_sync_token_from_before_a_restart_is_valid_until_something_changes()
    {
        using var root = new ContentRoot();
        string token;

        await using (var first = await Dav.StartAsync(directory: root.Path))
        {
            await first.PutAsync(Calendar + "a.ics", Event("a", "20260105T090000Z", "20260105T100000Z"));
            token = (await first.XmlAsync("REPORT", Calendar, Sync(string.Empty))).Root!.Element(D + "sync-token")!.Value;
        }

        await using (var second = await Dav.StartAsync(directory: root.Path))
        {
            // Nothing changed, so the token still describes the collection exactly — and having
            // recognised it, this process can diff against it from now on.
            Assert.Empty(Responses(await second.XmlAsync("REPORT", Calendar, Sync(token))));

            await second.PutAsync(Calendar + "b.ics", Event("b", "20260106T090000Z", "20260106T100000Z"));
            Assert.Equal([Calendar + "b.ics"], Hrefs(await second.XmlAsync("REPORT", Calendar, Sync(token))));
        }

        await using var third = await Dav.StartAsync(directory: root.Path);

        // Changed since the token, in a process that never saw it: nothing to diff against, so the
        // client is told to start over.
        await third.PutAsync(Calendar + "c.ics", Event("c", "20260107T090000Z", "20260107T100000Z"));

        var stale = await third.SendAsync("REPORT", Calendar, Sync(token));
        Assert.Equal(HttpStatusCode.Forbidden, stale.StatusCode);
        Assert.Contains("valid-sync-token", await stale.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sync_sees_a_change_made_to_the_store_directly()
    {
        await using var dav = await Dav.StartAsync();

        await dav.PutAsync(Calendar + "a.ics", Event("a", "20260105T090000Z", "20260105T100000Z"));
        var token = (await dav.XmlAsync("REPORT", Calendar, Sync(string.Empty))).Root!.Element(D + "sync-token")!.Value;

        // The app writes to its own storage, not through the mount.
        var store = new FileCalendarStore(Path.Combine(dav.Path, "calendars"));
        await store.PutObjectAsync("ada", "default", new DavObjectWrite("app.ics", Event("app", "20260110T090000Z", "20260110T100000Z"), "app", ContentComponent.Parse(Event("app", "20260110T090000Z", "20260110T100000Z")), true), Token);

        var delta = await dav.XmlAsync("REPORT", Calendar, Sync(token));
        Assert.Equal([Calendar + "app.ics"], Hrefs(delta));
    }

    // ---- CardDAV ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Contacts_are_created_queried_and_fetched(bool http2)
    {
        await using var dav = await Dav.StartAsync(http2);

        Assert.Equal(HttpStatusCode.Created, (await dav.PutAsync(Contacts + "ada.vcf", Card("c-ada", "Ada Lovelace", "ada@example.com"), "text/vcard", ifNoneMatchAny: true)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await dav.PutAsync(Contacts + "alan.vcf", Card("c-alan", "Alan Turing", "alan@example.org"), "text/vcard")).StatusCode);

        static string Query(string test, params (string Prop, string Text, string MatchType)[] filters) => $"""
            <CR:addressbook-query xmlns:D="DAV:" xmlns:CR="urn:ietf:params:xml:ns:carddav">
              <D:prop><D:getetag/><CR:address-data/></D:prop>
              <CR:filter test="{test}">
                {string.Concat(filters.Select(f => $"<CR:prop-filter name=\"{f.Prop}\"><CR:text-match match-type=\"{f.MatchType}\">{f.Text}</CR:text-match></CR:prop-filter>"))}
              </CR:filter>
            </CR:addressbook-query>
            """;

        async Task<List<string>> Run(string body)
            => Hrefs(await dav.XmlAsync("REPORT", Contacts, body, "1")).Select(h => h[Contacts.Length..]).Order().ToList();

        Assert.Equal(["ada.vcf"], await Run(Query("anyof", ("FN", "lovelace", "contains"))));
        Assert.Equal(["alan.vcf"], await Run(Query("anyof", ("EMAIL", "example.org", "ends-with"))));
        Assert.Equal(["ada.vcf", "alan.vcf"], await Run(Query("anyof", ("FN", "ada", "starts-with"), ("FN", "alan", "starts-with"))));
        Assert.Empty(await Run(Query("allof", ("FN", "ada", "starts-with"), ("FN", "alan", "starts-with"))));

        var data = await dav.XmlAsync("REPORT", Contacts, Query("anyof", ("FN", "Ada Lovelace", "equals")), "1");
        Assert.Contains("FN:Ada Lovelace", Found(ResponseFor(data, Contacts + "ada.vcf"), CR + "address-data")!.Value, StringComparison.Ordinal);

        // A limit returns the first n and a 507 against the collection saying the list was cut.
        var limited = $"""
            <CR:addressbook-query xmlns:D="DAV:" xmlns:CR="urn:ietf:params:xml:ns:carddav">
              <D:prop><D:getetag/></D:prop>
              <CR:filter/>
              <CR:limit><CR:nresults>1</CR:nresults></CR:limit>
            </CR:addressbook-query>
            """;

        var cut = await dav.XmlAsync("REPORT", Contacts, limited, "1");
        Assert.Contains("507", ResponseFor(cut, Contacts).Element(D + "status")!.Value, StringComparison.Ordinal);
        Assert.Equal(2, Responses(cut).Count());

        var multiget = $"""
            <CR:addressbook-multiget xmlns:D="DAV:" xmlns:CR="urn:ietf:params:xml:ns:carddav">
              <D:prop><D:getetag/><CR:address-data/></D:prop>
              <D:href>{Contacts}alan.vcf</D:href>
            </CR:addressbook-multiget>
            """;

        var fetched = await dav.XmlAsync("REPORT", Contacts, multiget, "1");
        Assert.Contains("FN:Alan Turing", Found(ResponseFor(fetched, Contacts + "alan.vcf"), CR + "address-data")!.Value, StringComparison.Ordinal);

        var get = await dav.Client.GetAsync(Contacts + "ada.vcf", Token);
        Assert.Equal("text/vcard", get.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Invalid_or_duplicate_contacts_are_refused()
    {
        await using var dav = await Dav.StartAsync();

        await dav.PutAsync(Contacts + "a.vcf", Card("same", "A", "a@example.com"), "text/vcard");

        var duplicate = await dav.PutAsync(Contacts + "b.vcf", Card("same", "B", "b@example.com"), "text/vcard");
        Assert.Equal(HttpStatusCode.Forbidden, duplicate.StatusCode);
        Assert.NotNull(XDocument.Parse(await duplicate.Content.ReadAsStringAsync(Token)).Root!.Element(CR + "no-uid-conflict"));

        var noUid = await dav.PutAsync(Contacts + "c.vcf", "BEGIN:VCARD\r\nVERSION:3.0\r\nFN:C\r\nEND:VCARD\r\n", "text/vcard");
        Assert.Equal(HttpStatusCode.Forbidden, noUid.StatusCode);
        Assert.NotNull(XDocument.Parse(await noUid.Content.ReadAsStringAsync(Token)).Root!.Element(CR + "valid-address-data"));

        var calendarInBook = await dav.PutAsync(Contacts + "d.vcf", Event("d", "20260105T090000Z", "20260105T100000Z"), "text/calendar");
        Assert.Equal(HttpStatusCode.Forbidden, calendarInBook.StatusCode);
    }

    [Fact]
    public async Task Contact_sync_collection_works_like_the_calendar_one()
    {
        await using var dav = await Dav.StartAsync();

        await dav.PutAsync(Contacts + "a.vcf", Card("a", "A", "a@example.com"), "text/vcard");
        var token = (await dav.XmlAsync("REPORT", Contacts, Sync(string.Empty))).Root!.Element(D + "sync-token")!.Value;

        await dav.SendAsync("DELETE", Contacts + "a.vcf");

        var delta = await dav.XmlAsync("REPORT", Contacts, Sync(token));
        Assert.Contains("404", ResponseFor(delta, Contacts + "a.vcf").Element(D + "status")!.Value, StringComparison.Ordinal);
    }
}
