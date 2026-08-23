using System.Net;
using System.Net.WebSockets;
using System.Text;
using Shiny.Net.HttpServer.WebSockets;
using ServerWebSocket = Shiny.Net.HttpServer.WebSockets.WebSocket;

namespace Shiny.Net.HttpServer.Tests;

/// <summary>
/// permessage-deflate against <see cref="ClientWebSocket"/> with real deflate on the other side —
/// a compression bug that only a matching implementation tolerates is the one worth catching.
/// </summary>
public class WebSocketCompressionTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    static async Task<(TestServer Server, ClientWebSocket Client)> ConnectAsync(
        Func<ServerWebSocket, Task> handle,
        bool clientCompression = true,
        Action<WebSocketAcceptOptions>? configure = null
    )
    {
        var server = await TestServer.StartAsync(app => app.MapGet("/ws", async ctx =>
        {
            var options = new WebSocketAcceptOptions { KeepAliveInterval = null };
            configure?.Invoke(options);

            await using var socket = await ctx.AcceptWebSocketAsync(options, ctx.RequestAborted);
            await handle(socket);
        }));

        var client = new ClientWebSocket();

        if (clientCompression)
            client.Options.DangerousDeflateOptions = new WebSocketDeflateOptions();

        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/ws"), TestContext.Current.CancellationToken);

        return (server, client);
    }

    static async Task<string> ReceiveTextAsync(ClientWebSocket client, CancellationToken cancellationToken)
    {
        var buffer = new byte[256 * 1024];
        var received = new List<byte>();

        while (true)
        {
            var result = await client.ReceiveAsync(buffer, cancellationToken);
            received.AddRange(buffer.AsSpan(0, result.Count).ToArray());

            if (result.EndOfMessage)
                return Encoding.UTF8.GetString(received.ToArray());
        }
    }

    [Fact]
    public async Task Negotiates_the_extension_when_the_client_offers_it()
    {
        var (server, client) = await ConnectAsync(async socket =>
        {
            await socket.SendAsync(socket.PerMessageDeflate ? "compressed" : "plain", CancellationToken.None);
            await socket.CloseAsync(cancellationToken: CancellationToken.None);
        });

        await using var _ = server;
        using var __ = client;

        Assert.Equal("compressed", await ReceiveTextAsync(client, Token));
    }

    [Fact]
    public async Task Stays_uncompressed_when_the_client_does_not_ask()
    {
        var (server, client) = await ConnectAsync(
            async socket =>
            {
                await socket.SendAsync(socket.PerMessageDeflate ? "compressed" : "plain", CancellationToken.None);
                await socket.CloseAsync(cancellationToken: CancellationToken.None);
            },
            clientCompression: false
        );

        await using var _ = server;
        using var __ = client;

        Assert.Equal("plain", await ReceiveTextAsync(client, Token));
    }

    [Fact]
    public async Task Stays_uncompressed_when_the_server_switched_it_off()
    {
        var (server, client) = await ConnectAsync(
            async socket =>
            {
                await socket.SendAsync(socket.PerMessageDeflate ? "compressed" : "plain", CancellationToken.None);
                await socket.CloseAsync(cancellationToken: CancellationToken.None);
            },
            configure: o => o.EnablePerMessageDeflate = false
        );

        await using var _ = server;
        using var __ = client;

        Assert.Equal("plain", await ReceiveTextAsync(client, Token));
    }

    /// <summary>The payload that makes compression worth having: repetitive JSON, both directions.</summary>
    [Fact]
    public async Task Round_trips_a_large_compressible_message()
    {
        var payload = string.Concat(Enumerable.Repeat("{\"device\":\"thermostat\",\"reading\":21.5},", 2000));

        var (server, client) = await ConnectAsync(async socket =>
        {
            var message = await socket.ReceiveAsync(CancellationToken.None);
            await socket.SendAsync(message!.Text, CancellationToken.None);
            await socket.CloseAsync(cancellationToken: CancellationToken.None);
        });

        await using var _ = server;
        using var __ = client;

        await client.SendAsync(Encoding.UTF8.GetBytes(payload), WebSocketMessageType.Text, true, Token);

        Assert.Equal(payload, await ReceiveTextAsync(client, Token));
    }

    [Fact]
    public async Task A_message_below_the_threshold_is_sent_as_is()
    {
        var (server, client) = await ConnectAsync(
            async socket =>
            {
                await socket.SendAsync("tiny", CancellationToken.None);
                await socket.CloseAsync(cancellationToken: CancellationToken.None);
            },
            configure: o => o.CompressionThreshold = 1024
        );

        await using var _ = server;
        using var __ = client;

        // The client decompresses either way, so what this proves is that an uncompressed frame on
        // a compressed socket is still framed correctly — RSV1 clear, no sync tail.
        Assert.Equal("tiny", await ReceiveTextAsync(client, Token));
    }

    [Fact]
    public void Negotiation_answers_with_no_context_takeover_both_ways()
    {
        var answer = Shiny.Net.HttpServer.WebSockets.PerMessageDeflate.Negotiate("permessage-deflate; client_max_window_bits");

        Assert.NotNull(answer);
        Assert.Contains("server_no_context_takeover", answer);
        Assert.Contains("client_no_context_takeover", answer);
    }

    [Fact]
    public void An_unknown_parameter_is_skipped_rather_than_accepted()
    {
        Assert.Null(Shiny.Net.HttpServer.WebSockets.PerMessageDeflate.Negotiate("permessage-deflate; something_else=1"));
        Assert.Null(Shiny.Net.HttpServer.WebSockets.PerMessageDeflate.Negotiate("some-other-extension"));
        Assert.Null(Shiny.Net.HttpServer.WebSockets.PerMessageDeflate.Negotiate(null));
    }

    [Fact]
    public void The_offer_falls_back_to_an_acceptable_alternative()
    {
        var answer = Shiny.Net.HttpServer.WebSockets.PerMessageDeflate.Negotiate(
            "permessage-deflate; unsupported_param, permessage-deflate"
        );

        Assert.NotNull(answer);
    }
}

public class WebSocketRegistryTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    static Task<TestServer> Hub(IWebSocketRegistry registry) => TestServer.StartAsync(app => app.MapGet("/ws", async ctx =>
    {
        await using var tracked = await ctx.AcceptTrackedWebSocketAsync(
            registry,
            new WebSocketAcceptOptions { KeepAliveInterval = null, EnablePerMessageDeflate = false },
            ctx.RequestAborted
        );

        if (ctx.Request.Query["group"].ToString() is { Length: > 0 } group)
            tracked.JoinGroup(group);

        // Held open until the client goes away, which is what makes it broadcastable.
        while (await tracked.ReceiveAsync(ctx.RequestAborted) is not null)
        {
        }
    }));

    static async Task<ClientWebSocket> ConnectAsync(TestServer server, string? group = null)
    {
        var client = new ClientWebSocket();
        var query = group is null ? "" : "?group=" + group;

        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/ws{query}"), TestContext.Current.CancellationToken);

        return client;
    }

    static async Task<string> ReceiveTextAsync(ClientWebSocket client, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var result = await client.ReceiveAsync(buffer, cancellationToken);

        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }

    static async Task WaitForCountAsync(IWebSocketRegistry registry, int count, CancellationToken cancellationToken)
    {
        while (registry.Count < count)
            await Task.Delay(10, cancellationToken);
    }

    [Fact]
    public async Task Broadcasts_to_every_connected_socket()
    {
        var registry = new WebSocketRegistry();
        await using var server = await Hub(registry);

        using var one = await ConnectAsync(server);
        using var two = await ConnectAsync(server);

        await WaitForCountAsync(registry, 2, Token);

        Assert.Equal(2, await registry.BroadcastAsync("the door unlocked", cancellationToken: Token));
        Assert.Equal("the door unlocked", await ReceiveTextAsync(one, Token));
        Assert.Equal("the door unlocked", await ReceiveTextAsync(two, Token));
    }

    [Fact]
    public async Task A_group_message_reaches_only_that_group()
    {
        var registry = new WebSocketRegistry();
        await using var server = await Hub(registry);

        using var kitchen = await ConnectAsync(server, "kitchen");
        using var garage = await ConnectAsync(server, "garage");

        await WaitForCountAsync(registry, 2, Token);

        Assert.Equal(1, await registry.SendToGroupAsync("kitchen", "oven is hot", Token));
        Assert.Equal("oven is hot", await ReceiveTextAsync(kitchen, Token));

        // The other socket gets the next broadcast, proving it was skipped rather than closed.
        await registry.BroadcastAsync("everyone", cancellationToken: Token);
        Assert.Equal("everyone", await ReceiveTextAsync(garage, Token));
    }

    [Fact]
    public async Task A_socket_can_be_addressed_by_id()
    {
        var registry = new WebSocketRegistry();
        await using var server = await Hub(registry);

        using var client = await ConnectAsync(server);
        await WaitForCountAsync(registry, 1, Token);

        var id = registry.Connections[0].Id;

        Assert.True(await registry.SendToAsync(id, "just you", Token));
        Assert.Equal("just you", await ReceiveTextAsync(client, Token));
        Assert.False(await registry.SendToAsync("nobody", "hello", Token));
    }

    [Fact]
    public async Task A_closed_socket_stops_being_tracked()
    {
        var registry = new WebSocketRegistry();
        await using var server = await Hub(registry);

        using (var client = await ConnectAsync(server))
        {
            await WaitForCountAsync(registry, 1, Token);
            await client.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, null, Token);
        }

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (registry.Count > 0 && DateTime.UtcNow < deadline)
            await Task.Delay(20, Token);

        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task Closing_them_all_disconnects_every_client()
    {
        var registry = new WebSocketRegistry();
        await using var server = await Hub(registry);

        using var client = await ConnectAsync(server);
        await WaitForCountAsync(registry, 1, Token);

        await registry.CloseAllAsync(cancellationToken: Token);

        var buffer = new byte[64];
        var result = await client.ReceiveAsync(buffer, Token);

        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        Assert.Equal(0, registry.Count);
    }
}
