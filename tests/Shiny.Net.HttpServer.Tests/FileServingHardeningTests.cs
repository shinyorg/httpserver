using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Shiny.Net.HttpServer.Compression;
using Shiny.Net.HttpServer.FileBrowser;
using Shiny.Net.HttpServer.Files;
using Shiny.Net.HttpServer.StaticFiles;

namespace Shiny.Net.HttpServer.Tests;

/// <summary>Links made for a test, or a skip where the OS will not make them.</summary>
static class Links
{
    public static void Directory(string link, string target)
    {
        try
        {
            System.IO.Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            // Windows without developer mode or elevation refuses to make a symbolic link.
            Assert.Skip($"This OS would not create a symbolic link: {ex.Message}");
        }
    }

    public static void File(string link, string target)
    {
        try
        {
            System.IO.File.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.Skip($"This OS would not create a symbolic link: {ex.Message}");
        }
    }
}

/// <summary>
/// A content root with a sibling directory outside it, holding the secret a link out of the root
/// would reach.
/// </summary>
sealed class RootWithOutside : IDisposable
{
    readonly string parent = Directory.CreateTempSubdirectory("shiny-contain-").FullName;

    public RootWithOutside()
    {
        Directory.CreateDirectory(this.Root);
        Directory.CreateDirectory(this.Outside);
        File.WriteAllText(System.IO.Path.Combine(this.Outside, "secret.txt"), "outside the root");
    }

    public string Root => System.IO.Path.Combine(this.parent, "root");

    public string Outside => System.IO.Path.Combine(this.parent, "outside");

    public string InRoot(string relative) => System.IO.Path.Combine(this.Root, relative);

    public void Dispose()
    {
        try
        {
            Directory.Delete(this.parent, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public class StaticFileContainmentTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// Only the last component's link target used to be checked, so a linked directory part way down
    /// the path led straight out of the root.
    /// </summary>
    [Fact]
    public async Task Refuses_a_file_under_a_linked_directory_that_leaves_the_root()
    {
        using var dirs = new RootWithOutside();
        Links.Directory(dirs.InRoot("link"), dirs.Outside);

        await using var server = await TestServer.StartAsync(app => app.UseStaticFiles(dirs.Root));

        var response = await server.Client.GetAsync("/link/secret.txt", Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Still_refuses_a_linked_file_that_leaves_the_root()
    {
        using var dirs = new RootWithOutside();
        Links.File(dirs.InRoot("secret.txt"), Path.Combine(dirs.Outside, "secret.txt"));

        await using var server = await TestServer.StartAsync(app => app.UseStaticFiles(dirs.Root));

        Assert.Equal(HttpStatusCode.NotFound, (await server.Client.GetAsync("/secret.txt", Token)).StatusCode);
    }

    /// <summary>A link that stays inside the root is ordinary content and keeps working.</summary>
    [Fact]
    public async Task Follows_a_linked_directory_that_stays_inside_the_root()
    {
        using var dirs = new RootWithOutside();
        Directory.CreateDirectory(dirs.InRoot("real"));
        File.WriteAllText(dirs.InRoot("real/app.js"), "console.log(1)");
        Links.Directory(dirs.InRoot("alias"), dirs.InRoot("real"));

        await using var server = await TestServer.StartAsync(app => app.UseStaticFiles(dirs.Root));

        Assert.Equal("console.log(1)", await server.Client.GetStringAsync("/alias/app.js", Token));
    }

    /// <summary>A relative link with enough ".." to climb out is the same escape by another spelling.</summary>
    [Fact]
    public async Task Refuses_a_relative_link_that_climbs_out_of_the_root()
    {
        using var dirs = new RootWithOutside();
        Directory.CreateDirectory(dirs.InRoot("sub"));
        Links.Directory(dirs.InRoot("sub/up"), Path.Combine("..", "..", "outside"));

        await using var server = await TestServer.StartAsync(app => app.UseStaticFiles(dirs.Root));

        Assert.Equal(HttpStatusCode.NotFound, (await server.Client.GetAsync("/sub/up/secret.txt", Token)).StatusCode);
    }

    /// <summary>
    /// <c>UseStaticFiles(path)</c> built its source before the options were configured, so the source
    /// never learned to look for sidecars and the option did nothing.
    /// </summary>
    [Fact]
    public async Task Serves_a_sidecar_when_given_a_path_with_precompressed_files_on()
    {
        using var root = new ContentRoot().With("app.js", "console.log('identity')").With("app.js.br", "brotli-bytes");

        await using var server = await TestServer.StartAsync(app => app.UseStaticFiles(root.Path, o => o.ServePrecompressedFiles = true));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/app.js");
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "br");

        var response = await server.Client.SendAsync(request, Token);

        Assert.Equal("br", Assert.Single(response.Content.Headers.ContentEncoding));
        Assert.Equal("brotli-bytes", await response.Content.ReadAsStringAsync(Token));
    }

    /// <summary>Only the literal "q=0" was read as a refusal; "q=0.0" is the same number.</summary>
    [Theory]
    [InlineData("br;q=0")]
    [InlineData("br;q=0.0")]
    [InlineData("br;q=0.000")]
    [InlineData("br; q=0.00")]
    [InlineData("BR;Q=0.0")]
    [InlineData("gzip;q=0, br;q=0.0")]
    public async Task Treats_any_zero_quality_as_a_refusal(string acceptEncoding)
    {
        using var root = new ContentRoot()
            .With("app.js", "identity")
            .With("app.js.br", "brotli")
            .With("app.js.gz", "gzip");

        await using var server = await TestServer.StartAsync(app => app.UseStaticFiles(
            new PhysicalFileSource(root.Path) { PrecompressedEncodings = ["br", "gzip"] },
            o => o.ServePrecompressedFiles = true
        ));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/app.js");
        request.Headers.TryAddWithoutValidation("Accept-Encoding", acceptEncoding);

        var response = await server.Client.SendAsync(request, Token);

        Assert.Empty(response.Content.Headers.ContentEncoding);
        Assert.Equal("identity", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Still_honours_a_non_zero_quality()
    {
        using var root = new ContentRoot().With("app.js", "identity").With("app.js.br", "brotli");

        await using var server = await TestServer.StartAsync(app => app.UseStaticFiles(root.Path, o => o.ServePrecompressedFiles = true));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/app.js");
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "br;q=0.001");

        Assert.Equal("br", Assert.Single((await server.Client.SendAsync(request, Token)).Content.Headers.ContentEncoding));
    }
}

public class FileBrowserContainmentTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    static Task<TestServer> StartAsync(string root, Action<FileBrowserOptions>? configure = null)
        => TestServer.StartAsync(app => app.MapFileBrowser("/files", o =>
        {
            o.RootPath = root;
            o.AllowWrite = true;
            o.AllowDelete = true;
            configure?.Invoke(o);
        }));

    [Fact]
    public async Task Refuses_to_read_through_a_linked_directory_that_leaves_the_root()
    {
        using var dirs = new RootWithOutside();
        Links.Directory(dirs.InRoot("link"), dirs.Outside);

        await using var server = await StartAsync(dirs.Root);

        Assert.Equal(HttpStatusCode.BadRequest, (await server.Client.GetAsync("/files/link/secret.txt", Token)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await server.Client.GetAsync("/files/link", Token)).StatusCode);
    }

    /// <summary>
    /// The write path only checked links on a target that already existed, so a PUT under a linked
    /// directory created a file outside the root.
    /// </summary>
    [Theory]
    [InlineData("/files/link/written.txt")]
    [InlineData("/files/link/new-dir/written.txt")]
    [InlineData("/files/link/new-dir/")]
    public async Task Refuses_to_write_through_a_linked_directory_that_leaves_the_root(string url)
    {
        using var dirs = new RootWithOutside();
        Links.Directory(dirs.InRoot("link"), dirs.Outside);

        await using var server = await StartAsync(dirs.Root);

        var response = await server.Client.PutAsync(url, new StringContent("pwned"), Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(["secret.txt"], Directory.EnumerateFileSystemEntries(dirs.Outside).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Refuses_to_delete_through_a_linked_directory_that_leaves_the_root()
    {
        using var dirs = new RootWithOutside();
        Links.Directory(dirs.InRoot("link"), dirs.Outside);

        await using var server = await StartAsync(dirs.Root);

        Assert.Equal(HttpStatusCode.BadRequest, (await server.Client.DeleteAsync("/files/link/secret.txt", Token)).StatusCode);
        Assert.True(File.Exists(Path.Combine(dirs.Outside, "secret.txt")));
    }

    [Fact]
    public async Task Reads_and_writes_through_a_linked_directory_inside_the_root()
    {
        using var dirs = new RootWithOutside();
        Directory.CreateDirectory(dirs.InRoot("real"));
        File.WriteAllText(dirs.InRoot("real/a.txt"), "inside");
        Links.Directory(dirs.InRoot("alias"), dirs.InRoot("real"));

        await using var server = await StartAsync(dirs.Root);

        Assert.Equal("inside", await server.Client.GetStringAsync("/files/alias/a.txt", Token));

        var put = await server.Client.PutAsync("/files/alias/b.txt", new StringContent("written"), Token);

        Assert.Equal(HttpStatusCode.Created, put.StatusCode);
        Assert.Equal("written", File.ReadAllText(dirs.InRoot("real/b.txt")));
    }

    /// <summary>
    /// The filter saw the request's spelling; on a case-insensitive file system "PRIVATE" is the
    /// directory a filter on "private" was written to hide.
    /// </summary>
    [Theory]
    [InlineData("/files/PRIVATE/keys.txt")]
    [InlineData("/files/Private/keys.txt")]
    [InlineData("/files/private/KEYS.TXT")]
    public async Task Applies_the_filter_whatever_case_the_request_uses(string url)
    {
        using var root = new ContentRoot().With("private/keys.txt", "secret").With("public/a.txt", "fine");
        Assert.SkipUnless(Directory.Exists(Path.Combine(root.Path, "PRIVATE")), "This file system is case-sensitive.");

        await using var server = await StartAsync(root.Path, o => o.Filter = p => !p.StartsWith("private", StringComparison.Ordinal));

        Assert.Equal(HttpStatusCode.BadRequest, (await server.Client.GetAsync(url, Token)).StatusCode);
        Assert.Equal("fine", await server.Client.GetStringAsync("/files/PUBLIC/a.txt", Token));
    }

    /// <summary>A link inside the root to a filtered directory is no way around the filter.</summary>
    [Fact]
    public async Task Applies_the_filter_to_where_a_link_leads()
    {
        using var root = new ContentRoot().With("private/keys.txt", "secret");
        Links.Directory(Path.Combine(root.Path, "shortcut"), Path.Combine(root.Path, "private"));

        await using var server = await StartAsync(root.Path, o => o.Filter = p => !p.StartsWith("private", StringComparison.Ordinal));

        Assert.Equal(HttpStatusCode.BadRequest, (await server.Client.GetAsync("/files/shortcut/keys.txt", Token)).StatusCode);
    }

    /// <summary>
    /// The Hidden attribute kept a file out of listings, but a request that already knew its name
    /// could still fetch it.
    /// </summary>
    [Fact]
    public async Task Refuses_a_hidden_attribute_file_by_name()
    {
        using var root = new ContentRoot().With("private.txt", "hidden").With("hidden-dir/inner.txt", "hidden too");
        var file = Path.Combine(root.Path, "private.txt");
        var directory = Path.Combine(root.Path, "hidden-dir");

        File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.Hidden);
        File.SetAttributes(directory, File.GetAttributes(directory) | FileAttributes.Hidden);
        Assert.SkipUnless(File.GetAttributes(file).HasFlag(FileAttributes.Hidden), "This OS has no Hidden attribute beyond a leading dot.");

        await using var server = await StartAsync(root.Path);

        Assert.Equal(HttpStatusCode.BadRequest, (await server.Client.GetAsync("/files/private.txt", Token)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await server.Client.GetAsync("/files/hidden-dir/inner.txt", Token)).StatusCode);

        await using var permissive = await StartAsync(root.Path, o => o.ServeHiddenFiles = true);

        Assert.Equal("hidden", await permissive.Client.GetStringAsync("/files/private.txt", Token));
    }
}

public class DownloadHardeningTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void Reports_a_zero_length_suffix_as_unsatisfiable()
    {
        Assert.False(RangeHeader.TryParse("bytes=-0", 1000, out _, out var unsatisfiable));
        Assert.True(unsatisfiable);
    }

    [Theory]
    [InlineData("bytes=+3-4")]
    [InlineData("bytes=3-+4")]
    [InlineData("bytes=-+3")]
    public void Ignores_a_range_whose_bounds_carry_a_sign(string header)
    {
        // A range is digits only (RFC 9110 §14.1.1). Read leniently, a signed bound is a value one
        // cache or proxy honours and another ignores - the same mismatch as a signed Content-Length.
        Assert.False(RangeHeader.TryParse(header, 1000, out _, out var unsatisfiable));
        Assert.False(unsatisfiable);
    }

    [Fact]
    public async Task Answers_416_for_a_zero_length_suffix()
    {
        await using var server = await TestServer.StartAsync(app => app.MapGet("/f", ctx =>
            FileDownloadResult.FromBytes("0123456789"u8.ToArray(), "text/plain").ExecuteAsync(ctx)));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/f");
        request.Headers.TryAddWithoutValidation("Range", "bytes=-0");

        var response = await server.Client.SendAsync(request, Token);

        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, response.StatusCode);
        Assert.Equal("bytes */10", response.Content.Headers.ContentRange?.ToString());
    }

    /// <summary>
    /// A file measured and then appended to - a log being written - used to be copied to the end,
    /// sending more than the Content-Length it had already promised.
    /// </summary>
    [Fact]
    public async Task Sends_the_length_it_promised_when_the_file_grows()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("shiny-grow-").FullName, "grow.log");
        File.WriteAllText(path, new string('a', 100_000));

        try
        {
            await using var server = await TestServer.StartAsync(app => app.MapGet("/log", async ctx =>
            {
                var result = FileDownloadResult.FromFile(path, "text/plain");
                await File.AppendAllTextAsync(path, new string('b', 1000), ctx.RequestAborted);
                await result.ExecuteAsync(ctx);
            }));

            var response = await server.Client.GetAsync("/log", Token);
            var body = await response.Content.ReadAsStringAsync(Token);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(new string('a', 100_000), body);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }
}

public class MultipartHardeningTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    static Task<TestServer> StartAsync()
        => TestServer.StartAsync(app => app.MapPost("/form", async ctx =>
        {
            var names = new List<string>();

            await foreach (var part in ctx.Request.ReadMultipartAsync(ctx.RequestAborted))
                names.Add($"{part.Name}={await part.ReadAsStringAsync(cancellationToken: ctx.RequestAborted)}");

            await ctx.Response.WriteAsync(string.Join(";", names));
        }));

    static ByteArrayContent Body(string text)
    {
        var content = new ByteArrayContent(Encoding.ASCII.GetBytes(text));
        content.Headers.TryAddWithoutValidation("Content-Type", "multipart/form-data; boundary=XyZ");
        return content;
    }

    /// <summary>
    /// RFC 2046 allows a preamble before the first delimiter, and some clients send a leading CRLF.
    /// Either used to read as a form with no parts at all, and no error.
    /// </summary>
    [Theory]
    [InlineData("This is a preamble.\r\n--XyZ\r\nContent-Disposition: form-data; name=\"a\"\r\n\r\nhello\r\n--XyZ--\r\n")]
    [InlineData("\r\n--XyZ\r\nContent-Disposition: form-data; name=\"a\"\r\n\r\nhello\r\n--XyZ--\r\n")]
    [InlineData("--XyZ\r\nContent-Disposition: form-data; name=\"a\"\r\n\r\nhello\r\n--XyZ--\r\n")]
    public async Task Skips_a_preamble_before_the_first_delimiter(string body)
    {
        await using var server = await StartAsync();

        var response = await server.Client.PostAsync("/form", Body(body), Token);

        Assert.Equal("a=hello", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Skips_a_preamble_longer_than_one_read()
    {
        await using var server = await StartAsync();

        var preamble = new string('p', 200_000) + "\r\n";
        var response = await server.Client.PostAsync(
            "/form",
            Body(preamble + "--XyZ\r\nContent-Disposition: form-data; name=\"a\"\r\n\r\nhello\r\n--XyZ--\r\n"),
            Token
        );

        Assert.Equal("a=hello", await response.Content.ReadAsStringAsync(Token));
    }

    /// <summary>No delimiter anywhere is a malformed body, not an empty form.</summary>
    [Theory]
    [InlineData("no delimiter in here at all")]
    [InlineData("")]
    [InlineData("--XyZnot-at-a-line-start but no other")]
    public async Task Refuses_a_body_without_a_delimiter(string body)
    {
        await using var server = await StartAsync();

        var response = await server.Client.PostAsync("/form", Body(body), Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// The body reader's 4 KB default buffer meant a part was handed out 4 KB at a time however big
    /// the caller's buffer, which halved multipart throughput against the same bytes sent raw.
    /// </summary>
    [Fact]
    public async Task Hands_a_part_out_in_reads_larger_than_four_kilobytes()
    {
        var largest = 0;

        await using var server = await TestServer.StartAsync(app => app.MapPost("/sizes", async ctx =>
        {
            var buffer = new byte[1024 * 1024];

            await foreach (var part in ctx.Request.ReadMultipartAsync(ctx.RequestAborted))
            {
                int read;
                while ((read = await part.Body.ReadAsync(buffer, ctx.RequestAborted)) > 0)
                    largest = Math.Max(largest, read);
            }

            await ctx.Response.WriteAsync("ok");
        }));

        var payload = new byte[4 * 1024 * 1024];
        Random.Shared.NextBytes(payload);

        using var content = new MultipartFormDataContent { { new StreamContent(new MemoryStream(payload), 1024 * 1024), "f", "f.bin" } };

        var response = await server.Client.PostAsync("/sizes", content, Token);

        Assert.Equal("ok", await response.Content.ReadAsStringAsync(Token));
        Assert.True(largest > 8 * 1024, $"The largest read was {largest} bytes.");
    }
}

public class CompressionValidatorTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    static string Json => "[" + string.Join(",", Enumerable.Range(0, 5000)) + "]";

    static Task<TestServer> StartAsync(ContentRoot root)
        => TestServer.StartAsync(app =>
        {
            app.UseResponseCompression();
            app.UseStaticFiles(root.Path);
        });

    static HttpRequestMessage Get(string? acceptEncoding, string? ifNoneMatch = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/data.json");

        if (acceptEncoding is not null)
            request.Headers.TryAddWithoutValidation("Accept-Encoding", acceptEncoding);

        if (ifNoneMatch is not null)
            request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);

        return request;
    }

    /// <summary>
    /// The gzip bytes went out under the identity entity tag, and still advertised ranges over a
    /// representation that cannot be ranged.
    /// </summary>
    [Fact]
    public async Task Gives_a_compressed_response_its_own_etag_and_no_ranges()
    {
        using var root = new ContentRoot().With("data.json", Json);
        await using var server = await StartAsync(root);

        var identity = await server.Client.SendAsync(Get(null), Token);
        var gzip = await server.Client.SendAsync(Get("gzip"), Token);

        Assert.Equal("gzip", Assert.Single(gzip.Content.Headers.ContentEncoding));

        var identityTag = identity.Headers.ETag!.Tag;
        var gzipTag = gzip.Headers.ETag!.Tag;

        Assert.Equal(identityTag[..^1] + "-gzip\"", gzipTag);
        Assert.Empty(gzip.Headers.AcceptRanges);
        Assert.Equal("bytes", Assert.Single(identity.Headers.AcceptRanges));
    }

    /// <summary>A client revalidating the compressed copy gets its 304, under the tag it holds.</summary>
    [Fact]
    public async Task Answers_304_to_the_compressed_etag()
    {
        using var root = new ContentRoot().With("data.json", Json);
        await using var server = await StartAsync(root);

        var gzipTag = (await server.Client.SendAsync(Get("gzip"), Token)).Headers.ETag!.Tag;

        var revalidated = await server.Client.SendAsync(Get("gzip", gzipTag), Token);

        Assert.Equal(HttpStatusCode.NotModified, revalidated.StatusCode);
        Assert.Equal(gzipTag, revalidated.Headers.ETag!.Tag);

        // And the identity tag still validates the identity copy.
        var identityTag = (await server.Client.SendAsync(Get(null), Token)).Headers.ETag!.Tag;

        Assert.Equal(HttpStatusCode.NotModified, (await server.Client.SendAsync(Get(null, identityTag), Token)).StatusCode);
    }

    /// <summary>The compressed tag must not validate the identity copy, or a cache would mix them.</summary>
    [Fact]
    public async Task Does_not_validate_the_identity_copy_with_the_compressed_etag()
    {
        using var root = new ContentRoot().With("data.json", Json);
        await using var server = await StartAsync(root);

        var gzipTag = (await server.Client.SendAsync(Get("gzip"), Token)).Headers.ETag!.Tag;

        Assert.Equal(HttpStatusCode.OK, (await server.Client.SendAsync(Get(null, gzipTag), Token)).StatusCode);
    }
}

public class ZipFileSourceRangeTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    static string BuildZip(string directory, CompressionLevel level, byte[] content)
    {
        var path = Path.Combine(directory, "site.zip");

        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using var entry = archive.CreateEntry("media/clip.bin", level).Open();
            entry.Write(content);
        }

        return path;
    }

    /// <summary>
    /// Stored entries used to be served through a non-seekable stream, so a range near the end of a
    /// large uncompressed video read and discarded everything before it.
    /// </summary>
    [Fact]
    public async Task Seeks_straight_to_a_range_in_a_stored_entry()
    {
        var directory = Directory.CreateTempSubdirectory("shiny-zip-range-").FullName;

        try
        {
            var content = new byte[256 * 1024];
            Random.Shared.NextBytes(content);

            var source = new ZipFileSource(BuildZip(directory, CompressionLevel.NoCompression, content));

            Assert.True(source.TryGetFile("media/clip.bin", out var file));

            await using (var stream = await file.Open(Token))
                Assert.True(stream.CanSeek);

            await using var server = await TestServer.StartAsync(app => app.UseStaticFiles(source, o => o.ServeUnknownFileTypes = true));

            using var request = new HttpRequestMessage(HttpMethod.Get, "/media/clip.bin");
            request.Headers.Range = new RangeHeaderValue(200_000, 200_099);

            var response = await server.Client.SendAsync(request, Token);

            Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
            Assert.Equal(content.AsSpan(200_000, 100).ToArray(), await response.Content.ReadAsByteArrayAsync(Token));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Each response used to open a fresh ZipArchive, re-reading the central directory. With the
    /// end-of-central-directory record wiped after the index was built, a response can only succeed
    /// if it does not.
    /// </summary>
    [Theory]
    [InlineData(CompressionLevel.NoCompression)]
    [InlineData(CompressionLevel.Optimal)]
    public async Task Serves_entries_without_rereading_the_central_directory(CompressionLevel level)
    {
        var directory = Directory.CreateTempSubdirectory("shiny-zip-index-").FullName;

        try
        {
            var content = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("0123456789", 5000)));
            var path = BuildZip(directory, level, content);
            var source = new ZipFileSource(path);

            var bytes = File.ReadAllBytes(path);
            var end = bytes.AsSpan().LastIndexOf([(byte)0x50, (byte)0x4b, (byte)0x05, (byte)0x06]);
            bytes.AsSpan(end, 4).Clear();
            File.WriteAllBytes(path, bytes);

            await using var server = await TestServer.StartAsync(app => app.UseStaticFiles(source, o => o.ServeUnknownFileTypes = true));

            Assert.Equal(content, await server.Client.GetByteArrayAsync("/media/clip.bin", Token));

            using var request = new HttpRequestMessage(HttpMethod.Get, "/media/clip.bin");
            request.Headers.Range = new RangeHeaderValue(49_990, 49_999);

            var ranged = await server.Client.SendAsync(request, Token);

            Assert.Equal(HttpStatusCode.PartialContent, ranged.StatusCode);
            Assert.Equal("0123456789", await ranged.Content.ReadAsStringAsync(Token));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
