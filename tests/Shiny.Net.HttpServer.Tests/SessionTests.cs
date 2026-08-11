using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Shiny.Net.HttpServer.Security;
using Shiny.Net.HttpServer.Sessions;

namespace Shiny.Net.HttpServer.Tests;

public class InMemorySessionStoreTests
{
    static SessionData Data(string key, string value)
        => new(new Dictionary<string, byte[]> { [key] = System.Text.Encoding.UTF8.GetBytes(value) });

    [Fact]
    public async Task Round_trips_a_session()
    {
        var store = new InMemorySessionStore();
        var token = TestContext.Current.CancellationToken;

        await store.SaveAsync("abc", Data("k", "v"), TimeSpan.FromMinutes(5), token);
        var loaded = await store.LoadAsync("abc", token);

        Assert.NotNull(loaded);
        Assert.Equal("v", System.Text.Encoding.UTF8.GetString(loaded.Values["k"]));
    }

    [Fact]
    public async Task Returns_nothing_for_an_unknown_session()
        => Assert.Null(await new InMemorySessionStore().LoadAsync("nope", TestContext.Current.CancellationToken));

    [Fact]
    public async Task Forgets_a_session_that_went_idle()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var store = new InMemorySessionStore(clock);
        var token = TestContext.Current.CancellationToken;

        await store.SaveAsync("abc", Data("k", "v"), TimeSpan.FromMinutes(20), token);

        clock.Advance(TimeSpan.FromMinutes(19));
        Assert.NotNull(await store.LoadAsync("abc", token));

        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Null(await store.LoadAsync("abc", token));
    }

    /// <summary>Activity has to restart the clock, or a session dies mid-use.</summary>
    [Fact]
    public async Task Refreshing_extends_the_idle_timeout()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var store = new InMemorySessionStore(clock);
        var token = TestContext.Current.CancellationToken;

        await store.SaveAsync("abc", Data("k", "v"), TimeSpan.FromMinutes(20), token);

        clock.Advance(TimeSpan.FromMinutes(15));
        await store.RefreshAsync("abc", TimeSpan.FromMinutes(20), token);

        clock.Advance(TimeSpan.FromMinutes(15));

        Assert.NotNull(await store.LoadAsync("abc", token));
    }

    [Fact]
    public async Task Removes_a_session()
    {
        var store = new InMemorySessionStore();
        var token = TestContext.Current.CancellationToken;

        await store.SaveAsync("abc", Data("k", "v"), TimeSpan.FromMinutes(5), token);
        await store.RemoveAsync("abc", token);

        Assert.Null(await store.LoadAsync("abc", token));
    }

    /// <summary>
    /// A session id comes from a cookie, so anyone can present a new one as often as they like —
    /// unbounded growth would be a memory exhaustion bug with a very easy trigger.
    /// </summary>
    [Fact]
    public async Task Evicts_when_it_is_over_capacity()
    {
        var store = new InMemorySessionStore { Capacity = 10 };
        var token = TestContext.Current.CancellationToken;

        for (var i = 0; i < 50; i++)
            await store.SaveAsync($"session-{i}", Data("k", "v"), TimeSpan.FromMinutes(5), token);

        Assert.True(store.Count <= 10, $"kept {store.Count} sessions with a capacity of 10");
    }

    [Fact]
    public async Task Prunes_expired_sessions()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var store = new InMemorySessionStore(clock);
        var token = TestContext.Current.CancellationToken;

        await store.SaveAsync("a", Data("k", "v"), TimeSpan.FromMinutes(1), token);
        await store.SaveAsync("b", Data("k", "v"), TimeSpan.FromMinutes(30), token);

        clock.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(1, store.Prune());
        Assert.Equal(1, store.Count);
    }
}

/// <summary>A clock the tests move by hand, so an idle timeout does not need a real wait.</summary>
sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
{
    DateTimeOffset now = start;

    public override DateTimeOffset GetUtcNow() => this.now;

    public void Advance(TimeSpan by) => this.now += by;
}

public class SessionTests
{
    static readonly byte[] Key = TicketProtector.CreateKey();

    static Task<TestServer> StartAsync(Action<SessionOptions>? extra = null) => TestServer.StartAsync(
        app =>
        {
            app.UseSessions();

            app.MapPost("/visit", async ctx =>
            {
                var session = ctx.RequestServices.GetRequiredService<ISession>();
                var count = session.GetInt32("visits") ?? 0;

                session.SetInt32("visits", count + 1);

                await ctx.Response.WriteTextAsync((count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
            });

            app.MapGet("/visits", ctx =>
            {
                var session = ctx.RequestServices.GetRequiredService<ISession>();

                return ctx.Response.WriteTextAsync(
                    (session.GetInt32("visits") ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture)
                );
            });

            app.MapGet("/id", ctx => ctx.Response.WriteTextAsync(ctx.Session!.Id));

            app.MapPost("/clear", async ctx =>
            {
                ctx.Session!.Clear();
                await ctx.Response.WriteTextAsync("cleared");
            });

            app.MapGet("/nothing", ctx => ctx.Response.WriteTextAsync("no session used"));
        },
        builder => builder.Services.AddSessions(o =>
        {
            o.Protector = new TicketProtector(Key);
            extra?.Invoke(o);
        })
    );

    /// <summary>A client that keeps its own cookies, so a session actually persists between calls.</summary>
    static HttpClient Client(TestServer server)
    {
        var handler = new HttpClientHandler { UseCookies = true, AllowAutoRedirect = false };

        return new HttpClient(handler) { BaseAddress = server.Client.BaseAddress };
    }

    static string? CookieFrom(HttpResponseMessage response)
        => response.Headers.TryGetValues("Set-Cookie", out var values) ? values.FirstOrDefault() : null;

    [Fact]
    public async Task Keeps_state_across_requests()
    {
        await using var server = await StartAsync();
        using var client = Client(server);
        var token = TestContext.Current.CancellationToken;

        Assert.Equal("1", await (await client.PostAsync("/visit", null, token)).Content.ReadAsStringAsync(token));
        Assert.Equal("2", await (await client.PostAsync("/visit", null, token)).Content.ReadAsStringAsync(token));
        Assert.Equal("3", await (await client.PostAsync("/visit", null, token)).Content.ReadAsStringAsync(token));

        Assert.Equal("3", await client.GetStringAsync("/visits", token));
    }

    /// <summary>Two visitors are two sessions; sharing one would be the worst possible bug here.</summary>
    [Fact]
    public async Task Keeps_visitors_apart()
    {
        await using var server = await StartAsync();
        using var first = Client(server);
        using var second = Client(server);
        var token = TestContext.Current.CancellationToken;

        await first.PostAsync("/visit", null, token);
        await first.PostAsync("/visit", null, token);
        await second.PostAsync("/visit", null, token);

        Assert.Equal("2", await first.GetStringAsync("/visits", token));
        Assert.Equal("1", await second.GetStringAsync("/visits", token));
    }

    /// <summary>
    /// A request that never writes a session must not mint one — otherwise every static asset would
    /// hand out a cookie nobody asked for.
    /// </summary>
    [Fact]
    public async Task Issues_no_cookie_for_a_request_that_did_not_use_the_session()
    {
        await using var server = await StartAsync();
        using var client = Client(server);

        var response = await client.GetAsync("/nothing", TestContext.Current.CancellationToken);

        Assert.Null(CookieFrom(response));
    }

    [Fact]
    public async Task Issues_a_cookie_once_something_is_written()
    {
        await using var server = await StartAsync();
        using var client = Client(server);
        var token = TestContext.Current.CancellationToken;

        var first = await client.PostAsync("/visit", null, token);
        var cookie = CookieFrom(first);

        Assert.NotNull(cookie);
        Assert.StartsWith(".shiny.session=", cookie, StringComparison.Ordinal);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);

        // And not again on the next request, since the session is no longer new.
        Assert.Null(CookieFrom(await client.PostAsync("/visit", null, token)));
    }

    /// <summary>The id is the credential. Sent in the clear it can be lifted from a log and replayed.</summary>
    [Fact]
    public async Task Does_not_put_the_session_id_in_the_cookie()
    {
        await using var server = await StartAsync();
        using var client = Client(server);
        var token = TestContext.Current.CancellationToken;

        var response = await client.PostAsync("/visit", null, token);
        var cookie = CookieFrom(response)!;
        var id = await client.GetStringAsync("/id", token);

        Assert.NotEmpty(id);
        Assert.DoesNotContain(id, cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Keeps_the_same_id_across_requests()
    {
        await using var server = await StartAsync();
        using var client = Client(server);
        var token = TestContext.Current.CancellationToken;

        await client.PostAsync("/visit", null, token);

        Assert.Equal(await client.GetStringAsync("/id", token), await client.GetStringAsync("/id", token));
    }

    [Fact]
    public async Task Starts_a_fresh_session_for_a_forged_cookie()
    {
        await using var server = await StartAsync();
        using var client = Client(server);
        var token = TestContext.Current.CancellationToken;

        using var request = new HttpRequestMessage(HttpMethod.Get, "/visits");
        request.Headers.Add("Cookie", ".shiny.session=not-a-real-ticket");

        var response = await client.SendAsync(request, token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("0", await response.Content.ReadAsStringAsync(token));
    }

    [Fact]
    public async Task Clearing_empties_the_session_without_ending_it()
    {
        await using var server = await StartAsync();
        using var client = Client(server);
        var token = TestContext.Current.CancellationToken;

        await client.PostAsync("/visit", null, token);
        var id = await client.GetStringAsync("/id", token);

        await client.PostAsync("/clear", null, token);

        Assert.Equal("0", await client.GetStringAsync("/visits", token));
        Assert.Equal(id, await client.GetStringAsync("/id", token));
    }

    /// <summary>
    /// State written before a handler threw is state the user already caused; losing it would be a
    /// second, quieter bug.
    /// <para>
    /// The session has to exist already. A brand-new session's cookie is staged on the response, and
    /// an unhandled exception resets the response to write its 500 — so the cookie goes with it and
    /// the client never learns the id. The data is still saved; it is simply orphaned.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Saves_what_was_written_before_a_handler_threw()
    {
        await using var server = await TestServer.StartAsync(
            app =>
            {
                app.UseSessions();

                app.MapPost("/start", ctx =>
                {
                    ctx.Session!.SetString("progress", "none");
                    return ctx.Response.WriteTextAsync("started");
                });

                app.MapPost("/half-done", ctx =>
                {
                    ctx.Session!.SetString("progress", "written");
                    throw new InvalidOperationException("boom");
                });

                app.MapGet("/progress", ctx => ctx.Response.WriteTextAsync(ctx.Session!.GetString("progress") ?? "none"));
            },
            builder => builder.Services.AddSessions(o => o.Protector = new TicketProtector(Key))
        );

        using var client = Client(server);
        var token = TestContext.Current.CancellationToken;

        // Establish the session first, so the client is holding its cookie before the failure.
        await client.PostAsync("/start", null, token);

        var failed = await client.PostAsync("/half-done", null, token);

        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Equal("written", await client.GetStringAsync("/progress", token));
    }

    [Fact]
    public async Task Round_trips_every_value_shape()
    {
        await using var server = await TestServer.StartAsync(
            app =>
            {
                app.UseSessions();

                app.MapPost("/write", ctx =>
                {
                    var s = ctx.Session!;

                    s.SetString("string", "hello");
                    s.SetInt32("int", -42);
                    s.SetInt64("long", 9_000_000_000L);
                    s.SetBoolean("bool", true);
                    s.SetGuid("guid", Guid.Empty);
                    s.SetDouble("double", 1.5);
                    s.SetDateTimeOffset("when", DateTimeOffset.UnixEpoch);

                    return ctx.Response.WriteTextAsync("written");
                });

                app.MapGet("/read", ctx =>
                {
                    var s = ctx.Session!;

                    return ctx.Response.WriteTextAsync(string.Join('|',
                        s.GetString("string"),
                        s.GetInt32("int"),
                        s.GetInt64("long"),
                        s.GetBoolean("bool"),
                        s.GetGuid("guid"),
                        s.GetDouble("double"),
                        s.GetDateTimeOffset("when")?.ToUnixTimeMilliseconds()
                    ));
                });
            },
            builder => builder.Services.AddSessions(o => o.Protector = new TicketProtector(Key))
        );

        using var client = Client(server);
        var token = TestContext.Current.CancellationToken;

        await client.PostAsync("/write", null, token);

        Assert.Equal(
            $"hello|-42|9000000000|True|{Guid.Empty}|1.5|0",
            await client.GetStringAsync("/read", token)
        );
    }

    [Fact]
    public async Task Reports_the_keys_that_are_set()
    {
        await using var server = await TestServer.StartAsync(
            app =>
            {
                app.UseSessions();

                app.MapGet("/keys", ctx =>
                {
                    var session = ctx.Session!;

                    session.SetString("a", "1");
                    session.SetString("b", "2");
                    session.Remove("a");

                    return ctx.Response.WriteTextAsync(string.Join(',', session.Keys));
                });
            },
            builder => builder.Services.AddSessions(o => o.Protector = new TicketProtector(Key))
        );

        using var client = Client(server);

        Assert.Equal("b", await client.GetStringAsync("/keys", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Refuses_a_configuration_with_no_protector()
    {
        Assert.Throws<InvalidOperationException>(
            () => new SessionMiddleware(new SessionOptions(), new InMemorySessionStore())
        );
    }

    /// <summary>
    /// A store of your own is the point of the interface.
    /// <para>
    /// Asserted the moment the response arrives, which is only sound because the session is
    /// committed before the first byte goes out. Committing afterwards would make this a race — and
    /// worse, would let a second request on another connection read a stale session.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Uses_a_custom_store()
    {
        var store = new CountingSessionStore();

        await using var server = await TestServer.StartAsync(
            app =>
            {
                app.UseSessions();
                app.MapPost("/visit", ctx =>
                {
                    ctx.Session!.SetString("k", "v");
                    return ctx.Response.WriteTextAsync("ok");
                });
            },
            builder => builder.Services.AddSessions(_ => store, o => o.Protector = new TicketProtector(Key))
        );

        using var client = Client(server);

        var response = await client.PostAsync("/visit", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, store.Saves);
    }
}

sealed class CountingSessionStore : ISessionStore
{
    readonly InMemorySessionStore inner = new();
    int saves;

    public int Saves => Volatile.Read(ref this.saves);

    public ValueTask<SessionData?> LoadAsync(string sessionId, CancellationToken cancellationToken)
        => this.inner.LoadAsync(sessionId, cancellationToken);

    public ValueTask SaveAsync(string sessionId, SessionData data, TimeSpan idleTimeout, CancellationToken cancellationToken)
    {
        // Requests are served concurrently, so the counter has to be too.
        Interlocked.Increment(ref this.saves);

        return this.inner.SaveAsync(sessionId, data, idleTimeout, cancellationToken);
    }

    public ValueTask RefreshAsync(string sessionId, TimeSpan idleTimeout, CancellationToken cancellationToken)
        => this.inner.RefreshAsync(sessionId, idleTimeout, cancellationToken);

    public ValueTask RemoveAsync(string sessionId, CancellationToken cancellationToken)
        => this.inner.RemoveAsync(sessionId, cancellationToken);
}
