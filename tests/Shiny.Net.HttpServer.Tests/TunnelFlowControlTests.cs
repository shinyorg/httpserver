using System.Net;
using System.Net.Sockets;
using System.Text;
using Shiny.Net.HttpServer.Tunneling;

namespace Shiny.Net.HttpServer.Tests;

/// <summary>
/// Every request on a tunnel shares one control connection. Without flow control the only backpressure is the frame
/// loop on each end waiting on a slow destination - which stops every other stream on the tunnel with it. These pin
/// down that one stream stalling, in either direction, leaves the others alone.
/// </summary>
public class TunnelFlowControlTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    internal const string Subdomain = "flow";

    /// <summary>A relay and a tunnelled server, each end configurable, and a client that only talks to the relay.</summary>
    internal sealed class Fixture : IAsyncDisposable
    {
        public required RelayServer Relay { get; init; }
        public required HttpServer Server { get; init; }
        public required RelayTunnelProvider Provider { get; init; }
        public required Task Running { get; init; }
        public required HttpClient Client { get; init; }
        public required CancellationTokenSource Stopping { get; init; }

        public string Host => $"{Subdomain}.localhost";

        public static async Task<Fixture> StartAsync(
            Action<HttpServer> configure,
            Action<RelayServerOptions>? relayOptions = null,
            Action<RelayTunnelOptions>? agentOptions = null
        )
        {
            var relaySettings = new RelayServerOptions
            {
                Address = IPAddress.Loopback,
                ControlPort = 0,
                PublicPort = 0,
                Domain = "localhost",
                Token = "secret"
            };
            relayOptions?.Invoke(relaySettings);

            var relay = new RelayServer(relaySettings);
            await relay.StartAsync();

            var server = new HttpServer(new HttpServerOptions { Port = 0, UseForwardedHeaders = true });
            configure(server);

            var agentSettings = new RelayTunnelOptions
            {
                Host = "127.0.0.1",
                Port = relay.ControlPort,
                Subdomain = Subdomain,
                Token = "secret",
                UseTls = false,
                ReconnectDelay = null
            };
            agentOptions?.Invoke(agentSettings);

            var provider = new RelayTunnelProvider(agentSettings);
            var stopping = new CancellationTokenSource();
            var running = server.RunTunnelAsync(provider, cancellationToken: stopping.Token);

            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (provider.PublicUrl is null && DateTime.UtcNow < deadline)
                await Task.Delay(25);

            Assert.NotNull(provider.PublicUrl);

            return new Fixture
            {
                Relay = relay,
                Server = server,
                Provider = provider,
                Running = running,
                Stopping = stopping,
                Client = new HttpClient
                {
                    BaseAddress = new Uri($"http://127.0.0.1:{relay.PublicPort}"),
                    Timeout = TimeSpan.FromSeconds(30)
                }
            };
        }

        public HttpRequestMessage Request(HttpMethod method, string path)
        {
            var request = new HttpRequestMessage(method, path);
            request.Headers.Host = this.Host;
            return request;
        }

        /// <summary>A raw public connection to the relay, for clients that misbehave in ways HttpClient will not.</summary>
        public async Task<Socket> ConnectRawAsync(int? receiveBuffer = null)
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            if (receiveBuffer is { } size)
                socket.ReceiveBufferSize = size;

            await socket.ConnectAsync(IPAddress.Loopback, this.Relay.PublicPort);
            return socket;
        }

        public async ValueTask DisposeAsync()
        {
            this.Client.Dispose();
            await this.Stopping.CancelAsync();

            try
            {
                await this.Running.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
            }

            await this.Provider.DisposeAsync();
            await this.Server.DisposeAsync();
            await this.Relay.DisposeAsync();
            this.Stopping.Dispose();
        }
    }

    static readonly byte[] Chunk = new byte[64 * 1024];

    static void MapBigDownload(HttpServer app, long size) => app.MapGet("/big", async ctx =>
    {
        ctx.Response.ContentType = "application/octet-stream";
        ctx.Response.ContentLength = size;

        try
        {
            for (long sent = 0; sent < size; sent += Chunk.Length)
                await ctx.Response.Body.WriteAsync(Chunk, ctx.RequestAborted);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException)
        {
            // The stalled client is torn down when the test ends.
        }
    });

    [Fact]
    public async Task A_public_client_that_stops_reading_does_not_stall_other_requests()
    {
        await using var fixture = await Fixture.StartAsync(app =>
        {
            MapBigDownload(app, 256L * 1024 * 1024);
            app.MapGet("/ok", ctx => ctx.Response.WriteAsync("ok"));
        });

        // Asks for a large download and never reads it: once the socket buffers fill, every write to it waits.
        using var stalled = await fixture.ConnectRawAsync(receiveBuffer: 4096);
        await stalled.SendAsync(Encoding.ASCII.GetBytes($"GET /big HTTP/1.1\r\nHost: {fixture.Host}\r\n\r\n"), Token);
        await Task.Delay(1500, Token);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        var response = await fixture.Client.SendAsync(fixture.Request(HttpMethod.Get, "/ok"), timeout.Token);
        Assert.Equal("ok", await response.Content.ReadAsStringAsync(timeout.Token));
        Assert.True(fixture.Provider.FlowControlNegotiated);
    }

    [Fact]
    public async Task A_handler_that_ignores_its_upload_does_not_stall_other_requests()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var fixture = await Fixture.StartAsync(app =>
        {
            // Never reads the body: holds on until the test lets it go, as a handler busy with something else would.
            app.MapPost("/hold", async ctx =>
            {
                await release.Task.WaitAsync(ctx.RequestAborted);
                await ctx.Response.WriteAsync("held");
            });
            app.MapGet("/ok", ctx => ctx.Response.WriteAsync("ok"));
        });

        const long Size = 256L * 1024 * 1024;
        using var uploader = await fixture.ConnectRawAsync();
        await uploader.SendAsync(Encoding.ASCII.GetBytes($"POST /hold HTTP/1.1\r\nHost: {fixture.Host}\r\nContent-Length: {Size}\r\n\r\n"), Token);

        using var stopUploading = new CancellationTokenSource();
        var uploading = Task.Run(async () =>
        {
            try
            {
                for (long sent = 0; sent < Size && !stopUploading.IsCancellationRequested; sent += Chunk.Length)
                    await uploader.SendAsync(Chunk, stopUploading.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
            }
        });

        try
        {
            await Task.Delay(1500, Token);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));

            var response = await fixture.Client.SendAsync(fixture.Request(HttpMethod.Get, "/ok"), timeout.Token);
            Assert.Equal("ok", await response.Content.ReadAsStringAsync(timeout.Token));
            Assert.True(fixture.Provider.FlowControlNegotiated);
        }
        finally
        {
            release.TrySetResult();
            await stopUploading.CancelAsync();
            uploader.Close();
            await uploading;
        }
    }

    // ---- Old and new ends together ----

    static byte[] Random(int size, int seed)
    {
        var data = new byte[size];
        new System.Random(seed).NextBytes(data);
        return data;
    }

    static void MapTraffic(HttpServer app, byte[] download)
    {
        app.MapGet("/small", ctx => ctx.Response.WriteAsync("small"));
        app.MapGet("/download", ctx => ctx.Response.WriteBytesAsync(download, "application/octet-stream", ctx.RequestAborted));
        app.MapPost("/echo", async ctx =>
        {
            using var body = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(body, ctx.RequestAborted);
            await ctx.Response.WriteBytesAsync(body.ToArray(), "application/octet-stream", ctx.RequestAborted);
        });
    }

    static async Task AssertTrafficAsync(Fixture fixture, byte[] download)
    {
        var small = await fixture.Client.SendAsync(fixture.Request(HttpMethod.Get, "/small"), Token);
        Assert.Equal("small", await small.Content.ReadAsStringAsync(Token));

        var downloaded = await fixture.Client.SendAsync(fixture.Request(HttpMethod.Get, "/download"), Token);
        Assert.Equal(download, await downloaded.Content.ReadAsByteArrayAsync(Token));

        var upload = Random(3 * 1024 * 1024 + 17, 2);
        var echo = fixture.Request(HttpMethod.Post, "/echo");
        echo.Content = new ByteArrayContent(upload);
        var echoed = await fixture.Client.SendAsync(echo, Token);
        Assert.Equal(upload, await echoed.Content.ReadAsByteArrayAsync(Token));

        // Many at once, each on its own stream.
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 24).Select(async _ =>
        {
            var response = await fixture.Client.SendAsync(fixture.Request(HttpMethod.Get, "/download"), Token);
            return await response.Content.ReadAsByteArrayAsync(Token);
        }));
        Assert.All(concurrent, body => Assert.Equal(download, body));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Every_pairing_of_old_and_new_ends_serves_correctly(bool relayFlow, bool agentFlow)
    {
        var download = Random(5 * 1024 * 1024 + 3, 1);

        await using var fixture = await Fixture.StartAsync(
            app => MapTraffic(app, download),
            relay => relay.EnableFlowControl = relayFlow,
            agent => agent.EnableFlowControl = agentFlow
        );

        await AssertTrafficAsync(fixture, download);

        // Flow control only when both ends speak it; otherwise the streams are served as before.
        var negotiated = relayFlow && agentFlow;
        Assert.Equal(negotiated, fixture.Provider.FlowControlNegotiated);
        Assert.Equal(negotiated, fixture.Relay.FindSession(fixture.Host)!.FlowControlNegotiated);
    }

    [Fact]
    public async Task The_smallest_windows_carry_large_bodies_intact()
    {
        // One frame's worth of window each way: every frame waits for its grant, so the credit path is exercised hard.
        var download = Random(9 * 1024 * 1024 + 11, 3);

        await using var fixture = await Fixture.StartAsync(
            app => MapTraffic(app, download),
            relay => relay.StreamWindowSize = TunnelSettings.MinimumWindow,
            agent => agent.StreamWindowSize = TunnelSettings.MinimumWindow
        );

        await AssertTrafficAsync(fixture, download);
        Assert.True(fixture.Provider.FlowControlNegotiated);
    }

    [Fact]
    public async Task Stream_slots_are_all_given_back_after_traffic()
    {
        var download = Random(1024 * 1024, 4);

        await using var fixture = await Fixture.StartAsync(app => MapTraffic(app, download));

        await AssertTrafficAsync(fixture, download);

        // A stream lasts as long as its public connection, and the client keeps those alive: closed first.
        fixture.Client.Dispose();

        var session = fixture.Relay.FindSession(fixture.Host)!;
        await Wait.UntilAsync(() => session.FlowStreams == 0 && fixture.Provider.FlowStreams == 0, Token);

        Assert.Equal(0, session.FlowStreams);
        Assert.Equal(0, fixture.Provider.FlowStreams);
    }

    [Fact]
    public async Task A_tunnel_at_its_stream_limit_answers_503_and_recovers()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;

        await using var fixture = await Fixture.StartAsync(
            app =>
            {
                app.MapGet("/hold", async ctx =>
                {
                    Interlocked.Increment(ref entered);
                    await release.Task.WaitAsync(ctx.RequestAborted);
                    await ctx.Response.WriteAsync("held");
                });
                app.MapGet("/ok", ctx => ctx.Response.WriteAsync("ok"));
            },
            relay => relay.MaxStreamsPerTunnel = 2
        );

        // Each on a client of its own, so each is its own public connection and its own stream.
        using var first = new HttpClient { BaseAddress = fixture.Client.BaseAddress };
        using var second = new HttpClient { BaseAddress = fixture.Client.BaseAddress };
        var holding = new[]
        {
            first.SendAsync(fixture.Request(HttpMethod.Get, "/hold"), Token),
            second.SendAsync(fixture.Request(HttpMethod.Get, "/hold"), Token)
        };
        await Wait.UntilAsync(() => Volatile.Read(ref entered) == 2, Token);

        var refused = await fixture.Client.SendAsync(fixture.Request(HttpMethod.Get, "/ok"), Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);

        release.SetResult();
        foreach (var response in await Task.WhenAll(holding))
            Assert.Equal("held", await response.Content.ReadAsStringAsync(Token));

        // The connections that carried them close when their clients go; until then they still count.
        first.Dispose();
        second.Dispose();
        var session = fixture.Relay.FindSession(fixture.Host)!;
        await Wait.UntilAsync(() => session.FlowStreams == 0, Token);

        using var fresh = new HttpClient { BaseAddress = fixture.Client.BaseAddress };
        var ok = await fresh.SendAsync(fixture.Request(HttpMethod.Get, "/ok"), Token);
        Assert.Equal("ok", await ok.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task A_public_client_that_stops_reading_is_disconnected_after_the_write_timeout()
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var fixture = await Fixture.StartAsync(
            app => app.MapGet("/big", async ctx =>
            {
                try
                {
                    for (var i = 0; i < 4096; i++)
                        await ctx.Response.Body.WriteAsync(Chunk, ctx.RequestAborted);
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException)
                {
                }
                finally
                {
                    finished.TrySetResult();
                }
            }),
            relay => relay.PublicWriteTimeout = TimeSpan.FromSeconds(1)
        );

        using var stalled = await fixture.ConnectRawAsync(receiveBuffer: 4096);
        await stalled.SendAsync(Encoding.ASCII.GetBytes($"GET /big HTTP/1.1\r\nHost: {fixture.Host}\r\n\r\n"), Token);

        // The relay gives up on the client, which ends the stream, which lets the app's handler finish.
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(20), Token);

        var session = fixture.Relay.FindSession(fixture.Host)!;
        await Wait.UntilAsync(() => session.FlowStreams == 0, Token);
        Assert.Equal(0, session.FlowStreams);
    }
}
