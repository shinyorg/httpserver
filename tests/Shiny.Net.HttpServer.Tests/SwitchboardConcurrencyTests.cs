using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shiny.Net.HttpServer.Switchboard;
using Shiny.Net.HttpServer.Switchboard.Internal;
using SwitchboardFixtures;

namespace SwitchboardFixtures
{
    /// <summary>Lets a test hold <c>GatedBoard</c>'s callbacks and stream where it wants them, and see the order things ran in.</summary>
    public sealed class GateProbe
    {
        readonly ConcurrentQueue<string> log = new();

        public TaskCompletionSource ConnectedEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseConnected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource StreamEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseStream { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Disconnected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Record(string entry) => this.log.Enqueue(entry);

        public string[] Log => [.. this.log];
    }

    public class GatedBoard(GateProbe probe) : Switchboard
    {
        public override async Task OnConnectedAsync()
        {
            probe.Record("connected:start");
            probe.ConnectedEntered.TrySetResult();
            await probe.ReleaseConnected.Task;
            probe.Record("connected:end");
        }

        public override Task OnDisconnectedAsync(DisconnectContext disconnect)
        {
            probe.Record("disconnected");
            probe.Disconnected.TrySetResult();
            return Task.CompletedTask;
        }

        public string Mark()
        {
            probe.Record("invoked");
            return "ok";
        }

        // Ignores cancellation on purpose: the body is still running after the line closes, which is the
        // case OnDisconnectedAsync has to wait out.
        public async IAsyncEnumerable<int> Stubborn([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return 1;

            // Past the first item: the gate is handed back, and the next move is pending on this body.
            probe.StreamEntered.TrySetResult();
            await probe.ReleaseStream.Task;
            probe.Record("stream:end");
            yield return 2;
        }
    }
}

namespace Shiny.Net.HttpServer.Tests
{
    /// <summary>Where calls, lifecycle callbacks, resumes and the sweeper meet on one line.</summary>
    public class SwitchboardConcurrencyTests
    {
        static CancellationToken Token => TestContext.Current.CancellationToken;

        static Task<TestServer> StartAsync(GateProbe probe, Action<SwitchboardOptions>? configure = null)
            => TestServer.StartAsync(
                app =>
                {
                    app.MapSwitchboard<GatedBoard>("/gated");
                    app.MapSwitchboard<TestBoard>("/board");
                },
                builder =>
                {
                    builder.Services.AddSingleton(probe);
                    builder.Services.AddSingleton<LifecycleProbe>();
                    builder.AddSwitchboard(o =>
                    {
                        o.HeartbeatInterval = TimeSpan.FromSeconds(30);
                        configure?.Invoke(o);
                    });
                }
            );

        static async Task<(EventStream Events, string LineId, string Token)> ConnectAsync(TestServer server, string path)
        {
            var events = await EventStream.OpenAsync(server.Client, new HttpRequestMessage(HttpMethod.Get, $"{path}/connect"), Token);
            var connected = await events.NextAsync(Token);
            Assert.Equal("connected", connected!.Event);

            using var json = JsonDocument.Parse(connected.Data);
            return (events, json.RootElement.GetProperty("lineId").GetString()!, json.RootElement.GetProperty("lineToken").GetString()!);
        }

        static HttpRequestMessage Invoke(string path, string token, string body)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{path}/invoke")
            {
                Content = new StringContent(body, Encoding.UTF8, new MediaTypeHeaderValue("application/json"))
            };
            request.Headers.Add("X-Switchboard-Line", token);

            return request;
        }

        static HttpRequestMessage HangUp(string path, string token)
        {
            var request = new HttpRequestMessage(HttpMethod.Delete, $"{path}/connect");
            request.Headers.Add("X-Switchboard-Line", token);

            return request;
        }

        static Line FindLine(TestServer server, string lineId)
            => server.Server.Services!
                .GetRequiredService<SwitchboardRuntime>()
                .GetBoard(SwitchboardRegistry.GetRequired(typeof(GatedBoard)))
                .FindLine(lineId)!;

        static Line NewLine() => new(Line.NewId(), new ClaimsPrincipal(new ClaimsIdentity()), null, 16, 16, 1, "HTTP/1.1", null);

        // ---- lifecycle callbacks and the gate ----

        [Fact]
        public async Task A_call_made_as_soon_as_the_line_connects_waits_for_OnConnected()
        {
            var probe = new GateProbe();
            await using var server = await StartAsync(probe);

            var (events, lineId, token) = await ConnectAsync(server, "/gated");
            await using var _ = events;
            await probe.ConnectedEntered.Task.WaitAsync(Token);

            // The client has its token and OnConnectedAsync is still running: the gate is not free to take.
            Assert.Equal(0, FindLine(server, lineId).Gate.CurrentCount);

            var call = server.Client.SendAsync(Invoke("/gated", token, """{"target":"Mark","args":[]}"""), Token);

            probe.ReleaseConnected.SetResult();
            using var response = await call;

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(["connected:start", "connected:end", "invoked"], probe.Log);
        }

        [Fact]
        public async Task A_hang_up_during_OnConnected_waits_for_it_before_OnDisconnected()
        {
            var probe = new GateProbe();
            await using var server = await StartAsync(probe);

            var (events, _, token) = await ConnectAsync(server, "/gated");
            await using var __ = events;
            await probe.ConnectedEntered.Task.WaitAsync(Token);

            using (var hangUp = await server.Client.SendAsync(HangUp("/gated", token), Token))
                Assert.Equal(HttpStatusCode.NoContent, hangUp.StatusCode);

            probe.ReleaseConnected.SetResult();
            await probe.Disconnected.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);

            Assert.Equal(["connected:start", "connected:end", "disconnected"], probe.Log);
        }

        [Fact]
        public async Task OnDisconnected_waits_for_a_stream_that_already_gave_the_gate_back()
        {
            var probe = new GateProbe();
            probe.ReleaseConnected.SetResult();
            await using var server = await StartAsync(probe);

            var (events, _, token) = await ConnectAsync(server, "/gated");
            await using var __ = events;

            using var stream = await server.Client.SendAsync(
                Invoke("/gated", token, """{"target":"Stubborn","args":[]}"""),
                HttpCompletionOption.ResponseHeadersRead,
                Token
            );
            Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
            await probe.StreamEntered.Task.WaitAsync(Token);

            using (var hangUp = await server.Client.SendAsync(HangUp("/gated", token), Token))
                Assert.Equal(HttpStatusCode.NoContent, hangUp.StatusCode);

            probe.ReleaseStream.SetResult();
            await probe.Disconnected.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);

            Assert.Equal(["connected:start", "connected:end", "stream:end", "disconnected"], probe.Log);
        }

        // ---- ordering ----

        [Fact]
        public async Task A_refused_call_gives_up_its_turn_instead_of_stalling_the_next()
        {
            // Long enough that the next call could only get through by the refused one giving its turn up.
            await using var server = await StartAsync(new GateProbe(), o => o.InvocationOrderingTimeout = TimeSpan.FromHours(1));

            var (events, _, token) = await ConnectAsync(server, "/board");
            await using var __ = events;

            using (var refused = await server.Client.SendAsync(Invoke("/board", token, """{"target":"NoSuchMethod","args":[],"seq":1}"""), Token))
                Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);

            using (var badArgs = await server.Client.SendAsync(Invoke("/board", token, """{"target":"Add","args":[1],"seq":2}"""), Token))
                Assert.Equal(HttpStatusCode.BadRequest, badArgs.StatusCode);

            using var next = await server.Client.SendAsync(Invoke("/board", token, """{"target":"Add","args":[1,2],"seq":3}"""), Token);
            Assert.Equal(HttpStatusCode.OK, next.StatusCode);
            Assert.Equal("""{"result":3}""", await next.Content.ReadAsStringAsync(Token));
        }

        [Fact]
        public async Task A_forfeited_turn_does_not_let_later_calls_overtake_earlier_ones()
        {
            var line = NewLine();

            // 3 gave up early, but 2 has not run: 4 still waits.
            line.ForfeitTurn(3);
            var four = line.WaitTurnAsync(4, TimeSpan.FromHours(1), Token).AsTask();
            Assert.True(line.WaitTurnAsync(1, TimeSpan.FromHours(1), Token).IsCompletedSuccessfully);
            line.EndTurn(1);
            Assert.False(four.IsCompleted);

            // 2 finishes, 3 is already done, so 4 goes.
            var two = line.WaitTurnAsync(2, TimeSpan.FromHours(1), Token);
            Assert.True(two.IsCompletedSuccessfully);
            line.EndTurn(2);

            await four.WaitAsync(TimeSpan.FromSeconds(10), Token);
        }

        // ---- the resume window ----

        [Fact]
        public void The_sweeper_cannot_expire_a_line_that_resumed_after_it_looked()
        {
            var line = NewLine();
            line.Attach(null, "HTTP/1.1", null);
            line.DropStream();

            // What the sweeper saw: detached, and long enough ago.
            Assert.NotNull(line.DetachedAt);

            // The client comes back before the sweeper acts on it.
            Assert.NotNull(line.Attach(1, "HTTP/1.1", null));

            Assert.False(line.TryCloseDetached(Environment.TickCount64 + 60_000, 1_000));
            Assert.False(line.IsClosed);
        }

        [Fact]
        public void An_expired_line_closes_and_refuses_a_late_resume()
        {
            var line = NewLine();
            line.Attach(null, "HTTP/1.1", null);
            line.DropStream();

            Assert.True(line.TryCloseDetached(Environment.TickCount64 + 60_000, 1_000));
            Assert.True(line.IsClosed);
            Assert.Null(line.Attach(1, "HTTP/1.1", null));
        }

        [Fact]
        public void A_line_not_yet_attached_is_not_expired_as_detached_since_boot()
        {
            var line = NewLine();

            Assert.False(line.TryCloseDetached(Environment.TickCount64, 1_000));
        }
    }
}
