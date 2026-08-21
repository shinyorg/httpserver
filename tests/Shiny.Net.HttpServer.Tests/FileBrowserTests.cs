using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shiny.Net.HttpServer.FileBrowser;
using Shiny.Net.HttpServer.Security;

namespace Shiny.Net.HttpServer.Tests;

public class FileBrowserTests
{
    static Task<TestServer> StartAsync(ContentRoot root, Action<FileBrowserOptions>? configure = null)
        => TestServer.StartAsync(app => app.MapFileBrowser("/files", o =>
        {
            o.RootPath = root.Path;
            configure?.Invoke(o);
        }));

    static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        return JsonDocument.Parse(body).RootElement.Clone();
    }

    [Fact]
    public async Task Lists_the_root_with_metadata()
    {
        using var root = new ContentRoot()
            .With("notes.txt", "hello")
            .With("docs/readme.md", "# hi");

        await using var server = await StartAsync(root);

        var listing = await JsonAsync(await server.Client.GetAsync("/files", TestContext.Current.CancellationToken));
        var entries = listing.GetProperty("entries");

        Assert.Equal(string.Empty, listing.GetProperty("path").GetString());
        Assert.Equal(2, entries.GetArrayLength());

        // Directories first, then files — a listing that mixes them is harder to read and to render.
        Assert.True(entries[0].GetProperty("isDirectory").GetBoolean());
        Assert.Equal("docs", entries[0].GetProperty("name").GetString());

        var file = entries[1];

        Assert.False(file.GetProperty("isDirectory").GetBoolean());
        Assert.Equal("notes.txt", file.GetProperty("name").GetString());
        Assert.Equal("notes.txt", file.GetProperty("path").GetString());
        Assert.Equal(5, file.GetProperty("size").GetInt64());
        Assert.Equal("text/plain; charset=utf-8", file.GetProperty("contentType").GetString());
        Assert.True(file.GetProperty("lastModifiedUtc").GetDateTimeOffset() > DateTimeOffset.UnixEpoch);
    }

    [Fact]
    public async Task Lists_a_subdirectory()
    {
        using var root = new ContentRoot().With("docs/readme.md", "# hi");
        await using var server = await StartAsync(root);

        var listing = await JsonAsync(await server.Client.GetAsync("/files/docs", TestContext.Current.CancellationToken));

        Assert.Equal("docs", listing.GetProperty("path").GetString());
        Assert.Equal("docs/readme.md", listing.GetProperty("entries")[0].GetProperty("path").GetString());
    }

    [Fact]
    public async Task Reads_a_file()
    {
        using var root = new ContentRoot().With("notes.txt", "hello there");
        await using var server = await StartAsync(root);

        var response = await server.Client.GetAsync("/files/notes.txt", TestContext.Current.CancellationToken);

        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("hello there", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>Downloads go through the file result, so resuming a large one works.</summary>
    [Fact]
    public async Task Supports_ranges_and_conditional_requests()
    {
        using var root = new ContentRoot().With("data.txt", "0123456789");
        await using var server = await StartAsync(root);

        using var ranged = new HttpRequestMessage(HttpMethod.Get, "/files/data.txt");
        ranged.Headers.Range = new RangeHeaderValue(2, 5);

        var partial = await server.Client.SendAsync(ranged, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.Equal("2345", await partial.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var first = await server.Client.GetAsync("/files/data.txt", TestContext.Current.CancellationToken);

        using var conditional = new HttpRequestMessage(HttpMethod.Get, "/files/data.txt");
        conditional.Headers.IfNoneMatch.Add(first.Headers.ETag!);

        Assert.Equal(
            HttpStatusCode.NotModified,
            (await server.Client.SendAsync(conditional, TestContext.Current.CancellationToken)).StatusCode
        );
    }

    [Fact]
    public async Task Answers_404_for_something_that_is_not_there()
    {
        using var root = new ContentRoot().With("notes.txt", "hello");
        await using var server = await StartAsync(root);

        var response = await server.Client.GetAsync("/files/nope.txt", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    // ---- Writing ----

    [Fact]
    public async Task Refuses_to_write_unless_writing_is_enabled()
    {
        using var root = new ContentRoot();
        await using var server = await StartAsync(root);

        var response = await server.Client.PutAsync(
            "/files/new.txt",
            new StringContent("nope"),
            TestContext.Current.CancellationToken
        );

        // No route is mapped at all, so the router answers rather than the handler.
        Assert.True(
            response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed,
            $"expected the write route not to exist, got {response.StatusCode}"
        );

        Assert.False(File.Exists(Path.Combine(root.Path, "new.txt")));
    }

    [Fact]
    public async Task Writes_a_new_file()
    {
        using var root = new ContentRoot();
        await using var server = await StartAsync(root, o => o.AllowWrite = true);

        var response = await server.Client.PutAsync(
            "/files/notes/new.txt",
            new StringContent("written over http"),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var entry = await JsonAsync(response);
        Assert.Equal("notes/new.txt", entry.GetProperty("path").GetString());

        // Parent directories are created, so a caller does not have to walk the tree first.
        Assert.Equal("written over http", await File.ReadAllTextAsync(
            Path.Combine(root.Path, "notes", "new.txt"),
            TestContext.Current.CancellationToken
        ));
    }

    [Fact]
    public async Task Overwrites_an_existing_file_and_says_so()
    {
        using var root = new ContentRoot().With("notes.txt", "before");
        await using var server = await StartAsync(root, o => o.AllowWrite = true);

        var response = await server.Client.PutAsync(
            "/files/notes.txt",
            new StringContent("after"),
            TestContext.Current.CancellationToken
        );

        // 200 rather than 201: nothing was created.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("after", await File.ReadAllTextAsync(
            Path.Combine(root.Path, "notes.txt"),
            TestContext.Current.CancellationToken
        ));
    }

    [Fact]
    public async Task Creates_a_directory_for_a_path_ending_in_a_slash()
    {
        using var root = new ContentRoot();
        await using var server = await StartAsync(root, o => o.AllowWrite = true);

        var response = await server.Client.PutAsync(
            "/files/photos/",
            new StringContent(string.Empty),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(Directory.Exists(Path.Combine(root.Path, "photos")));
    }

    /// <summary>A device has finite storage and the caller is on the far side of a network.</summary>
    [Fact]
    public async Task Refuses_a_body_over_the_limit()
    {
        using var root = new ContentRoot();

        await using var server = await StartAsync(root, o =>
        {
            o.AllowWrite = true;
            o.MaxUploadBytes = 32;
        });

        var response = await server.Client.PutAsync(
            "/files/big.bin",
            new StringContent(new string('x', 1024)),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);

        // And nothing is left behind, including the staging file.
        Assert.Empty(Directory.GetFiles(root.Path));
    }

    /// <summary>
    /// A failed upload must not leave a half-written file where a whole one used to be — the write
    /// lands on a staging file and is moved into place.
    /// </summary>
    [Fact]
    public async Task Leaves_the_previous_file_intact_when_a_write_is_refused()
    {
        using var root = new ContentRoot().With("notes.txt", "the original");

        await using var server = await StartAsync(root, o =>
        {
            o.AllowWrite = true;
            o.MaxUploadBytes = 8;
        });

        await server.Client.PutAsync(
            "/files/notes.txt",
            new StringContent(new string('x', 512)),
            TestContext.Current.CancellationToken
        );

        Assert.Equal("the original", await File.ReadAllTextAsync(
            Path.Combine(root.Path, "notes.txt"),
            TestContext.Current.CancellationToken
        ));

        Assert.Single(Directory.GetFiles(root.Path));
    }

    // ---- Deleting ----

    [Fact]
    public async Task Refuses_to_delete_unless_deleting_is_enabled()
    {
        using var root = new ContentRoot().With("notes.txt", "hello");
        await using var server = await StartAsync(root);

        await server.Client.DeleteAsync("/files/notes.txt", TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(root.Path, "notes.txt")));
    }

    [Fact]
    public async Task Deletes_a_file()
    {
        using var root = new ContentRoot().With("notes.txt", "hello");
        await using var server = await StartAsync(root, o => o.AllowDelete = true);

        var response = await server.Client.DeleteAsync("/files/notes.txt", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(File.Exists(Path.Combine(root.Path, "notes.txt")));
    }

    /// <summary>Recursive delete behind a URL is one mistyped path from taking everything.</summary>
    [Fact]
    public async Task Refuses_to_delete_a_directory_that_is_not_empty()
    {
        using var root = new ContentRoot().With("docs/readme.md", "# hi");
        await using var server = await StartAsync(root, o => o.AllowDelete = true);

        var response = await server.Client.DeleteAsync("/files/docs", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.True(Directory.Exists(Path.Combine(root.Path, "docs")));
    }

    [Fact]
    public async Task Deletes_an_empty_directory()
    {
        using var root = new ContentRoot();
        Directory.CreateDirectory(Path.Combine(root.Path, "empty"));

        await using var server = await StartAsync(root, o => o.AllowDelete = true);

        var response = await server.Client.DeleteAsync("/files/empty", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(Directory.Exists(Path.Combine(root.Path, "empty")));
    }

    // ---- Containment ----

    /// <summary>
    /// The path arrives already percent-decoded, so <c>%2e%2e%2f</c> is a plain <c>../</c> by the
    /// time anything looks at it.
    /// </summary>
    [Theory]
    [InlineData("/files/../secret.txt")]
    [InlineData("/files/%2e%2e/secret.txt")]
    [InlineData("/files/%2e%2e%2fsecret.txt")]
    [InlineData("/files/docs/../../secret.txt")]
    [InlineData("/files/..%2fsecret.txt")]
    public async Task Refuses_to_escape_the_root(string path)
    {
        using var outer = new ContentRoot().With("secret.txt", "TOP SECRET");

        var served = Path.Combine(outer.Path, "public");
        Directory.CreateDirectory(served);
        await File.WriteAllTextAsync(Path.Combine(served, "ok.txt"), "fine", TestContext.Current.CancellationToken);

        await using var server = await TestServer.StartAsync(app => app.MapFileBrowser("/files", o =>
        {
            o.RootPath = served;
            o.AllowWrite = true;
            o.AllowDelete = true;
        }));

        // The file it is reaching for really is there, one level up, so a pass here would be a
        // genuine escape rather than a missing fixture.
        Assert.Equal("fine", await server.Client.GetStringAsync("/files/ok.txt", TestContext.Current.CancellationToken));

        var response = await server.Client.GetAsync(path, TestContext.Current.CancellationToken);
        var body = response.IsSuccessStatusCode
            ? await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
            : string.Empty;

        Assert.DoesNotContain("TOP SECRET", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refuses_to_write_outside_the_root()
    {
        using var outer = new ContentRoot();

        var served = Path.Combine(outer.Path, "public");
        Directory.CreateDirectory(served);

        await using var server = await TestServer.StartAsync(app => app.MapFileBrowser("/files", o =>
        {
            o.RootPath = served;
            o.AllowWrite = true;
        }));

        await server.Client.PutAsync(
            "/files/../escaped.txt",
            new StringContent("nope"),
            TestContext.Current.CancellationToken
        );

        Assert.False(File.Exists(Path.Combine(outer.Path, "escaped.txt")));
    }

    [Fact]
    public async Task Hides_dotfiles_by_default()
    {
        using var root = new ContentRoot().With(".env", "API_KEY=secret").With("notes.txt", "hello");
        await using var server = await StartAsync(root);

        var listing = await JsonAsync(await server.Client.GetAsync("/files", TestContext.Current.CancellationToken));

        Assert.Equal(1, listing.GetProperty("entries").GetArrayLength());
        Assert.Equal("notes.txt", listing.GetProperty("entries")[0].GetProperty("name").GetString());

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await server.Client.GetAsync("/files/.env", TestContext.Current.CancellationToken)).StatusCode
        );
    }

    [Fact]
    public async Task Honours_a_filter()
    {
        using var root = new ContentRoot().With("keep.txt", "yes").With("hide.log", "no");

        await using var server = await StartAsync(root, o =>
            o.Filter = path => !path.EndsWith(".log", StringComparison.OrdinalIgnoreCase)
        );

        var listing = await JsonAsync(await server.Client.GetAsync("/files", TestContext.Current.CancellationToken));

        Assert.Equal(1, listing.GetProperty("entries").GetArrayLength());
        Assert.Equal("keep.txt", listing.GetProperty("entries")[0].GetProperty("name").GetString());

        // Hidden from the listing *and* refused directly — otherwise the filter is decoration.
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await server.Client.GetAsync("/files/hide.log", TestContext.Current.CancellationToken)).StatusCode
        );
    }
}

/// <summary>
/// Mounted at "/" the browser owns the whole site, which is what a plain file server is. The
/// prefix is the one part of the mapping that has no segment to hang the catch-all off, so it is
/// worth pinning down separately.
/// </summary>
public class FileBrowserRootMountTests
{
    static Task<TestServer> StartAsync(ContentRoot root)
        => TestServer.StartAsync(app => app.MapFileBrowser("/", o =>
        {
            o.RootPath = root.Path;
            o.AllowWrite = true;
            o.AllowDelete = true;
        }));

    [Fact]
    public async Task Serves_everything_from_the_site_root()
    {
        using var root = new ContentRoot()
            .With("notes.txt", "hello")
            .With("docs/readme.md", "# hi");

        await using var server = await StartAsync(root);
        var ct = TestContext.Current.CancellationToken;

        var listing = await server.Client.GetStringAsync("/", ct);

        Assert.Contains("\"notes.txt\"", listing);
        Assert.Equal("hello", await server.Client.GetStringAsync("/notes.txt", ct));
        Assert.Equal("# hi", await server.Client.GetStringAsync("/docs/readme.md", ct));
        Assert.Contains("\"docs/readme.md\"", await server.Client.GetStringAsync("/docs", ct));
    }

    [Fact]
    public async Task Writes_and_deletes_from_the_site_root()
    {
        using var root = new ContentRoot().With("notes.txt", "hello");

        await using var server = await StartAsync(root);
        var ct = TestContext.Current.CancellationToken;

        var written = await server.Client.PutAsync("/new.txt", new StringContent("written"), ct);

        Assert.Equal(HttpStatusCode.Created, written.StatusCode);
        Assert.Equal("written", File.ReadAllText(Path.Combine(root.Path, "new.txt")));

        var deleted = await server.Client.DeleteAsync("/notes.txt", ct);

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.False(File.Exists(Path.Combine(root.Path, "notes.txt")));
    }

    /// <summary>
    /// A browser over the whole site is only usable if the app can still own a path within it —
    /// literals beat the catch-all, so it can.
    /// </summary>
    [Fact]
    public async Task Leaves_more_specific_routes_alone()
    {
        using var root = new ContentRoot().With("health", "not the endpoint");

        await using var server = await TestServer.StartAsync(app =>
        {
            app.MapFileBrowser("/", o => o.RootPath = root.Path);
            app.MapGet("/health", ctx => ctx.Response.WriteTextAsync("ok"));
        });

        Assert.Equal("ok", await server.Client.GetStringAsync("/health", TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The templates are what every log line, diagnostic and OpenAPI path is built from, so a
    /// doubled slash would surface everywhere even though the router itself tolerates it.
    /// </summary>
    [Fact]
    public async Task Registers_routes_without_a_doubled_slash()
    {
        using var root = new ContentRoot().With("notes.txt", "hello");
        await using var server = await StartAsync(root);

        var templates = server.Server.Router.Endpoints.Select(x => x.Template.RawText).ToList();

        Assert.DoesNotContain(templates, x => x.Contains("//", StringComparison.Ordinal));
        Assert.Contains("/{*path}", templates);
    }
}

/// <summary>
/// The reason these are routes rather than middleware: each one can carry its own authorization.
/// </summary>
public class FileBrowserAuthorizationTests
{
    static Task<TestServer> StartAsync(ContentRoot root, Action<FileBrowserEndpoints> protect)
        => TestServer.StartAsync(
            app =>
            {
                app.UseAuthentication();
                app.UseAuthorization();

                protect(app.MapFileBrowser("/files", o =>
                {
                    o.RootPath = root.Path;
                    o.AllowWrite = true;
                    o.AllowDelete = true;
                }));
            },
            builder =>
            {
                builder.Services.AddAuthentication().AddApiKey(o => o.AddKey("secret-key", "editor", "editor"));
                builder.Services.AddAuthorization(o => o.AddPolicy("editors", p => p.RequireRole("editor")));
            }
        );

    static HttpRequestMessage Authenticated(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-API-Key", "secret-key");

        return request;
    }

    [Fact]
    public async Task Locks_every_route_when_asked_to()
    {
        using var root = new ContentRoot().With("notes.txt", "hello");
        await using var server = await StartAsync(root, endpoints => endpoints.RequireAuthorization());

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await server.Client.GetAsync("/files", TestContext.Current.CancellationToken)).StatusCode
        );

        using var authorized = Authenticated(HttpMethod.Get, "/files");

        Assert.Equal(
            HttpStatusCode.OK,
            (await server.Client.SendAsync(authorized, TestContext.Current.CancellationToken)).StatusCode
        );
    }

    /// <summary>
    /// The arrangement most apps actually want: anyone may look, only an editor may change
    /// anything.
    /// </summary>
    [Fact]
    public async Task Can_protect_only_the_routes_that_change_something()
    {
        using var root = new ContentRoot().With("notes.txt", "hello");
        await using var server = await StartAsync(root, endpoints => endpoints.RequireAuthorizationForChanges("editors"));

        // Reads stay open.
        Assert.Equal(
            HttpStatusCode.OK,
            (await server.Client.GetAsync("/files", TestContext.Current.CancellationToken)).StatusCode
        );

        // Writes and deletes do not.
        var anonymousWrite = await server.Client.PutAsync(
            "/files/new.txt",
            new StringContent("nope"),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.Unauthorized, anonymousWrite.StatusCode);
        Assert.False(File.Exists(Path.Combine(root.Path, "new.txt")));

        using var authorizedWrite = Authenticated(HttpMethod.Put, "/files/new.txt");
        authorizedWrite.Content = new StringContent("allowed");

        Assert.Equal(
            HttpStatusCode.Created,
            (await server.Client.SendAsync(authorizedWrite, TestContext.Current.CancellationToken)).StatusCode
        );
    }
}
