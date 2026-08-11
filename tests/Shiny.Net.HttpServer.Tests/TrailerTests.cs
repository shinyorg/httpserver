using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Shiny.Net.HttpServer.Tests;

/// <summary>
/// Trailing headers, which are what gRPC's status rides on and the only way to report something a
/// response could not know before it started writing.
/// </summary>
public class TrailerTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Http1_writes_trailers_after_the_terminating_chunk()
    {
        await using var server = await TestServer.StartAsync(app => app.OnGet("/stream", async ctx =>
        {
            ctx.Response.DeclareTrailer("X-Checksum");

            await ctx.Response.Body.WriteAsync("hello"u8.ToArray(), Token);

            // Set after the body has gone out, which is the entire point of a trailer.
            ctx.Response.AppendTrailer("X-Checksum", "abc123");
        }));

        var raw = await ReadWholeResponseAsync(server.Port, "/stream");

        Assert.Contains("Transfer-Encoding: chunked", raw);
        Assert.Contains("Trailer: X-Checksum", raw);

        // The trailer section hangs off the zero-length chunk, and the message ends with a blank line.
        Assert.Contains("\r\n0\r\nX-Checksum: abc123\r\n\r\n", raw);
    }

    [Fact]
    public async Task Http1_still_terminates_cleanly_without_trailers()
    {
        await using var server = await TestServer.StartAsync(
            app => app.OnGet("/stream", async ctx => await ctx.Response.Body.WriteAsync("hello"u8.ToArray(), Token))
        );

        var raw = await ReadWholeResponseAsync(server.Port, "/stream");

        Assert.Contains("\r\n0\r\n\r\n", raw);
    }

    [Fact]
    public async Task Http2_sends_trailers_in_a_trailing_headers_frame()
    {
        await using var server = await TestServer.StartAsync(app => app.OnGet("/stream", async ctx =>
        {
            await ctx.Response.WriteTextAsync("hello", cancellationToken: Token);
            ctx.Response.AppendTrailer("x-checksum", "abc123");
        }));

        using var client = CreateHttp2Client(server.Port);
        var response = await client.GetAsync("/stream", Token);

        Assert.Equal("hello", await response.Content.ReadAsStringAsync(Token));
        Assert.Equal("abc123", response.TrailingHeaders.GetValues("x-checksum").Single());
    }

    [Fact]
    public async Task Http2_folds_trailers_into_the_headers_of_an_empty_response()
    {
        await using var server = await TestServer.StartAsync(app => app.OnGet("/empty", ctx =>
        {
            // Nothing written at all: there is no second frame to hang trailers off, so they belong
            // in the one header block the response does send.
            ctx.Response.AppendTrailer("x-outcome", "nothing-to-do");
            return ValueTask.CompletedTask;
        }));

        using var client = CreateHttp2Client(server.Port);
        var response = await client.GetAsync("/empty", Token);

        Assert.Equal("nothing-to-do", response.Headers.GetValues("x-outcome").Single());
    }

    [Fact]
    public async Task Trailers_do_not_leak_between_requests_on_one_connection()
    {
        await using var server = await TestServer.StartAsync(app =>
        {
            app.OnGet("/with", async ctx =>
            {
                await ctx.Response.WriteTextAsync("first", cancellationToken: Token);
                ctx.Response.AppendTrailer("x-only-here", "yes");
            });

            app.OnGet("/without", ctx => ctx.Response.WriteTextAsync("second", cancellationToken: Token));
        });

        using var client = CreateHttp2Client(server.Port);

        var first = await client.GetAsync("/with", Token);
        Assert.Equal("yes", first.TrailingHeaders.GetValues("x-only-here").Single());

        var second = await client.GetAsync("/without", Token);
        Assert.Empty(second.TrailingHeaders);
        Assert.False(second.Headers.Contains("x-only-here"));
    }

    /// <summary>
    /// Reads to end of stream rather than to the end of the head. A trailer arrives after the last
    /// chunk, so a reader that stops at the first plausible end of message never sees one.
    /// </summary>
    static async Task<string> ReadWholeResponseAsync(int port, string path)
    {
        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(IPAddress.Loopback, port, Token);
        await socket.SendAsync(
            Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n"),
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

    static HttpClient CreateHttp2Client(int port) => new(new SocketsHttpHandler())
    {
        BaseAddress = new Uri($"http://127.0.0.1:{port}"),
        DefaultRequestVersion = HttpVersion.Version20,
        DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        Timeout = TimeSpan.FromSeconds(30)
    };
}
