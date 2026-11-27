using System.Net;
using Shiny.Net.HttpServer.Caching;
using Shiny.Net.HttpServer.Http1;
using Shiny.Net.HttpServer.Sessions;

namespace Shiny.Net.HttpServer.Tests;

/// <summary>
/// Races found by review that a fast machine never loses, pinned down so they cannot come back. Each test forces
/// the losing interleaving directly rather than hoping a slow runner hits it.
/// </summary>
public class ConcurrencyRegressionTests
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- PipeOperationTimer: a callback that was already queued when the operation finished ----

    [Fact]
    public void A_timer_callback_that_arrives_after_Stop_does_not_expire_the_connection()
    {
        var cancelled = 0;
        using var timer = new PipeOperationTimer(() => Interlocked.Increment(ref cancelled));

        // Armed long enough that the real timer stays out of it; Fire below stands in for its callback.
        timer.Start(TimeSpan.FromMinutes(5));
        timer.Stop();

        // What a starved thread pool delivers: the callback, late, for an operation that already finished.
        timer.Fire();

        Assert.False(timer.Expired);
        Assert.Equal(0, cancelled);
    }

    [Fact]
    public void A_timer_callback_from_an_earlier_arming_does_not_cut_the_next_operation_short()
    {
        var cancelled = 0;
        using var timer = new PipeOperationTimer(() => Interlocked.Increment(ref cancelled));

        // The next operation is armed; a callback left over from the one before arrives well inside its deadline.
        timer.Start(TimeSpan.FromMinutes(5));
        timer.Stop();
        timer.Start(TimeSpan.FromMinutes(5));

        timer.Fire();

        Assert.False(timer.Expired);
        Assert.Equal(0, cancelled);
    }

    [Fact]
    public void A_timer_callback_after_dispose_does_not_throw_on_the_timer_thread()
    {
        var timer = new PipeOperationTimer(() => throw new ObjectDisposedException("pipe"));

        timer.Start(TimeSpan.FromMinutes(5));
        timer.Dispose();

        timer.Fire();

        Assert.False(timer.Expired);
    }

    [Fact]
    public void A_timer_whose_cancel_hits_a_completed_pipe_does_not_throw()
    {
        using var timer = new PipeOperationTimer(() => throw new ObjectDisposedException("pipe"));

        // The real timer fires this time, on its own thread, and its cancel throws: nothing may escape it.
        timer.Start(TimeSpan.FromMilliseconds(1));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!timer.Expired && DateTime.UtcNow < deadline)
            Thread.Sleep(5);

        Assert.True(timer.Expired);
    }

    // ---- MemoryOutputCacheStore: concurrent stores for one key ----

    [Fact]
    public async Task Concurrent_stores_for_one_key_keep_the_byte_count_exact()
    {
        var store = new MemoryOutputCacheStore(maxBytes: 1024 * 1024);
        var now = DateTimeOffset.UtcNow;

        await Parallel.ForAsync(0, 2000, Token, async (i, ct) =>
            await store.SetAsync("same", new OutputCacheEntry(200, [], new byte[100 + (i % 7)], now, now.AddMinutes(5)), ct));

        var stored = await store.GetAsync("same", Token);
        Assert.NotNull(stored);
        Assert.Equal(1, store.Count);
        Assert.Equal(stored.Body.Length, store.SizeInBytes);
    }

    // ---- InMemorySessionStore: one session, parallel requests ----

    [Fact]
    public async Task Parallel_loads_of_one_session_do_not_share_state()
    {
        var store = new InMemorySessionStore();
        var data = new SessionData();
        data.Values["a"] = [1];
        await store.SaveAsync("s", data, TimeSpan.FromMinutes(5), Token);

        var first = await store.LoadAsync("s", Token);
        var second = await store.LoadAsync("s", Token);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotSame(first, second);

        // An uncommitted write in one request is not visible to another...
        first.Values["b"] = [2];
        Assert.False(second.Values.ContainsKey("b"));

        // ...and changing the instance after saving it does not reach into the store.
        data.Values["c"] = [3];
        var third = await store.LoadAsync("s", Token);
        Assert.False(third!.Values.ContainsKey("c"));
    }

    [Fact]
    public async Task Parallel_requests_writing_one_session_do_not_corrupt_it()
    {
        var store = new InMemorySessionStore();
        await store.SaveAsync("s", new SessionData(), TimeSpan.FromMinutes(5), Token);

        await Parallel.ForAsync(0, 200, Token, async (i, ct) =>
        {
            var loaded = (await store.LoadAsync("s", ct))!;
            for (var k = 0; k < 50; k++)
                loaded.Values[$"key{k}"] = [(byte)i];

            await store.SaveAsync("s", loaded, TimeSpan.FromMinutes(5), ct);
        });

        var final = await store.LoadAsync("s", Token);
        Assert.Equal(50, final!.Values.Count);
    }

    // ---- A RequestAborted callback that throws belongs to the app, not the connection ----

    [Fact]
    public async Task A_throwing_RequestAborted_callback_does_not_reset_a_delivered_HTTP1_response()
    {
        await using var server = await TestServer.StartAsync(app =>
        {
            app.MapGet("/register", ctx =>
            {
                ctx.RequestAborted.Register(() => throw new InvalidOperationException("the app's own bug"));
                return ctx.Response.WriteAsync("delivered");
            });
            app.MapGet("/next", ctx => ctx.Response.WriteAsync("still here"));
        });

        // The token is cancelled as the request ends, which runs the callback; the response must survive it, and
        // so must the keep-alive connection the next request goes out on.
        Assert.Equal("delivered", await server.Client.GetStringAsync("/register", Token));
        Assert.Equal("still here", await server.Client.GetStringAsync("/next", Token));
    }

    [Fact]
    public async Task A_throwing_RequestAborted_callback_on_a_reset_HTTP2_stream_leaves_the_connection_alone()
    {
        var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var server = await TestServer.StartAsync(app =>
        {
            app.MapGet("/hang", async ctx =>
            {
                ctx.RequestAborted.Register(() => throw new InvalidOperationException("the app's own bug"));
                registered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, ctx.RequestAborted);
                }
                catch (OperationCanceledException)
                {
                }
            });
            app.MapGet("/ok", ctx => ctx.Response.WriteAsync("ok"));
        });

        await using var client = await Http2RawClient.ConnectAsync(server.Port);
        await client.HeadersAsync(1, "GET", "/hang", endStream: true);
        await registered.Task.WaitAsync(Wait, Token);

        // The reset cancels the stream on the read loop, where the callback runs inline.
        await client.ResetAsync(1);

        await client.HeadersAsync(3, "GET", "/ok", endStream: true);
        Assert.NotNull(await client.WaitForEndAsync(3, Wait));
        Assert.Null(client.Frames.FirstOrDefault(x => x.Type == Http2RawFrame.GoAway));
    }

    // ---- HttpContext.Abort on HTTP/2 is one stream's business ----

    [Fact]
    public async Task Abort_on_HTTP2_resets_only_its_own_stream()
    {
        var slowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var server = await TestServer.StartAsync(app =>
        {
            app.MapGet("/slow", async ctx =>
            {
                slowStarted.TrySetResult();
                await release.Task;
                await ctx.Response.WriteAsync("slow done");
            });
            app.MapGet("/abort", ctx =>
            {
                ctx.Abort();
                return ValueTask.CompletedTask;
            });
        });

        await using var client = await Http2RawClient.ConnectAsync(server.Port);
        await client.HeadersAsync(1, "GET", "/slow", endStream: true);
        await slowStarted.Task.WaitAsync(Wait, Token);

        await client.HeadersAsync(3, "GET", "/abort", endStream: true);
        var reset = await client.WaitForResetAsync(3, Wait);
        Assert.NotNull(reset);
        Assert.Equal(8u, reset.ErrorCode); // CANCEL

        // The request that was in flight alongside it finishes on the same connection.
        release.SetResult();
        Assert.NotNull(await client.WaitForEndAsync(1, Wait));
        Assert.Null(client.Frames.FirstOrDefault(x => x.Type == Http2RawFrame.GoAway));
    }
}
