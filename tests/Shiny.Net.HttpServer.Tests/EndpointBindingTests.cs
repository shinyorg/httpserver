using System.Net;
using System.Net.Sockets;

namespace Shiny.Net.HttpServer.Tests;

public class EndpointBindingTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Uses_the_shorthand_when_no_endpoints_are_listed()
    {
        var options = new HttpServerOptions { Address = IPAddress.Loopback, Port = 0 };

        await using var server = new HttpServer(options);
        server.OnGet("/ping", ctx => ctx.Response.WriteAsync("pong"));
        await server.StartAsync(Token);

        Assert.Single(server.ListenUrls);
        Assert.Equal(server.ListenUrl, server.ListenUrls[0]);

        using var client = new HttpClient();
        Assert.Equal("pong", await client.GetStringAsync($"{server.ListenUrl}/ping", Token));
    }

    [Fact]
    public async Task Binds_several_cleartext_endpoints_at_once()
    {
        var options = new HttpServerOptions();
        options.Listen(IPAddress.Loopback, 0);
        options.Listen(IPAddress.Loopback, 0);

        await using var server = new HttpServer(options);
        server.OnGet("/ping", ctx => ctx.Response.WriteAsync("pong"));
        await server.StartAsync(Token);

        Assert.Equal(2, server.ListenUrls.Count);
        Assert.NotEqual(server.ListenUrls[0], server.ListenUrls[1]);

        using var client = new HttpClient();
        foreach (var url in server.ListenUrls)
            Assert.Equal("pong", await client.GetStringAsync($"{url}/ping", Token));
    }

    [Fact]
    public async Task Serves_cleartext_and_tls_side_by_side()
    {
        using var certificate = ServerCertificate.Create();

        var options = new HttpServerOptions();
        options.Listen(IPAddress.Loopback, 0);
        options.ListenHttps(IPAddress.Loopback, 0, certificate);

        await using var server = new HttpServer(options);
        server.OnGet("/scheme", ctx => ctx.Response.WriteAsync(ctx.Request.Scheme));
        await server.StartAsync(Token);

        var http = server.ListenUrls[0];
        var https = server.ListenUrls[1];

        Assert.StartsWith("http://", http);
        Assert.StartsWith("https://", https);

        using var plain = new HttpClient();
        Assert.Equal("http", await plain.GetStringAsync($"{http}/scheme", Token));

        using var secure = new HttpClient(CertificatePinning.CreateHandler(certificate));
        Assert.Equal("https", await secure.GetStringAsync($"{https}/scheme", Token));
    }

    [Fact]
    public async Task Endpoints_take_over_from_the_shorthand_entirely()
    {
        // Address/Port say one thing and Endpoints says another. The list wins, and the shorthand
        // is not quietly bound as well — otherwise "add one endpoint" would silently mean two.
        var options = new HttpServerOptions { Address = IPAddress.Loopback, Port = 0 };
        options.Listen(IPAddress.Loopback, 0);

        await using var server = new HttpServer(options);
        server.OnGet("/ping", ctx => ctx.Response.WriteAsync("pong"));
        await server.StartAsync(Token);

        Assert.Single(server.ListenUrls);
    }

    [Fact]
    public async Task Gives_each_endpoint_its_own_connection_ids()
    {
        var options = new HttpServerOptions();
        options.Listen(IPAddress.Loopback, 0);
        options.Listen(IPAddress.Loopback, 0);

        await using var server = new HttpServer(options);
        server.OnGet("/id", ctx => ctx.Response.WriteAsync(ctx.Connection.ConnectionId));
        await server.StartAsync(Token);

        using var first = new HttpClient();
        using var second = new HttpClient();

        var a = await first.GetStringAsync($"{server.ListenUrls[0]}/id", Token);
        var b = await second.GetStringAsync($"{server.ListenUrls[1]}/id", Token);

        // Both listeners count from one, so without a per-listener prefix these would collide.
        Assert.NotEqual(a, b);
    }

    [Fact]
    public async Task Unwinds_every_endpoint_when_one_of_them_cannot_bind()
    {
        // Squat on a port so the second endpoint is guaranteed to fail.
        using var squatter = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        squatter.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        squatter.Listen(1);
        var taken = ((IPEndPoint)squatter.LocalEndPoint!).Port;

        var options = new HttpServerOptions();
        options.Listen(IPAddress.Loopback, 0);
        options.Listen(IPAddress.Loopback, taken);

        await using var server = new HttpServer(options);
        server.OnGet("/ping", ctx => ctx.Response.WriteAsync("pong"));

        await Assert.ThrowsAsync<IOException>(() => server.StartAsync(Token));

        Assert.Equal(HttpServerState.Stopped, server.State);
        Assert.Empty(server.ListenUrls);

        // The endpoint that did bind must have let its port go, or a failed start leaks a socket
        // and the retry after fixing the config fails for a different reason.
        options.Endpoints.Clear();
        options.Listen(IPAddress.Loopback, 0);
        await server.StartAsync(Token);

        Assert.Single(server.ListenUrls);
    }

    [Fact]
    public async Task Picks_up_a_changed_endpoint_list_on_restart()
    {
        var options = new HttpServerOptions();
        options.Listen(IPAddress.Loopback, 0);

        await using var server = new HttpServer(options);
        server.OnGet("/ping", ctx => ctx.Response.WriteAsync("pong"));
        await server.StartAsync(Token);

        Assert.Single(server.ListenUrls);

        options.Listen(IPAddress.Loopback, 0);
        await server.RestartAsync(Token);

        Assert.Equal(2, server.ListenUrls.Count);

        using var client = new HttpClient();
        foreach (var url in server.ListenUrls)
            Assert.Equal("pong", await client.GetStringAsync($"{url}/ping", Token));
    }

    [Fact]
    public async Task Reports_no_urls_before_starting_and_after_stopping()
    {
        var options = new HttpServerOptions { Address = IPAddress.Loopback, Port = 0 };

        await using var server = new HttpServer(options);
        server.OnGet("/ping", ctx => ctx.Response.WriteAsync("pong"));

        Assert.Empty(server.ListenUrls);
        Assert.Null(server.ListenUrl);

        await server.StartAsync(Token);
        Assert.Single(server.ListenUrls);

        await server.StopAsync(Token);
        Assert.Empty(server.ListenUrls);
        Assert.Null(server.ListenUrl);
    }

    [Fact]
    public async Task Shares_the_connection_limit_across_endpoints()
    {
        var options = new HttpServerOptions { MaxConcurrentConnections = 4 };
        options.Listen(IPAddress.Loopback, 0);
        options.Listen(IPAddress.Loopback, 0);

        await using var server = new HttpServer(options);
        server.OnGet("/ping", ctx => ctx.Response.WriteAsync("pong"));
        await server.StartAsync(Token);

        using var client = new HttpClient();

        // Sequential requests reuse and release slots; the point is that a second listener does not
        // get its own private budget and blow past the cap.
        for (var i = 0; i < 8; i++)
        {
            var url = server.ListenUrls[i % 2];
            Assert.Equal("pong", await client.GetStringAsync($"{url}/ping", Token));
        }
    }
}
