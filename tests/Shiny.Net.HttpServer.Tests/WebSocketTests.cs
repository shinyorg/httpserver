using System.Net;
using System.Net.WebSockets;
using System.Text;
using Shiny.Net.HttpServer.WebSockets;
using ServerWebSocket = Shiny.Net.HttpServer.WebSockets.WebSocket;
using ServerCloseStatus = Shiny.Net.HttpServer.WebSockets.WebSocketCloseStatus;

namespace Shiny.Net.HttpServer.Tests;

/// <summary>
/// Tested against <see cref="ClientWebSocket"/> rather than a hand-rolled client. A framing bug that
/// only a matching hand-rolled client tolerates is exactly the bug worth catching, and the BCL
/// client is as unforgiving as a browser.
/// </summary>
public class WebSocketTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    static async Task<(TestServer Server, ClientWebSocket Client)> ConnectAsync(
        Func<ServerWebSocket, Task> handle,
        Action<WebSocketAcceptOptions>? configure = null,
        Action<ClientWebSocket>? configureClient = null
    )
    {
        var server = await TestServer.StartAsync(app => app.OnGet("/ws", async ctx =>
        {
            if (!ctx.Request.IsWebSocketRequest())
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsync("not a websocket request");
                return;
            }

            var options = new WebSocketAcceptOptions();
            configure?.Invoke(options);

            await using var socket = await ctx.AcceptWebSocketAsync(options, ctx.RequestAborted);
            await handle(socket);
        }));

        var client = new ClientWebSocket();
        configureClient?.Invoke(client);

        await client.ConnectAsync(
            new Uri($"ws://127.0.0.1:{server.Port}/ws"),
            TestContext.Current.CancellationToken
        );

        return (server, client);
    }

    static async Task<string> ReceiveTextAsync(ClientWebSocket client, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        var received = new List<byte>();

        while (true)
        {
            var result = await client.ReceiveAsync(buffer, cancellationToken);

            if (result.MessageType == WebSocketMessageType.Close)
                return string.Empty;

            received.AddRange(buffer.AsSpan(0, result.Count).ToArray());

            if (result.EndOfMessage)
                return Encoding.UTF8.GetString([.. received]);
        }
    }

    [Fact]
    public async Task Completes_the_handshake()
    {
        var (server, client) = await ConnectAsync(async socket =>
        {
            await socket.SendAsync("hello", CancellationToken.None);
            await socket.CloseAsync(cancellationToken: CancellationToken.None);
        });

        await using (server)
        using (client)
        {
            Assert.Equal(WebSocketState.Open, client.State);
            Assert.Equal("hello", await ReceiveTextAsync(client, Token));
        }
    }

    [Fact]
    public void Computes_the_handshake_accept_value()
        // The example from RFC 6455 §1.3, which is the whole point of a fixed test vector.
        => Assert.Equal("s3pPLMBiTxaQ9kYGzzhZRbK+xOo=", WebSocketExtensions.ComputeAccept("dGhlIHNhbXBsZSBub25jZQ=="));

    [Fact]
    public async Task Echoes_text_messages()
    {
        var (server, client) = await ConnectAsync(async socket =>
        {
            while (await socket.ReceiveAsync(CancellationToken.None) is { } message)
                await socket.SendAsync($"echo:{message.Text}", CancellationToken.None);
        });

        await using (server)
        using (client)
        {
            foreach (var text in (string[])["one", "two", "three"])
            {
                await client.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, Token);
                Assert.Equal($"echo:{text}", await ReceiveTextAsync(client, Token));
            }
        }
    }

    [Fact]
    public async Task Carries_binary_messages()
    {
        var payload = new byte[4096];
        Random.Shared.NextBytes(payload);

        var (server, client) = await ConnectAsync(async socket =>
        {
            var message = await socket.ReceiveAsync(CancellationToken.None);
            await socket.SendAsync(message!.Payload, CancellationToken.None);
        });

        await using (server)
        using (client)
        {
            await client.SendAsync(payload, WebSocketMessageType.Binary, true, Token);

            var buffer = new byte[8192];
            var received = new List<byte>();

            while (true)
            {
                var result = await client.ReceiveAsync(buffer, Token);
                received.AddRange(buffer.AsSpan(0, result.Count).ToArray());

                if (result.EndOfMessage)
                    break;
            }

            Assert.Equal(payload, received);
        }
    }

    [Fact]
    public async Task Reassembles_a_fragmented_message()
    {
        var (server, client) = await ConnectAsync(async socket =>
        {
            var message = await socket.ReceiveAsync(CancellationToken.None);
            await socket.SendAsync($"{message!.Text.Length}", CancellationToken.None);
        });

        await using (server)
        using (client)
        {
            // Three frames, one message: the client marks only the last as end-of-message.
            await client.SendAsync("aaa"u8.ToArray(), WebSocketMessageType.Text, false, Token);
            await client.SendAsync("bbb"u8.ToArray(), WebSocketMessageType.Text, false, Token);
            await client.SendAsync("ccc"u8.ToArray(), WebSocketMessageType.Text, true, Token);

            Assert.Equal("9", await ReceiveTextAsync(client, Token));
        }
    }

    [Fact]
    public async Task Sends_a_message_larger_than_one_frame_header_size()
    {
        // Crosses both the 126 and 65536 length encodings.
        var text = new string('x', 100_000);

        var (server, client) = await ConnectAsync(async socket =>
        {
            await socket.SendAsync(text, CancellationToken.None);
            await socket.CloseAsync(cancellationToken: CancellationToken.None);
        });

        await using (server)
        using (client)
            Assert.Equal(text, await ReceiveTextAsync(client, Token));
    }

    [Fact]
    public async Task Answers_a_ping_with_a_pong()
    {
        var pinged = new TaskCompletionSource();

        var (server, client) = await ConnectAsync(async socket =>
        {
            // The client's keepalive ping is handled inside ReceiveAsync and never surfaces here.
            await socket.ReceiveAsync(CancellationToken.None);
            pinged.TrySetResult();
        }, configureClient: c => c.Options.KeepAliveInterval = TimeSpan.FromMilliseconds(100));

        await using (server)
        using (client)
        {
            await client.SendAsync("after-ping"u8.ToArray(), WebSocketMessageType.Text, true, Token);
            await pinged.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        }
    }

    [Fact]
    public async Task Completes_the_close_handshake_from_the_client()
    {
        var closed = new TaskCompletionSource<WebSocketCloseResult?>();

        var (server, client) = await ConnectAsync(async socket =>
        {
            while (await socket.ReceiveAsync(CancellationToken.None) is not null)
            {
            }

            closed.TrySetResult(socket.CloseResult);
        });

        await using (server)
        using (client)
        {
            await client.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "done", Token);

            var result = await closed.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);

            Assert.NotNull(result);
            Assert.Equal(ServerCloseStatus.NormalClosure, result.Status);
            Assert.Equal("done", result.Description);
        }
    }

    [Fact]
    public async Task Closes_cleanly_from_the_server()
    {
        var (server, client) = await ConnectAsync(async socket =>
            await socket.CloseAsync(
                ServerCloseStatus.EndpointUnavailable,
                "going away",
                CancellationToken.None
            ));

        await using (server)
        using (client)
        {
            var result = await client.ReceiveAsync(new byte[64], Token);

            Assert.Equal(WebSocketMessageType.Close, result.MessageType);
            Assert.Equal(System.Net.WebSockets.WebSocketCloseStatus.EndpointUnavailable, result.CloseStatus);
            Assert.Equal("going away", result.CloseStatusDescription);
        }
    }

    [Fact]
    public async Task Negotiates_a_sub_protocol()
    {
        var (server, client) = await ConnectAsync(
            async socket =>
            {
                await socket.SendAsync(socket.SubProtocol ?? "(none)", CancellationToken.None);
                await socket.CloseAsync(cancellationToken: CancellationToken.None);
            },
            configure: o =>
            {
                o.SupportedSubProtocols.Add("chat.v2");
                o.SupportedSubProtocols.Add("chat.v1");
            },
            configureClient: c =>
            {
                c.Options.AddSubProtocol("chat.v1");
                c.Options.AddSubProtocol("chat.v2");
            }
        );

        await using (server)
        using (client)
        {
            // The server's preference order wins: it is the side that knows what it can do.
            Assert.Equal("chat.v2", client.SubProtocol);
            Assert.Equal("chat.v2", await ReceiveTextAsync(client, Token));
        }
    }

    [Fact]
    public async Task Rejects_a_plain_get_on_a_websocket_route()
    {
        await using var server = await TestServer.StartAsync(app => app.OnGet("/ws", async ctx =>
        {
            if (!ctx.Request.IsWebSocketRequest())
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsync("not a websocket request");
                return;
            }

            await using var socket = await ctx.AcceptWebSocketAsync(cancellationToken: ctx.RequestAborted);
        }));

        var response = await server.Client.GetAsync("/ws", Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("not a websocket request", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Refuses_a_message_over_the_configured_limit()
    {
        var faulted = new TaskCompletionSource<string>();

        var (server, client) = await ConnectAsync(
            async socket =>
            {
                try
                {
                    await socket.ReceiveAsync(CancellationToken.None);
                    faulted.TrySetResult("no error");
                }
                catch (WebSocketProtocolException ex)
                {
                    faulted.TrySetResult(ex.Status.ToString());
                }
            },
            configure: o => o.MaxMessageLength = 1024
        );

        await using (server)
        using (client)
        {
            await client.SendAsync(new byte[8192], WebSocketMessageType.Binary, true, Token);
            Assert.Equal("MessageTooBig", await faulted.Task.WaitAsync(TimeSpan.FromSeconds(10), Token));
        }
    }

    [Fact]
    public async Task Serves_ordinary_requests_on_the_same_server()
    {
        await using var server = await TestServer.StartAsync(app =>
        {
            app.OnGet("/ping", ctx => ctx.Response.WriteAsync("pong"));
            app.OnGet("/ws", async ctx =>
            {
                await using var socket = await ctx.AcceptWebSocketAsync(cancellationToken: ctx.RequestAborted);
                await socket.SendAsync("hi", CancellationToken.None);
                await socket.CloseAsync(cancellationToken: CancellationToken.None);
            });
        });

        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/ws"), Token);

        Assert.Equal("hi", await ReceiveTextAsync(client, Token));

        // The upgrade took over one connection, not the listener.
        Assert.Equal("pong", await server.Client.GetStringAsync("/ping", Token));
    }
}
