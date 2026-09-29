using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shiny.Net.HttpServer.Switchboard;
using SwitchboardFixtures;

namespace Shiny.Net.HttpServer.Tests;

/// <summary>
/// The switchboard over real sockets and the raw wire protocol — no client library in the loop, so
/// what is asserted here is the protocol itself.
/// </summary>
public class SwitchboardTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    const string UserHeader = "X-Test-User";
    const string RolesHeader = "X-Test-Roles";

    static Task<TestServer> StartAsync(Action<SwitchboardOptions>? configure = null)
        => TestServer.StartAsync(Configure, builder => ConfigureBuilder(builder, configure));

    static void Configure(HttpServer app)
    {
        // Stands in for real authentication: who the caller is comes from two headers.
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Headers.GetFirst(UserHeader) is { Length: > 0 } user)
            {
                var claims = new List<Claim> { new(ClaimTypes.Name, user) };
                foreach (var role in (ctx.Request.Headers.GetFirst(RolesHeader) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
                    claims.Add(new Claim(ClaimTypes.Role, role));

                ctx.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
            }

            await next(ctx);
        });

        app.MapSwitchboard<TestBoard>("/board");
        app.MapSwitchboard<SecureBoard>("/secure");
        app.MapSwitchboard<TypedBoard>("/typed");
    }

    static void ConfigureBuilder(ShinyHttpServerBuilder builder, Action<SwitchboardOptions>? configure)
    {
        builder.Services.AddSingleton<LifecycleProbe>();
        builder.AddSwitchboard(o =>
        {
            o.HeartbeatInterval = TimeSpan.FromSeconds(30);
            configure?.Invoke(o);
        });
    }

    // ---- connecting ----

    [Fact]
    public async Task Connect_announces_the_line_and_runs_OnConnected()
    {
        await using var server = await StartAsync();
        await using var line = await LineClient.ConnectAsync(server.Client);

        Assert.Matches("^[0-9a-f]{16}$", line.LineId);
        Assert.Matches("^[0-9a-f]{32}$", line.LineToken);

        // OnConnectedAsync called Clients.Caller — delivered on the same stream, with the first id.
        var welcome = await line.Events.NextAsync(Token);
        Assert.Equal("invoke", welcome!.Event);
        Assert.Equal("1", welcome.Id);
        Assert.Equal($$"""{"target":"Welcome","args":["{{line.LineId}}"]}""", welcome.Data);

        var probe = server.Server.Services!.GetRequiredService<LifecycleProbe>();
        await probe.WaitForAsync(e => e == $"connected:{line.LineId}", Token);
    }

    [Fact]
    public async Task A_class_level_authorize_protects_the_connect()
    {
        await using var server = await StartAsync();

        using var anonymous = await server.Client.GetAsync("/secure/connect", HttpCompletionOption.ResponseHeadersRead, Token);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        await using var line = await LineClient.ConnectAsync(server.Client, "/secure", user: "alice");
        var (status, body) = await line.InvokeAsync("Ping");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("""{"result":"pong"}""", body);
    }

    // ---- invoking ----

    [Fact]
    public async Task Invokes_methods_and_returns_their_results()
    {
        await using var server = await StartAsync();
        await using var line = await LineClient.ConnectAsync(server.Client);

        Assert.Equal((HttpStatusCode.OK, """{"result":5}"""), await line.InvokeAsync("Add", 2, 3));
        Assert.Equal((HttpStatusCode.OK, """{"result":"hi"}"""), await line.InvokeAsync("Echo", "hi"));
        Assert.Equal((HttpStatusCode.OK, """{"result":"hello world"}"""), await line.InvokeAsync("greet"));
        Assert.Equal((HttpStatusCode.OK, """{"result":"hello bob"}"""), await line.InvokeAsync("greet", "bob"));
        Assert.Equal((HttpStatusCode.NoContent, ""), await line.InvokeAsync("Nothing"));
    }

    [Fact]
    public async Task Binds_app_types_through_the_registered_json_context()
    {
        await using var server = await StartAsync();
        await using var line = await LineClient.ConnectAsync(server.Client);

        var (status, body) = await line.InvokeAsync("Bump", new Note("n", 1));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("""{"result":{"Title":"n","Priority":2}}""", body);
    }

    [Fact]
    public async Task Rejects_calls_that_cannot_be_bound()
    {
        await using var server = await StartAsync();
        await using var line = await LineClient.ConnectAsync(server.Client);

        Assert.Equal(HttpStatusCode.NotFound, (await line.InvokeAsync("Missing")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await line.InvokeAsync("Hidden")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await line.InvokeAsync("OnConnectedAsync")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await line.InvokeAsync("Greeting")).Status);

        var (tooFew, tooFewBody) = await line.InvokeAsync("Add", 1);
        Assert.Equal(HttpStatusCode.BadRequest, tooFew);
        Assert.Equal("'Add' takes 2 argument(s), not 1", Error(tooFewBody));

        var (wrongType, wrongTypeBody) = await line.InvokeAsync("Add", "one", 2);
        Assert.Equal(HttpStatusCode.BadRequest, wrongType);
        Assert.Equal("Argument 'a' is not a valid Int32.", Error(wrongTypeBody));
    }

    [Fact]
    public async Task Surfaces_SwitchboardException_and_hides_everything_else()
    {
        await using var server = await StartAsync();
        await using var line = await LineClient.ConnectAsync(server.Client);

        Assert.Equal((HttpStatusCode.BadRequest, """{"error":"nope"}"""), await line.InvokeAsync("Fail"));

        var (status, body) = await line.InvokeAsync("Crash");
        Assert.Equal(HttpStatusCode.InternalServerError, status);
        Assert.DoesNotContain("secret detail", body);
    }

    [Fact]
    public async Task Detailed_errors_send_the_exception_message()
    {
        await using var server = await StartAsync(o => o.EnableDetailedErrors = true);
        await using var line = await LineClient.ConnectAsync(server.Client);

        var (status, body) = await line.InvokeAsync("Crash");
        Assert.Equal(HttpStatusCode.InternalServerError, status);
        Assert.Contains("secret detail", body);
    }

    [Fact]
    public async Task Streams_an_async_enumerable_as_the_invoke_response()
    {
        await using var server = await StartAsync();
        await using var line = await LineClient.ConnectAsync(server.Client);

        using var request = line.InvokeRequest("Count", 3);
        await using var stream = await EventStream.OpenAsync(server.Client, request, Token);

        Assert.Equal("text/event-stream", stream.Response.Content.Headers.ContentType?.MediaType);

        var received = new List<string>();
        while (await stream.NextAsync(Token) is { } e)
        {
            received.Add($"{e.Event}:{e.Data}");
            if (e.Event == "complete")
                break;
        }

        Assert.Equal(["item:1", "item:2", "item:3", "complete:"], received);
    }

    [Fact]
    public async Task Items_last_as_long_as_the_line()
    {
        await using var server = await StartAsync();
        await using var line = await LineClient.ConnectAsync(server.Client);
        await using var other = await LineClient.ConnectAsync(server.Client);

        Assert.Equal("""{"result":1}""", (await line.InvokeAsync("Counter")).Body);
        Assert.Equal("""{"result":2}""", (await line.InvokeAsync("Counter")).Body);
        Assert.Equal("""{"result":1}""", (await other.InvokeAsync("Counter")).Body);
    }

    [Fact]
    public async Task Binds_services_and_the_caller_context()
    {
        await using var server = await StartAsync();
        await using var line = await LineClient.ConnectAsync(server.Client);

        Assert.Equal($$"""{"result":"{{line.LineId}}"}""", (await line.InvokeAsync("Scoped")).Body);
    }

    // ---- reaching clients ----

    [Fact]
    public async Task Broadcasts_reach_every_line_and_Others_skips_the_caller()
    {
        await using var server = await StartAsync();
        await using var a = await LineClient.ConnectAsync(server.Client);
        await using var b = await LineClient.ConnectAsync(server.Client);
        await a.SkipWelcomeAsync();
        await b.SkipWelcomeAsync();

        await a.InvokeAsync("Broadcast", "to all");
        Assert.Equal($$"""{"target":"Said","args":["{{a.LineId}}","to all"]}""", (await a.Events.NextAsync(Token))!.Data);
        Assert.Equal($$"""{"target":"Said","args":["{{a.LineId}}","to all"]}""", (await b.Events.NextAsync(Token))!.Data);

        await a.InvokeAsync("SayToOthers", "not you");
        await a.InvokeAsync("Broadcast", "marker");

        // a's next event is the marker: "not you" never reached it.
        Assert.Contains("marker", (await a.Events.NextAsync(Token))!.Data);
        Assert.Contains("not you", (await b.Events.NextAsync(Token))!.Data);
    }

    [Fact]
    public async Task Group_sends_reach_only_members()
    {
        await using var server = await StartAsync();
        await using var member = await LineClient.ConnectAsync(server.Client);
        await using var outsider = await LineClient.ConnectAsync(server.Client);
        await member.SkipWelcomeAsync();
        await outsider.SkipWelcomeAsync();

        await member.InvokeAsync("Join", "kitchen");
        await outsider.InvokeAsync("SayToGroup", "kitchen", "dinner");
        await outsider.InvokeAsync("SayToOthers", "marker");

        Assert.Contains("dinner", (await member.Events.NextAsync(Token))!.Data);
        Assert.Contains("marker", (await member.Events.NextAsync(Token))!.Data);

        var @operator = server.Server.Services!.GetRequiredService<IOperator<TestBoard>>();
        Assert.Equal([member.LineId], @operator.Groups.GetLines("kitchen"));
        Assert.Equal(1, await @operator.Groups.RemoveGroupAsync("kitchen"));
        Assert.Empty(@operator.Groups.GetLines("kitchen"));
    }

    [Fact]
    public async Task The_operator_reaches_lines_from_outside()
    {
        await using var server = await StartAsync();
        await using var line = await LineClient.ConnectAsync(server.Client, user: "alice");
        await line.SkipWelcomeAsync();

        var @operator = server.Server.Services!.GetRequiredService<IOperator<TestBoard>>();

        var info = @operator.Lines.Find(line.LineId);
        Assert.NotNull(info);
        Assert.Equal("alice", info.User);
        Assert.Equal(LineStatus.Attached, info.Status);

        await @operator.Clients.User("alice").SendAsync("Notice", "from outside", 42);
        Assert.Equal("""{"target":"Notice","args":["from outside",42]}""", (await line.Events.NextAsync(Token))!.Data);

        Assert.Throws<InvalidOperationException>(() => @operator.Clients.Caller);
    }

    /// <summary>
    /// Over HTTP/2 the event stream and every call share one connection — the case the design leans
    /// on, and the one where a stream that blocked the connection would show up at once.
    /// </summary>
    [Fact]
    public async Task Works_over_http2_with_calls_multiplexed_beside_the_stream()
    {
        await using var app = Testing.TestHttpServer.Create(
            Configure,
            builder =>
            {
                builder.Options.Http2.AllowCleartext = true;
                ConfigureBuilder(builder, null);
            },
            useHttp2: true
        );

        await using var a = await LineClient.ConnectAsync(app.Client);
        await using var b = await LineClient.ConnectAsync(app.Client);
        Assert.Equal(2, a.Events.Response.Version.Major);

        await a.SkipWelcomeAsync();
        await b.SkipWelcomeAsync();

        Assert.Equal((HttpStatusCode.OK, """{"result":3}"""), await a.InvokeAsync("Add", 1, 2));

        await b.InvokeAsync("Broadcast", "over h2");
        Assert.Contains("over h2", (await a.Events.NextAsync(Token))!.Data);
        Assert.Contains("over h2", (await b.Events.NextAsync(Token))!.Data);

        var @operator = app.Services.GetRequiredService<IOperator<TestBoard>>();
        Assert.Equal("HTTP/2", @operator.Lines.Find(a.LineId)!.Protocol);
    }

    // ---- hanging up ----

    [Fact]
    public async Task The_operator_hangs_up_and_the_token_dies()
    {
        await using var server = await StartAsync();
        await using var line = await LineClient.ConnectAsync(server.Client);
        await line.SkipWelcomeAsync();

        var @operator = server.Server.Services!.GetRequiredService<IOperator<TestBoard>>();
        Assert.True(await @operator.Lines.HangUpAsync(line.LineId, "Removed by a moderator"));

        var close = await line.Events.NextAsync(Token);
        Assert.Equal("close", close!.Event);
        Assert.Equal("""{"reason":"Removed by a moderator","allowReconnect":false}""", close.Data);
        Assert.Null(await line.Events.NextAsync(Token));

        Assert.Equal(HttpStatusCode.Gone, (await line.InvokeAsync("Add", 1, 2)).Status);

        var probe = server.Server.Services!.GetRequiredService<LifecycleProbe>();
        await probe.WaitForAsync(e => e == $"disconnected:{line.LineId}:HungUp:Removed by a moderator", Token);
        Assert.Null(@operator.Lines.Find(line.LineId));
    }

    [Fact]
    public async Task A_method_can_hang_up_on_its_own_line()
    {
        await using var server = await StartAsync();
        await using var line = await LineClient.ConnectAsync(server.Client);
        await line.SkipWelcomeAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await line.InvokeAsync("HangUpOnMe")).Status);
        Assert.Equal("close", (await line.Events.NextAsync(Token))!.Event);

        var probe = server.Server.Services!.GetRequiredService<LifecycleProbe>();
        await probe.WaitForAsync(e => e.StartsWith($"disconnected:{line.LineId}:HungUp", StringComparison.Ordinal), Token);
    }

    [Fact]
    public async Task The_client_hangs_up_with_a_delete()
    {
        await using var server = await StartAsync();
        await using var line = await LineClient.ConnectAsync(server.Client);
        await line.SkipWelcomeAsync();

        using var request = new HttpRequestMessage(HttpMethod.Delete, "/board/connect");
        request.Headers.Add("X-Switchboard-Line", line.LineToken);
        using var response = await server.Client.SendAsync(request, Token);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var probe = server.Server.Services!.GetRequiredService<LifecycleProbe>();
        await probe.WaitForAsync(e => e.StartsWith($"disconnected:{line.LineId}:ClientClosed", StringComparison.Ordinal), Token);
    }

    // ---- resuming ----

    // An HTTP/1.1 server learns that a client went away when a write to it fails, so the drop tests
    // run a fast heartbeat: that is what gives the server something to write.
    static readonly TimeSpan FastHeartbeat = TimeSpan.FromMilliseconds(50);

    [Fact]
    public async Task A_dropped_line_resumes_with_what_it_missed()
    {
        await using var server = await StartAsync(o => o.HeartbeatInterval = FastHeartbeat);
        var @operator = server.Server.Services!.GetRequiredService<IOperator<TestBoard>>();
        var probe = server.Server.Services!.GetRequiredService<LifecycleProbe>();

        await using var line = await LineClient.ConnectAsync(server.Client);
        await line.Events.NextAsync(Token);                       // Welcome, id 1
        await line.Events.DisposeAsync();

        await WaitUntilAsync(() => @operator.Lines.Find(line.LineId)?.Status == LineStatus.Detached);

        await @operator.Clients.Line(line.LineId).SendAsync("WhileYouWereOut", "first");
        await @operator.Clients.Line(line.LineId).SendAsync("WhileYouWereOut", "second");

        await using var resumed = await line.ResumeAsync(lastEventId: 1);
        var hello = await resumed.NextAsync(Token);
        Assert.Equal("resumed", hello!.Event);
        Assert.Equal($$"""{"lineId":"{{line.LineId}}","missed":false}""", hello.Data);

        var first = await resumed.NextAsync(Token);
        var second = await resumed.NextAsync(Token);
        Assert.Equal(("2", """{"target":"WhileYouWereOut","args":["first"]}"""), (first!.Id, first.Data));
        Assert.Equal(("3", """{"target":"WhileYouWereOut","args":["second"]}"""), (second!.Id, second.Data));

        await probe.WaitForAsync(e => e == $"reconnected:{line.LineId}", Token);

        var info = @operator.Lines.Find(line.LineId)!;
        Assert.Equal(LineStatus.Attached, info.Status);
        Assert.Equal(1, info.Resumes);

        // Same line, same token: calls keep working.
        Assert.Equal(HttpStatusCode.OK, (await line.InvokeAsync("Add", 1, 1)).Status);
    }

    [Fact]
    public async Task A_resume_says_when_messages_were_lost()
    {
        await using var server = await StartAsync(o =>
        {
            o.ReplayBufferSize = 1;
            o.HeartbeatInterval = FastHeartbeat;
        });
        var @operator = server.Server.Services!.GetRequiredService<IOperator<TestBoard>>();

        await using var line = await LineClient.ConnectAsync(server.Client);
        await line.Events.NextAsync(Token);
        await line.Events.DisposeAsync();
        await WaitUntilAsync(() => @operator.Lines.Find(line.LineId)?.Status == LineStatus.Detached);

        await @operator.Clients.All.SendAsync("Lost", 1);
        await @operator.Clients.All.SendAsync("Kept", 2);

        await using var resumed = await line.ResumeAsync(lastEventId: 1);
        Assert.Equal($$"""{"lineId":"{{line.LineId}}","missed":true}""", (await resumed.NextAsync(Token))!.Data);
        Assert.Contains("Kept", (await resumed.NextAsync(Token))!.Data);
    }

    [Fact]
    public async Task A_line_that_does_not_come_back_is_dropped()
    {
        await using var server = await StartAsync(o =>
        {
            o.ResumeWindow = TimeSpan.FromMilliseconds(200);
            o.HeartbeatInterval = FastHeartbeat;
        });
        var probe = server.Server.Services!.GetRequiredService<LifecycleProbe>();

        await using var line = await LineClient.ConnectAsync(server.Client);
        await line.Events.DisposeAsync();

        await probe.WaitForAsync(e => e.StartsWith($"disconnected:{line.LineId}:Dropped", StringComparison.Ordinal), Token);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/board/connect?token={line.LineToken}");
        using var response = await server.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Token);
        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
    }

    [Fact]
    public async Task Without_a_resume_window_a_drop_closes_the_line()
    {
        await using var server = await StartAsync(o =>
        {
            o.ResumeWindow = TimeSpan.Zero;
            o.HeartbeatInterval = FastHeartbeat;
        });
        var probe = server.Server.Services!.GetRequiredService<LifecycleProbe>();

        await using var line = await LineClient.ConnectAsync(server.Client);
        await line.Events.DisposeAsync();

        await probe.WaitForAsync(e => e.StartsWith($"disconnected:{line.LineId}:Dropped", StringComparison.Ordinal), Token);
    }

    // ---- who may call ----

    [Fact]
    public async Task A_token_is_only_good_for_the_principal_that_opened_the_line()
    {
        await using var server = await StartAsync();
        await using var line = await LineClient.ConnectAsync(server.Client, user: "alice");

        Assert.Equal((HttpStatusCode.OK, """{"result":"alice"}"""), await line.InvokeAsync("WhoAmI"));

        Assert.Equal(HttpStatusCode.Forbidden, (await line.InvokeAsAsync("WhoAmI", "mallory")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await line.InvokeAsAsync("WhoAmI", "")).Status);
    }

    [Fact]
    public async Task A_method_level_authorize_is_enforced_per_call()
    {
        await using var server = await StartAsync();

        await using var user = await LineClient.ConnectAsync(server.Client, user: "alice");
        Assert.Equal(HttpStatusCode.Forbidden, (await user.InvokeAsync("AdminOnly")).Status);

        await using var anonymous = await LineClient.ConnectAsync(server.Client);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.InvokeAsync("AdminOnly")).Status);

        await using var admin = await LineClient.ConnectAsync(server.Client, user: "root", roles: "admin");
        Assert.Equal((HttpStatusCode.OK, """{"result":"admin"}"""), await admin.InvokeAsync("AdminOnly"));
    }

    // ---- helpers ----

    static string? Error(string body)
    {
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("error").GetString();
    }

    static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        while (!condition())
            await Task.Delay(20, timeout.Token);
    }

    /// <summary>A raw protocol client: one event stream, plus JSON POSTs carrying the line token.</summary>
    sealed class LineClient : IAsyncDisposable
    {
        readonly HttpClient http;
        readonly string path;
        readonly string? user;
        readonly string? roles;

        LineClient(HttpClient http, string path, string? user, string? roles, EventStream events, string lineId, string lineToken)
        {
            this.http = http;
            this.path = path;
            this.user = user;
            this.roles = roles;
            this.Events = events;
            this.LineId = lineId;
            this.LineToken = lineToken;
        }

        public EventStream Events { get; }

        public string LineId { get; }

        public string LineToken { get; }

        public static async Task<LineClient> ConnectAsync(HttpClient http, string path = "/board", string? user = null, string? roles = null)
        {
            var request = NewRequest(http, HttpMethod.Get, $"{path}/connect");
            Identify(request, user, roles);

            var events = await EventStream.OpenAsync(http, request, Token);
            Assert.Equal(HttpStatusCode.OK, events.Response.StatusCode);

            var connected = await events.NextAsync(Token);
            Assert.Equal("connected", connected!.Event);

            using var json = JsonDocument.Parse(connected.Data);
            Assert.Equal(1, json.RootElement.GetProperty("protocol").GetInt32());

            return new LineClient(
                http,
                path,
                user,
                roles,
                events,
                json.RootElement.GetProperty("lineId").GetString()!,
                json.RootElement.GetProperty("lineToken").GetString()!
            );
        }

        public async Task SkipWelcomeAsync()
            => Assert.Contains("Welcome", (await this.Events.NextAsync(Token))!.Data);

        public Task<EventStream> ResumeAsync(long lastEventId)
        {
            var request = NewRequest(this.http, HttpMethod.Get, $"{this.path}/connect");
            request.Headers.Add("X-Switchboard-Line", this.LineToken);
            request.Headers.Add("Last-Event-ID", lastEventId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Identify(request, this.user, this.roles);

            return EventStream.OpenAsync(this.http, request, Token);
        }

        public HttpRequestMessage InvokeRequest(string target, params object?[] args)
            => this.InvokeRequest(target, args, this.user);

        HttpRequestMessage InvokeRequest(string target, object?[] args, string? asUser)
        {
            var request = NewRequest(this.http, HttpMethod.Post, $"{this.path}/invoke");
            request.Content = new StringContent(
                JsonSerializer.Serialize(new { target, args }),
                Encoding.UTF8,
                new MediaTypeHeaderValue("application/json")
            );

            request.Headers.Add("X-Switchboard-Line", this.LineToken);
            Identify(request, asUser, asUser == this.user ? this.roles : null);
            return request;
        }

        public Task<(HttpStatusCode Status, string Body)> InvokeAsync(string target, params object?[] args)
            => this.SendAsync(this.InvokeRequest(target, args, this.user));

        public Task<(HttpStatusCode Status, string Body)> InvokeAsAsync(string target, string? user)
            => this.SendAsync(this.InvokeRequest(target, [], user));

        async Task<(HttpStatusCode, string)> SendAsync(HttpRequestMessage request)
        {
            using (request)
            {
                using var response = await this.http.SendAsync(request, Token);
                return (response.StatusCode, await response.Content.ReadAsStringAsync(Token));
            }
        }

        /// <summary>A hand-built request ignores the client's default version, so it is copied across.</summary>
        static HttpRequestMessage NewRequest(HttpClient http, HttpMethod method, string url) => new(method, url)
        {
            Version = http.DefaultRequestVersion,
            VersionPolicy = http.DefaultVersionPolicy
        };

        static void Identify(HttpRequestMessage request, string? user, string? roles)
        {
            if (!string.IsNullOrEmpty(user))
                request.Headers.Add(UserHeader, user);

            if (!string.IsNullOrEmpty(roles))
                request.Headers.Add(RolesHeader, roles);
        }

        public ValueTask DisposeAsync() => this.Events.DisposeAsync();
    }
}

public sealed record SseEvent(string? Event, string? Id, string Data);

/// <summary>Reads a <c>text/event-stream</c> response one dispatched event at a time, skipping comments.</summary>
sealed class EventStream : IAsyncDisposable
{
    readonly StreamReader reader;
    bool disposed;

    EventStream(HttpResponseMessage response, StreamReader reader)
    {
        this.Response = response;
        this.reader = reader;
    }

    public HttpResponseMessage Response { get; }

    public static async Task<EventStream> OpenAsync(HttpClient http, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStreamAsync(cancellationToken);

        return new EventStream(response, new StreamReader(body));
    }

    /// <summary>The next event, or null when the stream ends.</summary>
    public async Task<SseEvent?> NextAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        string? name = null;
        string? id = null;
        StringBuilder? data = null;

        while (true)
        {
            var line = await this.reader.ReadLineAsync(timeout.Token);
            if (line is null)
                return null;

            if (line.Length == 0)
            {
                if (name is null && id is null && data is null)
                    continue;

                return new SseEvent(name, id, data?.ToString() ?? "");
            }

            if (line[0] == ':')
                continue;

            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? "" : line[(colon + 1)..];
            if (value.StartsWith(' '))
                value = value[1..];

            switch (field)
            {
                case "event":
                    name = value;
                    break;
                case "id":
                    id = value;
                    break;
                case "data":
                    data = data is null ? new StringBuilder(value) : data.Append('\n').Append(value);
                    break;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        if (!this.disposed)
        {
            this.disposed = true;
            this.reader.Dispose();
            this.Response.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}
