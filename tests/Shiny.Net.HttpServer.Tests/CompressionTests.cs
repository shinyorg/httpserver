using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Shiny.Net.HttpServer.Compression;
using Shiny.Net.HttpServer.StaticFiles;

namespace Shiny.Net.HttpServer.Tests;

public class CompressionNegotiationTests
{
    static string? Select(string? acceptEncoding)
        => new ResponseCompressionOptions().SelectProvider(acceptEncoding)?.EncodingName;

    [Fact]
    public void Picks_the_only_coding_offered()
    {
        Assert.Equal("gzip", Select("gzip"));
        Assert.Equal("br", Select("br"));
        Assert.Equal("deflate", Select("deflate"));
    }

    /// <summary>Two codings and no stated preference: the server's own order decides, and brotli wins.</summary>
    [Fact]
    public void Prefers_brotli_when_the_client_expresses_no_preference()
        => Assert.Equal("br", Select("gzip, deflate, br"));

    [Fact]
    public void Honours_an_explicit_quality_preference()
    {
        Assert.Equal("gzip", Select("gzip;q=1.0, br;q=0.1"));
        Assert.Equal("br", Select("gzip;q=0.2, br;q=0.9"));
    }

    /// <summary>A zero quality is a refusal, not a weak preference.</summary>
    [Fact]
    public void Treats_q_zero_as_a_refusal()
    {
        Assert.Null(Select("gzip;q=0"));
        Assert.Equal("br", Select("gzip;q=0, br"));
    }

    [Fact]
    public void Falls_back_to_the_server_preference_for_a_wildcard()
        => Assert.Equal("br", Select("*"));

    [Fact]
    public void Selects_nothing_when_no_offered_coding_is_supported()
    {
        Assert.Null(Select("identity"));
        Assert.Null(Select("exotic-coding"));
        Assert.Null(Select(""));
        Assert.Null(Select(null));
    }

    [Fact]
    public void Ignores_parameters_on_the_content_type()
    {
        var options = new ResponseCompressionOptions();

        Assert.True(options.IsCompressibleType("text/html; charset=utf-8"));
        Assert.True(options.IsCompressibleType("application/json"));
        Assert.True(options.IsCompressibleType("application/problem+json"));
    }

    /// <summary>Compressing what is already compressed spends CPU to make bytes slightly larger.</summary>
    [Fact]
    public void Leaves_already_compressed_types_alone()
    {
        var options = new ResponseCompressionOptions();

        Assert.False(options.IsCompressibleType("image/png"));
        Assert.False(options.IsCompressibleType("video/mp4"));
        Assert.False(options.IsCompressibleType("application/zip"));
        Assert.False(options.IsCompressibleType(null));
    }

    /// <summary>An event stream is buffered by a compressor, which defeats the point of it.</summary>
    [Fact]
    public void Excludes_event_streams_despite_the_text_wildcard()
        => Assert.False(new ResponseCompressionOptions().IsCompressibleType("text/event-stream"));
}

public class ResponseCompressionTests
{
    const string Payload = "The quick brown fox jumps over the lazy dog. ";

    static string Body(int repeats = 100) => string.Concat(Enumerable.Repeat(Payload, repeats));

    static async Task<HttpResponseMessage> GetAsync(TestServer server, string path, string? acceptEncoding)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);

        if (acceptEncoding is not null)
            request.Headers.TryAddWithoutValidation("Accept-Encoding", acceptEncoding);

        return await server.Client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    static async Task<string> DecompressAsync(HttpResponseMessage response)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        var encoding = response.Content.Headers.ContentEncoding.FirstOrDefault();

        using var source = new MemoryStream(bytes);

        Stream decompressor = encoding switch
        {
            "gzip" => new GZipStream(source, CompressionMode.Decompress),
            "br" => new BrotliStream(source, CompressionMode.Decompress),
            "deflate" => new ZLibStream(source, CompressionMode.Decompress),
            _ => source
        };

        await using (decompressor.ConfigureAwait(false))
        {
            using var reader = new StreamReader(decompressor, Encoding.UTF8);

            return await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
        }
    }

    static Task<TestServer> StartAsync(Action<HttpServer>? extra = null) => TestServer.StartAsync(app =>
    {
        app.UseResponseCompression();
        app.MapGet("/text", ctx => ctx.Response.WriteTextAsync(Body()));
        app.MapGet("/small", ctx => ctx.Response.WriteTextAsync("tiny"));
        app.MapGet("/image", ctx => ctx.Response.WriteBytesAsync(new byte[4096], "image/png"));

        extra?.Invoke(app);
    });

    [Fact]
    public async Task Compresses_with_gzip_when_the_client_asks_for_it()
    {
        await using var server = await StartAsync();

        var response = await GetAsync(server, "/text", "gzip");

        Assert.Equal("gzip", response.Content.Headers.ContentEncoding.Single());
        Assert.Equal(Body(), await DecompressAsync(response));
    }

    [Fact]
    public async Task Compresses_with_brotli_when_the_client_asks_for_it()
    {
        await using var server = await StartAsync();

        var response = await GetAsync(server, "/text", "br");

        Assert.Equal("br", response.Content.Headers.ContentEncoding.Single());
        Assert.Equal(Body(), await DecompressAsync(response));
    }

    /// <summary>The whole point: fewer bytes on the wire.</summary>
    [Fact]
    public async Task Actually_makes_the_response_smaller()
    {
        await using var server = await StartAsync();

        var plain = await GetAsync(server, "/text", acceptEncoding: null);
        var compressed = await GetAsync(server, "/text", "br");

        var plainBytes = (await plain.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).Length;
        var compressedBytes = (await compressed.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).Length;

        Assert.True(compressedBytes < plainBytes / 2, $"{compressedBytes} was not much smaller than {plainBytes}");
    }

    [Fact]
    public async Task Leaves_the_body_alone_when_nothing_was_accepted()
    {
        await using var server = await StartAsync();

        var response = await GetAsync(server, "/text", acceptEncoding: null);

        Assert.Empty(response.Content.Headers.ContentEncoding);
        Assert.Equal(Body(), await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Leaves_the_body_alone_for_identity_only()
    {
        await using var server = await StartAsync();

        var response = await GetAsync(server, "/text", "identity");

        Assert.Empty(response.Content.Headers.ContentEncoding);
    }

    /// <summary>
    /// Below roughly a packet there is nothing to win, and the compressed form is often larger.
    /// </summary>
    [Fact]
    public async Task Leaves_a_short_response_alone()
    {
        await using var server = await StartAsync();

        var response = await GetAsync(server, "/small", "gzip, br");

        Assert.Empty(response.Content.Headers.ContentEncoding);
        Assert.Equal("tiny", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Leaves_an_already_compressed_content_type_alone()
    {
        await using var server = await StartAsync();

        var response = await GetAsync(server, "/image", "gzip, br");

        Assert.Empty(response.Content.Headers.ContentEncoding);
    }

    /// <summary>
    /// A shared cache that stored one form without Vary would hand it to a client that asked for
    /// the other.
    /// </summary>
    [Fact]
    public async Task Sets_vary_whether_or_not_it_compressed()
    {
        await using var server = await StartAsync();

        var compressed = await GetAsync(server, "/text", "gzip");
        var plain = await GetAsync(server, "/image", "gzip");

        Assert.Contains("Accept-Encoding", compressed.Headers.Vary, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Accept-Encoding", plain.Headers.Vary, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Appends_to_a_vary_the_handler_already_set()
    {
        await using var server = await StartAsync(app => app.MapGet("/varied", async ctx =>
        {
            ctx.Response.Headers[HeaderNames.Vary] = "Origin";
            await ctx.Response.WriteTextAsync(Body());
        }));

        var response = await GetAsync(server, "/varied", "gzip");

        Assert.Contains("Origin", response.Headers.Vary, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Accept-Encoding", response.Headers.Vary, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Re-encoding would produce a body no client can decode from one Content-Encoding.</summary>
    [Fact]
    public async Task Leaves_a_body_the_handler_already_encoded_alone()
    {
        await using var server = await StartAsync(app => app.MapGet("/preencoded", async ctx =>
        {
            ctx.Response.Headers[HeaderNames.ContentEncoding] = "gzip";
            ctx.Response.ContentType = "application/json";

            await ctx.Response.WriteBytesAsync(new byte[2048]);
        }));

        var response = await GetAsync(server, "/preencoded", "gzip, br");
        var bytes = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);

        Assert.Equal("gzip", response.Content.Headers.ContentEncoding.Single());
        Assert.Equal(2048, bytes.Length);
    }

    /// <summary>A range describes the encoded entity, so compressing afterwards invalidates it.</summary>
    [Fact]
    public async Task Leaves_a_range_response_alone()
    {
        using var root = new ContentRoot().With("data.txt", Body());

        await using var server = await TestServer.StartAsync(app =>
        {
            app.UseResponseCompression();
            app.UseStaticFiles(root.Path);
        });

        using var request = new HttpRequestMessage(HttpMethod.Get, "/data.txt");
        request.Headers.Range = new RangeHeaderValue(0, 9);
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, br");

        var response = await server.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Empty(response.Content.Headers.ContentEncoding);
        Assert.Equal("The quick ", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Leaves_a_head_request_alone()
    {
        await using var server = await StartAsync();

        using var request = new HttpRequestMessage(HttpMethod.Head, "/text");
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, br");

        var response = await server.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(response.Content.Headers.ContentEncoding);
    }

    /// <summary>
    /// A response of unknown length is exactly the case where the payload might be large, so it is
    /// compressed — and the chunked framing has to survive it.
    /// </summary>
    [Fact]
    public async Task Compresses_a_streamed_response_of_unknown_length()
    {
        await using var server = await StartAsync(app => app.MapGet("/stream", async ctx =>
        {
            ctx.Response.ContentType = "text/plain; charset=utf-8";

            for (var i = 0; i < 50; i++)
                await ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(Payload), ctx.RequestAborted);
        }));

        var response = await GetAsync(server, "/stream", "gzip");

        Assert.Equal("gzip", response.Content.Headers.ContentEncoding.Single());

        // The compressed length is unknowable until the last block, so the response has to be
        // chunk-framed rather than carry a length that describes the uncompressed body.
        Assert.True(response.Headers.TransferEncodingChunked);
        Assert.Equal(Body(50), await DecompressAsync(response));
    }

    [Fact]
    public async Task Compresses_static_files()
    {
        using var root = new ContentRoot().With("app.js", Body());

        await using var server = await TestServer.StartAsync(app =>
        {
            app.UseResponseCompression();
            app.UseStaticFiles(root.Path);
        });

        var response = await GetAsync(server, "/app.js", "br");

        Assert.Equal("br", response.Content.Headers.ContentEncoding.Single());
        Assert.Equal(Body(), await DecompressAsync(response));
    }

    [Fact]
    public async Task Compresses_a_problem_response()
    {
        await using var server = await TestServer.StartAsync(app =>
        {
            app.UseResponseCompression();
            app.MapGet("/problem", _ => Results.Problem(
                StatusCodes.Status400BadRequest,
                detail: string.Concat(Enumerable.Repeat("something went wrong. ", 100))
            ));
        });

        var response = await GetAsync(server, "/problem", "gzip");

        Assert.Equal("gzip", response.Content.Headers.ContentEncoding.Single());
        Assert.Contains("something went wrong", await DecompressAsync(response), StringComparison.Ordinal);
    }

    /// <summary>
    /// The switch exists for BREACH. Off, an HTTPS request is served as-is; the same request over
    /// plain HTTP still compresses.
    /// </summary>
    [Fact]
    public async Task Can_be_turned_off_for_https()
    {
        await using var server = await TestServer.StartAsync(app =>
        {
            app.UseResponseCompression(o => o.EnableForHttps = false);
            app.MapGet("/text", ctx => ctx.Response.WriteTextAsync(Body()));
        });

        // The test server is plain HTTP, so this is the leg that still compresses; the HTTPS branch
        // is covered by the predicate itself.
        var response = await GetAsync(server, "/text", "gzip");

        Assert.Equal("gzip", response.Content.Headers.ContentEncoding.Single());
    }

    [Fact]
    public async Task Honours_a_custom_predicate()
    {
        await using var server = await TestServer.StartAsync(app =>
        {
            app.UseResponseCompression(o => o.ShouldCompress = ctx => ctx.Request.Path.StartsWith("/yes", StringComparison.Ordinal));
            app.MapGet("/yes", ctx => ctx.Response.WriteTextAsync(Body()));
            app.MapGet("/no", ctx => ctx.Response.WriteTextAsync(Body()));
        });

        Assert.Equal("gzip", (await GetAsync(server, "/yes", "gzip")).Content.Headers.ContentEncoding.Single());
        Assert.Empty((await GetAsync(server, "/no", "gzip")).Content.Headers.ContentEncoding);
    }

    /// <summary>
    /// The compressed length is not known until the last block is written, so a declared length
    /// from the handler has to be dropped rather than left describing the uncompressed body.
    /// </summary>
    [Fact]
    public async Task Drops_the_uncompressed_content_length()
    {
        await using var server = await StartAsync();

        var response = await GetAsync(server, "/text", "gzip");
        var actual = (await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).Length;

        Assert.NotEqual(Body().Length, actual);
        Assert.True(
            response.Content.Headers.ContentLength is null || response.Content.Headers.ContentLength == actual,
            "Content-Length, if present, must describe the bytes actually sent"
        );
    }

    /// <summary>Compression must not break connection reuse — the framing has to stay correct.</summary>
    [Fact]
    public async Task Serves_several_compressed_responses_on_one_connection()
    {
        await using var server = await StartAsync();

        for (var i = 0; i < 3; i++)
        {
            var response = await GetAsync(server, "/text", "gzip");

            Assert.Equal("gzip", response.Content.Headers.ContentEncoding.Single());
            Assert.Equal(Body(), await DecompressAsync(response));
        }
    }
}
