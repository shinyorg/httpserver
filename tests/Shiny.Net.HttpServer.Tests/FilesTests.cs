using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Shiny.Net.HttpServer.Files;

namespace Shiny.Net.HttpServer.Tests;

public class ContentDispositionTests
{
    [Fact]
    public void Parses_a_form_field()
    {
        var parsed = ContentDisposition.Parse("form-data; name=\"comment\"");

        Assert.Equal("comment", parsed.Name);
        Assert.Null(parsed.FileName);
    }

    [Fact]
    public void Parses_a_file_part()
    {
        var parsed = ContentDisposition.Parse("form-data; name=\"upload\"; filename=\"report.pdf\"");

        Assert.Equal("upload", parsed.Name);
        Assert.Equal("report.pdf", parsed.FileName);
    }

    [Fact]
    public void Prefers_the_extended_file_name()
    {
        var parsed = ContentDisposition.Parse(
            "form-data; name=\"f\"; filename=\"naive.txt\"; filename*=UTF-8''na%C3%AFve.txt"
        );

        Assert.Equal("naïve.txt", parsed.FileName);
    }

    [Fact]
    public void Ignores_semicolons_inside_a_quoted_value()
    {
        var parsed = ContentDisposition.Parse("form-data; name=\"f\"; filename=\"a;b.txt\"");
        Assert.Equal("a;b.txt", parsed.FileName);
    }

    [Theory]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("..\\..\\windows\\system32\\cmd.exe", "cmd.exe")]
    [InlineData("/absolute/path.txt", "path.txt")]
    [InlineData("plain.txt", "plain.txt")]
    public void Strips_directories_from_the_file_name(string sent, string expected)
    {
        // A client-supplied filename is untrusted input; joining it onto a directory unmodified is
        // the classic path-traversal hole.
        var parsed = ContentDisposition.Parse($"form-data; name=\"f\"; filename=\"{sent}\"");
        Assert.Equal(expected, parsed.SafeFileName);
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("dir/")]
    public void Refuses_a_file_name_that_is_not_one(string sent)
    {
        var parsed = ContentDisposition.Parse($"form-data; name=\"f\"; filename=\"{sent}\"");
        Assert.Null(parsed.SafeFileName);
    }

    [Fact]
    public void Builds_a_download_header_with_both_forms()
    {
        var header = ContentDisposition.ForDownload("naïve report.pdf");

        Assert.Contains("attachment;", header);
        Assert.Contains("filename=\"na_ve report.pdf\"", header);
        Assert.Contains("filename*=UTF-8''na%C3%AFve%20report.pdf", header);
    }
}

public class RangeHeaderTests
{
    [Theory]
    [InlineData("bytes=0-99", 1000, 0, 99)]
    [InlineData("bytes=100-", 1000, 100, 999)]
    [InlineData("bytes=-100", 1000, 900, 999)]
    [InlineData("bytes=0-", 1000, 0, 999)]
    [InlineData("bytes=500-99999", 1000, 500, 999)]
    [InlineData("bytes=-99999", 1000, 0, 999)]
    public void Parses_a_single_range(string header, long length, long from, long to)
    {
        Assert.True(RangeHeader.TryParse(header, length, out var range, out _));
        Assert.Equal(from, range.From);
        Assert.Equal(to, range.To);
        Assert.Equal(to - from + 1, range.Length);
    }

    [Fact]
    public void Reports_a_range_entirely_past_the_end_as_unsatisfiable()
    {
        Assert.False(RangeHeader.TryParse("bytes=2000-3000", 1000, out _, out var unsatisfiable));
        Assert.True(unsatisfiable);
    }

    [Theory]
    [InlineData("bytes=0-99, 200-299")]
    [InlineData("items=0-99")]
    [InlineData("bytes=abc-def")]
    [InlineData("bytes=99-0")]
    [InlineData("")]
    [InlineData(null)]
    public void Declines_what_it_cannot_serve_faithfully(string? header)
    {
        // Declining means a 200 with the whole entity, which is always a legal answer.
        Assert.False(RangeHeader.TryParse(header, 1000, out _, out var unsatisfiable));
        Assert.False(unsatisfiable);
    }
}

public class DownloadTests : IDisposable
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    readonly string directory = Directory.CreateTempSubdirectory("shiny-download-tests").FullName;

    public void Dispose() => Directory.Delete(this.directory, recursive: true);

    string WriteFile(string name, string content)
    {
        var path = Path.Combine(this.directory, name);
        File.WriteAllText(path, content);

        return path;
    }

    [Fact]
    public async Task Serves_a_whole_file()
    {
        var path = this.WriteFile("hello.txt", "hello world");

        await using var server = await TestServer.StartAsync(
            app => app.MapGet("/file", _ => FileDownloadResult.FromFile(path))
        );

        var response = await server.Client.GetAsync("/file", Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("hello world", await response.Content.ReadAsStringAsync(Token));
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("bytes", response.Headers.AcceptRanges.Single());
        Assert.NotNull(response.Headers.ETag);
    }

    [Fact]
    public async Task Serves_a_byte_range()
    {
        var path = this.WriteFile("range.txt", "0123456789");

        await using var server = await TestServer.StartAsync(
            app => app.MapGet("/file", _ => FileDownloadResult.FromFile(path))
        );

        var request = new HttpRequestMessage(HttpMethod.Get, "/file");
        request.Headers.Range = new RangeHeaderValue(2, 5);

        var response = await server.Client.SendAsync(request, Token);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("2345", await response.Content.ReadAsStringAsync(Token));
        Assert.Equal("bytes 2-5/10", response.Content.Headers.ContentRange?.ToString());
        Assert.Equal(4, response.Content.Headers.ContentLength);
    }

    [Fact]
    public async Task Serves_a_suffix_range()
    {
        var path = this.WriteFile("suffix.txt", "0123456789");

        await using var server = await TestServer.StartAsync(
            app => app.MapGet("/file", _ => FileDownloadResult.FromFile(path))
        );

        var request = new HttpRequestMessage(HttpMethod.Get, "/file");
        request.Headers.Range = new RangeHeaderValue(null, 3);

        var response = await server.Client.SendAsync(request, Token);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("789", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Answers_416_for_a_range_past_the_end()
    {
        var path = this.WriteFile("small.txt", "0123456789");

        await using var server = await TestServer.StartAsync(
            app => app.MapGet("/file", _ => FileDownloadResult.FromFile(path))
        );

        var request = new HttpRequestMessage(HttpMethod.Get, "/file");
        request.Headers.Range = new RangeHeaderValue(5000, 6000);

        var response = await server.Client.SendAsync(request, Token);

        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, response.StatusCode);
        Assert.Equal("bytes */10", response.Content.Headers.ContentRange?.ToString());
    }

    [Fact]
    public async Task Answers_304_when_the_etag_still_matches()
    {
        var path = this.WriteFile("cached.txt", "cache me");

        await using var server = await TestServer.StartAsync(
            app => app.MapGet("/file", _ => FileDownloadResult.FromFile(path))
        );

        var first = await server.Client.GetAsync("/file", Token);
        var etag = first.Headers.ETag!;

        var request = new HttpRequestMessage(HttpMethod.Get, "/file");
        request.Headers.IfNoneMatch.Add(etag);

        var second = await server.Client.SendAsync(request, Token);

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        Assert.Empty(await second.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Sends_the_file_again_when_the_etag_does_not_match()
    {
        var path = this.WriteFile("changed.txt", "content");

        await using var server = await TestServer.StartAsync(
            app => app.MapGet("/file", _ => FileDownloadResult.FromFile(path))
        );

        var request = new HttpRequestMessage(HttpMethod.Get, "/file");
        request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue("\"something-else\""));

        var response = await server.Client.SendAsync(request, Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("content", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Ignores_a_range_when_If_Range_no_longer_matches()
    {
        var path = this.WriteFile("moved.txt", "0123456789");

        await using var server = await TestServer.StartAsync(
            app => app.MapGet("/file", _ => FileDownloadResult.FromFile(path))
        );

        var request = new HttpRequestMessage(HttpMethod.Get, "/file");
        request.Headers.Range = new RangeHeaderValue(2, 5);
        request.Headers.IfRange = new RangeConditionHeaderValue(new EntityTagHeaderValue("\"stale\""));

        // The entity changed under the client, so resuming would splice two different files.
        var response = await server.Client.SendAsync(request, Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("0123456789", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Sets_a_content_disposition_for_a_named_download()
    {
        var path = this.WriteFile("report.pdf", "%PDF-fake");

        await using var server = await TestServer.StartAsync(
            app => app.MapGet("/file", _ => FileDownloadResult.FromFile(path, downloadName: "monthly report.pdf"))
        );

        var response = await server.Client.GetAsync("/file", Token);
        var disposition = response.Content.Headers.GetValues(HeaderNames.ContentDisposition).Single();

        Assert.StartsWith("attachment;", disposition);
        Assert.Contains("monthly report.pdf", disposition);
    }

    [Fact]
    public async Task Serves_bytes_from_memory_with_ranges()
    {
        var content = Encoding.ASCII.GetBytes("abcdefghij");

        await using var server = await TestServer.StartAsync(
            app => app.MapGet("/bytes", _ => FileDownloadResult.FromBytes(content, "text/plain"))
        );

        var request = new HttpRequestMessage(HttpMethod.Get, "/bytes");
        request.Headers.Range = new RangeHeaderValue(3, 4);

        var response = await server.Client.SendAsync(request, Token);
        Assert.Equal("de", await response.Content.ReadAsStringAsync(Token));
    }

    [Theory]
    [InlineData("a.html", "text/html")]
    [InlineData("a.js", "text/javascript")]
    [InlineData("a.png", "image/png")]
    [InlineData("a.unknown", "application/octet-stream")]
    [InlineData("noextension", "application/octet-stream")]
    public void Maps_extensions_to_content_types(string name, string expected)
        => Assert.StartsWith(expected, ContentTypes.ForFileName(name));
}

public class UploadTests : IDisposable
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    readonly string directory = Directory.CreateTempSubdirectory("shiny-upload-tests").FullName;

    public void Dispose() => Directory.Delete(this.directory, recursive: true);

    [Fact]
    public void Reads_the_boundary_out_of_a_content_type()
    {
        Assert.Equal("abc123", MultipartReader.GetBoundary("multipart/form-data; boundary=abc123"));
        Assert.Equal("a b", MultipartReader.GetBoundary("multipart/form-data; boundary=\"a b\""));
        Assert.Null(MultipartReader.GetBoundary("application/json"));
        Assert.Null(MultipartReader.GetBoundary("multipart/form-data; boundary=" + new string('x', 71)));
    }

    [Fact]
    public async Task Reads_form_values_and_files()
    {
        await using var server = await TestServer.StartAsync(app => app.MapPost("/upload", async ctx =>
        {
            var form = await ctx.Request.ReadFormAsync(cancellationToken: ctx.RequestAborted);
            var file = form.GetFile("document")!;

            await ctx.Response.WriteAsync(
                $"{form.GetFirst("title")}|{file.FileName}|{file.Length}|{Encoding.UTF8.GetString(file.Content)}"
            );
        }));

        using var content = new MultipartFormDataContent("test-boundary")
        {
            { new StringContent("Quarterly"), "title" },
            { new ByteArrayContent("file body"u8.ToArray()), "document", "report.txt" }
        };

        var response = await server.Client.PostAsync("/upload", content, Token);

        Assert.Equal("Quarterly|report.txt|9|file body", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Streams_a_large_upload_straight_to_disk()
    {
        var target = Path.Combine(this.directory, "large.bin");

        await using var server = await TestServer.StartAsync(app => app.MapPost("/stream", async ctx =>
        {
            await foreach (var section in ctx.Request.ReadMultipartAsync(ctx.RequestAborted))
            {
                if (!section.IsFile)
                    continue;

                await section.SaveToAsync(Path.Combine(this.directory, section.SafeFileName()!), ctx.RequestAborted);
            }

            await ctx.Response.WriteAsync("saved");
        }));

        // Comfortably larger than any single pipe read, so this exercises a boundary search that
        // spans many buffers.
        var payload = new byte[512 * 1024];
        Random.Shared.NextBytes(payload);

        using var content = new MultipartFormDataContent
        {
            { new ByteArrayContent(payload), "file", "large.bin" }
        };

        var response = await server.Client.PostAsync("/stream", content, Token);

        Assert.Equal("saved", await response.Content.ReadAsStringAsync(Token));
        Assert.Equal(payload, await File.ReadAllBytesAsync(target, Token));
    }

    [Fact]
    public async Task Handles_a_payload_that_contains_the_boundary_prefix()
    {
        // The bytes "\r\n--" appear inside the body itself, which is exactly the case a naive
        // scan-and-split gets wrong.
        var payload = Encoding.ASCII.GetBytes("before\r\n--not-the-boundary\r\nafter");

        await using var server = await TestServer.StartAsync(app => app.MapPost("/tricky", async ctx =>
        {
            var form = await ctx.Request.ReadFormAsync(cancellationToken: ctx.RequestAborted);
            await ctx.Response.WriteAsync(form.GetFile("f")!.Length.ToString());
        }));

        using var content = new MultipartFormDataContent
        {
            { new ByteArrayContent(payload), "f", "tricky.bin" }
        };

        var response = await server.Client.PostAsync("/tricky", content, Token);
        Assert.Equal(payload.Length.ToString(), await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Reads_several_files_in_one_request()
    {
        await using var server = await TestServer.StartAsync(app => app.MapPost("/many", async ctx =>
        {
            var form = await ctx.Request.ReadFormAsync(cancellationToken: ctx.RequestAborted);
            await ctx.Response.WriteAsync(string.Join(",", form.Files.Select(f => $"{f.Name}:{f.FileName}")));
        }));

        using var content = new MultipartFormDataContent
        {
            { new ByteArrayContent("one"u8.ToArray()), "a", "one.txt" },
            { new ByteArrayContent("two"u8.ToArray()), "b", "two.txt" }
        };

        var response = await server.Client.PostAsync("/many", content, Token);
        Assert.Equal("a:one.txt,b:two.txt", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Skipping_a_section_still_advances_to_the_next()
    {
        await using var server = await TestServer.StartAsync(app => app.MapPost("/skip", async ctx =>
        {
            var names = new List<string>();

            await foreach (var section in ctx.Request.ReadMultipartAsync(ctx.RequestAborted))
                names.Add(section.Name ?? "?");   // deliberately never reads the body

            await ctx.Response.WriteAsync(string.Join(",", names));
        }));

        using var content = new MultipartFormDataContent
        {
            { new ByteArrayContent(new byte[100_000]), "big", "big.bin" },
            { new StringContent("small"), "tail" }
        };

        var response = await server.Client.PostAsync("/skip", content, Token);
        Assert.Equal("big,tail", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Reads_a_url_encoded_form()
    {
        await using var server = await TestServer.StartAsync(app => app.MapPost("/form", async ctx =>
        {
            var form = await ctx.Request.ReadFormAsync(cancellationToken: ctx.RequestAborted);
            await ctx.Response.WriteAsync($"{form.GetFirst("name")}|{form.GetFirst("note")}");
        }));

        using var content = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("name", "Ada Lovelace"),
            new KeyValuePair<string, string>("note", "a+b c")
        ]);

        var response = await server.Client.PostAsync("/form", content, Token);
        Assert.Equal("Ada Lovelace|a+b c", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Refuses_a_file_over_the_buffering_limit()
    {
        await using var server = await TestServer.StartAsync(app => app.MapPost("/limited", async ctx =>
        {
            var form = await ctx.Request.ReadFormAsync(maxFileSize: 1024, cancellationToken: ctx.RequestAborted);
            await ctx.Response.WriteAsync($"{form.Files.Count}");
        }));

        using var content = new MultipartFormDataContent
        {
            { new ByteArrayContent(new byte[8192]), "f", "too-big.bin" }
        };

        var response = await server.Client.PostAsync("/limited", content, Token);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Streams_a_raw_put_body_to_disk()
    {
        var target = Path.Combine(this.directory, "put.bin");

        await using var server = await TestServer.StartAsync(app => app.MapPut("/raw", async ctx =>
        {
            var written = await ctx.Request.SaveBodyToAsync(target, ctx.RequestAborted);
            await ctx.Response.WriteAsync(written.ToString());
        }));

        var payload = new byte[64 * 1024];
        Random.Shared.NextBytes(payload);

        var response = await server.Client.PutAsync("/raw", new ByteArrayContent(payload), Token);

        Assert.Equal(payload.Length.ToString(), await response.Content.ReadAsStringAsync(Token));
        Assert.Equal(payload, await File.ReadAllBytesAsync(target, Token));
    }

    [Fact]
    public async Task Reports_whether_the_body_is_a_form()
    {
        await using var server = await TestServer.StartAsync(app => app.MapPost("/is-form", ctx =>
            ctx.Response.WriteAsync(ctx.Request.HasFormContentType().ToString())));

        var form = await server.Client.PostAsync("/is-form", new FormUrlEncodedContent([]), Token);
        Assert.Equal("True", await form.Content.ReadAsStringAsync(Token));

        var json = await server.Client.PostAsync("/is-form", new StringContent("{}", Encoding.UTF8, "application/json"), Token);
        Assert.Equal("False", await json.Content.ReadAsStringAsync(Token));
    }
}
