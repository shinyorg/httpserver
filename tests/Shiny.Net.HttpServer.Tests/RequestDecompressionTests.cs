using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Shiny.Net.HttpServer.Compression;

namespace Shiny.Net.HttpServer.Tests;

public class RequestDecompressionTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    static HttpContent Compressed(string body, string encoding)
    {
        var buffer = new MemoryStream();
        using (var compressor = Wrap(buffer, encoding))
            compressor.Write(Encoding.UTF8.GetBytes(body));

        var content = new ByteArrayContent(buffer.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        content.Headers.ContentEncoding.Add(encoding);

        return content;
    }

    static Stream Wrap(Stream output, string encoding) => encoding switch
    {
        "br" => new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true),
        "gzip" => new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true),
        "deflate" => new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true),
        _ => throw new ArgumentOutOfRangeException(nameof(encoding))
    };

    static Task<TestServer> EchoServer(Action<RequestDecompressionOptions>? configure = null)
        => TestServer.StartAsync(server =>
        {
            server.UseRequestDecompression(configure);
            server.MapPost("/echo", async ctx =>
            {
                using var reader = new StreamReader(ctx.Request.Body);
                var body = await reader.ReadToEndAsync(ctx.RequestAborted);

                await ctx.Response.WriteTextAsync(body, cancellationToken: ctx.RequestAborted);
            });
        });

    [Theory]
    [InlineData("br")]
    [InlineData("gzip")]
    [InlineData("deflate")]
    public async Task Decompresses_every_coding_it_offers(string encoding)
    {
        await using var test = await EchoServer();

        var response = await test.Client.PostAsync("/echo", Compressed("hello from a phone", encoding), Token);

        Assert.Equal("hello from a phone", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Leaves_an_uncompressed_body_alone()
    {
        await using var test = await EchoServer();

        var response = await test.Client.PostAsync("/echo", new StringContent("plain"), Token);

        Assert.Equal("plain", await response.Content.ReadAsStringAsync(Token));
    }

    /// <summary>The binder decides there is a body from Content-Length, which decompression has just invalidated.</summary>
    [Fact]
    public async Task A_decompressed_body_still_counts_as_a_body()
    {
        await using var test = await TestServer.StartAsync(server =>
        {
            server.UseRequestDecompression();
            server.MapPost("/probe", ctx => ctx.Response.WriteTextAsync(
                ctx.Request.HasBody ? "body" : "empty",
                cancellationToken: ctx.RequestAborted
            ));
        });

        var response = await test.Client.PostAsync("/probe", Compressed("something", "gzip"), Token);

        Assert.Equal("body", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task An_unsupported_coding_is_refused_with_415()
    {
        await using var test = await EchoServer();

        var content = new ByteArrayContent([1, 2, 3]);
        content.Headers.ContentEncoding.Add("exotic");

        var response = await test.Client.PostAsync("/echo", content, Token);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Contains("gzip", response.Headers.GetValues("Accept-Encoding").Single());
    }

    [Fact]
    public async Task An_unsupported_coding_can_be_passed_through_instead()
    {
        await using var test = await EchoServer(o => o.RejectUnsupportedEncodings = false);

        var content = new StringContent("as sent");
        content.Headers.ContentEncoding.Add("exotic");

        var response = await test.Client.PostAsync("/echo", content, Token);

        Assert.Equal("as sent", await response.Content.ReadAsStringAsync(Token));
    }

    /// <summary>A small upload that expands without bound is the whole reason this has a limit.</summary>
    [Fact]
    public async Task A_body_that_expands_past_the_limit_is_refused_with_413()
    {
        await using var test = await EchoServer(o => o.MaxDecompressedBytes = 1024);

        var response = await test.Client.PostAsync("/echo", Compressed(new string('a', 100_000), "gzip"), Token);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task A_stack_of_codings_is_refused_rather_than_half_handled()
    {
        await using var test = await EchoServer();

        var content = new ByteArrayContent([1, 2, 3]);
        content.Headers.TryAddWithoutValidation("Content-Encoding", "gzip, br");

        var response = await test.Client.PostAsync("/echo", content, Token);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }
}
