using System.Net;
using System.Net.Sockets;
using System.Text;
using Shiny.Net.HttpServer.Compression;

namespace Shiny.Net.HttpServer.Tests;

[Route("/api/sized")]
public class SizedEndpoints
{
    [Post("/small")]
    [RequestSizeLimit(16)]
    public async Task<string> Small(HttpContext ctx) => (await Length(ctx)).ToString();

    [Post("/large")]
    [RequestSizeLimit(1024 * 1024)]
    public async Task<string> Large(HttpContext ctx) => (await Length(ctx)).ToString();

    [Post("/unbounded")]
    [DisableRequestSizeLimit]
    public async Task<string> Unbounded(HttpContext ctx) => (await Length(ctx)).ToString();

    static async Task<long> Length(HttpContext ctx)
    {
        using var buffer = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(buffer, ctx.RequestAborted);
        return buffer.Length;
    }
}

public class RequestSizeLimitTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    const long ServerLimit = 1024;

    static Task<TestServer> StartAsync(Action<HttpServer> configure)
        => TestServer.StartAsync(configure, builder => builder.Options.Limits.MaxRequestBodySize = ServerLimit);

    static async ValueTask Echo(HttpContext ctx)
    {
        using var buffer = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(buffer, ctx.RequestAborted);
        await ctx.Response.WriteAsync(buffer.Length.ToString());
    }

    static ByteArrayContent Body(int size) => new(new byte[size]);

    /// <summary>No Content-Length, so HTTP/1.1 sends it chunked and HTTP/2 without a length.</summary>
    static StreamContent Streamed(int size) => new(new UnseekableStream(new byte[size]));

    sealed class UnseekableStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }

    static HttpClient Http2Client(int port) => new(new SocketsHttpHandler())
    {
        BaseAddress = new Uri($"http://127.0.0.1:{port}"),
        DefaultRequestVersion = HttpVersion.Version20,
        DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        Timeout = TimeSpan.FromSeconds(30)
    };

    [Fact]
    public async Task The_server_wide_limit_still_applies_by_default()
    {
        await using var test = await StartAsync(app => app.MapPost("/echo", Echo));

        var ok = await test.Client.PostAsync("/echo", Body(512), Token);
        Assert.Equal("512", await ok.Content.ReadAsStringAsync(Token));

        var refused = await test.Client.PostAsync("/echo", Body(4096), Token);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, refused.StatusCode);
    }

    [Fact]
    public async Task A_route_can_raise_the_limit()
    {
        await using var test = await StartAsync(app =>
        {
            app.MapPost("/upload", Echo).WithRequestSizeLimit(64 * 1024);
            app.MapPost("/other", Echo);
        });

        var raised = await test.Client.PostAsync("/upload", Body(32 * 1024), Token);
        Assert.Equal("32768", await raised.Content.ReadAsStringAsync(Token));

        var other = await test.Client.PostAsync("/other", Body(32 * 1024), Token);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, other.StatusCode);
    }

    [Fact]
    public async Task A_route_can_lower_the_limit()
    {
        await using var test = await StartAsync(app => app.MapPost("/tiny", Echo).WithRequestSizeLimit(10));

        var refused = await test.Client.PostAsync("/tiny", Body(100), Token);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, refused.StatusCode);
    }

    [Fact]
    public async Task A_route_can_remove_the_limit()
    {
        await using var test = await StartAsync(app => app.MapPost("/any", Echo).DisableRequestSizeLimit());

        var response = await test.Client.PostAsync("/any", Body(256 * 1024), Token);
        Assert.Equal("262144", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task The_route_builder_form_applies_too()
    {
        await using var test = await StartAsync(app =>
            app.MapGroup("/g", group => group.MapPost("/upload", Echo).WithRequestSizeLimit(64 * 1024))
        );

        var response = await test.Client.PostAsync("/g/upload", Body(32 * 1024), Token);
        Assert.Equal("32768", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task A_chunked_body_is_held_to_the_routes_limit()
    {
        await using var test = await StartAsync(app =>
        {
            app.MapPost("/upload", Echo).WithRequestSizeLimit(64 * 1024);
            app.MapPost("/other", Echo);
        });

        var raised = await test.Client.PostAsync("/upload", Streamed(32 * 1024), Token);
        Assert.Equal("32768", await raised.Content.ReadAsStringAsync(Token));

        var refused = await test.Client.PostAsync("/other", Streamed(32 * 1024), Token);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, refused.StatusCode);
    }

    [Fact]
    public async Task Middleware_ahead_of_routing_can_set_the_limit()
    {
        await using var test = await StartAsync(app =>
        {
            app.Use((ctx, next) =>
            {
                if (ctx.Request.Path.StartsWith("/bulk", StringComparison.Ordinal))
                    ctx.Request.MaxBodySize = 64 * 1024;

                return next(ctx);
            });
            app.MapPost("/bulk/import", Echo);
        });

        var response = await test.Client.PostAsync("/bulk/import", Body(32 * 1024), Token);
        Assert.Equal("32768", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task The_limit_is_fixed_once_the_body_is_read()
    {
        await using var test = await StartAsync(app => app.MapPost("/fixed", async ctx =>
        {
            var before = ctx.Request.IsMaxBodySizeReadOnly;
            await ctx.Request.Body.ReadAsync(new byte[1], ctx.RequestAborted);

            var threw = false;
            try
            {
                ctx.Request.MaxBodySize = 1;
            }
            catch (InvalidOperationException)
            {
                threw = true;
            }

            await ctx.Response.WriteAsync($"{before}:{ctx.Request.IsMaxBodySizeReadOnly}:{threw}");
        }));

        var response = await test.Client.PostAsync("/fixed", Body(8), Token);
        Assert.Equal("False:True:True", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task An_unread_body_over_the_limit_is_not_drained()
    {
        await using var test = await StartAsync(app => app.MapPost("/ignore", ctx => ctx.Response.WriteAsync("ignored")));

        // The handler answers without reading. The connection must close rather than swallow a body
        // the limit was there to refuse.
        var raw = await test.SendRawAsync(
            "POST /ignore HTTP/1.1\r\nHost: localhost\r\nContent-Length: 1000000\r\n\r\n"
        );

        Assert.Contains("200 OK", raw);
        Assert.Contains("ignored", raw);
    }

    [Fact]
    public async Task Continue_is_not_sent_for_a_body_over_the_limit()
    {
        await using var test = await StartAsync(app => app.MapPost("/echo", Echo));

        var raw = await test.SendRawAsync(
            "POST /echo HTTP/1.1\r\nHost: localhost\r\nContent-Length: 5000\r\nExpect: 100-continue\r\n\r\n"
        );

        Assert.DoesNotContain("100 Continue", raw);
        Assert.Contains("413", raw);
    }

    [Fact]
    public async Task Continue_is_not_sent_when_the_handler_never_reads()
    {
        await using var test = await StartAsync(app => { });

        var raw = await test.SendRawAsync(
            "POST /nowhere HTTP/1.1\r\nHost: localhost\r\nContent-Length: 10\r\nExpect: 100-continue\r\n\r\n"
        );

        Assert.DoesNotContain("100 Continue", raw);
        Assert.Contains("404", raw);
    }

    [Fact]
    public async Task Continue_is_sent_when_the_body_is_read()
    {
        await using var test = await StartAsync(app => app.MapPost("/upload", Echo).WithRequestSizeLimit(64 * 1024));

        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(IPAddress.Loopback, test.Port, Token);
        await socket.SendAsync(
            Encoding.ASCII.GetBytes("POST /upload HTTP/1.1\r\nHost: localhost\r\nContent-Length: 4096\r\nExpect: 100-continue\r\n\r\n"),
            Token
        );

        var interim = await ReceiveUntilAsync(socket, "\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 100 Continue", interim);

        await socket.SendAsync(new byte[4096], Token);

        var final = await ReceiveUntilAsync(socket, "4096");
        Assert.Contains("200 OK", final);
    }

    static async Task<string> ReceiveUntilAsync(Socket socket, string marker)
    {
        var text = new StringBuilder();
        var buffer = new byte[4096];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        while (!text.ToString().Contains(marker, StringComparison.Ordinal))
        {
            var read = await socket.ReceiveAsync(buffer, SocketFlags.None, timeout.Token);
            if (read == 0)
                break;

            text.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }

        return text.ToString();
    }

    [Fact]
    public async Task Http2_enforces_the_server_wide_limit()
    {
        await using var test = await StartAsync(app => app.MapPost("/echo", Echo));
        using var client = Http2Client(test.Port);

        var declared = await client.PostAsync("/echo", Body(4096), Token);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, declared.StatusCode);

        var streamed = await client.PostAsync("/echo", Streamed(4096), Token);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, streamed.StatusCode);

        // The connection is still good after a refusal.
        var ok = await client.PostAsync("/echo", Body(512), Token);
        Assert.Equal("512", await ok.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Http2_honours_a_routes_limit()
    {
        await using var test = await StartAsync(app =>
        {
            app.MapPost("/upload", Echo).WithRequestSizeLimit(256 * 1024);
            app.MapPost("/any", Echo).DisableRequestSizeLimit();
        });
        using var client = Http2Client(test.Port);

        var declared = await client.PostAsync("/upload", Body(128 * 1024), Token);
        Assert.Equal("131072", await declared.Content.ReadAsStringAsync(Token));

        var streamed = await client.PostAsync("/upload", Streamed(128 * 1024), Token);
        Assert.Equal("131072", await streamed.Content.ReadAsStringAsync(Token));

        var unbounded = await client.PostAsync("/any", Streamed(512 * 1024), Token);
        Assert.Equal("524288", await unbounded.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Http2_keeps_serving_after_a_handler_ignores_its_body()
    {
        await using var test = await StartAsync(app =>
        {
            app.MapPost("/ignore", async ctx =>
            {
                // Long enough for more body to arrive than the stream's pipe holds.
                await Task.Delay(300, ctx.RequestAborted);
                await ctx.Response.WriteAsync("ignored");
            }).DisableRequestSizeLimit();
            app.MapGet("/ping", ctx => ctx.Response.WriteAsync("pong"));
        });
        using var client = Http2Client(test.Port);

        var ignored = await client.PostAsync("/ignore", Body(512 * 1024), Token);
        Assert.Equal("ignored", await ignored.Content.ReadAsStringAsync(Token));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        Assert.Equal("pong", await client.GetStringAsync("/ping", timeout.Token));
    }

    [Fact]
    public async Task Generated_endpoints_read_the_attributes()
    {
        await using var test = await StartAsync(app => app.MapSizedEndpoints());

        Assert.Equal(
            HttpStatusCode.RequestEntityTooLarge,
            (await test.Client.PostAsync("/api/sized/small", Body(64), Token)).StatusCode
        );

        var large = await test.Client.PostAsync("/api/sized/large", Body(64 * 1024), Token);
        Assert.Equal("65536", await large.Content.ReadAsStringAsync(Token));

        var unbounded = await test.Client.PostAsync("/api/sized/unbounded", Body(64 * 1024), Token);
        Assert.Equal("65536", await unbounded.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Decompression_follows_the_routes_limit()
    {
        await using var test = await StartAsync(app =>
        {
            app.UseRequestDecompression();
            app.MapPost("/upload", Echo).WithRequestSizeLimit(64 * 1024);
        });

        // 32KB of zeros compresses far below the server-wide 1KB, and expands well past it.
        using var compressed = new MemoryStream();
        await using (var gzip = new System.IO.Compression.GZipStream(compressed, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            await gzip.WriteAsync(new byte[32 * 1024], Token);

        var content = new ByteArrayContent(compressed.ToArray());
        content.Headers.ContentEncoding.Add("gzip");

        var response = await test.Client.PostAsync("/upload", content, Token);
        Assert.Equal("32768", await response.Content.ReadAsStringAsync(Token));
    }
}
