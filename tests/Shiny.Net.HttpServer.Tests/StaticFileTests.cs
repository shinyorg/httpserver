using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Shiny.Net.HttpServer.Compression;
using Shiny.Net.HttpServer.StaticFiles;

namespace Shiny.Net.HttpServer.Tests;

/// <summary>
/// A temporary content directory, torn down with the test.
/// </summary>
sealed class ContentRoot : IDisposable
{
    public ContentRoot()
    {
        this.Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "shiny-static-" + Guid.NewGuid().ToString("n")[..8]
        );

        Directory.CreateDirectory(this.Path);
    }

    public string Path { get; }

    public ContentRoot With(string relativePath, string content)
    {
        var full = System.IO.Path.Combine(this.Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);

        return this;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(this.Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public class StaticFileTests
{
    [Fact]
    public async Task Serves_a_file_with_the_right_content_type()
    {
        using var root = new ContentRoot().With("app.js", "console.log(1);");
        await using var server = await TestServer.StartAsync(app => app.UseStaticFiles(root.Path));

        var response = await server.Client.GetAsync("/app.js", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/javascript", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("console.log(1);", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Serves_the_default_document_for_a_directory()
    {
        using var root = new ContentRoot()
            .With("index.html", "<h1>root</h1>")
            .With("docs/index.html", "<h1>docs</h1>");

        await using var server = await TestServer.StartAsync(app => app.UseStaticFiles(root.Path));

        Assert.Equal("<h1>root</h1>", await server.Client.GetStringAsync("/", TestContext.Current.CancellationToken));
        Assert.Equal("<h1>docs</h1>", await server.Client.GetStringAsync("/docs/", TestContext.Current.CancellationToken));
    }

    /// <summary>A path that resolves to no file is not the static handler's to answer.</summary>
    [Fact]
    public async Task Falls_through_to_the_pipeline_when_nothing_matches()
    {
        using var root = new ContentRoot().With("app.js", "x");

        await using var server = await TestServer.StartAsync(app =>
        {
            app.UseStaticFiles(root.Path);
            app.MapGet("/api/ping", ctx => ctx.Response.WriteTextAsync("pong"));
        });

        Assert.Equal("pong", await server.Client.GetStringAsync("/api/ping", TestContext.Current.CancellationToken));

        var missing = await server.Client.GetAsync("/nope.js", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    /// <summary>
    /// The request path arrives already percent-decoded, so <c>%2e%2e%2f</c> is a plain <c>../</c>
    /// by the time anything looks at it — which is exactly how this keeps being rediscovered.
    /// </summary>
    [Theory]
    [InlineData("/../secret.txt")]
    [InlineData("/%2e%2e/secret.txt")]
    [InlineData("/%2e%2e%2fsecret.txt")]
    [InlineData("/sub/../../secret.txt")]
    [InlineData("/..%2fsecret.txt")]
    [InlineData("/....//secret.txt")]
    public async Task Refuses_to_escape_the_content_root(string path)
    {
        using var outer = new ContentRoot().With("secret.txt", "TOP SECRET");
        var contentPath = Path.Combine(outer.Path, "public");
        Directory.CreateDirectory(contentPath);
        File.WriteAllText(Path.Combine(contentPath, "ok.txt"), "fine");

        await using var server = await TestServer.StartAsync(app => app.UseStaticFiles(contentPath));

        // The file it is reaching for really is there, one level up — so a pass here would be a
        // genuine escape rather than a missing fixture.
        Assert.Equal("fine", await server.Client.GetStringAsync("/ok.txt", TestContext.Current.CancellationToken));

        var response = await server.Client.GetAsync(path, TestContext.Current.CancellationToken);
        var body = response.IsSuccessStatusCode
            ? await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
            : string.Empty;

        Assert.DoesNotContain("TOP SECRET", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Does_not_serve_dotfiles()
    {
        using var root = new ContentRoot().With(".env", "API_KEY=secret").With("app.js", "x");
        await using var server = await TestServer.StartAsync(app => app.UseStaticFiles(root.Path));

        var response = await server.Client.GetAsync("/.env", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Serves_dotfiles_when_told_to()
    {
        using var root = new ContentRoot().With(".well-known/thing.json", "{}");

        await using var server = await TestServer.StartAsync(app => app.UseStaticFiles(
            new PhysicalFileSource(root.Path) { ServeHiddenFiles = true }
        ));

        Assert.Equal("{}", await server.Client.GetStringAsync("/.well-known/thing.json", TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Guessing a type for an unknown extension is how an upload directory starts serving HTML.
    /// </summary>
    [Fact]
    public async Task Does_not_serve_unknown_extensions_by_default()
    {
        using var root = new ContentRoot().With("thing.weird", "payload");
        await using var server = await TestServer.StartAsync(app => app.UseStaticFiles(root.Path));

        var response = await server.Client.GetAsync("/thing.weird", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Serves_unknown_extensions_when_told_to()
    {
        using var root = new ContentRoot().With("thing.weird", "payload");

        await using var server = await TestServer.StartAsync(app => app.UseStaticFiles(
            root.Path,
            o =>
            {
                o.ServeUnknownFileTypes = true;
                o.DefaultContentType = "application/octet-stream";
            }
        ));

        var response = await server.Client.GetAsync("/thing.weird", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Honours_a_content_type_override()
    {
        using var root = new ContentRoot().With("data.weird", "payload");

        await using var server = await TestServer.StartAsync(app => app.UseStaticFiles(
            root.Path,
            o => o.ContentTypeOverrides[".weird"] = "application/x-weird"
        ));

        var response = await server.Client.GetAsync("/data.weird", TestContext.Current.CancellationToken);

        Assert.Equal("application/x-weird", response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>Conditional requests are the point of an ETag, and they come from the download path.</summary>
    [Fact]
    public async Task Answers_a_matching_conditional_request_with_304()
    {
        using var root = new ContentRoot().With("app.js", "console.log(1);");
        await using var server = await TestServer.StartAsync(app => app.UseStaticFiles(root.Path));

        var first = await server.Client.GetAsync("/app.js", TestContext.Current.CancellationToken);
        var etag = first.Headers.ETag;

        Assert.NotNull(etag);

        using var conditional = new HttpRequestMessage(HttpMethod.Get, "/app.js");
        conditional.Headers.IfNoneMatch.Add(etag);

        var second = await server.Client.SendAsync(conditional, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
    }

    [Fact]
    public async Task Serves_a_byte_range()
    {
        using var root = new ContentRoot().With("data.txt", "0123456789");
        await using var server = await TestServer.StartAsync(app => app.UseStaticFiles(root.Path));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/data.txt");
        request.Headers.Range = new RangeHeaderValue(2, 5);

        var response = await server.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("2345", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Serves_under_a_request_path_prefix()
    {
        using var root = new ContentRoot().With("app.js", "x");

        await using var server = await TestServer.StartAsync(app => app.UseStaticFiles(
            root.Path,
            o => o.RequestPath = "/assets"
        ));

        Assert.Equal("x", await server.Client.GetStringAsync("/assets/app.js", TestContext.Current.CancellationToken));

        var unprefixed = await server.Client.GetAsync("/app.js", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, unprefixed.StatusCode);
    }

    /// <summary>"/assetsomething" begins with "/assets" without being inside it.</summary>
    [Fact]
    public async Task Does_not_treat_a_prefix_match_as_a_directory_match()
    {
        using var root = new ContentRoot().With("app.js", "x");

        await using var server = await TestServer.StartAsync(app => app.UseStaticFiles(
            root.Path,
            o => o.RequestPath = "/assets"
        ));

        var response = await server.Client.GetAsync("/assetsapp.js", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Sets_cache_control_and_runs_the_prepare_callback()
    {
        using var root = new ContentRoot().With("app.js", "x");
        var seen = string.Empty;

        await using var server = await TestServer.StartAsync(app => app.UseStaticFiles(
            root.Path,
            o =>
            {
                o.CacheFor(TimeSpan.FromHours(1), immutable: true);
                o.OnPrepareResponse = ctx =>
                {
                    seen = ctx.File.Name;
                    ctx.HttpContext.Response.Headers["X-Served-By"] = "static";
                };
            }
        ));

        var response = await server.Client.GetAsync("/app.js", TestContext.Current.CancellationToken);

        Assert.Equal("public, max-age=3600, immutable", response.Headers.CacheControl?.ToString());
        Assert.Equal("static", response.Headers.GetValues("X-Served-By").Single());
        Assert.Equal("app.js", seen);
    }

    [Fact]
    public async Task Ignores_methods_other_than_get_and_head()
    {
        using var root = new ContentRoot().With("app.js", "x");

        await using var server = await TestServer.StartAsync(app =>
        {
            app.UseStaticFiles(root.Path);
            app.MapPost("/app.js", ctx => ctx.Response.WriteTextAsync("posted"));
        });

        var response = await server.Client.PostAsync("/app.js", content: null, TestContext.Current.CancellationToken);

        Assert.Equal("posted", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Answers_head_with_headers_and_no_body()
    {
        using var root = new ContentRoot().With("app.js", "console.log(1);");
        await using var server = await TestServer.StartAsync(app => app.UseStaticFiles(root.Path));

        using var request = new HttpRequestMessage(HttpMethod.Head, "/app.js");
        var response = await server.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(15, response.Content.Headers.ContentLength);
        Assert.Empty(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }
}

public class SpaFallbackTests
{
    static Task<TestServer> StartAsync(ContentRoot root) => TestServer.StartAsync(app =>
    {
        app.UseStaticFiles(root.Path, o => o.FallbackFile = "index.html");
        app.MapGet("/api/ping", ctx => ctx.Response.WriteTextAsync("pong"));
    });

    /// <summary>A deep link belongs to the client-side router, which lives in the entry document.</summary>
    [Fact]
    public async Task Serves_the_entry_document_for_a_deep_link()
    {
        using var root = new ContentRoot().With("index.html", "<div id=app></div>");
        await using var server = await StartAsync(root);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/orders/42");
        request.Headers.Accept.ParseAdd("text/html");

        var response = await server.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("<div id=app></div>", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A missing asset must 404 honestly. Answering with HTML produces a script that fails to parse
    /// and an error message pointing nowhere near the actual problem.
    /// </summary>
    [Fact]
    public async Task Does_not_serve_the_entry_document_for_a_missing_asset()
    {
        using var root = new ContentRoot().With("index.html", "<div id=app></div>");
        await using var server = await StartAsync(root);

        var response = await server.Client.GetAsync("/missing.js", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Does_not_serve_the_entry_document_to_a_client_that_wants_json()
    {
        using var root = new ContentRoot().With("index.html", "<div id=app></div>");
        await using var server = await StartAsync(root);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/unknown");
        request.Headers.Accept.ParseAdd("application/json");

        var response = await server.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>A real route must win over the catch-all.</summary>
    [Fact]
    public async Task Leaves_matched_routes_alone()
    {
        using var root = new ContentRoot().With("index.html", "<div id=app></div>");
        await using var server = await StartAsync(root);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/ping");
        request.Headers.Accept.ParseAdd("text/html");

        var response = await server.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal("pong", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }
}

public class EmbeddedFileSourceTests
{
    static readonly System.Reflection.Assembly TestAssembly = typeof(EmbeddedFileSourceTests).Assembly;

    /// <summary>
    /// The packaged-app case: a MAUI or single-file build has no content directory, so the assets
    /// travel inside the assembly.
    /// </summary>
    [Fact]
    public async Task Serves_a_file_embedded_in_the_assembly()
    {
        await using var server = await TestServer.StartAsync(app => app.UseEmbeddedFiles(
            TestAssembly,
            "Shiny.Net.HttpServer.Tests.TestAssets"
        ));

        var response = await server.Client.GetAsync("/embedded.html", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("from the assembly", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Resource names are flattened at build time, so a subdirectory is only recoverable by
    /// reversing that — which is the one thing worth testing here.
    /// </summary>
    [Fact]
    public async Task Serves_a_file_from_an_embedded_subdirectory()
    {
        await using var server = await TestServer.StartAsync(app => app.UseEmbeddedFiles(
            TestAssembly,
            "Shiny.Net.HttpServer.Tests.TestAssets"
        ));

        var body = await server.Client.GetStringAsync("/css/site.css", TestContext.Current.CancellationToken);

        Assert.Contains("rebeccapurple", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Serves_an_embedded_default_document()
    {
        await using var server = await TestServer.StartAsync(app => app.UseEmbeddedFiles(
            TestAssembly,
            "Shiny.Net.HttpServer.Tests.TestAssets",
            o => o.DefaultDocuments.Insert(0, "embedded.html")
        ));

        var body = await server.Client.GetStringAsync("/", TestContext.Current.CancellationToken);

        Assert.Contains("from the assembly", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reports_a_stable_etag_for_embedded_content()
    {
        var source = new EmbeddedFileSource(TestAssembly, "Shiny.Net.HttpServer.Tests.TestAssets");

        Assert.True(source.TryGetFile("embedded.html", out var first));
        Assert.True(source.TryGetFile("embedded.html", out var second));

        Assert.Equal(first.ETag, second.ETag);
        Assert.NotEqual(string.Empty, first.ETag);
    }

    [Fact]
    public void Refuses_traversal_out_of_the_resource_namespace()
    {
        var source = new EmbeddedFileSource(TestAssembly, "Shiny.Net.HttpServer.Tests.TestAssets");

        Assert.False(source.TryGetFile("../other.html", out _));
        Assert.False(source.TryGetFile("nope.html", out _));
    }

    /// <summary>
    /// The development arrangement: a directory in front so an edit shows up without a rebuild,
    /// embedded resources behind it so the packaged app still works.
    /// </summary>
    [Fact]
    public async Task Prefers_an_earlier_source_in_a_composite()
    {
        using var root = new ContentRoot().With("embedded.html", "<h1>from disk</h1>");

        await using var server = await TestServer.StartAsync(app => app.UseStaticFiles(
            new CompositeFileSource(
                new PhysicalFileSource(root.Path),
                new EmbeddedFileSource(TestAssembly, "Shiny.Net.HttpServer.Tests.TestAssets")
            )
        ));

        Assert.Contains(
            "from disk",
            await server.Client.GetStringAsync("/embedded.html", TestContext.Current.CancellationToken),
            StringComparison.Ordinal
        );

        // Still reachable through the second source.
        Assert.Contains(
            "rebeccapurple",
            await server.Client.GetStringAsync("/css/site.css", TestContext.Current.CancellationToken),
            StringComparison.Ordinal
        );
    }
}

/// <summary>
/// Serving a published Blazor WebAssembly app. Verified against a real publish output during
/// development; these pin the three things that make it work at all.
/// </summary>
public class BlazorWebAssemblyTests
{
    /// <summary>
    /// The runtime cannot start without its globalization data, and an unknown extension is not
    /// served — so a missing content type here is a blank page, not a slow page.
    /// </summary>
    [Theory]
    [InlineData("icudt_EFIGS.dat", "application/octet-stream")]
    [InlineData("app.blat", "application/octet-stream")]
    [InlineData("App.wasm", "application/wasm")]
    [InlineData("App.webcil", "application/octet-stream")]
    [InlineData("App.pdb", "application/octet-stream")]
    [InlineData("dotnet.js", "text/javascript; charset=utf-8")]
    [InlineData("blazor.boot.json", "application/json; charset=utf-8")]
    [InlineData("manifest.webmanifest", "application/manifest+json")]
    public void Knows_the_content_types_a_blazor_publish_emits(string file, string expected)
    {
        Assert.Equal(expected, Shiny.Net.HttpServer.Files.ContentTypes.ForFileName(file));
        Assert.True(Shiny.Net.HttpServer.Files.ContentTypes.IsKnownExtension(Path.GetExtension(file)));
    }

    [Fact]
    public async Task Serves_the_app_and_falls_back_for_client_side_routes()
    {
        using var root = new ContentRoot()
            .With("index.html", "<div id=app></div>")
            .With("_framework/dotnet.js", "// runtime")
            .With("_framework/icudt.dat", "ICU");

        await using var server = await TestServer.StartAsync(app => app.UseBlazorWebAssembly(root.Path));

        Assert.Equal("ICU", await server.Client.GetStringAsync("/_framework/icudt.dat", TestContext.Current.CancellationToken));

        using var deepLink = new HttpRequestMessage(HttpMethod.Get, "/counter");
        deepLink.Headers.Accept.ParseAdd("text/html");

        var response = await server.Client.SendAsync(deepLink, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("<div id=app></div>", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A publish compresses every asset once at maximum effort. Recompressing that per request at a
    /// level chosen for speed spends CPU to produce a larger result.
    /// </summary>
    [Fact]
    public async Task Prefers_a_precompressed_sidecar()
    {
        using var root = new ContentRoot()
            .With("index.html", "<div id=app></div>")
            .With("_framework/app.wasm", "the original bytes")
            .With("_framework/app.wasm.br", "BROTLI")
            .With("_framework/app.wasm.gz", "GZIP");

        await using var server = await TestServer.StartAsync(app => app.UseBlazorWebAssembly(root.Path));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/_framework/app.wasm");
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, br");

        var response = await server.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal("br", response.Content.Headers.ContentEncoding.Single());
        Assert.Equal("BROTLI", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        // The type still describes what the bytes decompress to, not the container.
        Assert.Equal("application/wasm", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Accept-Encoding", response.Headers.Vary, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Falls_back_to_gzip_and_then_to_the_original()
    {
        using var root = new ContentRoot()
            .With("index.html", "<div id=app></div>")
            .With("_framework/app.wasm", "the original bytes")
            .With("_framework/app.wasm.gz", "GZIP");

        await using var server = await TestServer.StartAsync(app => app.UseBlazorWebAssembly(root.Path));

        using var gzipOnly = new HttpRequestMessage(HttpMethod.Get, "/_framework/app.wasm");
        gzipOnly.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip");

        var gzip = await server.Client.SendAsync(gzipOnly, TestContext.Current.CancellationToken);

        Assert.Equal("gzip", gzip.Content.Headers.ContentEncoding.Single());
        Assert.Equal("GZIP", await gzip.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        // A client that accepts nothing gets the file itself, uncompressed.
        var plain = await server.Client.GetAsync("/_framework/app.wasm", TestContext.Current.CancellationToken);

        Assert.Empty(plain.Content.Headers.ContentEncoding);
        Assert.Equal("the original bytes", await plain.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>A client that refused a coding must not be sent it.</summary>
    [Fact]
    public async Task Does_not_serve_a_coding_the_client_refused()
    {
        using var root = new ContentRoot()
            .With("index.html", "<div id=app></div>")
            .With("_framework/app.wasm", "the original bytes")
            .With("_framework/app.wasm.br", "BROTLI");

        await using var server = await TestServer.StartAsync(app => app.UseBlazorWebAssembly(root.Path));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/_framework/app.wasm");
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "br;q=0");

        var response = await server.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Empty(response.Content.Headers.ContentEncoding);
    }

    /// <summary>Sidecars are opt-in: an unrelated .gz must not be served as an encoding of something else.</summary>
    [Fact]
    public async Task Ignores_sidecars_for_ordinary_static_files()
    {
        using var root = new ContentRoot()
            .With("data.txt", "the original bytes")
            .With("data.txt.br", "BROTLI");

        await using var server = await TestServer.StartAsync(app => app.UseStaticFiles(root.Path));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/data.txt");
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "br");

        var response = await server.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Empty(response.Content.Headers.ContentEncoding);
        Assert.Equal("the original bytes", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Framework files are named after their content hash, so they can be cached forever. The entry
    /// document keeps its URL, and caching that is how a deploy fails to reach anyone.
    /// </summary>
    [Fact]
    public async Task Caches_fingerprinted_assets_forever_and_the_entry_document_never()
    {
        using var root = new ContentRoot()
            .With("index.html", "<div id=app></div>")
            .With("_framework/app.wasm", "bytes");

        await using var server = await TestServer.StartAsync(app => app.UseBlazorWebAssembly(root.Path));

        var asset = await server.Client.GetAsync("/_framework/app.wasm", TestContext.Current.CancellationToken);
        var document = await server.Client.GetAsync("/", TestContext.Current.CancellationToken);

        Assert.Equal("public, max-age=31536000, immutable", asset.Headers.CacheControl?.ToString());
        Assert.True(document.Headers.CacheControl?.NoCache);
    }

    /// <summary>
    /// A precompressed body must not be compressed again — the client is told one encoding and
    /// would receive two.
    /// </summary>
    [Fact]
    public async Task Does_not_recompress_a_sidecar()
    {
        using var root = new ContentRoot()
            .With("index.html", "<div id=app></div>")
            .With("_framework/app.wasm", new string('x', 4096))
            .With("_framework/app.wasm.br", "BROTLI");

        await using var server = await TestServer.StartAsync(app =>
        {
            app.UseResponseCompression();
            app.UseBlazorWebAssembly(root.Path);
        });

        using var request = new HttpRequestMessage(HttpMethod.Get, "/_framework/app.wasm");
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "br, gzip");

        var response = await server.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal("br", response.Content.Headers.ContentEncoding.Single());
        Assert.Equal("BROTLI", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }
}

/// <summary>
/// A temporary zip archive, torn down with the test.
/// </summary>
sealed class ZipContent : IDisposable
{
    readonly List<(string Path, string Content)> pending = [];

    public ZipContent()
        => this.Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "shiny-zip-" + Guid.NewGuid().ToString("n")[..8] + ".zip"
        );

    public string Path { get; }

    public ZipContent With(string entryPath, string content)
    {
        this.pending.Add((entryPath, content));
        return this;
    }

    /// <summary>Writes the archive and hands back its path.</summary>
    public string Build()
    {
        using var file = File.Create(this.Path);
        using var archive = new System.IO.Compression.ZipArchive(file, System.IO.Compression.ZipArchiveMode.Create);

        foreach (var (path, content) in this.pending)
        {
            // No BOM. Encoding.UTF8 emits one, which would put three bytes in front of every
            // entry and quietly shift the offsets a range test is asserting on.
            using var writer = new StreamWriter(archive.CreateEntry(path).Open(), new UTF8Encoding(false));
            writer.Write(content);
        }

        return this.Path;
    }

    public void Dispose()
    {
        try
        {
            File.Delete(this.Path);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>
/// Serving out of a zip, which is what a packaged app ships when its assets are a publish output:
/// one resource in the assembly instead of a few thousand, and the paths survive intact.
/// </summary>
public class ZipFileSourceTests
{
    static readonly System.Reflection.Assembly TestAssembly = typeof(ZipFileSourceTests).Assembly;

    const string EmbeddedArchive = "Shiny.Net.HttpServer.Tests.Assets.zip";

    [Fact]
    public async Task Serves_a_file_from_a_zip_on_disk()
    {
        using var zip = new ZipContent()
            .With("app.js", "console.log(1);")
            .With("css/site.css", "body { color: rebeccapurple; }");

        await using var server = await TestServer.StartAsync(app => app.UseStaticFiles(new ZipFileSource(zip.Build())));

        var response = await server.Client.GetAsync("/app.js", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/javascript", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("console.log(1);", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        // A path inside the archive is a path, not a mangled resource name - which is the whole
        // reason a zip beats loose embedded resources for a publish output.
        Assert.Contains(
            "rebeccapurple",
            await server.Client.GetStringAsync("/css/site.css", TestContext.Current.CancellationToken),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task Serves_a_file_from_a_zip_embedded_in_the_assembly()
    {
        await using var server = await TestServer.StartAsync(
            app => app.UseStaticFiles(new ZipFileSource(TestAssembly, EmbeddedArchive))
        );

        var response = await server.Client.GetAsync("/embedded.html", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("from the assembly", body, StringComparison.Ordinal);

        Assert.Contains(
            "rebeccapurple",
            await server.Client.GetStringAsync("/css/site.css", TestContext.Current.CancellationToken),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void Reports_a_useful_error_for_a_missing_resource()
    {
        var error = Assert.Throws<FileNotFoundException>(() => new ZipFileSource(TestAssembly, "Nope.zip"));

        // The available names are in the message, because the one thing anyone gets wrong here is
        // the resource name and it is not discoverable from the project file.
        Assert.Contains(EmbeddedArchive, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reports_a_missing_archive_by_path()
        => Assert.Throws<FileNotFoundException>(() => new ZipFileSource("./no-such-archive.zip"));

    /// <summary>
    /// An archive zipped with its parent folder, which is what most zip tools produce.
    /// </summary>
    [Fact]
    public async Task Serves_from_a_directory_inside_the_archive()
    {
        using var zip = new ZipContent()
            .With("wwwroot/index.html", "<h1>inside</h1>")
            .With("readme.txt", "not served");

        await using var server = await TestServer.StartAsync(
            app => app.UseStaticFiles(new ZipFileSource(zip.Build(), "wwwroot"))
        );

        Assert.Contains(
            "inside",
            await server.Client.GetStringAsync("/index.html", TestContext.Current.CancellationToken),
            StringComparison.Ordinal
        );

        var outside = await server.Client.GetAsync("/readme.txt", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, outside.StatusCode);
    }

    /// <summary>
    /// A zipped Blazor publish carries the precompressed variants, and they are the reason the
    /// archive is worth serving from at all rather than unpacking it.
    /// </summary>
    [Fact]
    public async Task Prefers_a_precompressed_sidecar_inside_the_archive()
    {
        using var zip = new ZipContent()
            .With("index.html", "<div id=app></div>")
            .With("_framework/app.wasm", "the original bytes")
            .With("_framework/app.wasm.br", "BROTLI")
            .With("_framework/app.wasm.gz", "GZIP");

        await using var server = await TestServer.StartAsync(
            app => app.UseBlazorWebAssembly(new ZipFileSource(zip.Build()) { PrecompressedEncodings = ["br", "gzip"] })
        );

        using var request = new HttpRequestMessage(HttpMethod.Get, "/_framework/app.wasm");
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, br");

        var response = await server.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal("br", response.Content.Headers.ContentEncoding.Single());
        Assert.Equal("BROTLI", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("application/wasm", response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>
    /// The single-page fallback still applies, so a deep link into a zipped Blazor app reloads.
    /// </summary>
    [Fact]
    public async Task Falls_back_to_the_entry_document()
    {
        using var zip = new ZipContent().With("index.html", "<div id=app></div>");

        await using var server = await TestServer.StartAsync(
            app => app.UseBlazorWebAssembly(new ZipFileSource(zip.Build()))
        );

        using var request = new HttpRequestMessage(HttpMethod.Get, "/orders/42");
        request.Headers.TryAddWithoutValidation("Accept", "text/html");

        var response = await server.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            "id=app",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal
        );
    }

    /// <summary>
    /// The ETag comes from the archive's own checksum, so identical content tags identically no
    /// matter when either archive was written.
    /// </summary>
    [Fact]
    public void Tags_content_rather_than_timestamps()
    {
        using var first = new ZipContent().With("a.txt", "same");
        using var second = new ZipContent().With("a.txt", "same");
        using var different = new ZipContent().With("a.txt", "other");

        var one = new ZipFileSource(first.Build());
        var two = new ZipFileSource(second.Build());
        var three = new ZipFileSource(different.Build());

        Assert.True(one.TryGetFile("a.txt", out var a));
        Assert.True(two.TryGetFile("a.txt", out var b));
        Assert.True(three.TryGetFile("a.txt", out var c));

        Assert.Equal(a.ETag, b.ETag);
        Assert.NotEqual(a.ETag, c.ETag);
    }

    [Fact]
    public void Refuses_traversal_out_of_the_archive()
    {
        using var zip = new ZipContent().With("app.js", "console.log(1);");
        var source = new ZipFileSource(zip.Build());

        Assert.False(source.TryGetFile("../secrets.txt", out _));
        Assert.False(source.TryGetFile("nope.js", out _));
        Assert.True(source.TryGetFile("app.js", out _));
    }

    /// <summary>
    /// A deflated entry cannot seek, so a range over one is served by reading up to the start and
    /// discarding. Worth pinning: it is the one path where a zip behaves unlike a file on disk.
    /// </summary>
    [Fact]
    public async Task Serves_a_range_out_of_a_compressed_entry()
    {
        using var zip = new ZipContent().With("data.txt", "0123456789");

        await using var server = await TestServer.StartAsync(app => app.UseStaticFiles(new ZipFileSource(zip.Build())));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/data.txt");
        request.Headers.Range = new RangeHeaderValue(4, 6);

        var response = await server.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("456", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// One source, many requests: each response opens its own archive, so nothing is shared but the
    /// index. Reading the same entry from several requests at once is the case that would fail if
    /// an archive were held open and reused.
    /// </summary>
    [Fact]
    public async Task Serves_the_same_entry_to_concurrent_requests()
    {
        using var zip = new ZipContent().With("data.txt", new string('x', 64 * 1024));

        await using var server = await TestServer.StartAsync(app => app.UseStaticFiles(new ZipFileSource(zip.Build())));

        var bodies = await Task.WhenAll(
            Enumerable
                .Range(0, 8)
                .Select(_ => server.Client.GetStringAsync("/data.txt", TestContext.Current.CancellationToken))
        );

        Assert.All(bodies, body => Assert.Equal(64 * 1024, body.Length));
    }

    /// <summary>
    /// A directory in front of an archive is the development arrangement, and the sidecars inside
    /// the archive have to survive being behind it.
    /// </summary>
    [Fact]
    public async Task Offers_sidecars_through_a_composite()
    {
        using var root = new ContentRoot().With("index.html", "<div id=app></div>");
        using var zip = new ZipContent()
            .With("_framework/app.wasm", "the original bytes")
            .With("_framework/app.wasm.br", "BROTLI");

        await using var server = await TestServer.StartAsync(app => app.UseBlazorWebAssembly(
            new CompositeFileSource(
                new PhysicalFileSource(root.Path),
                new ZipFileSource(zip.Build()) { PrecompressedEncodings = ["br", "gzip"] }
            )
        ));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/_framework/app.wasm");
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "br");

        var response = await server.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal("br", response.Content.Headers.ContentEncoding.Single());
        Assert.Equal("BROTLI", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }
}
