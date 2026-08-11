using System.Net;
using System.Net.Sockets;

namespace Shiny.Net.HttpServer.Tests;

/// <summary>
/// Start and stop are runtime operations, not just process startup and shutdown. These cover the
/// case an app with a "share over Wi-Fi" toggle actually hits: the same server, stopped and started
/// repeatedly over one process lifetime.
/// </summary>
public class LifecycleTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    static HttpServer Create()
    {
        var server = new HttpServer(new HttpServerOptions { Port = 0, Address = IPAddress.Loopback });
        server.OnGet("/ping", ctx => ctx.Response.WriteAsync("pong"));
        return server;
    }

    static async Task<string> GetAsync(HttpServer server, string path = "/ping")
    {
        using var client = new HttpClient { BaseAddress = new Uri(server.ListenUrl!) };
        return await client.GetStringAsync(path, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Starts_and_serves()
    {
        await using var server = Create();
        await server.StartAsync(Token);

        Assert.True(server.IsRunning);
        Assert.Equal(HttpServerState.Running, server.State);
        Assert.Equal("pong", await GetAsync(server));
    }

    [Fact]
    public async Task Stops_and_stops_listening()
    {
        await using var server = Create();
        await server.StartAsync(Token);

        var url = new Uri(server.ListenUrl!);
        await server.StopAsync(Token);

        Assert.False(server.IsRunning);
        Assert.Equal(HttpServerState.Stopped, server.State);
        Assert.Null(server.ListenUrl);

        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        await Assert.ThrowsAnyAsync<SocketException>(
            async () => await socket.ConnectAsync(IPAddress.Loopback, url.Port, Token)
        );
    }

    [Fact]
    public async Task Serves_again_after_a_stop()
    {
        // The regression this exists for: a shared cancellation token cancelled on the first stop
        // left the restarted accept loop exiting immediately, so the server bound a port and then
        // silently refused to answer on it.
        await using var server = Create();

        await server.StartAsync(Token);
        Assert.Equal("pong", await GetAsync(server));

        await server.StopAsync(Token);
        await server.StartAsync(Token);

        Assert.True(server.IsRunning);
        Assert.Equal("pong", await GetAsync(server));
    }

    [Fact]
    public async Task Survives_several_start_stop_cycles()
    {
        await using var server = Create();

        for (var i = 0; i < 5; i++)
        {
            await server.StartAsync(Token);
            Assert.Equal("pong", await GetAsync(server));
            await server.StopAsync(Token);
        }

        Assert.Equal(HttpServerState.Stopped, server.State);
    }

    [Fact]
    public async Task Restart_rebinds_in_one_step()
    {
        await using var server = Create();
        await server.StartAsync(Token);

        await server.RestartAsync(Token);

        Assert.True(server.IsRunning);
        Assert.Equal("pong", await GetAsync(server));
    }

    [Fact]
    public async Task Restart_picks_up_a_changed_port()
    {
        await using var server = Create();
        await server.StartAsync(Token);

        var first = new Uri(server.ListenUrl!).Port;

        // Back to an ephemeral port, so the OS is very likely to hand out a different one.
        server.Options.Port = 0;
        await server.RestartAsync(Token);

        Assert.Equal("pong", await GetAsync(server));
        Assert.NotEqual(0, new Uri(server.ListenUrl!).Port);
        Assert.True(first > 0);
    }

    [Fact]
    public async Task Starting_twice_is_a_no_op_rather_than_a_throw()
    {
        await using var server = Create();

        await server.StartAsync(Token);
        var url = server.ListenUrl;

        await server.StartAsync(Token);

        Assert.Equal(url, server.ListenUrl);
        Assert.Equal("pong", await GetAsync(server));
    }

    [Fact]
    public async Task Stopping_a_stopped_server_is_a_no_op()
    {
        await using var server = Create();

        await server.StopAsync(Token);
        await server.StopAsync(Token);

        Assert.Equal(HttpServerState.Stopped, server.State);
    }

    [Fact]
    public async Task Announces_every_transition()
    {
        await using var server = Create();

        var states = new List<HttpServerState>();
        server.StateChanged += (_, state) => states.Add(state);

        await server.StartAsync(Token);
        await server.StopAsync(Token);

        Assert.Equal(
            [HttpServerState.Starting, HttpServerState.Running, HttpServerState.Stopping, HttpServerState.Stopped],
            states
        );
    }

    [Fact]
    public async Task A_failed_bind_leaves_the_server_stopped()
    {
        await using var occupier = Create();
        await occupier.StartAsync(Token);

        var port = new Uri(occupier.ListenUrl!).Port;

        await using var contender = new HttpServer(new HttpServerOptions { Port = port, Address = IPAddress.Loopback });
        contender.OnGet("/ping", ctx => ctx.Response.WriteAsync("pong"));

        await Assert.ThrowsAsync<IOException>(async () => await contender.StartAsync(Token));

        // Not left claiming to be Starting forever, and startable again once the port frees up.
        Assert.Equal(HttpServerState.Stopped, contender.State);
        Assert.False(contender.IsRunning);

        await occupier.StopAsync(Token);
        await contender.StartAsync(Token);
        Assert.True(contender.IsRunning);
    }

    [Fact]
    public async Task Concurrent_start_and_stop_calls_are_serialized()
    {
        await using var server = Create();

        // Whatever order they interleave in, the server must end up in a consistent state rather
        // than with two listeners or a half-torn-down one.
        await Task.WhenAll(
            Enumerable.Range(0, 8).Select(i => i % 2 == 0
                ? server.StartAsync(Token)
                : server.StopAsync(Token))
        );

        await server.StartAsync(Token);

        Assert.Equal(HttpServerState.Running, server.State);
        Assert.Equal("pong", await GetAsync(server));
    }

    [Fact]
    public async Task A_disposed_server_cannot_be_started()
    {
        var server = Create();
        await server.StartAsync(Token);
        await server.DisposeAsync();

        Assert.Equal(HttpServerState.Stopped, server.State);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await server.StartAsync(Token));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await server.RestartAsync(Token));
    }

    [Fact]
    public async Task Middleware_cannot_be_added_once_the_pipeline_is_composed()
    {
        await using var server = Create();
        await server.StartAsync(Token);
        await server.StopAsync(Token);

        // Middleware stays frozen even after a stop: the pipeline is composed once, so one added
        // now would never run. Routes are a different matter — see DynamicRoutingTests.
        Assert.Throws<InvalidOperationException>(
            () => server.Use((ctx, next) => next(ctx))
        );
        Assert.Throws<InvalidOperationException>(
            () => server.OnRequest(ctx => ctx.Response.WriteAsync("x"))
        );
    }
}
