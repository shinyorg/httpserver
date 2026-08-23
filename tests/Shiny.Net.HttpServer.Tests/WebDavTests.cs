using System.Net;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using Shiny.Net.HttpServer.Security;
using Shiny.Net.HttpServer.WebDav;

namespace Shiny.Net.HttpServer.Tests;

public class WebDavTests
{
    static readonly XNamespace Dav = "DAV:";

    static Task<TestServer> StartAsync(ContentRoot root, Action<WebDavOptions>? configure = null)
        => TestServer.StartAsync(app => app.MapWebDav("/dav", o =>
        {
            o.RootPath = root.Path;
            o.AllowWrite = true;
            o.AllowDelete = true;
            configure?.Invoke(o);
        }));

    static HttpRequestMessage Request(string method, string path, string? body = null)
    {
        var message = new HttpRequestMessage(new HttpMethod(method), path);

        if (body is not null)
            message.Content = new StringContent(body, Encoding.UTF8, "application/xml");

        return message;
    }

    static async Task<XDocument> XmlAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        return XDocument.Parse(text);
    }

    /// <summary>The one response element for a given href, so a test can assert against it by name.</summary>
    static XElement ResponseFor(XDocument document, string href)
        => document.Root!
            .Elements(Dav + "response")
            .Single(r => r.Element(Dav + "href")!.Value == href);

    static string? PropertyValue(XElement response, string name)
        => response
            .Elements(Dav + "propstat")
            .Where(p => p.Element(Dav + "status")!.Value.Contains("200"))
            .Select(p => p.Element(Dav + "prop")!.Element(Dav + name))
            .FirstOrDefault(e => e is not null)
            ?.Value;

    // ---- OPTIONS ----

    [Fact]
    public async Task Options_advertises_class_two_and_the_allowed_verbs()
    {
        using var root = new ContentRoot();
        await using var server = await StartAsync(root);

        var response = await server.Client.SendAsync(Request("OPTIONS", "/dav"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("1, 2", string.Join(", ", response.Headers.GetValues("DAV")));
        Assert.Equal("DAV", response.Headers.GetValues("MS-Author-Via").Single());

        var allow = string.Join(", ", response.Content.Headers.Allow);

        foreach (var verb in (string[])["PROPFIND", "PUT", "MKCOL", "COPY", "MOVE", "DELETE", "LOCK", "UNLOCK"])
            Assert.Contains(verb, allow, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Options_on_a_read_only_mount_offers_only_the_read_verbs()
    {
        using var root = new ContentRoot();

        await using var server = await StartAsync(root, o =>
        {
            o.AllowWrite = false;
            o.AllowDelete = false;
            o.EnableLocking = false;
        });

        var response = await server.Client.SendAsync(Request("OPTIONS", "/dav"), TestContext.Current.CancellationToken);
        var allow = string.Join(", ", response.Content.Headers.Allow);

        Assert.Equal("1", response.Headers.GetValues("DAV").Single());
        Assert.DoesNotContain("PUT", allow, StringComparison.Ordinal);
        Assert.DoesNotContain("LOCK", allow, StringComparison.Ordinal);
        Assert.Contains("PROPFIND", allow, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_read_only_mount_refuses_a_write_with_405_and_an_allow_header()
    {
        using var root = new ContentRoot();
        await using var server = await StartAsync(root, o => o.AllowWrite = false);

        var response = await server.Client.PutAsync(
            "/dav/new.txt",
            new StringContent("nope"),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.DoesNotContain("PUT", string.Join(", ", response.Content.Headers.Allow), StringComparison.Ordinal);
    }

    // ---- PROPFIND ----

    [Fact]
    public async Task Propfind_depth_one_lists_the_collection_and_its_members()
    {
        using var root = new ContentRoot()
            .With("notes.txt", "hello")
            .With("docs/readme.md", "# hi");

        await using var server = await StartAsync(root);

        var request = Request("PROPFIND", "/dav", "<?xml version=\"1.0\"?><D:propfind xmlns:D=\"DAV:\"><D:allprop/></D:propfind>");
        request.Headers.Add("Depth", "1");

        var response = await server.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(207, (int)response.StatusCode);

        var document = await XmlAsync(response);
        var responses = document.Root!.Elements(Dav + "response").ToList();

        Assert.Equal(3, responses.Count);

        // A collection's href ends in a slash: clients resolve member URLs against it, and one
        // without the slash resolves them against the parent.
        var self = ResponseFor(document, "/dav/");
        Assert.NotNull(self.Descendants(Dav + "collection").SingleOrDefault());

        var file = ResponseFor(document, "/dav/notes.txt");

        Assert.Equal("5", PropertyValue(file, "getcontentlength"));
        Assert.Equal("text/plain; charset=utf-8", PropertyValue(file, "getcontenttype"));
        Assert.Equal("notes.txt", PropertyValue(file, "displayname"));
        Assert.NotNull(PropertyValue(file, "getetag"));
        Assert.Empty(file.Descendants(Dav + "collection"));

        // Depth 1 stops at the members. The file inside docs/ is a member of docs/, not of the root.
        Assert.DoesNotContain(responses, r => r.Element(Dav + "href")!.Value.Contains("readme.md"));
    }

    [Fact]
    public async Task Propfind_depth_zero_describes_only_the_resource()
    {
        using var root = new ContentRoot().With("notes.txt", "hello");
        await using var server = await StartAsync(root);

        var request = Request("PROPFIND", "/dav", "<D:propfind xmlns:D=\"DAV:\"><D:allprop/></D:propfind>");
        request.Headers.Add("Depth", "0");

        var document = await XmlAsync(await server.Client.SendAsync(request, TestContext.Current.CancellationToken));

        Assert.Single(document.Root!.Elements(Dav + "response"));
    }

    [Fact]
    public async Task Propfind_names_a_property_it_does_not_have_in_a_404_propstat()
    {
        using var root = new ContentRoot().With("notes.txt", "hello");
        await using var server = await StartAsync(root);

        var request = Request(
            "PROPFIND",
            "/dav/notes.txt",
            """<D:propfind xmlns:D="DAV:"><D:prop><D:getcontentlength/><D:nonesuch/></D:prop></D:propfind>"""
        );
        request.Headers.Add("Depth", "0");

        var document = await XmlAsync(await server.Client.SendAsync(request, TestContext.Current.CancellationToken));
        var response = ResponseFor(document, "/dav/notes.txt");

        Assert.Equal("5", PropertyValue(response, "getcontentlength"));

        var missing = response
            .Elements(Dav + "propstat")
            .Single(p => p.Element(Dav + "status")!.Value.Contains("404"));

        Assert.NotNull(missing.Element(Dav + "prop")!.Element(Dav + "nonesuch"));
    }

    [Fact]
    public async Task Propfind_with_infinite_depth_is_refused_with_the_condition_that_says_why()
    {
        using var root = new ContentRoot().With("docs/readme.md", "# hi");
        await using var server = await StartAsync(root);

        var request = Request("PROPFIND", "/dav");
        request.Headers.Add("Depth", "infinity");

        var response = await server.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var document = await XmlAsync(response);

        Assert.Equal(Dav + "error", document.Root!.Name);
        Assert.NotNull(document.Root.Element(Dav + "propfind-finite-depth"));
    }

    [Fact]
    public async Task Propfind_with_infinite_depth_walks_the_tree_when_it_is_allowed()
    {
        using var root = new ContentRoot()
            .With("docs/guides/deep.md", "# deep")
            .With("notes.txt", "hello");

        await using var server = await StartAsync(root, o => o.AllowInfiniteDepth = true);

        var request = Request("PROPFIND", "/dav");
        request.Headers.Add("Depth", "infinity");

        var document = await XmlAsync(await server.Client.SendAsync(request, TestContext.Current.CancellationToken));

        // root, notes.txt, docs/, docs/guides/, docs/guides/deep.md
        Assert.Equal(5, document.Root!.Elements(Dav + "response").Count());
        Assert.NotNull(ResponseFor(document, "/dav/docs/guides/deep.md"));
    }

    [Fact]
    public async Task Propfind_percent_encodes_names_in_hrefs()
    {
        using var root = new ContentRoot().With("holiday photos/a b.txt", "x");
        await using var server = await StartAsync(root);

        var request = Request("PROPFIND", "/dav/holiday%20photos");
        request.Headers.Add("Depth", "1");

        var document = await XmlAsync(await server.Client.SendAsync(request, TestContext.Current.CancellationToken));

        Assert.NotNull(ResponseFor(document, "/dav/holiday%20photos/a%20b.txt"));
    }

    [Fact]
    public async Task Propfind_on_something_that_is_not_there_is_a_404()
    {
        using var root = new ContentRoot();
        await using var server = await StartAsync(root);

        var request = Request("PROPFIND", "/dav/nope.txt");
        request.Headers.Add("Depth", "0");

        var response = await server.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Propfind_hides_dotfiles_unless_they_are_asked_for()
    {
        using var root = new ContentRoot()
            .With(".env", "SECRET=1")
            .With("notes.txt", "hello");

        await using var server = await StartAsync(root);

        var request = Request("PROPFIND", "/dav");
        request.Headers.Add("Depth", "1");

        var document = await XmlAsync(await server.Client.SendAsync(request, TestContext.Current.CancellationToken));

        Assert.DoesNotContain(
            document.Root!.Elements(Dav + "response"),
            r => r.Element(Dav + "href")!.Value.Contains(".env")
        );
    }

    // ---- PUT / GET / DELETE / MKCOL ----

    [Fact]
    public async Task Put_creates_then_replaces()
    {
        using var root = new ContentRoot();
        await using var server = await StartAsync(root);

        var created = await server.Client.PutAsync(
            "/dav/notes.txt",
            new StringContent("first"),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.NotNull(created.Headers.ETag);

        var replaced = await server.Client.PutAsync(
            "/dav/notes.txt",
            new StringContent("second"),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.NoContent, replaced.StatusCode);
        Assert.Equal("second", await File.ReadAllTextAsync(Path.Combine(root.Path, "notes.txt"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Put_into_a_collection_that_does_not_exist_is_a_conflict()
    {
        using var root = new ContentRoot();
        await using var server = await StartAsync(root);

        var response = await server.Client.PutAsync(
            "/dav/missing/notes.txt",
            new StringContent("x"),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Get_serves_the_bytes_without_turning_them_into_a_download()
    {
        using var root = new ContentRoot().With("notes.txt", "hello");
        await using var server = await StartAsync(root);

        var response = await server.Client.GetAsync("/dav/notes.txt", TestContext.Current.CancellationToken);

        Assert.Equal("hello", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Null(response.Content.Headers.ContentDisposition);
        Assert.NotNull(response.Headers.ETag);
    }

    [Fact]
    public async Task Get_on_a_collection_links_its_members_by_absolute_href()
    {
        using var root = new ContentRoot()
            .With("notes.txt", "hello")
            .With("my docs/readme.md", "# hi");

        await using var server = await StartAsync(root);

        var html = await server.Client.GetStringAsync("/dav", TestContext.Current.CancellationToken);

        // Absolute, because a browser at "/dav" resolves a relative href against "/".
        Assert.Contains("href=\"/dav/notes.txt\"", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/dav/my%20docs/\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_on_a_nested_collection_links_its_members_and_its_parent()
    {
        using var root = new ContentRoot().With("docs/sub/b.md", "b");
        await using var server = await StartAsync(root);

        var html = await server.Client.GetStringAsync("/dav/docs/sub", TestContext.Current.CancellationToken);

        Assert.Contains("href=\"/dav/docs/sub/b.md\"", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/dav/docs/\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_on_the_mount_root_links_a_child_of_the_root_collection()
    {
        using var root = new ContentRoot().With("docs/a.md", "a");
        await using var server = await StartAsync(root);

        var html = await server.Client.GetStringAsync("/dav/docs/", TestContext.Current.CancellationToken);

        // The parent of a first-level collection is the mount root itself.
        Assert.Contains("href=\"/dav/\"", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// The page offers exactly what the mount allows. A button that only ever earns a 403 is worse
    /// than no button, because it reads as a server that is broken rather than one that is read-only.
    /// </summary>
    [Fact]
    public async Task Get_on_a_collection_offers_the_verbs_the_mount_allows()
    {
        using var root = new ContentRoot().With("notes.txt", "hello");
        await using var server = await StartAsync(root);

        var html = await server.Client.GetStringAsync("/dav", TestContext.Current.CancellationToken);

        Assert.Contains("data-can-write=\"1\"", html, StringComparison.Ordinal);
        Assert.Contains("data-can-delete=\"1\"", html, StringComparison.Ordinal);
        Assert.Contains("data-can-move=\"1\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"new-folder\"", html, StringComparison.Ordinal);
        Assert.Contains("data-act=\"rename\"", html, StringComparison.Ordinal);
        Assert.Contains("data-act=\"delete\"", html, StringComparison.Ordinal);

        // The collection's own href is what every one of those verbs is built from.
        Assert.Contains("data-href=\"/dav/\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_on_a_collection_of_a_read_only_mount_offers_none_of_them()
    {
        using var root = new ContentRoot().With("notes.txt", "hello");

        await using var server = await StartAsync(root, o =>
        {
            o.AllowWrite = false;
            o.AllowDelete = false;
        });

        var html = await server.Client.GetStringAsync("/dav", TestContext.Current.CancellationToken);

        Assert.DoesNotContain("data-can-write", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-can-delete", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-act=", html, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"new-folder\"", html, StringComparison.Ordinal);

        // Reading is still what the mount is for, so the listing and its downloads stay.
        Assert.Contains("href=\"/dav/notes.txt\"", html, StringComparison.Ordinal);
        Assert.Contains("download=\"notes.txt\"", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// A file name is attacker-supplied on any mount that allows writing, and it lands in the page
    /// as both text and an attribute value.
    /// </summary>
    [Fact]
    public async Task Get_on_a_collection_escapes_a_name_that_is_markup()
    {
        using var root = new ContentRoot().With("<img src=x onerror=alert(1)>.txt", "hello");
        await using var server = await StartAsync(root);

        var html = await server.Client.GetStringAsync("/dav", TestContext.Current.CancellationToken);

        Assert.DoesNotContain("<img src=x", html, StringComparison.Ordinal);
        Assert.Contains("&lt;img src=x onerror=alert(1)&gt;.txt", html, StringComparison.Ordinal);
    }

    /// <summary>A listing the back button serves from cache is one an upload or a delete has moved on from.</summary>
    [Fact]
    public async Task Get_on_a_collection_is_not_cached()
    {
        using var root = new ContentRoot().With("notes.txt", "hello");
        await using var server = await StartAsync(root);

        var response = await server.Client.GetAsync("/dav", TestContext.Current.CancellationToken);

        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Mkcol_creates_a_collection_and_refuses_to_create_it_twice()
    {
        using var root = new ContentRoot();
        await using var server = await StartAsync(root);

        var created = await server.Client.SendAsync(Request("MKCOL", "/dav/docs"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.True(Directory.Exists(Path.Combine(root.Path, "docs")));

        var again = await server.Client.SendAsync(Request("MKCOL", "/dav/docs"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, again.StatusCode);
    }

    [Fact]
    public async Task Mkcol_with_a_body_is_unsupported_media()
    {
        using var root = new ContentRoot();
        await using var server = await StartAsync(root);

        var response = await server.Client.SendAsync(
            Request("MKCOL", "/dav/docs", "<x/>"),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task Delete_takes_the_whole_subtree()
    {
        using var root = new ContentRoot().With("docs/a.md", "a").With("docs/sub/b.md", "b");
        await using var server = await StartAsync(root);

        var response = await server.Client.DeleteAsync("/dav/docs", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(Directory.Exists(Path.Combine(root.Path, "docs")));
    }

    [Fact]
    public async Task Delete_of_the_mount_root_is_refused()
    {
        using var root = new ContentRoot().With("notes.txt", "hello");
        await using var server = await StartAsync(root);

        var response = await server.Client.DeleteAsync("/dav", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(Directory.Exists(root.Path));
    }

    // ---- COPY / MOVE ----

    [Fact]
    public async Task Copy_duplicates_a_subtree()
    {
        using var root = new ContentRoot().With("docs/a.md", "a").With("docs/sub/b.md", "b");
        await using var server = await StartAsync(root);

        var request = Request("COPY", "/dav/docs");
        request.Headers.Add("Destination", "/dav/backup");

        var response = await server.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("b", await File.ReadAllTextAsync(Path.Combine(root.Path, "backup", "sub", "b.md"), TestContext.Current.CancellationToken));
        Assert.True(Directory.Exists(Path.Combine(root.Path, "docs")));
    }

    [Fact]
    public async Task Copy_with_depth_zero_takes_the_collection_without_its_members()
    {
        using var root = new ContentRoot().With("docs/a.md", "a");
        await using var server = await StartAsync(root);

        var request = Request("COPY", "/dav/docs");
        request.Headers.Add("Destination", "/dav/empty");
        request.Headers.Add("Depth", "0");

        await server.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.True(Directory.Exists(Path.Combine(root.Path, "empty")));
        Assert.False(File.Exists(Path.Combine(root.Path, "empty", "a.md")));
    }

    [Fact]
    public async Task Copy_onto_an_existing_destination_needs_permission_to_overwrite()
    {
        using var root = new ContentRoot().With("a.txt", "a").With("b.txt", "b");
        await using var server = await StartAsync(root);

        var refused = Request("COPY", "/dav/a.txt");
        refused.Headers.Add("Destination", "/dav/b.txt");
        refused.Headers.Add("Overwrite", "F");

        Assert.Equal(
            HttpStatusCode.PreconditionFailed,
            (await server.Client.SendAsync(refused, TestContext.Current.CancellationToken)).StatusCode
        );

        var allowed = Request("COPY", "/dav/a.txt");
        allowed.Headers.Add("Destination", "/dav/b.txt");

        var response = await server.Client.SendAsync(allowed, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("a", await File.ReadAllTextAsync(Path.Combine(root.Path, "b.txt"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Move_renames_and_leaves_nothing_behind()
    {
        using var root = new ContentRoot().With("a.txt", "a");
        await using var server = await StartAsync(root);

        var request = Request("MOVE", "/dav/a.txt");
        request.Headers.Add("Destination", "/dav/b.txt");

        var response = await server.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.False(File.Exists(Path.Combine(root.Path, "a.txt")));
        Assert.Equal("a", await File.ReadAllTextAsync(Path.Combine(root.Path, "b.txt"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Move_into_the_collection_being_moved_is_refused()
    {
        using var root = new ContentRoot().With("docs/a.md", "a");
        await using var server = await StartAsync(root);

        var request = Request("MOVE", "/dav/docs");
        request.Headers.Add("Destination", "/dav/docs/inner");

        var response = await server.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_destination_on_another_server_is_a_bad_gateway()
    {
        using var root = new ContentRoot().With("a.txt", "a");
        await using var server = await StartAsync(root);

        var request = Request("COPY", "/dav/a.txt");
        request.Headers.Add("Destination", "http://elsewhere.example/dav/b.txt");

        var response = await server.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task A_destination_that_climbs_out_of_the_root_is_refused()
    {
        using var root = new ContentRoot().With("a.txt", "a");
        await using var server = await StartAsync(root);

        var request = Request("COPY", "/dav/a.txt");
        request.Headers.Add("Destination", "/dav/../escaped.txt");

        var response = await server.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(root.Path)!, "escaped.txt")));
    }

    // ---- PROPPATCH ----

    [Fact]
    public async Task Proppatch_stores_a_dead_property_and_propfind_gives_it_back()
    {
        using var root = new ContentRoot().With("a.txt", "a");
        await using var server = await StartAsync(root);

        var patch = Request(
            "PROPPATCH",
            "/dav/a.txt",
            """
            <D:propertyupdate xmlns:D="DAV:" xmlns:Z="urn:example">
              <D:set><D:prop><Z:colour>blue</Z:colour></D:prop></D:set>
            </D:propertyupdate>
            """
        );

        var patched = await server.Client.SendAsync(patch, TestContext.Current.CancellationToken);

        Assert.Equal(207, (int)patched.StatusCode);

        var find = Request(
            "PROPFIND",
            "/dav/a.txt",
            """<D:propfind xmlns:D="DAV:" xmlns:Z="urn:example"><D:prop><Z:colour/></D:prop></D:propfind>"""
        );
        find.Headers.Add("Depth", "0");

        var document = await XmlAsync(await server.Client.SendAsync(find, TestContext.Current.CancellationToken));

        var value = document
            .Descendants(XNamespace.Get("urn:example") + "colour")
            .Single()
            .Value;

        Assert.Equal("blue", value);
    }

    [Fact]
    public async Task Proppatch_refuses_a_live_property_and_rolls_the_rest_back()
    {
        using var root = new ContentRoot().With("a.txt", "a");
        await using var server = await StartAsync(root);

        var patch = Request(
            "PROPPATCH",
            "/dav/a.txt",
            """
            <D:propertyupdate xmlns:D="DAV:" xmlns:Z="urn:example">
              <D:set><D:prop><Z:colour>blue</Z:colour><D:getetag>"forged"</D:getetag></D:prop></D:set>
            </D:propertyupdate>
            """
        );

        var document = await XmlAsync(await server.Client.SendAsync(patch, TestContext.Current.CancellationToken));
        var response = document.Root!.Elements(Dav + "response").Single();

        var forbidden = response
            .Elements(Dav + "propstat")
            .Single(p => p.Element(Dav + "status")!.Value.Contains("403"));

        Assert.NotNull(forbidden.Element(Dav + "prop")!.Element(Dav + "getetag"));

        // The one that could have worked reports 424, and nothing was written.
        var dependent = response
            .Elements(Dav + "propstat")
            .Single(p => p.Element(Dav + "status")!.Value.Contains("424"));

        Assert.NotNull(dependent.Element(Dav + "prop")!.Element(XNamespace.Get("urn:example") + "colour"));

        var find = Request(
            "PROPFIND",
            "/dav/a.txt",
            """<D:propfind xmlns:D="DAV:" xmlns:Z="urn:example"><D:prop><Z:colour/></D:prop></D:propfind>"""
        );
        find.Headers.Add("Depth", "0");

        var after = await XmlAsync(await server.Client.SendAsync(find, TestContext.Current.CancellationToken));

        Assert.Contains(
            after.Descendants(Dav + "propstat"),
            p => p.Element(Dav + "status")!.Value.Contains("404")
        );
    }

    // ---- LOCK / UNLOCK ----

    const string LockBody = """
        <D:lockinfo xmlns:D="DAV:">
          <D:lockscope><D:exclusive/></D:lockscope>
          <D:locktype><D:write/></D:locktype>
          <D:owner><D:href>mailto:someone@example.com</D:href></D:owner>
        </D:lockinfo>
        """;

    static async Task<string> LockAsync(TestServer server, string path, string? depth = "0")
    {
        var request = Request("LOCK", path, LockBody);

        if (depth is not null)
            request.Headers.Add("Depth", depth);

        var response = await server.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.True(
            response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created,
            $"LOCK {path} answered {(int)response.StatusCode}"
        );

        return response.Headers.GetValues("Lock-Token").Single().Trim('<', '>');
    }

    [Fact]
    public async Task A_lock_blocks_a_write_that_does_not_present_the_token()
    {
        using var root = new ContentRoot().With("a.txt", "a");
        await using var server = await StartAsync(root);

        var token = await LockAsync(server, "/dav/a.txt");

        Assert.StartsWith("opaquelocktoken:", token, StringComparison.Ordinal);

        var blocked = await server.Client.PutAsync(
            "/dav/a.txt",
            new StringContent("nope"),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(423, (int)blocked.StatusCode);

        var document = await XmlAsync(blocked);

        Assert.NotNull(document.Root!.Element(Dav + "lock-token-submitted"));

        var allowed = Request("PUT", "/dav/a.txt");
        allowed.Content = new StringContent("yes");
        allowed.Headers.Add("If", $"(<{token}>)");

        var written = await server.Client.SendAsync(allowed, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, written.StatusCode);
        Assert.Equal("yes", await File.ReadAllTextAsync(Path.Combine(root.Path, "a.txt"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_lock_on_an_unmapped_url_creates_the_resource()
    {
        using var root = new ContentRoot();
        await using var server = await StartAsync(root);

        var request = Request("LOCK", "/dav/new.txt", LockBody);
        request.Headers.Add("Depth", "0");

        var response = await server.Client.SendAsync(request, TestContext.Current.CancellationToken);

        // RFC 4918 §7.3 — this is how a Mac saves a new file, and 404 here means Finder cannot
        // write to the mount at all.
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(File.Exists(Path.Combine(root.Path, "new.txt")));

        var document = await XmlAsync(response);

        Assert.Equal("/dav/new.txt", document.Descendants(Dav + "lockroot").Single().Element(Dav + "href")!.Value);
        Assert.Contains("mailto:someone@example.com", document.Descendants(Dav + "owner").Single().ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_deep_lock_on_a_collection_covers_what_is_inside_it()
    {
        using var root = new ContentRoot().With("docs/a.md", "a");
        await using var server = await StartAsync(root);

        var token = await LockAsync(server, "/dav/docs", depth: "infinity");

        var blocked = await server.Client.PutAsync(
            "/dav/docs/a.md",
            new StringContent("nope"),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(423, (int)blocked.StatusCode);

        var allowed = Request("PUT", "/dav/docs/a.md");
        allowed.Content = new StringContent("yes");
        allowed.Headers.Add("If", $"(<{token}>)");

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await server.Client.SendAsync(allowed, TestContext.Current.CancellationToken)).StatusCode
        );
    }

    [Fact]
    public async Task A_second_exclusive_lock_is_refused()
    {
        using var root = new ContentRoot().With("a.txt", "a");
        await using var server = await StartAsync(root);

        await LockAsync(server, "/dav/a.txt");

        var second = Request("LOCK", "/dav/a.txt", LockBody);
        second.Headers.Add("Depth", "0");

        var response = await server.Client.SendAsync(second, TestContext.Current.CancellationToken);

        Assert.Equal(423, (int)response.StatusCode);
        Assert.NotNull((await XmlAsync(response)).Root!.Element(Dav + "no-conflicting-lock"));
    }

    [Fact]
    public async Task Unlock_releases_and_a_stale_token_is_a_conflict()
    {
        using var root = new ContentRoot().With("a.txt", "a");
        await using var server = await StartAsync(root);

        var token = await LockAsync(server, "/dav/a.txt");

        var unlock = Request("UNLOCK", "/dav/a.txt");
        unlock.Headers.Add("Lock-Token", $"<{token}>");

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await server.Client.SendAsync(unlock, TestContext.Current.CancellationToken)).StatusCode
        );

        // The write that was blocked a moment ago now goes through with no token at all.
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await server.Client.PutAsync("/dav/a.txt", new StringContent("free"), TestContext.Current.CancellationToken)).StatusCode
        );

        var again = Request("UNLOCK", "/dav/a.txt");
        again.Headers.Add("Lock-Token", $"<{token}>");

        Assert.Equal(
            HttpStatusCode.Conflict,
            (await server.Client.SendAsync(again, TestContext.Current.CancellationToken)).StatusCode
        );
    }

    [Fact]
    public async Task Lockdiscovery_reports_the_lock_a_propfind_asks_about()
    {
        using var root = new ContentRoot().With("a.txt", "a");
        await using var server = await StartAsync(root);

        var token = await LockAsync(server, "/dav/a.txt");

        var find = Request(
            "PROPFIND",
            "/dav/a.txt",
            """<D:propfind xmlns:D="DAV:"><D:prop><D:lockdiscovery/></D:prop></D:propfind>"""
        );
        find.Headers.Add("Depth", "0");

        var document = await XmlAsync(await server.Client.SendAsync(find, TestContext.Current.CancellationToken));

        Assert.Equal(
            token,
            document.Descendants(Dav + "locktoken").Single().Element(Dav + "href")!.Value
        );
    }

    [Fact]
    public async Task An_if_header_naming_a_stale_etag_fails_the_precondition()
    {
        using var root = new ContentRoot().With("a.txt", "a");
        await using var server = await StartAsync(root);

        var request = Request("PUT", "/dav/a.txt");
        request.Content = new StringContent("nope");
        request.Headers.Add("If", "([\"not-the-tag\"])");

        var response = await server.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        Assert.Equal("a", await File.ReadAllTextAsync(Path.Combine(root.Path, "a.txt"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Locking_can_be_turned_off_entirely()
    {
        using var root = new ContentRoot().With("a.txt", "a");
        await using var server = await StartAsync(root, o => o.EnableLocking = false);

        var response = await server.Client.SendAsync(
            Request("LOCK", "/dav/a.txt", LockBody),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    // ---- path safety and authorization ----

    [Theory]
    [InlineData("/dav/../../etc/passwd")]
    [InlineData("/dav/%2e%2e/%2e%2e/etc/passwd")]
    public async Task A_path_that_climbs_out_of_the_root_never_resolves(string path)
    {
        using var root = new ContentRoot().With("a.txt", "a");
        await using var server = await StartAsync(root);

        var response = await server.Client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.True(
            response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.BadRequest,
            $"{path} answered {(int)response.StatusCode}"
        );
    }

    [Fact]
    public async Task A_filter_hides_an_entry_and_refuses_every_operation_on_it()
    {
        using var root = new ContentRoot().With("public.txt", "ok").With("private.txt", "no");

        await using var server = await StartAsync(root, o => o.Filter = p => p != "private.txt");

        var request = Request("PROPFIND", "/dav");
        request.Headers.Add("Depth", "1");

        var document = await XmlAsync(await server.Client.SendAsync(request, TestContext.Current.CancellationToken));

        Assert.DoesNotContain(
            document.Root!.Elements(Dav + "response"),
            r => r.Element(Dav + "href")!.Value.Contains("private")
        );

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await server.Client.GetAsync("/dav/private.txt", TestContext.Current.CancellationToken)).StatusCode
        );
    }

    static Task<TestServer> StartSecuredAsync(ContentRoot root, bool changesOnly) => TestServer.StartAsync(
        app =>
        {
            app.UseAuthentication();
            app.UseAuthorization();

            var mount = app.MapWebDav("/dav", o =>
            {
                o.RootPath = root.Path;
                o.AllowWrite = true;
                o.AllowDelete = true;
            });

            if (changesOnly)
                mount.RequireAuthorizationForChanges();
            else
                mount.RequireAuthorization();
        },
        builder =>
        {
            builder.AddAuthentication().AddBasic(o => o.AddUser("ada", "hunter2"));
            builder.AddAuthorization();
        }
    );

    [Fact]
    public async Task Require_authorization_covers_every_verb_the_mount_registered()
    {
        using var root = new ContentRoot().With("a.txt", "a");
        await using var server = await StartSecuredAsync(root, changesOnly: false);

        foreach (var (method, path) in ((string, string)[])
                 [
                     ("PROPFIND", "/dav"),
                     ("GET", "/dav/a.txt"),
                     ("OPTIONS", "/dav"),
                     ("PUT", "/dav/b.txt"),
                     ("MKCOL", "/dav/docs"),
                     ("DELETE", "/dav/a.txt"),
                     ("LOCK", "/dav/a.txt"),
                     ("UNLOCK", "/dav/a.txt")
                 ])
        {
            var response = await server.Client.SendAsync(Request(method, path), TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task Require_authorization_for_changes_leaves_reads_open()
    {
        using var root = new ContentRoot().With("a.txt", "a");
        await using var server = await StartSecuredAsync(root, changesOnly: true);

        Assert.Equal(
            HttpStatusCode.OK,
            (await server.Client.GetAsync("/dav/a.txt", TestContext.Current.CancellationToken)).StatusCode
        );

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await server.Client.PutAsync("/dav/b.txt", new StringContent("x"), TestContext.Current.CancellationToken)).StatusCode
        );
    }
}
