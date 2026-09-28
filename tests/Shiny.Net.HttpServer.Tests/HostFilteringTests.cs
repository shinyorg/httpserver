using System.IO.Pipelines;
using System.Net;
using System.Text;
using Shiny.Net.HttpServer.Http2.Hpack;
using Shiny.Net.HttpServer.Http3;
using Shiny.Net.HttpServer.Security;
using Shiny.Net.HttpServer.Testing;
using Shiny.Net.HttpServer.Transports;
using Shiny.Net.HttpServer.Tunneling;
using Shiny.Net.HttpServer.WebSockets;

namespace Shiny.Net.HttpServer.Tests;

public class HostMatcherTests
{
    static HostMatcher Matcher(Action<HostFilteringOptions>? configure = null)
    {
        var options = new HostFilteringOptions();
        configure?.Invoke(options);
        return new HostMatcher(options);
    }

    [Theory]
    [InlineData("device.example.com", true)]
    [InlineData("DEVICE.Example.COM", true)]
    [InlineData("device.example.com:8443", true)]
    [InlineData("device.example.com.", true)]
    [InlineData("other.example.com", false)]
    [InlineData("device.example.com.evil.test", false)]
    [InlineData("a.api.example.org", true)]
    [InlineData("a.b.api.example.org:81", true)]
    [InlineData("api.example.org", false)]
    [InlineData("evilapi.example.org", false)]
    public void Matches_exact_names_and_wildcards_ignoring_case_and_port(string host, bool expected)
    {
        var matcher = Matcher(o =>
        {
            o.AllowedHosts.Add("device.example.com");
            o.AllowedHosts.Add("*.api.example.org");
        });

        Assert.Equal(expected, matcher.IsAllowed(host));
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("localhost:5000")]
    [InlineData("app.localhost")]
    [InlineData("127.0.0.1:8080")]
    [InlineData("127.9.9.9")]
    [InlineData("[::1]:8080")]
    [InlineData("[::1]")]
    public void Admits_loopback_names_by_default(string host)
        => Assert.True(Matcher(o => o.AllowIpAddressHosts = false).IsAllowed(host));

    [Fact]
    public void Loopback_names_can_be_switched_off()
    {
        var matcher = Matcher(o =>
        {
            o.AllowLoopbackHosts = false;
            o.AllowIpAddressHosts = false;
        });

        Assert.False(matcher.IsAllowed("localhost"));
        Assert.False(matcher.IsAllowed("127.0.0.1"));
        Assert.False(matcher.IsAllowed("[::1]"));
    }

    [Theory]
    [InlineData("192.168.1.20")]
    [InlineData("192.168.1.20:8080")]
    [InlineData("[fe80::1]:8080")]
    [InlineData("[2001:db8::7]")]
    public void Admits_ip_literals_by_default_because_they_cannot_be_rebound(string host)
        => Assert.True(Matcher().IsAllowed(host));

    [Fact]
    public void Admits_only_listed_ip_literals_when_asked_to()
    {
        var matcher = Matcher(o =>
        {
            o.AllowIpAddressHosts = false;
            o.AllowedHosts.Add("192.168.1.20");
            o.AllowedHosts.Add("fe80::1");
            o.AllowedHosts.Add("[2001:db8::7]:443");
        });

        Assert.True(matcher.IsAllowed("192.168.1.20:8080"));
        Assert.True(matcher.IsAllowed("[fe80:0:0::1]:8080"));
        Assert.True(matcher.IsAllowed("[2001:db8::7]"));
        Assert.False(matcher.IsAllowed("192.168.1.21"));
        Assert.False(matcher.IsAllowed("[fe80::2]"));
    }

    [Theory]
    [InlineData("evil.example")]
    [InlineData("::1")]                 // unbracketed IPv6 is not a Host
    [InlineData("[::1")]
    [InlineData("[::1]x")]
    [InlineData("[192.168.1.1]")]       // brackets are for IPv6 only
    [InlineData("host:port")]
    [InlineData("host:99999")]
    [InlineData("ho st")]
    [InlineData("host/path")]
    [InlineData("user@host")]
    [InlineData("0x7f.1")]              // what IPAddress.TryParse calls loopback, and a browser never sends
    [InlineData("2130706433")]
    public void Refuses_names_it_was_not_given_and_hosts_that_are_malformed(string host)
        => Assert.False(Matcher(o => o.AllowLoopbackHosts = false).IsAllowed(host));

    [Fact]
    public void Treats_a_missing_host_according_to_AllowEmptyHosts()
    {
        Assert.True(Matcher().IsAllowed(null));
        Assert.True(Matcher().IsAllowed(""));
        Assert.False(Matcher(o => o.AllowEmptyHosts = false).IsAllowed(null));
        Assert.False(Matcher(o => o.AllowEmptyHosts = false).IsAllowed(" "));
    }

    [Fact]
    public void A_star_entry_admits_everything()
        => Assert.True(Matcher(o => o.AllowedHosts.Add("*")).IsAllowed("anything.at.all"));

    [Theory]
    [InlineData("")]
    [InlineData("*.")]
    [InlineData("*.192.168.1.1")]
    [InlineData("bad host")]
    [InlineData("http://example.com")]
    public void A_malformed_entry_fails_when_the_matcher_is_built(string entry)
        => Assert.Throws<ArgumentException>(() => Matcher(o => o.AllowedHosts.Add(entry)));

    [Fact]
    public void A_public_url_is_read_each_time_so_a_new_tunnel_address_is_picked_up()
    {
        string? url = null;
        var matcher = Matcher(o => o.AllowPublicUrl(() => url));

        Assert.False(matcher.IsAllowed("abc.trycloudflare.com"));

        url = "https://abc.trycloudflare.com";
        Assert.True(matcher.IsAllowed("abc.trycloudflare.com"));
        Assert.True(matcher.IsAllowed("ABC.trycloudflare.com:443"));

        url = "https://def.trycloudflare.com/";
        Assert.False(matcher.IsAllowed("abc.trycloudflare.com"));
        Assert.True(matcher.IsAllowed("def.trycloudflare.com"));
    }

    [Fact]
    public void A_tunnel_provider_admits_its_public_url()
    {
        var provider = new StubTunnel { PublicUrl = "https://device-7.relay.example" };
        var matcher = Matcher(o => o.AllowTunnel(provider));

        Assert.True(matcher.IsAllowed("device-7.relay.example"));
        Assert.False(matcher.IsAllowed("device-8.relay.example"));
    }

    sealed class StubTunnel : ITunnelProvider
    {
        public string Name => "stub";
        public string? PublicUrl { get; set; }
        public string ListenDescription => "stub";
        public ValueTask BindAsync(CancellationToken cancellationToken = default) => default;
        public ValueTask<IConnection?> AcceptAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<IConnection?>(null);
        public ValueTask UnbindAsync(CancellationToken cancellationToken = default) => default;
        public ValueTask DisposeAsync() => default;
    }
}

public class HostFilteringMiddlewareTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    static Task<TestServer> StartAsync(Action<HostFilteringOptions>? configure = null, Action<HttpServerOptions>? server = null)
        => TestServer.StartAsync(
            app =>
            {
                app.UseHostFiltering();
                app.MapGet("/ping", ctx => ctx.Response.WriteAsync($"pong via {ctx.Request.Protocol} to {ctx.Request.Host}"));
            },
            builder =>
            {
                server?.Invoke(builder.Options);
                builder.AddHostFiltering(configure);
            }
        );

    static HttpRequestMessage Get(string host, string path = "/ping", bool http2 = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Host = host;

        if (http2)
        {
            request.Version = HttpVersion.Version20;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        }

        return request;
    }

    [Fact]
    public async Task Answers_a_listed_host()
    {
        await using var server = await StartAsync(o => o.AllowedHosts.Add("printer.local"));

        var response = await server.Client.SendAsync(Get("printer.local:8080"), Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("pong via HTTP/1.1 to printer.local:8080", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Refuses_a_rebound_name_with_problem_details()
    {
        await using var server = await StartAsync(o => o.AllowedHosts.Add("printer.local"));

        var response = await server.Client.SendAsync(Get("evil.example"), Token);
        var body = await response.Content.ReadAsStringAsync(Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("evil.example", body);
    }

    [Fact]
    public async Task Refuses_before_routing_so_an_unmapped_path_does_not_404()
    {
        await using var server = await StartAsync();

        var response = await server.Client.SendAsync(Get("evil.example", "/nothing-here"), Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task With_nothing_configured_admits_loopback_and_addresses_but_not_names()
    {
        await using var server = await StartAsync();

        // The client's own Host — 127.0.0.1:port — is an address literal.
        Assert.Equal(HttpStatusCode.OK, (await server.Client.GetAsync("/ping", Token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await server.Client.SendAsync(Get("localhost"), Token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await server.Client.SendAsync(Get("192.168.1.20:8080"), Token)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await server.Client.SendAsync(Get("myphone.local"), Token)).StatusCode);
    }

    [Fact]
    public async Task Leaves_out_the_host_when_asked_not_to_echo_it()
    {
        await using var server = await StartAsync(o => o.IncludeFailureMessage = false);

        var response = await server.Client.SendAsync(Get("evil.example"), Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("evil.example", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Admits_an_http10_request_without_a_host_unless_told_otherwise()
    {
        await using (var server = await StartAsync())
        {
            var raw = await server.SendRawAsync("GET /ping HTTP/1.0\r\n\r\n");
            Assert.StartsWith("HTTP/1.1 200", raw);
        }

        await using (var server = await StartAsync(o => o.AllowEmptyHosts = false))
        {
            var raw = await server.SendRawAsync("GET /ping HTTP/1.0\r\n\r\n");
            Assert.StartsWith("HTTP/1.1 400", raw);
        }
    }

    [Fact]
    public async Task A_forwarded_host_cannot_launder_a_rebound_name()
    {
        // A rebinding page is same-origin to its browser, so it can set X-Forwarded-Host freely.
        await using var server = await StartAsync(
            o => o.AllowedHosts.Add("printer.local"),
            s => s.UseForwardedHeaders = true
        );

        var laundered = Get("evil.example");
        laundered.Headers.Add("X-Forwarded-Host", "printer.local");
        Assert.Equal(HttpStatusCode.BadRequest, (await server.Client.SendAsync(laundered, Token)).StatusCode);

        // And the other way round: an allowed Host does not carry a disallowed forwarded one.
        var forwarded = Get("printer.local");
        forwarded.Headers.Add("X-Forwarded-Host", "evil.example");
        Assert.Equal(HttpStatusCode.BadRequest, (await server.Client.SendAsync(forwarded, Token)).StatusCode);
    }

    [Fact]
    public async Task Refuses_a_websocket_upgrade_to_a_rebound_name()
    {
        await using var server = await TestServer.StartAsync(
            app =>
            {
                app.UseHostFiltering();
                app.MapGet("/ws", async ctx =>
                {
                    await using var socket = await ctx.AcceptWebSocketAsync(cancellationToken: ctx.RequestAborted);
                });
            }
        );

        var raw = await server.SendRawAsync(
            "GET /ws HTTP/1.1\r\nHost: evil.example\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
            "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n"
        );

        Assert.StartsWith("HTTP/1.1 400", raw);
    }

    [Fact]
    public async Task Works_without_a_container()
    {
        var server = new HttpServer(new HttpServerOptions { Port = 0, Address = IPAddress.Loopback });
        server.UseHostFiltering(o => o.AllowedHosts.Add("printer.local"));
        server.MapGet("/ping", ctx => ctx.Response.WriteAsync("pong"));

        await using (server)
        {
            using var client = server.CreateInMemoryClient();

            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Get("printer.local"), Token)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(Get("evil.example"), Token)).StatusCode);
        }
    }

    [Fact]
    public async Task Filters_http2_by_authority()
    {
        await using var server = await StartAsync(o => o.AllowedHosts.Add("printer.local"));

        using var client = new HttpClient(new SocketsHttpHandler())
        {
            BaseAddress = new Uri($"http://127.0.0.1:{server.Port}"),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };

        var allowed = await client.SendAsync(Get("printer.local", http2: true), Token);
        Assert.Equal(HttpVersion.Version20, allowed.Version);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal("pong via HTTP/2 to printer.local", await allowed.Content.ReadAsStringAsync(Token));

        var refused = await client.SendAsync(Get("evil.example", http2: true), Token);
        Assert.Equal(HttpVersion.Version20, refused.Version);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("evil.example", await refused.Content.ReadAsStringAsync(Token));
    }

    /// <summary>
    /// Through the real HTTP/3 request mapper — the part of the HTTP/3 path host filtering depends
    /// on — because QUIC is not available on every machine the suite runs on.
    /// </summary>
    [Theory]
    [InlineData("printer.local:443", StatusCodes.Status200OK)]
    [InlineData("evil.example:443", StatusCodes.Status400BadRequest)]
    public async Task Filters_http3_by_authority(string authority, int expected)
    {
        var context = new HttpContext();
        var fields = new List<HeaderField>
        {
            new(":method", "GET"),
            new(":scheme", "https"),
            new(":authority", authority),
            new(":path", "/ping")
        };

        Assert.True(Http3RequestMapper.TryApply(context, fields, [], out var error), error);
        Assert.Equal(authority, context.Request.Host);

        var body = new MemoryBodyControl();
        context.Response.Bind(body);

        var options = new HostFilteringOptions();
        options.AllowedHosts.Add("printer.local");
        var reached = false;

        await new HostFilteringMiddleware(options).InvokeAsync(context, _ =>
        {
            reached = true;
            return ValueTask.CompletedTask;
        });

        Assert.Equal(expected == StatusCodes.Status200OK, reached);
        Assert.Equal(expected, context.Response.StatusCode);
    }

    [Fact]
    public async Task Lets_tunnelled_requests_through_by_default()
    {
        var server = new HttpServer(new HttpServerOptions { Port = 0 });
        server.UseHostFiltering(o => o.AllowedHosts.Add("printer.local"));
        server.MapGet("/ping", ctx => ctx.Response.WriteAsync("pong"));

        await using (server)
        {
            var response = await SendThroughTunnelAsync(server, "device-7.relay.example");
            Assert.StartsWith("HTTP/1.1 200", response);
        }
    }

    [Fact]
    public async Task Holds_tunnelled_requests_to_the_list_when_asked_and_admits_the_tunnels_own_name()
    {
        var url = "https://device-7.relay.example";

        var server = new HttpServer(new HttpServerOptions { Port = 0 });
        server.UseHostFiltering(o =>
        {
            o.AllowTunneledConnections = false;
            o.AllowPublicUrl(() => url);
        });
        server.MapGet("/ping", ctx => ctx.Response.WriteAsync("pong"));

        await using (server)
        {
            Assert.StartsWith("HTTP/1.1 200", await SendThroughTunnelAsync(server, "device-7.relay.example"));
            Assert.StartsWith("HTTP/1.1 400", await SendThroughTunnelAsync(server, "evil.example"));
        }
    }

    static async Task<string> SendThroughTunnelAsync(HttpServer server, string host)
    {
        // The same connection type the relay, SSH and Azure Relay providers hand the server.
        var connection = new DuplexPipeConnection("tunnel", isTunneled: true);
        var serving = server.ServeAsync(connection, Token);

        await connection.TransportWriter.WriteAsync(
            Encoding.ASCII.GetBytes($"GET /ping HTTP/1.1\r\nHost: {host}\r\nConnection: close\r\n\r\n"),
            Token
        );

        var response = new StringBuilder();
        while (true)
        {
            var result = await connection.TransportReader.ReadAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(10), Token);
            foreach (var segment in result.Buffer)
                response.Append(Encoding.ASCII.GetString(segment.Span));

            connection.TransportReader.AdvanceTo(result.Buffer.End);
            if (result.IsCompleted)
                break;
        }

        await connection.TransportWriter.CompleteAsync();
        await serving.WaitAsync(TimeSpan.FromSeconds(10), Token);

        return response.ToString();
    }

    sealed class MemoryBodyControl : IResponseBodyControl
    {
        readonly MemoryStream stream = new();
        PipeWriter? writer;

        public bool HasStarted { get; private set; }
        public Stream Stream => this.stream;
        public PipeWriter Writer => this.writer ??= PipeWriter.Create(this.stream);

        public ValueTask StartAsync(CancellationToken cancellationToken)
        {
            this.HasStarted = true;
            return default;
        }

        public ValueTask CompleteAsync(CancellationToken cancellationToken) => default;
    }
}
