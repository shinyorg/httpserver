using System.Net;
using System.Net.Sockets;
using Shiny.Net.HttpServer.Transports;

namespace Shiny.Net.HttpServer.Tests;

/// <summary>
/// The failures nobody is standing next to. Everything here is a way for a server to stop serving
/// without anyone being told — the exact complaint these cover is an app whose toggle reads "on"
/// while the port refuses connections, and whose logs say nothing at all.
/// </summary>
public class ResilienceTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    static HttpServer Create(Action<HttpServerOptions>? configure = null)
    {
        var options = new HttpServerOptions { Port = 0, Address = IPAddress.Loopback };
        configure?.Invoke(options);

        var server = new HttpServer(options);
        server.MapGet("/ping", ctx => ctx.Response.WriteAsync("pong"));

        return server;
    }

    static async Task<string> GetAsync(HttpServer server)
    {
        using var client = new HttpClient { BaseAddress = new Uri(server.ListenUrl!) };
        return await client.GetStringAsync("/ping", TestContext.Current.CancellationToken);
    }

    /// <summary>Polls rather than sleeping a fixed amount: these transitions are driven off the thread pool.</summary>
    static async Task WaitFor(Func<bool> condition, string what, int timeoutMs = 10_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;

        while (Environment.TickCount64 < deadline)
        {
            if (condition())
                return;

            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        Assert.Fail($"Timed out waiting for {what}");
    }

    // ---- A listener that dies underneath a running server ----

    [Fact]
    public async Task A_dead_listener_is_reported_rather_than_ignored()
    {
        // The top suspect behind "the server seems to shut down randomly": AcceptAsync returned null
        // because the socket had gone, and the loop simply returned. No log, no transition, and a
        // server that went on answering IsRunning with true forever.
        await using var server = Create(o => o.RecoverFromListenerFaults = false);

        server.ListenerFactory = (_, _, _) => new ScriptedListener("http://dead", (attempt, ct) =>
            attempt == 1 ? new ValueTask<IConnection?>((IConnection?)null) : Never(ct)
        );

        var changes = new List<HttpServerStateChange>();
        server.StateTransitioned += (_, change) =>
        {
            lock (changes)
                changes.Add(change);
        };

        await server.StartAsync(Token);
        await WaitFor(() => server.State == HttpServerState.Stopped, "the server to report itself stopped");

        Assert.False(server.IsRunning);
        Assert.Equal(HttpServerStateReason.ListenerFaulted, server.LastStateChange!.Reason);
        Assert.IsType<HttpServerListenerException>(server.LastStateChange.Exception);

        // Not merely present on the final transition — carried by every transition the fault caused,
        // so a consumer watching for Stopping learns why at the same moment the server does.
        lock (changes)
        {
            Assert.Contains(changes, x => x is { State: HttpServerState.Stopping, Reason: HttpServerStateReason.ListenerFaulted });
        }
    }

    [Fact]
    public async Task A_dead_listener_is_rebound_by_default()
    {
        await using var server = Create();

        var built = 0;
        server.ListenerFactory = (_, _, _) =>
        {
            // The first listener dies immediately; the replacement the recovery builds is healthy.
            var generation = Interlocked.Increment(ref built);

            return new ScriptedListener($"http://gen{generation}", (attempt, ct) =>
                generation == 1 && attempt == 1
                    ? new ValueTask<IConnection?>((IConnection?)null)
                    : Never(ct)
            );
        };

        await server.StartAsync(Token);

        await WaitFor(
            () => server.State == HttpServerState.Running && server.LastStateChange?.Reason == HttpServerStateReason.ListenerFaulted,
            "the server to rebind after the listener died"
        );

        Assert.True(built >= 2);
        Assert.True(server.IsRunning);
    }

    [Fact]
    public async Task A_transient_accept_failure_recovers_without_stopping_the_server()
    {
        // A SocketException out of AcceptAsync used to escape the loop entirely and fault the
        // accept-loop task, which nothing observed until the finalizer thread got to it.
        await using var server = Create(o =>
        {
            o.AcceptRetryDelay = TimeSpan.FromMilliseconds(10);
            o.AcceptRetryMaxDelay = TimeSpan.FromMilliseconds(20);
        });

        var listener = new ScriptedListener("http://flaky", (attempt, ct) => attempt <= 3
            ? throw new SocketException((int)SocketError.ConnectionAborted)
            : Never(ct)
        );

        server.ListenerFactory = (_, _, _) => listener;

        await server.StartAsync(Token);
        await WaitFor(() => listener.AcceptCount > 3, "the accept loop to get past the transient failures");

        Assert.Equal(HttpServerState.Running, server.State);
        Assert.Equal(HttpServerStateReason.Requested, server.LastStateChange!.Reason);
        Assert.Null(server.LastStateChange.Exception);
    }

    [Fact]
    public async Task An_accept_that_never_recovers_is_treated_as_a_dead_listener()
    {
        await using var server = Create(o =>
        {
            o.RecoverFromListenerFaults = false;
            o.AcceptRetryAttempts = 2;
            o.AcceptRetryDelay = TimeSpan.FromMilliseconds(10);
        });

        server.ListenerFactory = (_, _, _) => new ScriptedListener(
            "http://broken",
            (_, _) => throw new SocketException((int)SocketError.TooManyOpenSockets)
        );

        await server.StartAsync(Token);
        await WaitFor(() => server.State == HttpServerState.Stopped, "the server to give up on the listener");

        Assert.Equal(HttpServerStateReason.ListenerFaulted, server.LastStateChange!.Reason);

        // The socket error is kept as the inner exception rather than replaced: "the listener is
        // dead" is useless without "because the process ran out of sockets".
        var fault = Assert.IsType<HttpServerListenerException>(server.LastStateChange.Exception);
        Assert.IsType<SocketException>(fault.InnerException);
    }

    // ---- A restart whose start half fails ----

    [Fact]
    public async Task A_restart_retries_a_start_that_the_port_refuses()
    {
        // The restart case the network-change path hits on a real device: the stop succeeds, and the
        // bind that follows is refused because something still holds the port. One attempt and the
        // server is stopped permanently.
        await using var server = Create(o =>
        {
            o.StartRetryAttempts = 40;
            o.StartRetryDelay = TimeSpan.FromMilliseconds(50);
            o.StartRetryMaxDelay = TimeSpan.FromMilliseconds(50);
        });

        await server.StartAsync(Token);

        // Pin the server to a fixed port, then take that port before restarting: the first binds
        // must fail, and the retry must still be going when the port is handed back.
        var port = GetFreePort();
        server.Options.Port = port;

        var occupier = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        occupier.Bind(new IPEndPoint(IPAddress.Loopback, port));
        occupier.Listen(1);

        var restart = server.RestartAsync(Token);

        await WaitFor(() => server.State == HttpServerState.Starting, "the restart to reach its start half");
        await Task.Delay(150, Token);

        Assert.Equal(HttpServerState.Starting, server.State);

        occupier.Dispose();
        await restart;

        Assert.True(server.IsRunning);
        Assert.Equal(HttpServerStateReason.Restarting, server.LastStateChange!.Reason);
        Assert.Equal("pong", await GetAsync(server));
    }

    [Fact]
    public async Task A_restart_that_cannot_bind_at_all_says_so_unmistakably()
    {
        await using var server = Create(o =>
        {
            o.StartRetryAttempts = 3;
            o.StartRetryDelay = TimeSpan.FromMilliseconds(20);
            o.StartRetryMaxDelay = TimeSpan.FromMilliseconds(20);
        });

        await server.StartAsync(Token);

        var port = GetFreePort();
        server.Options.Port = port;

        using var occupier = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        occupier.Bind(new IPEndPoint(IPAddress.Loopback, port));
        occupier.Listen(1);

        await Assert.ThrowsAsync<IOException>(async () => await server.RestartAsync(Token));

        Assert.Equal(HttpServerState.Stopped, server.State);
        Assert.Equal(HttpServerStateReason.BindFailed, server.LastStateChange!.Reason);
        Assert.IsType<IOException>(server.LastStateChange.Exception);
    }

    [Fact]
    public async Task A_start_the_app_asked_for_is_not_retried()
    {
        // Deliberate asymmetry, and worth pinning down: the caller of StartAsync gets the failure
        // immediately and decides for itself. Retrying behind a toggle only makes it look stuck.
        await using var occupier = Create();
        await occupier.StartAsync(Token);

        var port = new Uri(occupier.ListenUrl!).Port;

        await using var contender = Create(o =>
        {
            o.Port = port;
            o.StartRetryAttempts = 20;
            o.StartRetryDelay = TimeSpan.FromSeconds(5);
        });

        var started = Environment.TickCount64;
        await Assert.ThrowsAsync<IOException>(async () => await contender.StartAsync(Token));

        Assert.True(Environment.TickCount64 - started < 5_000, "StartAsync retried a start the caller asked for");
        Assert.Equal(HttpServerStateReason.BindFailed, contender.LastStateChange!.Reason);
    }

    // ---- Handlers ----

    [Fact]
    public async Task A_StateChanged_handler_that_throws_does_not_take_the_server_down()
    {
        // This one was a live bug rather than a hypothetical: SetState was invoked from inside
        // StartCoreAsync's try, so a UI handler that threw was caught by the start's own catch,
        // which unwound the listeners and reported a failure for a start that had already succeeded.
        await using var server = Create();

        server.StateChanged += (_, _) => throw new InvalidOperationException("a handler drawing a button threw");
        server.StateTransitioned += (_, _) => throw new InvalidOperationException("and so did the other one");

        var reached = new List<HttpServerState>();
        server.StateChanged += (_, state) => reached.Add(state);

        await server.StartAsync(Token);

        Assert.True(server.IsRunning);
        Assert.Equal("pong", await GetAsync(server));

        // The second handler still ran, so one bad subscriber does not silence the rest.
        Assert.Contains(HttpServerState.Running, reached);

        await server.StopAsync(Token);
        Assert.Equal(HttpServerState.Stopped, server.State);
    }

    // ---- Reasons ----

    [Fact]
    public async Task Every_transition_carries_why_it_happened()
    {
        await using var server = Create();

        var changes = new List<HttpServerStateChange>();
        server.StateTransitioned += (_, change) => changes.Add(change);

        Assert.Null(server.LastStateChange);

        await server.StartAsync(Token);
        await server.StopAsync(Token);

        Assert.Equal(
            [HttpServerState.Starting, HttpServerState.Running, HttpServerState.Stopping, HttpServerState.Stopped],
            changes.Select(x => x.State)
        );
        Assert.All(changes, x => Assert.Equal(HttpServerStateReason.Requested, x.Reason));
        Assert.All(changes, x => Assert.Null(x.Exception));

        Assert.Equal(new HttpServerStateChange(HttpServerState.Stopped, HttpServerStateReason.Requested), server.LastStateChange);
    }

    [Fact]
    public async Task A_restart_marks_its_stop_half_as_a_restart()
    {
        // What lets a subscriber leave its notification, mDNS advertisement or UI alone across a
        // rebind instead of tearing it down and rebuilding it a moment later.
        await using var server = Create();
        await server.StartAsync(Token);

        var changes = new List<HttpServerStateChange>();
        server.StateTransitioned += (_, change) => changes.Add(change);

        await server.RestartAsync(Token);

        Assert.All(changes, x => Assert.Equal(HttpServerStateReason.Restarting, x.Reason));
        Assert.Contains(changes, x => x.State == HttpServerState.Stopped);
        Assert.Contains(changes, x => x.State == HttpServerState.Running);
    }

    [Fact]
    public async Task Disposal_is_reported_as_disposal()
    {
        var server = Create();
        await server.StartAsync(Token);
        await server.DisposeAsync();

        Assert.Equal(HttpServerState.Stopped, server.State);
        Assert.Equal(HttpServerStateReason.Disposed, server.LastStateChange!.Reason);
    }

    static int GetFreePort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }

    /// <summary>An accept that never completes — the healthy idle listener, as far as the loop can tell.</summary>
    static async ValueTask<IConnection?> Never(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// A listener whose every accept is decided by the test. There is no way to make a real socket
    /// listener abort, vanish, or fail to accept on demand, and those are precisely the behaviours
    /// the accept loop now exists to survive.
    /// </summary>
    sealed class ScriptedListener(string description, Func<int, CancellationToken, ValueTask<IConnection?>> onAccept)
        : IConnectionListener
    {
        int accepts;

        public string ListenDescription => description;

        /// <summary>How many times the loop has come back for another connection.</summary>
        public int AcceptCount => Volatile.Read(ref this.accepts);

        public ValueTask BindAsync(CancellationToken cancellationToken = default) => default;

        public ValueTask<IConnection?> AcceptAsync(CancellationToken cancellationToken = default)
            => onAccept(Interlocked.Increment(ref this.accepts), cancellationToken);

        public ValueTask UnbindAsync(CancellationToken cancellationToken = default) => default;

        public ValueTask DisposeAsync() => default;
    }
}
