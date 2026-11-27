using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Shiny.Net.HttpServer.Tunneling;

namespace Shiny.Net.HttpServer.Tests;

/// <summary>
/// Peers that do not keep to the flow-control rules, played by hand over the raw protocol: a fake client against the
/// real relay, and a fake relay against the real client. A stream whose peer breaks the rules is reset; the tunnel and
/// its other streams are not.
/// </summary>
public class TunnelFlowControlAbuseTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    /// <summary>One end of a tunnel, driven frame by frame, recording every frame the other end sends.</summary>
    sealed class RawPeer : IAsyncDisposable
    {
        readonly Socket socket;
        readonly NetworkStream stream;
        readonly Channel<(TunnelFrameType Type, uint StreamId, byte[] Payload)> received = System.Threading.Channels.Channel.CreateUnbounded<(TunnelFrameType, uint, byte[])>();
        readonly List<(TunnelFrameType Type, uint StreamId, byte[] Payload)> seen = [];
        readonly Task loop;

        public TunnelChannel Channel { get; }

        public RawPeer(Socket socket)
        {
            this.socket = socket;
            this.stream = new NetworkStream(socket, ownsSocket: true);
            this.Channel = new TunnelChannel(PipeReader.Create(this.stream), PipeWriter.Create(this.stream));
            this.loop = Task.Run(async () =>
            {
                try
                {
                    await this.Channel.RunAsync((type, id, payload, _) =>
                    {
                        this.received.Writer.TryWrite((type, id, payload.ToArray()));
                        return ValueTask.CompletedTask;
                    });
                }
                catch
                {
                }
                finally
                {
                    this.received.Writer.TryComplete();
                }
            });
        }

        /// <summary>Waits for a frame matching <paramref name="match"/>; null when the connection closes or time runs out.</summary>
        public async Task<(TunnelFrameType Type, uint StreamId, byte[] Payload)?> WaitForAsync(Func<(TunnelFrameType Type, uint StreamId, byte[] Payload), bool> match)
        {
            var found = this.seen.FirstOrDefault(match);
            if (found.Payload is not null)
                return found;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
            timeout.CancelAfter(Wait);

            try
            {
                await foreach (var frame in this.received.Reader.ReadAllAsync(timeout.Token))
                {
                    this.seen.Add(frame);
                    if (match(frame))
                        return frame;
                }
            }
            catch (OperationCanceledException)
            {
            }

            return null;
        }

        /// <summary>Takes in every frame that has arrived so far, without waiting for more.</summary>
        public void Drain()
        {
            while (this.received.Reader.TryRead(out var frame))
                this.seen.Add(frame);
        }

        /// <summary>Data bytes seen so far on <paramref name="streamId"/>.</summary>
        public long SeenData(uint streamId)
            => this.seen.Where(f => f.Type == TunnelFrameType.Data && f.StreamId == streamId).Sum(f => (long)f.Payload.Length);

        /// <summary>True once the other end has closed the connection.</summary>
        public async Task<bool> WaitForCloseAsync()
        {
            await this.loop.WaitAsync(Wait, Token);
            return true;
        }

        public async ValueTask DisposeAsync()
        {
            this.socket.Dispose();
            await this.loop.WaitAsync(Wait, CancellationToken.None);
        }
    }

    static TunnelSettings PeerSettings(int window = TunnelSettings.MinimumWindow) => new(TunnelSettings.CurrentVersion, window, 16);

    // ---- A client that breaks the rules, against the real relay ----

    sealed class RelayRig : IAsyncDisposable
    {
        public required RelayServer Relay { get; init; }
        public required RawPeer Agent { get; init; }

        public static async Task<RelayRig> StartAsync(bool sendSettings = true)
        {
            var relay = new RelayServer(new RelayServerOptions
            {
                Address = IPAddress.Loopback,
                ControlPort = 0,
                PublicPort = 0,
                Domain = "localhost",
                Token = "secret",
                StreamWindowSize = TunnelSettings.MinimumWindow
            });
            await relay.StartAsync();

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(IPAddress.Loopback, relay.ControlPort);
            var agent = new RawPeer(socket);

            await agent.Channel.SendAsync(TunnelFrameType.Hello, 0, "secret\nabuse", Token);
            Assert.NotNull(await agent.WaitForAsync(f => f.Type == TunnelFrameType.Settings));
            Assert.NotNull(await agent.WaitForAsync(f => f.Type == TunnelFrameType.HelloAck));

            if (sendSettings)
            {
                await agent.Channel.SendAsync(TunnelFrameType.Settings, 0, PeerSettings().Encode(), Token);
                await Shiny.Net.HttpServer.Tests.Wait.UntilAsync(() => relay.FindSession("abuse.localhost")?.FlowControlNegotiated == true, Token);
            }

            return new RelayRig { Relay = relay, Agent = agent };
        }

        /// <summary>A public request to the tunnel; returns the client socket and the stream id the fake client was given.</summary>
        public async Task<(Socket Client, uint StreamId)> OpenAsync(string request = "GET /x HTTP/1.1\r\nHost: abuse.localhost\r\n\r\n", int? receiveBuffer = null)
        {
            var client = new Socket(SocketType.Stream, ProtocolType.Tcp);
            if (receiveBuffer is { } size)
                client.ReceiveBufferSize = size;

            await client.ConnectAsync(IPAddress.Loopback, this.Relay.PublicPort);
            await client.SendAsync(Encoding.ASCII.GetBytes(request), Token);

            var opened = await this.Agent.WaitForAsync(f => f.Type is TunnelFrameType.OpenWithFlowControl or TunnelFrameType.Open && !this.claimed.Contains(f.StreamId));
            Assert.NotNull(opened);
            this.claimed.Add(opened.Value.StreamId);

            return (client, opened.Value.StreamId);
        }

        readonly HashSet<uint> claimed = [];

        public async ValueTask DisposeAsync()
        {
            await this.Agent.DisposeAsync();
            await this.Relay.DisposeAsync();
        }
    }

    static async Task<string> ReadUntilClosedAsync(Socket client)
    {
        var text = new StringBuilder();
        var buffer = new byte[64 * 1024];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(Wait);

        try
        {
            int read;
            while ((read = await client.ReceiveAsync(buffer, timeout.Token)) > 0)
                text.Append(Encoding.Latin1.GetString(buffer, 0, read));

            text.Append("<<EOF>>");
        }
        catch (SocketException)
        {
            text.Append("<<RESET>>");
        }

        return text.ToString();
    }

    [Fact]
    public async Task The_relay_opens_streams_with_flow_control_once_the_client_has_sent_settings()
    {
        await using var rig = await RelayRig.StartAsync();

        var (client, streamId) = await rig.OpenAsync();
        using var _ = client;

        Assert.NotNull(await rig.Agent.WaitForAsync(f => f.Type == TunnelFrameType.OpenWithFlowControl && f.StreamId == streamId));
    }

    [Fact]
    public async Task A_client_that_never_sends_settings_gets_streams_without_flow_control()
    {
        await using var rig = await RelayRig.StartAsync(sendSettings: false);

        var (client, streamId) = await rig.OpenAsync();
        using var _ = client;

        Assert.NotNull(await rig.Agent.WaitForAsync(f => f.Type == TunnelFrameType.Open && f.StreamId == streamId));
    }

    [Fact]
    public async Task A_client_that_sends_past_the_relays_window_has_that_stream_reset()
    {
        await using var rig = await RelayRig.StartAsync();

        // The public client does not read, so nothing the relay queues for it drains and no credit comes back.
        var (stalled, streamId) = await rig.OpenAsync(receiveBuffer: 4096);
        using var _ = stalled;

        var flood = new byte[TunnelProtocol.MaxPayloadLength];
        for (var i = 0; i < 128; i++)
            await rig.Agent.Channel.SendAsync(TunnelFrameType.Data, streamId, flood, Token);

        Assert.NotNull(await rig.Agent.WaitForAsync(f => f.Type == TunnelFrameType.CloseStream && f.StreamId == streamId));

        // The tunnel is fine: another stream still opens and is served.
        var (client, next) = await rig.OpenAsync();
        using var __ = client;
        var response = "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"u8.ToArray();
        await rig.Agent.Channel.SendAsync(TunnelFrameType.Data, next, response, Token);
        await rig.Agent.Channel.SendAsync(TunnelFrameType.CloseStream, next, Token);

        Assert.Contains("ok", await ReadUntilClosedAsync(client));
    }

    [Fact]
    public async Task A_client_that_grants_more_than_a_window_can_hold_has_that_stream_reset()
    {
        await using var rig = await RelayRig.StartAsync();

        var (client, streamId) = await rig.OpenAsync();
        using var _ = client;

        await rig.Agent.Channel.SendAsync(TunnelFrameType.WindowUpdate, streamId, TunnelSettings.EncodeWindowUpdate(int.MaxValue), Token);

        Assert.NotNull(await rig.Agent.WaitForAsync(f => f.Type == TunnelFrameType.CloseStream && f.StreamId == streamId));
        Assert.Contains("<<", await ReadUntilClosedAsync(client));
    }

    [Fact]
    public async Task Frames_the_relay_does_not_know_are_ignored()
    {
        await using var rig = await RelayRig.StartAsync();

        await rig.Agent.Channel.SendAsync((TunnelFrameType)200, 0, "from the future"u8.ToArray(), Token);
        await rig.Agent.Channel.SendAsync(TunnelFrameType.WindowUpdate, 999, TunnelSettings.EncodeWindowUpdate(10), Token);
        await rig.Agent.Channel.SendAsync(TunnelFrameType.WindowUpdate, 1, new byte[4], Token);

        // A second Settings is ignored rather than allowed to change the windows of streams to come.
        await rig.Agent.Channel.SendAsync(TunnelFrameType.Settings, 0, PeerSettings(1 << 20).Encode(), Token);

        var (client, streamId) = await rig.OpenAsync();
        using var _ = client;
        await rig.Agent.Channel.SendAsync(TunnelFrameType.Data, streamId, "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"u8.ToArray(), Token);
        await rig.Agent.Channel.SendAsync(TunnelFrameType.CloseStream, streamId, Token);

        Assert.Contains("ok", await ReadUntilClosedAsync(client));
    }

    [Fact]
    public async Task Malformed_settings_from_the_client_drop_the_tunnel()
    {
        await using var rig = await RelayRig.StartAsync(sendSettings: false);

        await rig.Agent.Channel.SendAsync(TunnelFrameType.Settings, 0, new byte[5], Token);

        Assert.True(await rig.Agent.WaitForCloseAsync());
        await Shiny.Net.HttpServer.Tests.Wait.UntilAsync(() => rig.Relay.FindSession("abuse.localhost") is null, Token);
        Assert.Null(rig.Relay.FindSession("abuse.localhost"));
    }

    // ---- A relay that breaks the rules, against the real client ----

    sealed class AgentRig : IAsyncDisposable
    {
        public required TcpListener Listener { get; init; }
        public required HttpServer Server { get; init; }
        public required RelayTunnelProvider Provider { get; init; }
        public required Task Running { get; init; }
        public required RawPeer Relay { get; init; }
        public required CancellationTokenSource Stopping { get; init; }

        public static async Task<AgentRig> StartAsync(Action<HttpServer> configure, bool sendSettings)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();

            var server = new HttpServer(new HttpServerOptions { Port = 0 });
            configure(server);

            var provider = new RelayTunnelProvider(new RelayTunnelOptions
            {
                Host = "127.0.0.1",
                Port = ((IPEndPoint)listener.LocalEndpoint).Port,
                Token = "secret",
                Subdomain = "abuse",
                UseTls = false,
                ReconnectDelay = null,
                StreamWindowSize = TunnelSettings.MinimumWindow
            });

            var stopping = new CancellationTokenSource();
            var running = server.RunTunnelAsync(provider, cancellationToken: stopping.Token);

            var relay = new RawPeer(await listener.AcceptSocketAsync(Token));
            Assert.NotNull(await relay.WaitForAsync(f => f.Type == TunnelFrameType.Hello));

            if (sendSettings)
                await relay.Channel.SendAsync(TunnelFrameType.Settings, 0, PeerSettings().Encode(), Token);

            await relay.Channel.SendAsync(TunnelFrameType.HelloAck, 0, "http://abuse.localhost", Token);

            if (sendSettings)
                Assert.NotNull(await relay.WaitForAsync(f => f.Type == TunnelFrameType.Settings));

            return new AgentRig
            {
                Listener = listener,
                Server = server,
                Provider = provider,
                Running = running,
                Relay = relay,
                Stopping = stopping
            };
        }

        public async ValueTask DisposeAsync()
        {
            await this.Stopping.CancelAsync();
            await this.Relay.DisposeAsync();

            try
            {
                await this.Running.WaitAsync(Wait, CancellationToken.None);
            }
            catch
            {
            }

            await this.Provider.DisposeAsync();
            await this.Server.DisposeAsync();
            this.Listener.Stop();
            this.Stopping.Dispose();
        }
    }

    [Fact]
    public async Task The_client_refuses_a_flow_controlled_stream_before_any_settings()
    {
        await using var rig = await AgentRig.StartAsync(app => app.MapGet("/x", ctx => ctx.Response.WriteAsync("x")), sendSettings: false);

        await rig.Relay.Channel.SendAsync(TunnelFrameType.OpenWithFlowControl, 1, "127.0.0.1:1", Token);

        Assert.NotNull(await rig.Relay.WaitForAsync(f => f.Type == TunnelFrameType.CloseStream && f.StreamId == 1));
    }

    [Fact]
    public async Task A_relay_that_sends_past_the_clients_window_has_that_stream_reset_and_the_rest_carry_on()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var rig = await AgentRig.StartAsync(
            app =>
            {
                app.MapPost("/hold", async ctx =>
                {
                    await release.Task.WaitAsync(ctx.RequestAborted);
                    await ctx.Response.WriteAsync("held");
                });
                app.MapGet("/ok", ctx => ctx.Response.WriteAsync("ok"));
            },
            sendSettings: true
        );

        try
        {
            // A handler that never reads its body, and a relay that ignores the client's window.
            await rig.Relay.Channel.SendAsync(TunnelFrameType.OpenWithFlowControl, 1, "127.0.0.1:1", Token);
            await rig.Relay.Channel.SendAsync(TunnelFrameType.Data, 1, "POST /hold HTTP/1.1\r\nHost: abuse.localhost\r\nContent-Length: 100000000\r\n\r\n"u8.ToArray(), Token);

            var flood = new byte[TunnelProtocol.MaxPayloadLength];
            for (var i = 0; i < 64; i++)
                await rig.Relay.Channel.SendAsync(TunnelFrameType.Data, 1, flood, Token);

            Assert.NotNull(await rig.Relay.WaitForAsync(f => f.Type == TunnelFrameType.CloseStream && f.StreamId == 1));

            // Another stream on the same tunnel is still served.
            await rig.Relay.Channel.SendAsync(TunnelFrameType.OpenWithFlowControl, 3, "127.0.0.1:1", Token);
            await rig.Relay.Channel.SendAsync(TunnelFrameType.Data, 3, "GET /ok HTTP/1.1\r\nHost: abuse.localhost\r\nConnection: close\r\n\r\n"u8.ToArray(), Token);

            var answered = await rig.Relay.WaitForAsync(f => f.Type == TunnelFrameType.Data && f.StreamId == 3);
            Assert.NotNull(answered);
            Assert.StartsWith("HTTP/1.1 200", Encoding.ASCII.GetString(answered.Value.Payload));
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task The_client_holds_its_response_to_the_relays_window()
    {
        await using var rig = await AgentRig.StartAsync(
            app => app.MapGet("/big", async ctx =>
            {
                var chunk = new byte[TunnelProtocol.MaxPayloadLength];
                try
                {
                    for (var i = 0; i < 64; i++)
                        await ctx.Response.Body.WriteAsync(chunk, ctx.RequestAborted);
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException)
                {
                }
            }),
            sendSettings: true
        );

        await rig.Relay.Channel.SendAsync(TunnelFrameType.OpenWithFlowControl, 1, "127.0.0.1:1", Token);
        await rig.Relay.Channel.SendAsync(TunnelFrameType.Data, 1, "GET /big HTTP/1.1\r\nHost: abuse.localhost\r\n\r\n"u8.ToArray(), Token);

        // The relay declared one frame's worth and grants nothing: something arrives, but never more than that.
        var relay = rig.Relay;
        await Shiny.Net.HttpServer.Tests.Wait.UntilAsync(() =>
        {
            relay.Drain();
            return relay.SeenData(1) > 0;
        }, Token);
        await Task.Delay(500, Token);
        relay.Drain();

        var received = relay.SeenData(1);
        Assert.InRange(received, 1, TunnelSettings.MinimumWindow);

        // A grant lets more through - and again no more than it allows.
        await relay.Channel.SendAsync(TunnelFrameType.WindowUpdate, 1, TunnelSettings.EncodeWindowUpdate(TunnelSettings.MinimumWindow), Token);
        await Shiny.Net.HttpServer.Tests.Wait.UntilAsync(() =>
        {
            relay.Drain();
            return relay.SeenData(1) > received;
        }, Token);
        await Task.Delay(500, Token);
        relay.Drain();

        Assert.InRange(relay.SeenData(1), received + 1, 2L * TunnelSettings.MinimumWindow);
    }
}
