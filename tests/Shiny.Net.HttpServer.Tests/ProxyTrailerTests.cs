using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Grpc.Core;
using Grpc.Net.Client;
using Shiny.Net.HttpServer.Grpc;
using Shiny.Net.HttpServer.Proxy;
using Shiny.Net.HttpServer.Telemetry;

namespace Shiny.Net.HttpServer.Tests;

/// <summary>
/// Response trailers through <see cref="HttpForwarder"/> — which is what gRPC's status rides on — and
/// the proxy's metrics.
/// </summary>
public class ProxyTrailerTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    static readonly Marshaller<string> Utf8 = Marshallers.Create(Encoding.UTF8.GetBytes, bytes => Encoding.UTF8.GetString(bytes));

    /// <summary>An edge forwarding everything to <paramref name="upstreamPort"/> over cleartext HTTP/2.</summary>
    static Task<TestServer> Edge(int upstreamPort) => TestServer.StartAsync(server => server.MapProxy(
        "/{*path}",
        $"http://127.0.0.1:{upstreamPort}",
        o =>
        {
            o.RequestVersion = HttpVersion.Version20;
            o.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        }
    ));

    static HttpClient Http2Client(int port) => new(new SocketsHttpHandler())
    {
        BaseAddress = new Uri($"http://127.0.0.1:{port}"),
        DefaultRequestVersion = HttpVersion.Version20,
        DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
    };

    [Fact]
    public async Task Relays_upstream_trailers_to_an_http2_caller()
    {
        await using var upstream = await TestServer.StartAsync(app => app.MapGet("/stream", async ctx =>
        {
            await ctx.Response.WriteTextAsync("body", cancellationToken: Token);
            ctx.Response.AppendTrailer("x-checksum", "abc123");
        }));
        await using var edge = await Edge(upstream.Port);

        using var client = Http2Client(edge.Port);
        var response = await client.GetAsync("/stream", Token);

        Assert.Equal("body", await response.Content.ReadAsStringAsync(Token));
        Assert.Equal("abc123", response.TrailingHeaders.GetValues("x-checksum").Single());
    }

    [Fact]
    public async Task Relays_upstream_trailers_to_an_http11_caller_after_the_last_chunk()
    {
        await using var upstream = await TestServer.StartAsync(app => app.MapGet("/stream", async ctx =>
        {
            ctx.Response.DeclareTrailer("X-Checksum");
            await ctx.Response.Body.WriteAsync("body"u8.ToArray(), Token);
            ctx.Response.AppendTrailer("X-Checksum", "abc123");
        }));

        // HTTP/1.1 all the way: the upstream's Trailer announcement has to survive the hop too.
        await using var edge = await TestServer.StartAsync(server => server.MapProxy("/{*path}", $"http://127.0.0.1:{upstream.Port}"));

        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(IPAddress.Loopback, edge.Port, Token);
        await socket.SendAsync(Encoding.ASCII.GetBytes("GET /stream HTTP/1.1\r\nHost: localhost\r\nTE: trailers\r\nConnection: close\r\n\r\n"), Token);

        var raw = new StringBuilder();
        var buffer = new byte[8192];
        int read;
        while ((read = await socket.ReceiveAsync(buffer, SocketFlags.None, Token)) > 0)
            raw.Append(Encoding.ASCII.GetString(buffer, 0, read));

        var text = raw.ToString();
        Assert.True(text.Contains("Trailer: X-Checksum"), text);
        Assert.True(text.Contains("\r\n0\r\nX-Checksum: abc123\r\n\r\n"), text.Replace("\r\n", "<CRLF>"));
        Assert.Contains("\r\n0\r\nX-Checksum: abc123\r\n\r\n", text);
    }

    [Fact]
    public async Task Passes_te_trailers_upstream_and_nothing_else_from_te()
    {
        await using var upstream = await TestServer.StartAsync(app => app.MapGet("/te", ctx =>
            ctx.Response.WriteTextAsync(ctx.Request.Headers.GetFirst("TE") ?? "(none)", cancellationToken: Token)));
        await using var edge = await TestServer.StartAsync(server => server.MapProxy("/{*path}", $"http://127.0.0.1:{upstream.Port}"));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/te");
        request.Headers.TryAddWithoutValidation("TE", "gzip, trailers");

        var response = await edge.Client.SendAsync(request, Token);
        Assert.Equal("trailers", await response.Content.ReadAsStringAsync(Token));

        Assert.Equal("(none)", await edge.Client.GetStringAsync("/te", Token));
    }

    [Fact]
    public async Task Proxies_grpc_calls_with_their_status()
    {
        await using var upstream = await TestServer.StartAsync(app => app.MapGrpcService("test.Echo", svc =>
        {
            svc.AddMarshaller<string>(Encoding.UTF8.GetBytes, bytes => Encoding.UTF8.GetString(bytes));
            svc.MapUnary<string, string>("Say", (request, _) => new ValueTask<string>($"hello {request}"));
            svc.MapUnary<string, string>("Missing", (_, _) => throw new GrpcStatusException(GrpcStatusCode.NotFound, "no such greeting"));
        }));
        await using var edge = await Edge(upstream.Port);

        using var channel = GrpcChannel.ForAddress($"http://127.0.0.1:{edge.Port}", new GrpcChannelOptions { HttpHandler = new SocketsHttpHandler() });
        var invoker = channel.CreateCallInvoker();

        var reply = await invoker.AsyncUnaryCall(
            new Method<string, string>(MethodType.Unary, "test.Echo", "Say", Utf8, Utf8),
            null,
            new CallOptions(cancellationToken: Token),
            "world"
        );
        Assert.Equal("hello world", reply);

        var error = await Assert.ThrowsAsync<RpcException>(async () => await invoker.AsyncUnaryCall(
            new Method<string, string>(MethodType.Unary, "test.Echo", "Missing", Utf8, Utf8),
            null,
            new CallOptions(cancellationToken: Token),
            "x"
        ));
        Assert.Equal(StatusCode.NotFound, error.StatusCode);
        Assert.Equal("no such greeting", error.Status.Detail);
    }

    [Fact]
    public async Task Reports_upstream_duration_and_errors()
    {
        await using var upstream = await TestServer.StartAsync(app => app.MapGet("/ok", ctx => ctx.Response.WriteTextAsync("ok", cancellationToken: Token)));

        var measurements = new ConcurrentQueue<(string Instrument, double Value, Dictionary<string, object?> Tags)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == HttpServerTelemetry.MeterName && instrument.Name.StartsWith("shiny.proxy.", StringComparison.Ordinal))
                    l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((i, v, t, _) => measurements.Enqueue((i.Name, v, t.ToArray().ToDictionary(x => x.Key, x => x.Value))));
        listener.SetMeasurementEventCallback<double>((i, v, t, _) => measurements.Enqueue((i.Name, v, t.ToArray().ToDictionary(x => x.Key, x => x.Value))));
        listener.Start();

        // A port nothing listens on, found by binding and letting go.
        int dead;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            dead = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        await using var edge = await TestServer.StartAsync(server =>
        {
            server.MapProxy("/good/{*path}", $"http://127.0.0.1:{upstream.Port}");
            server.MapProxy("/bad/{*path}", $"http://127.0.0.1:{dead}");
        });

        Assert.Equal("ok", await edge.Client.GetStringAsync("/good/ok", Token));
        Assert.Equal(HttpStatusCode.BadGateway, (await edge.Client.GetAsync("/bad/ok", Token)).StatusCode);

        var good = $"127.0.0.1:{upstream.Port}";
        var bad = $"127.0.0.1:{dead}";

        var duration = Assert.Single(measurements, x => x.Instrument == "shiny.proxy.upstream.request.duration" && Equals(x.Tags["server.address"], good));
        Assert.Equal(200, duration.Tags["http.response.status_code"]);
        Assert.Equal("GET", duration.Tags["http.request.method"]);

        var failed = Assert.Single(measurements, x => x.Instrument == "shiny.proxy.upstream.request.duration" && Equals(x.Tags["server.address"], bad));
        Assert.Equal("Request", failed.Tags["error.type"]);

        var error = Assert.Single(measurements, x => x.Instrument == "shiny.proxy.upstream.errors" && Equals(x.Tags.GetValueOrDefault("server.address"), bad));
        Assert.Equal("Request", error.Tags["error.type"]);
        Assert.DoesNotContain(measurements, x => x.Instrument == "shiny.proxy.upstream.errors" && Equals(x.Tags.GetValueOrDefault("server.address"), good));
    }
}
