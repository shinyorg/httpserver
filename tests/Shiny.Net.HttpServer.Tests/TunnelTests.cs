using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Text;
using Shiny.Net.HttpServer.Transports;
using Shiny.Net.HttpServer.Tunneling;

namespace Shiny.Net.HttpServer.Tests;

public class TunnelProtocolTests
{
    static ReadOnlySequence<byte> Framed(params (TunnelFrameType Type, uint Stream, string Payload)[] frames)
    {
        var pipe = new Pipe();
        foreach (var (type, stream, payload) in frames)
            TunnelProtocol.Write(pipe.Writer, type, stream, Encoding.UTF8.GetBytes(payload));

        pipe.Writer.FlushAsync().AsTask().GetAwaiter().GetResult();
        var result = pipe.Reader.ReadAsync().AsTask().GetAwaiter().GetResult();

        return result.Buffer;
    }

    [Fact]
    public void Round_trips_a_frame()
    {
        var buffer = Framed((TunnelFrameType.Data, 7, "hello"));

        Assert.True(TunnelProtocol.TryRead(ref buffer, out var type, out var streamId, out var payload));
        Assert.Equal(TunnelFrameType.Data, type);
        Assert.Equal(7u, streamId);
        Assert.Equal("hello", Encoding.UTF8.GetString(payload.ToArray()));
        Assert.True(buffer.IsEmpty);
    }

    [Fact]
    public void Reads_several_frames_from_one_buffer()
    {
        var buffer = Framed(
            (TunnelFrameType.Open, 1, "127.0.0.1:1234"),
            (TunnelFrameType.Data, 1, "body"),
            (TunnelFrameType.CloseStream, 1, "")
        );

        Assert.True(TunnelProtocol.TryRead(ref buffer, out var first, out _, out _));
        Assert.True(TunnelProtocol.TryRead(ref buffer, out var second, out _, out var body));
        Assert.True(TunnelProtocol.TryRead(ref buffer, out var third, out _, out var empty));

        Assert.Equal(TunnelFrameType.Open, first);
        Assert.Equal(TunnelFrameType.Data, second);
        Assert.Equal("body", Encoding.UTF8.GetString(body.ToArray()));
        Assert.Equal(TunnelFrameType.CloseStream, third);
        Assert.True(empty.IsEmpty);
    }

    [Fact]
    public void Leaves_a_partial_frame_untouched()
    {
        var complete = Framed((TunnelFrameType.Data, 1, "hello")).ToArray();

        // Everything except the last payload byte.
        var partial = new ReadOnlySequence<byte>(complete.AsMemory(0, complete.Length - 1));
        var buffer = partial;

        Assert.False(TunnelProtocol.TryRead(ref buffer, out _, out _, out _));
        Assert.Equal(partial.Length, buffer.Length);
    }

    [Fact]
    public void Leaves_a_partial_header_untouched()
    {
        var buffer = new ReadOnlySequence<byte>(new byte[TunnelProtocol.HeaderLength - 1]);
        Assert.False(TunnelProtocol.TryRead(ref buffer, out _, out _, out _));
    }

    [Fact]
    public void Rejects_an_oversized_declared_payload()
    {
        var header = new byte[TunnelProtocol.HeaderLength];
        header[0] = (byte)TunnelFrameType.Data;
        // Declare a payload far beyond the cap.
        header[5] = 0xFF;
        header[6] = 0xFF;
        header[7] = 0xFF;
        header[8] = 0xFF;

        var buffer = new ReadOnlySequence<byte>(header);
        Assert.Throws<TunnelProtocolException>(() =>
        {
            var local = buffer;
            TunnelProtocol.TryRead(ref local, out _, out _, out _);
        });
    }

    [Fact]
    public void Rejects_a_payload_above_the_cap_when_writing()
    {
        var pipe = new Pipe();
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TunnelProtocol.Write(pipe.Writer, TunnelFrameType.Data, 1, new byte[TunnelProtocol.MaxPayloadLength + 1])
        );
    }
}

public class DuplexPipeConnectionTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Carries_bytes_from_the_transport_to_the_application()
    {
        await using var connection = new DuplexPipeConnection("test", new IPEndPoint(IPAddress.Loopback, 1));

        await connection.TransportWriter.WriteAsync("inbound"u8.ToArray(), Token);

        var result = await connection.Input.ReadAsync(Token);
        Assert.Equal("inbound", Encoding.ASCII.GetString(result.Buffer.ToArray()));
    }

    [Fact]
    public async Task Carries_bytes_from_the_application_to_the_transport()
    {
        await using var connection = new DuplexPipeConnection("test");

        await connection.Output.WriteAsync("outbound"u8.ToArray(), Token);

        var result = await connection.TransportReader.ReadAsync(Token);
        Assert.Equal("outbound", Encoding.ASCII.GetString(result.Buffer.ToArray()));
    }

    [Fact]
    public async Task Reports_itself_as_tunnelled()
    {
        await using var connection = new DuplexPipeConnection("test");
        Assert.True(connection.IsTunneled);
    }

    [Fact]
    public async Task Signals_Aborted_when_aborted()
    {
        var connection = new DuplexPipeConnection("test");

        Assert.False(connection.Aborted.IsCancellationRequested);
        connection.Abort();
        Assert.True(connection.Aborted.IsCancellationRequested);

        await connection.DisposeAsync();
    }

    [Fact]
    public async Task Serves_a_real_request_without_a_socket()
    {
        // The point of the abstraction, in one test: no listener, no port, no socket — the same
        // pipeline that serves TCP serves an in-memory connection identically.
        var server = new HttpServer(new HttpServerOptions { Port = 0 });
        server.OnGet("/ping", ctx => ctx.Response.WriteAsync("pong"));

        await using (server)
        {
            var connection = new DuplexPipeConnection("in-memory");
            var serving = server.ServeAsync(connection, Token);

            await connection.TransportWriter.WriteAsync(
                "GET /ping HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n"u8.ToArray(),
                Token
            );

            var response = new StringBuilder();
            while (!response.ToString().Contains("pong"))
            {
                var result = await connection.TransportReader.ReadAsync(Token);
                response.Append(Encoding.ASCII.GetString(result.Buffer.ToArray()));
                connection.TransportReader.AdvanceTo(result.Buffer.End);

                if (result.IsCompleted)
                    break;
            }

            Assert.Contains("HTTP/1.1 200 OK", response.ToString());
            Assert.Contains("pong", response.ToString());

            await connection.TransportWriter.CompleteAsync();
            await serving.WaitAsync(TimeSpan.FromSeconds(10), Token);
        }
    }
}

public class TunnelEndToEndTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// A relay, a tunnelled server, and an HTTP client that only ever talks to the relay. Every
    /// hop is real: two listeners, an outbound registration, and frames on the wire.
    /// </summary>
    sealed class TunnelFixture : IAsyncDisposable
    {
        public required RelayServer Relay { get; init; }
        public required HttpServer Server { get; init; }
        public required RelayTunnelProvider Provider { get; init; }
        public required Task Running { get; init; }
        public required HttpClient Client { get; init; }
        public required string Host { get; init; }
        public required CancellationTokenSource Stopping { get; init; }

        public static async Task<TunnelFixture> StartAsync(Action<HttpServer> configure, string subdomain = "test")
        {
            var relay = new RelayServer(new RelayServerOptions
            {
                Address = IPAddress.Loopback,
                ControlPort = 0,
                PublicPort = 0,
                Domain = "localhost",
                Token = "secret"
            });

            await relay.StartAsync();

            var server = new HttpServer(new HttpServerOptions { Port = 0, UseForwardedHeaders = true });
            configure(server);

            var provider = new RelayTunnelProvider(new RelayTunnelOptions
            {
                Host = "127.0.0.1",
                Port = relay.ControlPort,
                Subdomain = subdomain,
                Token = "secret",
                UseTls = false,
                ReconnectDelay = null
            });

            var stopping = new CancellationTokenSource();
            var running = server.RunTunnelAsync(provider, cancellationToken: stopping.Token);

            // RunTunnelAsync binds before accepting; wait for registration to land.
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (provider.PublicUrl is null && DateTime.UtcNow < deadline)
                await Task.Delay(25);

            Assert.NotNull(provider.PublicUrl);

            return new TunnelFixture
            {
                Relay = relay,
                Server = server,
                Provider = provider,
                Running = running,
                Stopping = stopping,
                Host = $"{subdomain}.localhost",
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

    [Fact]
    public async Task Serves_a_request_arriving_through_the_tunnel()
    {
        await using var fixture = await TunnelFixture.StartAsync(
            app => app.OnGet("/ping", ctx => ctx.Response.WriteAsync("pong"))
        );

        var response = await fixture.Client.SendAsync(fixture.Request(HttpMethod.Get, "/ping"), Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("pong", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Reports_the_public_url_the_relay_assigned()
    {
        await using var fixture = await TunnelFixture.StartAsync(app => app.OnGet("/x", ctx => ctx.Response.WriteAsync("x")), "named");

        Assert.Contains("named.localhost", fixture.Provider.PublicUrl);
        Assert.Contains("named.localhost", fixture.Relay.RegisteredHosts);
    }

    [Fact]
    public async Task Carries_a_request_body_and_a_larger_response()
    {
        var payload = new string('z', 200_000);

        await using var fixture = await TunnelFixture.StartAsync(app => app.OnPost("/echo", async ctx =>
        {
            var body = await ctx.Request.ReadBodyAsStringAsync(
                maxLength: 1024 * 1024,
                cancellationToken: ctx.RequestAborted
            );
            await ctx.Response.WriteAsync(body + body);
        }));

        var request = fixture.Request(HttpMethod.Post, "/echo");
        request.Content = new StringContent(payload);

        var response = await fixture.Client.SendAsync(request, Token);
        var text = await response.Content.ReadAsStringAsync(Token);

        // Comfortably past the 64 KiB frame cap in both directions, so this covers splitting and
        // reassembly rather than just the happy single-frame path.
        Assert.Equal(payload.Length * 2, text.Length);
    }

    [Fact]
    public async Task Reuses_one_tunnelled_connection_for_several_requests()
    {
        await using var fixture = await TunnelFixture.StartAsync(app => app.OnGet("/id", ctx =>
            ctx.Response.WriteAsync(ctx.Connection.ConnectionId)));

        var first = await fixture.Client.SendAsync(fixture.Request(HttpMethod.Get, "/id"), Token);
        var second = await fixture.Client.SendAsync(fixture.Request(HttpMethod.Get, "/id"), Token);

        Assert.Equal(
            await first.Content.ReadAsStringAsync(Token),
            await second.Content.ReadAsStringAsync(Token)
        );
    }

    [Fact]
    public async Task Marks_tunnelled_connections_as_tunnelled()
    {
        await using var fixture = await TunnelFixture.StartAsync(app => app.OnGet("/how", ctx =>
            ctx.Response.WriteAsync(ctx.Connection.IsTunneled ? "tunnel" : "socket")));

        var response = await fixture.Client.SendAsync(fixture.Request(HttpMethod.Get, "/how"), Token);
        Assert.Equal("tunnel", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Forwards_the_original_client_address()
    {
        await using var fixture = await TunnelFixture.StartAsync(app => app.OnGet("/who", ctx =>
            ctx.Response.WriteAsync(
                $"{ctx.Request.Headers.GetFirst("X-Forwarded-For")}|{ctx.Request.Headers.GetFirst("X-Forwarded-Host")}"
            )));

        var response = await fixture.Client.SendAsync(fixture.Request(HttpMethod.Get, "/who"), Token);
        var text = await response.Content.ReadAsStringAsync(Token);

        Assert.Contains("127.0.0.1", text);
        Assert.Contains("test.localhost", text);
    }

    [Fact]
    public async Task Returns_404_for_a_host_with_no_tunnel()
    {
        await using var fixture = await TunnelFixture.StartAsync(app => app.OnGet("/x", ctx => ctx.Response.WriteAsync("x")));

        var request = new HttpRequestMessage(HttpMethod.Get, "/x");
        request.Headers.Host = "nobody.localhost";

        var response = await fixture.Client.SendAsync(request, Token);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Rejects_a_registration_with_the_wrong_token()
    {
        var relay = new RelayServer(new RelayServerOptions
        {
            Address = IPAddress.Loopback,
            ControlPort = 0,
            PublicPort = 0,
            Token = "secret"
        });

        await using (relay)
        {
            await relay.StartAsync(Token);

            var provider = new RelayTunnelProvider(new RelayTunnelOptions
            {
                Host = "127.0.0.1",
                Port = relay.ControlPort,
                Token = "wrong",
                UseTls = false,
                ReconnectDelay = null,
                HandshakeTimeout = TimeSpan.FromSeconds(10)
            });

            await using (provider)
            {
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await provider.BindAsync(Token));
                Assert.Contains("refused", ex.Message);
            }
        }
    }

    [Fact]
    public async Task Refuses_a_subdomain_that_is_already_taken()
    {
        await using var first = await TunnelFixture.StartAsync(app => app.OnGet("/x", ctx => ctx.Response.WriteAsync("x")), "taken");

        var provider = new RelayTunnelProvider(new RelayTunnelOptions
        {
            Host = "127.0.0.1",
            Port = first.Relay.ControlPort,
            Subdomain = "taken",
            Token = "secret",
            UseTls = false,
            ReconnectDelay = null,
            HandshakeTimeout = TimeSpan.FromSeconds(10)
        });

        await using (provider)
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await provider.BindAsync(Token));
    }

    [Fact]
    public async Task Assigns_a_subdomain_when_none_was_requested()
    {
        await using var fixture = await TunnelFixture.StartAsync(
            app => app.OnGet("/x", ctx => ctx.Response.WriteAsync("x")),
            subdomain: ""
        );

        Assert.NotNull(fixture.Provider.PublicUrl);
        Assert.Single(fixture.Relay.RegisteredHosts);
        Assert.DoesNotContain("..localhost", fixture.Provider.PublicUrl);
    }

    [Fact]
    public async Task Refuses_a_host_switch_on_a_reused_connection()
    {
        await using var fixture = await TunnelFixture.StartAsync(
            app => app.OnGet("/who", ctx => ctx.Response.WriteAsync("mine")),
            "pinned"
        );

        // The first request binds this connection to pinned.localhost. Sending a second request for
        // a different host down the same socket must not reach another tenant's tunnel — 421 tells
        // the client to open a new connection, which is exactly what RFC 9110 defines it for.
        var first = await fixture.Client.SendAsync(fixture.Request(HttpMethod.Get, "/who"), Token);
        Assert.Equal("mine", await first.Content.ReadAsStringAsync(Token));

        var switched = new HttpRequestMessage(HttpMethod.Get, "/who");
        switched.Headers.Host = "somewhere-else.localhost";

        var response = await fixture.Client.SendAsync(switched, Token);
        Assert.Equal(HttpStatusCode.MisdirectedRequest, response.StatusCode);
    }

    [Fact]
    public async Task Handles_concurrent_requests_over_one_tunnel()
    {
        await using var fixture = await TunnelFixture.StartAsync(app => app.OnGet("/slow", async ctx =>
        {
            await Task.Delay(20, ctx.RequestAborted);
            await ctx.Response.WriteAsync("done");
        }));

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 20).Select(async _ =>
            {
                var response = await fixture.Client.SendAsync(fixture.Request(HttpMethod.Get, "/slow"), Token);
                return await response.Content.ReadAsStringAsync(Token);
            })
        );

        Assert.All(responses, r => Assert.Equal("done", r));
    }

    [Fact]
    public async Task Routes_two_tunnels_by_their_host()
    {
        await using var first = await TunnelFixture.StartAsync(app => app.OnGet("/who", ctx => ctx.Response.WriteAsync("first")), "one");

        var second = new HttpServer(new HttpServerOptions { Port = 0 });
        second.OnGet("/who", ctx => ctx.Response.WriteAsync("second"));

        var provider = new RelayTunnelProvider(new RelayTunnelOptions
        {
            Host = "127.0.0.1",
            Port = first.Relay.ControlPort,
            Subdomain = "two",
            Token = "secret",
            UseTls = false,
            ReconnectDelay = null
        });

        using var stopping = new CancellationTokenSource();
        var running = second.RunTunnelAsync(provider, cancellationToken: stopping.Token);

        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (provider.PublicUrl is null && DateTime.UtcNow < deadline)
                await Task.Delay(25, Token);

            // A separate client per host, because that is what every real client does: connection
            // pools are keyed by authority. Reusing one connection across hosts is the case the
            // next test covers.
            using var secondClient = new HttpClient
            {
                BaseAddress = first.Client.BaseAddress,
                Timeout = TimeSpan.FromSeconds(30)
            };

            var toFirst = new HttpRequestMessage(HttpMethod.Get, "/who");
            toFirst.Headers.Host = "one.localhost";
            var toSecond = new HttpRequestMessage(HttpMethod.Get, "/who");
            toSecond.Headers.Host = "two.localhost";

            Assert.Equal("first", await (await first.Client.SendAsync(toFirst, Token)).Content.ReadAsStringAsync(Token));
            Assert.Equal("second", await (await secondClient.SendAsync(toSecond, Token)).Content.ReadAsStringAsync(Token));
        }
        finally
        {
            await stopping.CancelAsync();
            try
            {
                await running.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
            }

            await provider.DisposeAsync();
            await second.DisposeAsync();
        }
    }
}
