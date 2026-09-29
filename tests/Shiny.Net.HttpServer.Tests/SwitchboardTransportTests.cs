using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Shiny.Net.HttpServer.Switchboard;
using Shiny.Net.HttpServer.Switchboard.Client;
using SwitchboardFixtures;

namespace Shiny.Net.HttpServer.Tests;

/// <summary>
/// The switchboard end to end over the transports it has to survive: a tunnel, HTTP/2 on a real
/// socket, and the server itself going away and coming back.
/// </summary>
public class SwitchboardTransportTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    static void Configure(ShinyHttpServerBuilder builder)
    {
        builder.Services.AddSingleton<LifecycleProbe>();
        builder.AddSwitchboard(o => o.HeartbeatInterval = TimeSpan.FromSeconds(30));
    }

    static async Task<T> NextAsync<T>(ChannelReader<T> reader)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        return await reader.ReadAsync(timeout.Token);
    }

    /// <summary>
    /// Through the relay tunnel, every hop real. The point is incremental delivery: a tunnel that
    /// buffered the response would deliver the connected event only when the line closed.
    /// </summary>
    [Fact]
    public async Task Works_through_a_relay_tunnel()
    {
        await using var server = await TestServer.StartAsync(app => app.MapSwitchboard<TestBoard>("/board"), Configure);
        await using var tunnel = await RelayHarness.StartAsync(server.Server, "switchboard");

        await using var line = new SwitchboardLineBuilder()
            .WithUrl(new Uri(tunnel.Client.BaseAddress!, "/board"), o =>
            {
                o.HttpVersion = HttpVersion.Version11;
                o.Headers["Host"] = "switchboard.localhost";
            })
            .WithJson(SwitchboardFixtureJson.Default)
            .Build();

        var welcomes = Channel.CreateUnbounded<string>();
        line.On<string>("Welcome", id => welcomes.Writer.TryWrite(id));

        await line.StartAsync(Token).WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Equal(line.LineId, await NextAsync(welcomes.Reader));

        Assert.Equal(5, await line.InvokeAsync<int>("Add", 2, 3));

        var items = new List<int>();
        await foreach (var item in line.StreamAsync<int>("Count", 3).WithCancellation(Token))
            items.Add(item);
        Assert.Equal([1, 2, 3], items);
    }

    /// <summary>HTTP/2 over a real socket, the event stream and every call sharing one connection.</summary>
    [Fact]
    public async Task Works_over_http2_on_a_real_socket()
    {
        await using var server = await TestServer.StartAsync(
            app => app.MapSwitchboard<TestBoard>("/board"),
            b =>
            {
                b.Options.Http2.AllowCleartext = true;
                Configure(b);
            }
        );

        await using var line = new SwitchboardLineBuilder()
            .WithUrl(new Uri(server.Client.BaseAddress!, "/board"), o =>
            {
                o.HttpVersion = HttpVersion.Version20;
                o.HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;
            })
            .Build();

        var said = Channel.CreateUnbounded<string>();
        line.On<string, string>("Said", (_, text) => said.Writer.TryWrite(text));

        await line.StartAsync(Token);
        await line.InvokeAsync("Broadcast", "over h2");
        Assert.Equal("over h2", await NextAsync(said.Reader));

        var @operator = server.Server.Services!.GetRequiredService<IOperator<TestBoard>>();
        Assert.Equal("HTTP/2", @operator.Lines.Find(line.LineId!)!.Protocol);
    }

    /// <summary>
    /// A server that stops — a MAUI app going to the background — ends every stream with it. Lines
    /// are held for the resume window, so a client that comes back once the server restarts carries
    /// on with the same line, groups and all.
    /// </summary>
    [Fact]
    public async Task A_line_resumes_across_a_server_restart()
    {
        var port = FreePort();

        var builder = HttpServer.CreateBuilder();
        builder.Options.Address = IPAddress.Loopback;
        builder.Options.Port = port;
        Configure(builder);

        var server = builder.Build();
        server.MapSwitchboard<TestBoard>("/board");
        await server.StartAsync(Token);

        try
        {
            await using var line = new SwitchboardLineBuilder()
                .WithUrl($"http://127.0.0.1:{port}/board", o => o.HttpVersion = HttpVersion.Version11)
                .WithAutomaticReconnect([.. Enumerable.Repeat(TimeSpan.FromMilliseconds(200), 50)])
                .Build();

            var reconnected = Channel.CreateUnbounded<LineReconnectedEventArgs>();
            line.Reconnected += (_, e) => reconnected.Writer.TryWrite(e);
            var notices = Channel.CreateUnbounded<string>();
            line.On<string>("Notice", n => notices.Writer.TryWrite(n));

            await line.StartAsync(Token);
            var id = line.LineId!;
            await line.InvokeAsync("Join", "room");

            await server.StopAsync(Token);
            Assert.Equal(HttpServerState.Stopped, server.State);
            await server.StartAsync(Token);

            var e = await NextAsync(reconnected.Reader);
            Assert.False(e.NewLine);
            Assert.Equal(id, e.LineId);

            var @operator = server.Services!.GetRequiredService<IOperator<TestBoard>>();
            await @operator.Clients.Group("room").SendAsync("Notice", "welcome back");
            Assert.Equal("welcome back", await NextAsync(notices.Reader));
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task Records_line_and_call_metrics()
    {
        var seen = new System.Collections.Concurrent.ConcurrentBag<string>();

        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name.StartsWith("shiny.switchboard.", StringComparison.Ordinal))
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) => Record(instrument.Name, tags));
        listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) => Record(instrument.Name, tags));
        listener.Start();

        void Record(string name, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            foreach (var tag in tags)
            {
                // Other tests run in parallel on the same meter; only this test's switchboard counts.
                if (tag.Key == "switchboard" && (string?)tag.Value == nameof(SecureBoard))
                    seen.Add(name);
            }
        }

        await using var server = await TestServer.StartAsync(
            app =>
            {
                app.Use(async (ctx, next) =>
                {
                    ctx.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([new(System.Security.Claims.ClaimTypes.Name, "metrics")], "test"));
                    await next(ctx);
                });
                app.MapSwitchboard<SecureBoard>("/secure");
            },
            Configure
        );

        await using (var line = new SwitchboardLineBuilder().WithUrl(new Uri(server.Client.BaseAddress!, "/secure"), o => o.HttpVersion = HttpVersion.Version11).Build())
        {
            await line.StartAsync(Token);
            Assert.Equal("pong", await line.InvokeAsync<string>("Ping"));
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!seen.Contains("shiny.switchboard.lines.closed"))
            await Task.Delay(20, timeout.Token);

        Assert.Contains("shiny.switchboard.lines.opened", seen);
        Assert.Contains("shiny.switchboard.lines.active", seen);
        Assert.Contains("shiny.switchboard.invocation.duration", seen);
    }

    static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
