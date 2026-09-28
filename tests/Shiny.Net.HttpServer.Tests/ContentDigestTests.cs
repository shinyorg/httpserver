using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Shiny.Net.HttpServer.Compression;
using Shiny.Net.HttpServer.Http3;
using Shiny.Net.HttpServer.Integrity;

namespace Shiny.Net.HttpServer.Tests;

[Route("/api/digested")]
public class DigestedEndpoints
{
    [Get("/always")]
    [ContentDigest]
    public string Always() => "signed";

    [Post("/strict")]
    [ContentDigest(RequireRequestDigest = true, AlwaysEmitResponseDigest = false)]
    public string Strict() => "accepted";

    [Get("/off")]
    [DisableContentDigest]
    public string Off() => "plain";
}

public class DigestFieldTests
{
    // RFC 9530 §B.1: the digests of {"hello": "world"}.
    const string Hello = "{\"hello\": \"world\"}";

    [Fact]
    public void Computes_the_rfc_examples()
    {
        Assert.Equal("sha-256=:X48E9qOokqqrvdts8nOJRJN3OWDUoyWxBf7kbu9DBPE=:", DigestFields.Compute(Encoding.UTF8.GetBytes(Hello)));
        Assert.Equal(
            "sha-512=:WZDPaVn/7XgHaAy8pmojAkGWoRx2UFChF41A2svX+TaPm+AbwAgBWnrIiYllu7BNNyealdVLvRwEmTHWXvJwew==:",
            DigestFields.Compute(Encoding.UTF8.GetBytes(Hello), DigestFields.Sha512)
        );
    }

    [Fact]
    public async Task Computes_the_same_digest_from_a_stream()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Hello));

        Assert.Equal(DigestFields.Compute(Encoding.UTF8.GetBytes(Hello)), await DigestFields.ComputeAsync(stream, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Parses_a_dictionary_with_several_algorithms_and_parameters()
    {
        Assert.True(DigestFields.TryParse(
            "sha-256=:X48E9qOokqqrvdts8nOJRJN3OWDUoyWxBf7kbu9DBPE=:;note=1, unixsum=:AAAA:",
            out var digests
        ));

        Assert.Equal(["sha-256", "unixsum"], digests.Select(d => d.Key));
        Assert.Equal(32, digests[0].Value.Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData("sha-256")]
    [InlineData("sha-256=abc")]
    [InlineData("sha-256=:not base64!:")]
    [InlineData("SHA 256=:AAAA:")]
    public void Refuses_malformed_values(string value) => Assert.False(DigestFields.TryParse(value, out _));

    [Theory]
    [InlineData("sha-256=1, sha-512=3", "sha-512")]
    [InlineData("sha-512=3, sha-256=10", "sha-256")]
    [InlineData("sha-256=5, sha-512=5", "sha-256")]
    [InlineData("md5=10, sha-512=1", "sha-512")]
    [InlineData("sha-256=0", null)]
    [InlineData("unixsum=10", null)]
    public void Selects_the_most_preferred_supported_algorithm(string want, string? expected)
        => Assert.Equal(expected, DigestFields.SelectPreferred(want));
}

public class ContentDigestTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    static string Sha256Of(byte[] body) => DigestFields.Compute(body);

    static string Sha256Of(string body) => Sha256Of(Encoding.UTF8.GetBytes(body));

    /// <summary>Echoes the request body; flags whether the handler got past reading it.</summary>
    sealed class Echo
    {
        public bool Acted;

        public async ValueTask HandleAsync(HttpContext ctx)
        {
            var body = await ctx.Request.ReadBodyAsStringAsync(maxLength: 8 * 1024 * 1024, cancellationToken: ctx.RequestAborted);
            this.Acted = true;
            await ctx.Response.WriteTextAsync($"got {body.Length}", cancellationToken: ctx.RequestAborted);
        }
    }

    static Task<TestServer> StartAsync(Action<HttpServer> map, Action<ContentDigestOptions>? configure = null)
        => TestServer.StartAsync(
            app =>
            {
                app.UseContentDigest();
                map(app);
            },
            builder => builder.AddContentDigest(configure)
        );

    static HttpRequestMessage Upload(string path, string body, string? digest)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body) };

        if (digest is not null)
            request.Content.Headers.TryAddWithoutValidation(DigestFields.ContentDigest, digest);

        return request;
    }

    // ---- requests -------------------------------------------------------------------------------

    [Fact]
    public async Task A_matching_request_digest_passes_through()
    {
        var echo = new Echo();
        await using var server = await StartAsync(app => app.MapPost("/upload", echo.HandleAsync));

        var response = await server.Client.SendAsync(Upload("/upload", "hello", Sha256Of("hello")), Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("got 5", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task A_mismatched_request_digest_is_a_400_problem_and_the_handler_never_acts()
    {
        var echo = new Echo();
        await using var server = await StartAsync(app => app.MapPost("/upload", echo.HandleAsync));

        var response = await server.Client.SendAsync(Upload("/upload", "hello", Sha256Of("hellO")), Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("sha-256", response.Headers.GetValues(DigestFields.WantContentDigest).Single());
        Assert.False(echo.Acted);
    }

    [Fact]
    public async Task Every_supported_digest_in_the_header_has_to_match()
    {
        var echo = new Echo();
        await using var server = await StartAsync(app => app.MapPost("/upload", echo.HandleAsync));

        var wrong512 = DigestFields.Compute("other"u8, DigestFields.Sha512);
        var response = await server.Client.SendAsync(Upload("/upload", "hello", $"{Sha256Of("hello")}, {wrong512}"), Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_handler_that_swallows_the_mismatch_is_still_answered_400()
    {
        await using var server = await StartAsync(app => app.MapPost("/upload", async ctx =>
        {
            try
            {
                await ctx.Request.ReadBodyAsStringAsync(maxLength: 1024, cancellationToken: ctx.RequestAborted);
            }
            catch (BadHttpRequestException)
            {
            }

            await ctx.Response.WriteTextAsync("looks fine to me", cancellationToken: ctx.RequestAborted);
        }));

        var response = await server.Client.SendAsync(Upload("/upload", "hello", Sha256Of("nope")), Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Unsupported_algorithms_are_refused_by_default_and_can_be_let_through()
    {
        var echo = new Echo();

        await using (var server = await StartAsync(app => app.MapPost("/upload", echo.HandleAsync)))
        {
            var response = await server.Client.SendAsync(Upload("/upload", "hello", "md5=:XUFAKrxLKna5cZ2REBfFkg==:"), Token);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        await using (var server = await StartAsync(app => app.MapPost("/upload", echo.HandleAsync), o => o.RejectUnsupportedAlgorithms = false))
        {
            var response = await server.Client.SendAsync(Upload("/upload", "hello", "md5=:XUFAKrxLKna5cZ2REBfFkg==:"), Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task A_malformed_header_is_a_400()
    {
        var echo = new Echo();
        await using var server = await StartAsync(app => app.MapPost("/upload", echo.HandleAsync));

        var response = await server.Client.SendAsync(Upload("/upload", "hello", "sha-256=nope"), Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(echo.Acted);
    }

    [Fact]
    public async Task RequireContentDigest_refuses_a_body_without_one()
    {
        var echo = new Echo();
        await using var server = await StartAsync(app =>
        {
            app.MapPost("/strict", echo.HandleAsync).RequireContentDigest();
            app.MapPost("/lax", echo.HandleAsync);
        });

        Assert.Equal(HttpStatusCode.BadRequest, (await server.Client.SendAsync(Upload("/strict", "hello", null), Token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await server.Client.SendAsync(Upload("/strict", "hello", Sha256Of("hello")), Token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await server.Client.SendAsync(Upload("/lax", "hello", null), Token)).StatusCode);
    }

    [Fact]
    public async Task A_large_chunked_upload_is_verified_as_it_streams()
    {
        var payload = RandomNumberGenerator.GetBytes(3 * 1024 * 1024);
        long received = 0;

        await using var server = await StartAsync(app => app.MapPost("/stream", async ctx =>
        {
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = await ctx.Request.Body.ReadAsync(buffer, ctx.RequestAborted)) > 0)
                received += read;

            await ctx.Response.WriteTextAsync("stored", cancellationToken: ctx.RequestAborted);
        }));

        HttpRequestMessage Chunked(string digest)
        {
            // No length: a chunked body, so nothing is known about it until the last chunk.
            var content = new StreamContent(new NonSeekableStream(payload));
            content.Headers.TryAddWithoutValidation(DigestFields.ContentDigest, digest);
            return new HttpRequestMessage(HttpMethod.Post, "/stream") { Content = content };
        }

        var good = await server.Client.SendAsync(Chunked(Sha256Of(payload)), Token);
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
        Assert.Equal(payload.Length, received);

        var bad = await server.Client.SendAsync(Chunked(Sha256Of([1, 2, 3])), Token);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task The_digest_covers_the_compressed_bytes_on_the_wire_ahead_of_decompression()
    {
        var echo = new Echo();
        await using var server = await TestServer.StartAsync(
            app =>
            {
                app.UseContentDigest();
                app.UseRequestDecompression();
                app.MapPost("/upload", echo.HandleAsync);
            },
            builder => builder.AddContentDigest()
        );

        var compressed = Gzip(Encoding.UTF8.GetBytes(new string('a', 5000)));
        var content = new ByteArrayContent(compressed);
        content.Headers.ContentEncoding.Add("gzip");
        content.Headers.TryAddWithoutValidation(DigestFields.ContentDigest, Sha256Of(compressed));

        var response = await server.Client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/upload") { Content = content }, Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("got 5000", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task A_digest_of_empty_content_is_checked_on_a_request_without_a_body()
    {
        await using var server = await StartAsync(app => app.MapDelete("/thing", ctx => ctx.Response.WriteTextAsync("gone", cancellationToken: ctx.RequestAborted)));

        HttpRequestMessage Delete(string digest)
        {
            var request = new HttpRequestMessage(HttpMethod.Delete, "/thing");
            request.Headers.TryAddWithoutValidation(DigestFields.ContentDigest, digest);
            return request;
        }

        Assert.Equal(HttpStatusCode.OK, (await server.Client.SendAsync(Delete(Sha256Of([])), Token)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await server.Client.SendAsync(Delete(Sha256Of("x")), Token)).StatusCode);
    }

    // ---- responses ------------------------------------------------------------------------------

    static HttpRequestMessage Get(string path, string? want = null, string? wantRepr = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);

        if (want is not null)
            request.Headers.TryAddWithoutValidation(DigestFields.WantContentDigest, want);

        if (wantRepr is not null)
            request.Headers.TryAddWithoutValidation(DigestFields.WantReprDigest, wantRepr);

        return request;
    }

    static string? Header(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values)
            ? values.Single()
            : null;

    [Fact]
    public async Task No_digest_is_added_unless_the_client_asks()
    {
        await using var server = await StartAsync(app => app.MapGet("/text", ctx => ctx.Response.WriteTextAsync("hello", cancellationToken: ctx.RequestAborted)));

        var response = await server.Client.SendAsync(Get("/text"), Token);

        Assert.Null(Header(response, DigestFields.ContentDigest));
    }

    [Fact]
    public async Task Want_content_digest_is_answered_with_a_header_over_the_body()
    {
        await using var server = await StartAsync(app => app.MapGet("/text", ctx => ctx.Response.WriteTextAsync("hello", cancellationToken: ctx.RequestAborted)));

        var response = await server.Client.SendAsync(Get("/text", want: "sha-256=10"), Token);
        var body = await response.Content.ReadAsByteArrayAsync(Token);

        Assert.Equal(Sha256Of(body), Header(response, DigestFields.ContentDigest));
        Assert.Equal(5, response.Content.Headers.ContentLength);
    }

    [Fact]
    public async Task The_clients_preferred_algorithm_is_used()
    {
        await using var server = await StartAsync(app => app.MapGet("/text", ctx => ctx.Response.WriteTextAsync("hello", cancellationToken: ctx.RequestAborted)));

        var response = await server.Client.SendAsync(Get("/text", want: "sha-256=1, sha-512=9"), Token);

        Assert.Equal(DigestFields.Compute("hello"u8, DigestFields.Sha512), Header(response, DigestFields.ContentDigest));
    }

    [Fact]
    public async Task WithContentDigest_adds_one_unasked_and_Never_ignores_the_ask()
    {
        await using (var server = await StartAsync(app => app.MapGet("/text", ctx => ctx.Response.WriteTextAsync("hello", cancellationToken: ctx.RequestAborted)).WithContentDigest()))
        {
            var response = await server.Client.SendAsync(Get("/text"), Token);
            Assert.Equal(Sha256Of("hello"), Header(response, DigestFields.ContentDigest));
        }

        await using (var server = await StartAsync(
            app => app.MapGet("/text", ctx => ctx.Response.WriteTextAsync("hello", cancellationToken: ctx.RequestAborted)),
            o => o.ResponseDigest = DigestEmission.Never
        ))
        {
            var response = await server.Client.SendAsync(Get("/text", want: "sha-256=10"), Token);
            Assert.Null(Header(response, DigestFields.ContentDigest));
        }
    }

    [Fact]
    public async Task Want_repr_digest_is_answered_on_a_full_response_but_not_on_a_range()
    {
        await using var server = await StartAsync(app =>
        {
            app.MapGet("/full", ctx => ctx.Response.WriteTextAsync("hello", cancellationToken: ctx.RequestAborted));
            app.MapGet("/part", ctx =>
            {
                ctx.Response.StatusCode = StatusCodes.Status206PartialContent;
                ctx.Response.Headers.Set(HeaderNames.ContentRange, "bytes 0-1/5");
                return ctx.Response.WriteTextAsync("he", cancellationToken: ctx.RequestAborted);
            });
        });

        var full = await server.Client.SendAsync(Get("/full", wantRepr: "sha-256=10"), Token);
        Assert.Equal(Sha256Of("hello"), Header(full, DigestFields.ReprDigest));
        Assert.Null(Header(full, DigestFields.ContentDigest));

        var part = await server.Client.SendAsync(Get("/part", want: "sha-256=10", wantRepr: "sha-256=10"), Token);
        Assert.Null(Header(part, DigestFields.ReprDigest));
        Assert.Equal(Sha256Of("he"), Header(part, DigestFields.ContentDigest));
    }

    [Fact]
    public async Task A_digest_the_handler_set_itself_is_left_alone()
    {
        await using var server = await StartAsync(app => app.MapGet("/file", ctx =>
        {
            ctx.Response.Headers.Set(DigestFields.ContentDigest, "sha-256=:precomputed00000000000000000000000000000000=:");
            return ctx.Response.WriteTextAsync("hello", cancellationToken: ctx.RequestAborted);
        }));

        var response = await server.Client.SendAsync(Get("/file", want: "sha-256=10"), Token);

        Assert.StartsWith("sha-256=:precomputed", Header(response, DigestFields.ContentDigest));
    }

    [Fact]
    public async Task No_content_responses_carry_no_digest()
    {
        await using var server = await StartAsync(app => app.MapGet("/none", ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status204NoContent;
            return ValueTask.CompletedTask;
        }).WithContentDigest());

        var response = await server.Client.SendAsync(Get("/none"), Token);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Null(Header(response, DigestFields.ContentDigest));
    }

    [Fact]
    public async Task Http1_sends_the_digest_of_a_streamed_body_as_a_declared_trailer()
    {
        var chunk = Encoding.ASCII.GetBytes(new string('z', 1000));

        await using var server = await StartAsync(app => app.MapGet("/stream", async ctx =>
        {
            await ctx.Response.StartAsync(ctx.RequestAborted);

            for (var i = 0; i < 100; i++)
                await ctx.Response.Body.WriteAsync(chunk, ctx.RequestAborted);
        }));

        var raw = await ReadWholeResponseAsync(server.Port, "/stream", "Want-Content-Digest: sha-256=10");
        var expected = Sha256Of(Enumerable.Repeat(chunk, 100).SelectMany(x => x).ToArray());

        Assert.Contains("Transfer-Encoding: chunked", raw);
        Assert.Contains("Trailer: Content-Digest", raw);
        Assert.Contains($"\r\n0\r\nContent-Digest: {expected}\r\n\r\n", raw);
    }

    [Fact]
    public async Task Http1_goes_over_the_buffer_and_switches_to_a_trailer_without_losing_bytes()
    {
        var body = RandomNumberGenerator.GetBytes(200_000);

        await using var server = await StartAsync(
            app => app.MapGet("/big", async ctx =>
            {
                // Several writes, none of which says how long the whole is.
                for (var offset = 0; offset < body.Length; offset += 7000)
                    await ctx.Response.Body.WriteAsync(body.AsMemory(offset, Math.Min(7000, body.Length - offset)), ctx.RequestAborted);
            }),
            o => o.MaxBufferedResponseBytes = 16 * 1024
        );

        var response = await server.Client.SendAsync(Get("/big", want: "sha-256=10"), HttpCompletionOption.ResponseHeadersRead, Token);
        var received = await response.Content.ReadAsByteArrayAsync(Token);

        Assert.Equal(body, received);
        Assert.Equal(Sha256Of(body), response.TrailingHeaders.GetValues(DigestFields.ContentDigest).Single());
    }

    [Fact]
    public async Task Http1_cannot_trail_a_streamed_body_that_declared_its_length()
    {
        var body = RandomNumberGenerator.GetBytes(100_000);

        await using var server = await StartAsync(
            app => app.MapGet("/sized", async ctx =>
            {
                ctx.Response.ContentLength = body.Length;
                await ctx.Response.StartAsync(ctx.RequestAborted);
                await ctx.Response.Body.WriteAsync(body, ctx.RequestAborted);
            })
        );

        var response = await server.Client.SendAsync(Get("/sized", want: "sha-256=10"), Token);

        Assert.Equal(body, await response.Content.ReadAsByteArrayAsync(Token));
        Assert.Null(Header(response, DigestFields.ContentDigest));
        Assert.Empty(response.TrailingHeaders);
    }

    [Fact]
    public async Task Http2_sends_a_small_body_digest_as_a_header()
    {
        await using var server = await StartAsync(app => app.MapGet("/text", ctx => ctx.Response.WriteTextAsync("hello", cancellationToken: ctx.RequestAborted)));
        using var client = CreateHttp2Client(server.Port);

        var response = await client.SendAsync(H2(Get("/text", want: "sha-512=10")), Token);

        Assert.Equal(HttpVersion.Version20, response.Version);
        Assert.Equal(DigestFields.Compute("hello"u8, DigestFields.Sha512), Header(response, DigestFields.ContentDigest));
    }

    [Fact]
    public async Task Http2_sends_a_streamed_body_digest_as_a_trailer_even_with_a_declared_length()
    {
        var body = RandomNumberGenerator.GetBytes(150_000);

        await using var server = await StartAsync(app => app.MapGet("/sized", async ctx =>
        {
            // HTTP/2 frames the body itself, so a declared length does not stop a trailer.
            ctx.Response.ContentLength = body.Length;
            await ctx.Response.StartAsync(ctx.RequestAborted);
            await ctx.Response.Body.WriteAsync(body, ctx.RequestAborted);
        }));

        using var client = CreateHttp2Client(server.Port);
        var response = await client.SendAsync(H2(Get("/sized", want: "sha-256=10", wantRepr: "sha-256=10")), Token);

        Assert.Equal(body, await response.Content.ReadAsByteArrayAsync(Token));
        Assert.Equal(Sha256Of(body), response.TrailingHeaders.GetValues(DigestFields.ContentDigest).Single());
        Assert.Equal(Sha256Of(body), response.TrailingHeaders.GetValues(DigestFields.ReprDigest).Single());
    }

    [Fact]
    public async Task Http2_verifies_request_digests()
    {
        var echo = new Echo();
        await using var server = await StartAsync(app => app.MapPost("/upload", echo.HandleAsync));
        using var client = CreateHttp2Client(server.Port);

        var good = await client.SendAsync(H2(Upload("/upload", "hello", Sha256Of("hello"))), Token);
        var bad = await client.SendAsync(H2(Upload("/upload", "hello", Sha256Of("bye"))), Token);

        Assert.Equal(HttpVersion.Version20, bad.Version);
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Registered_ahead_of_compression_the_digest_covers_the_compressed_bytes()
    {
        var text = new string('q', 20_000);

        await using var server = await TestServer.StartAsync(
            app =>
            {
                app.UseContentDigest();
                app.UseResponseCompression();
                app.MapGet("/text", ctx => ctx.Response.WriteTextAsync(text, cancellationToken: ctx.RequestAborted));
            },
            builder => builder.AddContentDigest()
        );

        var request = Get("/text", want: "sha-256=10");
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));

        // The default client does not decompress, so this is exactly what came off the wire.
        var response = await server.Client.SendAsync(request, Token);
        var wire = await response.Content.ReadAsByteArrayAsync(Token);

        Assert.Equal("gzip", response.Content.Headers.ContentEncoding.Single());
        Assert.Equal(Sha256Of(wire), Header(response, DigestFields.ContentDigest));
    }

    [Fact]
    public async Task Registered_behind_compression_it_declines_rather_than_digest_the_wrong_bytes()
    {
        var text = new string('q', 20_000);

        await using var server = await TestServer.StartAsync(
            app =>
            {
                app.UseResponseCompression();
                app.UseContentDigest();
                app.MapGet("/text", ctx => ctx.Response.WriteTextAsync(text, cancellationToken: ctx.RequestAborted));
            },
            builder => builder.AddContentDigest()
        );

        var request = Get("/text", want: "sha-256=10");
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));

        var response = await server.Client.SendAsync(request, Token);

        Assert.Equal("gzip", response.Content.Headers.ContentEncoding.Single());
        Assert.Null(Header(response, DigestFields.ContentDigest));
    }

    [Fact]
    public async Task Generated_endpoints_honour_the_attributes()
    {
        await using var server = await StartAsync(app => app.MapDigestedEndpoints());

        Assert.Equal(Sha256Of("signed"), Header(await server.Client.SendAsync(Get("/api/digested/always"), Token), DigestFields.ContentDigest));
        Assert.Null(Header(await server.Client.SendAsync(Get("/api/digested/off", want: "sha-256=10"), Token), DigestFields.ContentDigest));

        Assert.Equal(HttpStatusCode.BadRequest, (await server.Client.SendAsync(Upload("/api/digested/strict", "x", null), Token)).StatusCode);

        var strict = await server.Client.SendAsync(Upload("/api/digested/strict", "x", Sha256Of("x")), Token);
        Assert.Equal(HttpStatusCode.OK, strict.StatusCode);
        Assert.Null(Header(strict, DigestFields.ContentDigest));
    }

    [Fact]
    public async Task A_group_convention_applies_to_its_routes()
    {
        await using var server = await StartAsync(app => app.MapGroup("/signed", group =>
        {
            var signed = group.WithContentDigest();
            signed.MapGet("/a", ctx => ctx.Response.WriteTextAsync("a", cancellationToken: ctx.RequestAborted));
            signed.MapGet("/b", ctx => ctx.Response.WriteTextAsync("b", cancellationToken: ctx.RequestAborted)).DisableContentDigest();
        }));

        Assert.Equal(Sha256Of("a"), Header(await server.Client.SendAsync(Get("/signed/a"), Token), DigestFields.ContentDigest));
        Assert.Null(Header(await server.Client.SendAsync(Get("/signed/b"), Token), DigestFields.ContentDigest));
    }

    [Fact]
    public async Task Digests_survive_the_trip_through_a_tunnel()
    {
        var echo = new Echo();

        var builder = HttpServer.CreateBuilder();
        builder.Options.Port = 0;
        builder.AddContentDigest();
        await using var server = builder.Build();

        server.UseContentDigest();
        server.MapPost("/upload", echo.HandleAsync);
        server.MapGet("/text", ctx => ctx.Response.WriteTextAsync("hello", cancellationToken: ctx.RequestAborted));

        await using var tunnel = await RelayHarness.StartAsync(server, "digest");

        var good = await tunnel.Client.SendAsync(tunnel.Route(Upload("/upload", "hello", Sha256Of("hello"))), Token);
        var bad = await tunnel.Client.SendAsync(tunnel.Route(Upload("/upload", "hello", Sha256Of("bye"))), Token);
        var digested = await tunnel.Client.SendAsync(tunnel.Route(Get("/text", want: "sha-256=10")), Token);

        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(Sha256Of("hello"), Header(digested, DigestFields.ContentDigest));
    }

    // ---- HTTP/3 (QUIC needs msquic: Windows and Linux, not macOS) ----------------------------------

    [Fact]
    public async Task Http3_verifies_requests_and_trails_streamed_responses()
    {
        Assert.SkipUnless(Http3Listener.IsSupported, "QUIC is not supported on this platform.");

        using var certificate = ServerCertificate.Create();
        var echo = new Echo();
        var body = RandomNumberGenerator.GetBytes(120_000);

        await using var server = new HttpServer(new HttpServerOptions
        {
            Address = IPAddress.Loopback,
            Port = 0,
            Https = new HttpsOptions { Certificate = certificate }
        });

        server.UseContentDigest();
        server.MapPost("/upload", echo.HandleAsync);
        server.MapGet("/text", ctx => ctx.Response.WriteTextAsync("hello", cancellationToken: ctx.RequestAborted));
        server.MapGet("/stream", async ctx =>
        {
            await ctx.Response.StartAsync(ctx.RequestAborted);
            await ctx.Response.Body.WriteAsync(body, ctx.RequestAborted);
        });

        await using var h3 = await server.ListenHttp3Async(o =>
        {
            o.Port = 0;
            o.Certificate = certificate;
        }, Token);

        await server.StartAsync(Token);

        using var client = new HttpClient(CertificatePinning.CreateHandler(certificate))
        {
            BaseAddress = new Uri($"https://127.0.0.1:{h3.BoundEndPoint!.Port}"),
            Timeout = TimeSpan.FromSeconds(30)
        };

        static HttpRequestMessage H3(HttpRequestMessage request)
        {
            request.Version = HttpVersion.Version30;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
            return request;
        }

        var good = await client.SendAsync(H3(Upload("/upload", "hello", Sha256Of("hello"))), Token);
        var bad = await client.SendAsync(H3(Upload("/upload", "hello", Sha256Of("bye"))), Token);
        var small = await client.SendAsync(H3(Get("/text", want: "sha-256=10")), Token);
        var streamed = await client.SendAsync(H3(Get("/stream", want: "sha-256=10")), Token);

        Assert.Equal(HttpVersion.Version30, good.Version);
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(Sha256Of("hello"), Header(small, DigestFields.ContentDigest));
        Assert.Equal(body, await streamed.Content.ReadAsByteArrayAsync(Token));
        Assert.Equal(Sha256Of(body), streamed.TrailingHeaders.GetValues(DigestFields.ContentDigest).Single());
    }

    // ---- helpers --------------------------------------------------------------------------------

    static byte[] Gzip(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
            gzip.Write(data);

        return output.ToArray();
    }

    static HttpRequestMessage H2(HttpRequestMessage request)
    {
        request.Version = HttpVersion.Version20;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        return request;
    }

    static HttpClient CreateHttp2Client(int port) => new(new SocketsHttpHandler())
    {
        BaseAddress = new Uri($"http://127.0.0.1:{port}"),
        Timeout = TimeSpan.FromSeconds(30)
    };

    /// <summary>Reads to the end of the connection, so the trailer section after the last chunk is included.</summary>
    static async Task<string> ReadWholeResponseAsync(int port, string path, string extraHeader)
    {
        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(IPAddress.Loopback, port, Token);
        await socket.SendAsync(
            Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: localhost\r\n{extraHeader}\r\nConnection: close\r\n\r\n"),
            Token
        );

        var response = new StringBuilder();
        var buffer = new byte[8192];

        while (true)
        {
            var read = await socket.ReceiveAsync(buffer, SocketFlags.None, Token);
            if (read == 0)
                return response.ToString();

            response.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }
    }

    /// <summary>A stream that will not say how long it is, so the client has to send it chunked.</summary>
    sealed class NonSeekableStream(byte[] data) : Stream
    {
        readonly MemoryStream inner = new(data);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => this.inner.Read(buffer, offset, Math.Min(count, 8192));
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
