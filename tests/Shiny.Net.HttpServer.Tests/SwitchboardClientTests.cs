using System.Collections.Concurrent;
using System.Security.Claims;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Shiny.Net.HttpServer.Switchboard;
using Shiny.Net.HttpServer.Switchboard.Client;
using Shiny.Net.HttpServer.Switchboard.Internal;
using SwitchboardFixtures;
using ClientException = Shiny.Net.HttpServer.Switchboard.Client.SwitchboardException;
using ServerException = Shiny.Net.HttpServer.Switchboard.SwitchboardException;

namespace Shiny.Net.HttpServer.Tests;

/// <summary>The .NET client against the real server, over real sockets.</summary>
public class SwitchboardClientTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    sealed class Harness : IAsyncDisposable
    {
        public required TestServer Server { get; init; }

        public IServiceProvider Services => this.Server.Server.Services!;

        public LifecycleProbe Probe => this.Services.GetRequiredService<LifecycleProbe>();

        public IOperator<TestBoard> Operator => this.Services.GetRequiredService<IOperator<TestBoard>>();

        public SwitchboardLine Line(string path = "/board", Action<SwitchboardLineBuilder>? configure = null, string? user = null)
        {
            var builder = new SwitchboardLineBuilder()
                .WithUrl(new Uri(this.Server.Client.BaseAddress!, path), o =>
                {
                    o.HttpVersion = System.Net.HttpVersion.Version11;
                    if (user is not null)
                        o.Headers["X-Test-User"] = user;
                })
                .WithJson(SwitchboardFixtureJson.Default);

            configure?.Invoke(builder);
            return builder.Build();
        }

        /// <summary>Cuts a line's stream on the server without closing the line — a network drop.</summary>
        public void Drop(string lineId)
        {
            var runtime = this.Services.GetRequiredService<SwitchboardRuntime>();
            runtime.GetBoard(SwitchboardRegistry.GetRequired(typeof(TestBoard))).FindLine(lineId)!.DropStream();
        }

        public ValueTask DisposeAsync() => this.Server.DisposeAsync();
    }

    static async Task<Harness> StartAsync(Action<SwitchboardOptions>? configure = null, Action<ShinyHttpServerBuilder>? builder = null)
    {
        var server = await TestServer.StartAsync(
            app =>
            {
                app.Use(async (ctx, next) =>
                {
                    if (ctx.Request.Headers.GetFirst("X-Test-User") is { Length: > 0 } user)
                        ctx.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, user)], "test"));

                    await next(ctx);
                });

                app.MapSwitchboard<TestBoard>("/board");
                app.MapSwitchboard<TypedBoard>("/typed");
            },
            b =>
            {
                b.Services.AddSingleton<LifecycleProbe>();
                b.AddSwitchboard(o =>
                {
                    o.HeartbeatInterval = TimeSpan.FromSeconds(30);
                    configure?.Invoke(o);
                });
                builder?.Invoke(b);
            }
        );

        return new Harness { Server = server };
    }

    /// <summary>Collects what arrives on a line, for asserting on later.</summary>
    static Channel<T> Collect<T>(out Action<T> add)
    {
        var channel = Channel.CreateUnbounded<T>();
        add = value => channel.Writer.TryWrite(value);
        return channel;
    }

    static async Task<T> NextAsync<T>(Channel<T> channel)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        return await channel.Reader.ReadAsync(timeout.Token);
    }

    // ---- basics ----

    [Fact]
    public async Task Connects_calls_and_receives()
    {
        await using var app = await StartAsync();
        await using var line = app.Line();

        var welcomes = Collect<string>(out var welcome);
        var said = Collect<(string From, string Text)>(out var say);
        line.On<string>("Welcome", welcome);
        line.On<string, string>("Said", (from, text) => say((from, text)));

        var states = new List<LineState>();
        line.StateChanged += (_, e) => states.Add(e.Current);

        await line.StartAsync(Token);

        Assert.Equal(LineState.Connected, line.State);
        Assert.Equal([LineState.Connecting, LineState.Connected], states);
        Assert.Equal(line.LineId, await NextAsync(welcomes));

        Assert.Equal(5, await line.InvokeAsync<int>("Add", 2, 3));
        Assert.Equal(new Note("n", 2), await line.InvokeAsync<Note>("Bump", new Note("n", 1)));
        Assert.Equal("hello world", await line.InvokeAsync<string>("greet"));
        await line.InvokeAsync("Nothing");

        await line.SendAsync("Broadcast", "hi");
        Assert.Equal((line.LineId!, "hi"), await NextAsync(said));
    }

    [Fact]
    public async Task Streams_items_as_the_server_yields_them()
    {
        await using var app = await StartAsync();
        await using var line = app.Line();
        await line.StartAsync(Token);

        var items = new List<int>();
        await foreach (var item in line.StreamAsync<int>("Count", 4).WithCancellation(Token))
            items.Add(item);

        Assert.Equal([1, 2, 3, 4], items);
    }

    [Fact]
    public async Task A_stream_does_not_hold_the_line()
    {
        await using var app = await StartAsync();
        await using var line = app.Line();
        await line.StartAsync(Token);

        await using var slow = line.StreamAsync<int>("Slow").GetAsyncEnumerator(Token);
        Assert.True(await slow.MoveNextAsync());

        // With one call at a time per line, this would wait for the stream to finish if the stream
        // kept the line to itself.
        Assert.Equal(3, await line.InvokeAsync<int>("Add", 1, 2).WaitAsync(TimeSpan.FromSeconds(5), Token));

        app.Probe.StreamGate.TrySetResult();
        Assert.True(await slow.MoveNextAsync());
        Assert.Equal(2, slow.Current);
    }

    [Fact]
    public async Task Errors_arrive_as_SwitchboardException()
    {
        await using var app = await StartAsync();
        await using var line = app.Line();
        await line.StartAsync(Token);

        var refused = await Assert.ThrowsAsync<ClientException>(() => line.InvokeAsync("Fail"));
        Assert.Equal("nope", refused.Message);
        Assert.Equal(400, refused.StatusCode);

        var missing = await Assert.ThrowsAsync<ClientException>(() => line.InvokeAsync("NoSuchMethod"));
        Assert.Equal(404, missing.StatusCode);

        var crash = await Assert.ThrowsAsync<ClientException>(() => line.InvokeAsync("Crash"));
        Assert.Equal(500, crash.StatusCode);
    }

    [Fact]
    public async Task Calls_run_in_the_order_they_were_made()
    {
        await using var app = await StartAsync();
        await using var line = app.Line();
        await line.StartAsync(Token);

        // Fired without waiting: separate requests, free to overtake each other on the way.
        await Task.WhenAll(Enumerable.Range(1, 25).Select(i => line.InvokeAsync("Record", i)));

        Assert.Equal(Enumerable.Range(1, 25), await line.InvokeAsync<int[]>("Recorded"));
    }

    [Fact]
    public async Task Calls_fail_while_not_connected()
    {
        await using var app = await StartAsync();
        await using var line = app.Line();

        await Assert.ThrowsAsync<InvalidOperationException>(() => line.InvokeAsync("Nothing"));
    }

    // ---- closing ----

    [Fact]
    public async Task Stopping_tells_the_server()
    {
        await using var app = await StartAsync();
        await using var line = app.Line();

        var closed = Collect<LineClosedEventArgs>(out var close);
        line.Closed += (_, e) => close(e);

        await line.StartAsync(Token);
        var id = line.LineId!;
        await line.StopAsync(Token);

        Assert.Equal(LineCloseReason.ClientClosed, (await NextAsync(closed)).Reason);
        Assert.Equal(LineState.Disconnected, line.State);
        await app.Probe.WaitForAsync(e => e.StartsWith($"disconnected:{id}:ClientClosed", StringComparison.Ordinal), Token);
    }

    [Fact]
    public async Task A_hang_up_closes_the_line_and_it_does_not_come_back()
    {
        await using var app = await StartAsync();
        await using var line = app.Line(configure: b => b.WithAutomaticReconnect(TimeSpan.Zero));

        var closed = Collect<LineClosedEventArgs>(out var close);
        line.Closed += (_, e) => close(e);
        var reconnects = 0;
        line.Reconnecting += (_, _) => Interlocked.Increment(ref reconnects);

        await line.StartAsync(Token);
        await app.Operator.Lines.HangUpAsync(line.LineId!, "Removed by a moderator");

        var e = await NextAsync(closed);
        Assert.Equal(LineCloseReason.HungUp, e.Reason);
        Assert.Equal("Removed by a moderator", e.Message);
        Assert.Equal(0, reconnects);
        Assert.Equal(LineState.Disconnected, line.State);
    }

    [Fact]
    public async Task A_line_can_be_started_again_after_closing()
    {
        await using var app = await StartAsync();
        await using var line = app.Line();

        await line.StartAsync(Token);
        var first = line.LineId;
        await line.StopAsync(Token);

        await line.StartAsync(Token);
        Assert.NotEqual(first, line.LineId);
        Assert.Equal(3, await line.InvokeAsync<int>("Add", 1, 2));
    }

    // ---- dropping and resuming ----

    [Fact]
    public async Task A_drop_resumes_the_same_line_with_what_it_missed()
    {
        await using var app = await StartAsync();
        await using var line = app.Line(configure: b => b.WithAutomaticReconnect(TimeSpan.FromMilliseconds(300)));

        var reconnecting = Collect<LineReconnectingEventArgs>(out var onReconnecting);
        line.Reconnecting += (_, e) => onReconnecting(e);
        var reconnected = Collect<LineReconnectedEventArgs>(out var onReconnected);
        line.Reconnected += (_, e) => onReconnected(e);
        var notices = Collect<string>(out var notice);
        line.On<string>("Notice", notice);

        await line.StartAsync(Token);
        var id = line.LineId!;
        await line.InvokeAsync("Join", "room");

        app.Drop(id);
        await NextAsync(reconnecting);     // the client has noticed, and waits 300 ms before resuming
        await app.Operator.Clients.Group("room").SendAsync("Notice", "while you were out");

        var e = await NextAsync(reconnected);
        Assert.False(e.NewLine);
        Assert.Equal(id, e.LineId);
        Assert.Equal("while you were out", await NextAsync(notices));

        // Groups and Items survived.
        await app.Operator.Clients.Group("room").SendAsync("Notice", "still in the room");
        Assert.Equal("still in the room", await NextAsync(notices));
        await app.Probe.WaitForAsync(x => x == $"reconnected:{id}", Token);
    }

    [Fact]
    public async Task A_line_the_server_forgot_comes_back_as_a_new_one()
    {
        await using var app = await StartAsync(o => o.ResumeWindow = TimeSpan.FromMilliseconds(100));
        await using var line = app.Line(configure: b => b.WithAutomaticReconnect(TimeSpan.FromMilliseconds(600)));

        var reconnected = Collect<LineReconnectedEventArgs>(out var onReconnected);
        line.Reconnected += (_, e) => onReconnected(e);

        await line.StartAsync(Token);
        var id = line.LineId!;
        app.Drop(id);

        var e = await NextAsync(reconnected);
        Assert.True(e.NewLine);
        Assert.NotEqual(id, e.LineId);
        Assert.Equal(e.LineId, line.LineId);
        Assert.Equal(3, await line.InvokeAsync<int>("Add", 1, 2));
    }

    [Fact]
    public async Task A_server_close_that_allows_reconnect_reconnects_as_a_new_line()
    {
        await using var app = await StartAsync();
        await using var line = app.Line(configure: b => b.WithAutomaticReconnect(TimeSpan.Zero));

        var reconnected = Collect<LineReconnectedEventArgs>(out var onReconnected);
        line.Reconnected += (_, e) => onReconnected(e);

        await line.StartAsync(Token);
        var id = line.LineId!;
        await app.Operator.Lines.HangUpAllAsync("restarting", allowReconnect: true);

        var e = await NextAsync(reconnected);
        Assert.True(e.NewLine);
        Assert.NotEqual(id, e.LineId);
    }

    [Fact]
    public async Task Without_automatic_reconnect_a_drop_closes_the_line()
    {
        await using var app = await StartAsync();
        await using var line = app.Line();

        var closed = Collect<LineClosedEventArgs>(out var close);
        line.Closed += (_, e) => close(e);

        await line.StartAsync(Token);
        app.Drop(line.LineId!);

        Assert.Equal(LineCloseReason.Dropped, (await NextAsync(closed)).Reason);
    }

    [Fact]
    public async Task A_silent_server_counts_as_a_drop()
    {
        await using var app = await StartAsync(o => o.HeartbeatInterval = TimeSpan.Zero);

        // A short dead-link timeout, against a server that sends nothing at all after the welcome.
        await using var quiet = new SwitchboardLineBuilder()
            .WithUrl(new Uri(app.Server.Client.BaseAddress!, "/board"), o =>
            {
                o.HttpVersion = System.Net.HttpVersion.Version11;
                o.ServerTimeout = TimeSpan.FromMilliseconds(300);
            })
            .WithAutomaticReconnect(TimeSpan.Zero)
            .Build();

        var quietReconnecting = Collect<LineReconnectingEventArgs>(out var onQuietReconnecting);
        quiet.Reconnecting += (_, e) => onQuietReconnecting(e);
        var quietReconnected = Collect<LineReconnectedEventArgs>(out var onQuietReconnected);
        quiet.Reconnected += (_, e) => onQuietReconnected(e);

        await quiet.StartAsync(Token);

        Assert.IsType<TimeoutException>((await NextAsync(quietReconnecting)).Exception);
        Assert.False((await NextAsync(quietReconnected)).NewLine);
    }

    // ---- client results ----

    [Fact]
    public async Task The_server_can_wait_for_the_clients_answer()
    {
        await using var app = await StartAsync();
        await using var line = app.Line();
        line.On<string, string>("Answer", q => Task.FromResult($"answer to {q}"));
        await line.StartAsync(Token);

        // Asked from inside a call, with one call at a time per line: the answer must not queue
        // behind the call that is waiting for it.
        Assert.Equal("answer to life", await line.InvokeAsync<string>("Ask", "life"));

        // And from outside any call.
        Assert.Equal("answer to x", await app.Operator.Clients.Line(line.LineId!).InvokeAsync<string>("Answer", ["x"], Token));
    }

    [Fact]
    public async Task A_client_without_an_answer_fails_the_call_at_once()
    {
        await using var app = await StartAsync();
        await using var line = app.Line();
        line.On<string>("Answer", _ => { });
        await line.StartAsync(Token);

        var error = await Assert.ThrowsAsync<ServerException>(
            () => app.Operator.Clients.Line(line.LineId!).InvokeAsync<string>("Answer", ["x"], Token).WaitAsync(TimeSpan.FromSeconds(5), Token));
        Assert.Contains("does not return a result", error.Message);

        var none = await Assert.ThrowsAsync<ServerException>(
            () => app.Operator.Clients.Line(line.LineId!).InvokeAsync<string>("Unhandled", [], Token).WaitAsync(TimeSpan.FromSeconds(5), Token));
        Assert.Contains("no handler", none.Message);
    }

    [Fact]
    public async Task A_failing_answer_reaches_the_server_as_its_message()
    {
        await using var app = await StartAsync();
        await using var line = app.Line();
        line.On<string, string>("Answer", _ => throw new InvalidOperationException("cannot say"));
        await line.StartAsync(Token);

        var error = await Assert.ThrowsAsync<ClientException>(() => line.InvokeAsync<string>("Ask", "q"));
        Assert.Equal("cannot say", error.Message);
    }

    [Fact]
    public async Task An_unanswered_question_times_out()
    {
        await using var app = await StartAsync(o => o.ClientResultTimeout = TimeSpan.FromMilliseconds(200));
        await using var line = app.Line();
        line.On<string, string>("Answer", async _ => { await Task.Delay(TimeSpan.FromSeconds(30), Token); return ""; });
        await line.StartAsync(Token);

        await Assert.ThrowsAsync<TimeoutException>(
            () => app.Operator.Clients.Line(line.LineId!).InvokeAsync<string>("Answer", ["q"], Token));
    }

    [Fact]
    public async Task A_hang_up_fails_the_questions_still_waiting()
    {
        await using var app = await StartAsync();
        await using var line = app.Line();
        var asked = new TaskCompletionSource();
        line.On<string, string>("Answer", async _ =>
        {
            asked.TrySetResult();
            await Task.Delay(TimeSpan.FromSeconds(30));
            return "";
        });
        await line.StartAsync(Token);

        var question = app.Operator.Clients.Line(line.LineId!).InvokeAsync<string>("Answer", ["q"], Token);
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        await app.Operator.Lines.HangUpAsync(line.LineId!);

        await Assert.ThrowsAsync<LineClosedException>(() => question.WaitAsync(TimeSpan.FromSeconds(5), Token));
    }

    [Fact]
    public async Task A_question_survives_a_drop_and_is_answered_once()
    {
        await using var app = await StartAsync();
        await using var line = app.Line(configure: b => b.WithAutomaticReconnect(TimeSpan.FromMilliseconds(200)));

        var answers = 0;
        line.On<string, string>("Answer", q =>
        {
            Interlocked.Increment(ref answers);
            return Task.FromResult("late " + q);
        });

        await line.StartAsync(Token);
        var id = line.LineId!;

        // Asked while the line has no stream: the question waits in the replay buffer.
        app.Drop(id);
        await WaitUntilAsync(() => app.Operator.Lines.Find(id)?.Status == LineStatus.Detached);
        var answer = app.Operator.Clients.Line(id).InvokeAsync<string>("Answer", ["q"], Token);

        Assert.Equal("late q", await answer.WaitAsync(TimeSpan.FromSeconds(10), Token));
        Assert.Equal(1, answers);
    }

    // ---- filters ----

    [Fact]
    public async Task Filters_wrap_calls_and_lifecycle_and_can_refuse()
    {
        await using var app = await StartAsync(builder: b => b.AddSwitchboardFilter<RecordingFilter>());
        await using var line = app.Line();
        await using var blocked = app.Line(user: "blocked");

        await line.StartAsync(Token);
        await app.Probe.WaitForAsync(e => e == $"filter:connected:{line.LineId}", Token);

        await line.InvokeAsync("Nothing");
        await app.Probe.WaitForAsync(e => e == "filter:before:Nothing", Token);
        await app.Probe.WaitForAsync(e => e == "filter:after:Nothing", Token);

        await blocked.StartAsync(Token);
        var refused = await Assert.ThrowsAsync<ClientException>(() => blocked.InvokeAsync("Nothing"));
        Assert.Equal("Blocked by a filter", refused.Message);
    }

    // ---- typed ----

    [Fact]
    public async Task The_typed_client_calls_receives_and_answers()
    {
        await using var app = await StartAsync();
        await using var typed = new TypedBoardClient(app.Line("/typed"));
        await using var other = new TypedBoardClient(app.Line("/typed"));

        var posted = Collect<(string From, string Text)>(out var post);
        typed.Posted += (from, text) => { post((from, text)); return Task.CompletedTask; };
        typed.Answer = q => Task.FromResult($"typed {q}");

        await typed.Line.StartAsync(Token);
        await other.Line.StartAsync(Token);

        Assert.Equal(7, await typed.Add(3, 4));

        var counted = new List<int>();
        await foreach (var i in typed.Count(3, Token))
            counted.Add(i);
        Assert.Equal([1, 2, 3], counted);

        await other.Post("from the other line");
        Assert.Equal((other.Line.LineId!, "from the other line"), await NextAsync(posted));

        Assert.Equal("typed q", await typed.AskCaller("q"));

        // A question to more than one line cannot be answered, and says so.
        var error = await Assert.ThrowsAsync<ClientException>(() => typed.AskEveryone("q"));
        Assert.Equal(500, error.StatusCode);
    }

    [Fact]
    public async Task Register_binds_a_whole_client_implementation()
    {
        await using var app = await StartAsync(o => o.EnableDetailedErrors = true);
        await using var typed = new TypedBoardClient(app.Line("/typed"));
        await typed.Line.StartAsync(Token);

        var receiver = new Receiver();
        using (typed.Register(receiver))
        {
            await typed.Post("hello");
            Assert.Equal("hello", await NextAsync(receiver.Posts));
            Assert.Equal("registered q", await typed.AskCaller("q"));
        }

        // Unregistered, with no property handler either: the server is told there is no answer.
        var error = await Assert.ThrowsAsync<ClientException>(() => typed.AskCaller("q"));
        Assert.Contains("No handler is set for 'Answer'", error.Message);
    }

    [Fact]
    public async Task The_typed_operator_calls_through_the_client_interface()
    {
        await using var app = await StartAsync();
        await using var typed = new TypedBoardClient(app.Line("/typed"));

        var pinged = new TaskCompletionSource();
        typed.Pinged += () => { pinged.TrySetResult(); return Task.CompletedTask; };
        typed.Answer = q => Task.FromResult("op " + q);
        await typed.Line.StartAsync(Token);

        var @operator = app.Services.GetRequiredService<IOperator<TypedBoard, ITypedClient>>();
        await @operator.Clients.All.Pinged();
        await pinged.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);

        Assert.Equal("op q", await @operator.Clients.Line(typed.Line.LineId!).Answer("q"));
        await Assert.ThrowsAsync<NotSupportedException>(() => @operator.Clients.All.Answer("q"));
    }

    sealed class Receiver : ITypedClient
    {
        public Channel<string> Posts { get; } = Channel.CreateUnbounded<string>();

        public Task Posted(string from, string text)
        {
            this.Posts.Writer.TryWrite(text);
            return Task.CompletedTask;
        }

        public Task Pinged() => Task.CompletedTask;

        public Task<string> Answer(string question) => Task.FromResult("registered " + question);
    }

    static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        while (!condition())
            await Task.Delay(10, timeout.Token);
    }
}
