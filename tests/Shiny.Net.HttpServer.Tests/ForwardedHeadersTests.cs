using System.Net;
using Shiny.Net.HttpServer.Security;

namespace Shiny.Net.HttpServer.Tests;

// Every proxy appends to X-Forwarded-For, so only the right-hand end was written by someone trusted.
// The server used to take the left-most entry — whatever the client sent — letting any caller behind an
// ordinary appending proxy choose their own address (and scheme and host).
public class ForwardedHeadersTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    static Task<TestServer> StartAsync(Action<ForwardedHeadersOptions>? configure = null) => TestServer.StartAsync(
        app => app.MapGet("/who", ctx => ctx.Response.WriteTextAsync(
            $"{ctx.Connection.RemoteIpAddress}|{ctx.Request.Scheme}|{ctx.Request.Host}"
        )),
        builder =>
        {
            builder.Options.UseForwardedHeaders = true;
            configure?.Invoke(builder.Options.ForwardedHeaders);
        }
    );

    static async Task<string> WhoAsync(TestServer server, string? forwardedFor = null, string? proto = null, string? host = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/who");

        if (forwardedFor is not null)
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        if (proto is not null)
            request.Headers.Add("X-Forwarded-Proto", proto);
        if (host is not null)
            request.Headers.Add("X-Forwarded-Host", host);

        using var response = await server.Client.SendAsync(request, Token);
        return await response.Content.ReadAsStringAsync(Token);
    }

    [Fact]
    public async Task Takes_the_address_the_nearest_proxy_saw_not_the_one_the_client_claimed()
    {
        await using var server = await StartAsync();

        var who = await WhoAsync(server, "6.6.6.6, 203.0.113.9");
        Assert.StartsWith("203.0.113.9|", who, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scheme_and_host_come_from_the_same_hop()
    {
        await using var server = await StartAsync();

        var who = await WhoAsync(server, "6.6.6.6, 203.0.113.9", proto: "https, http", host: "evil.example, app.example");
        Assert.Equal("203.0.113.9|http|app.example", who);
    }

    [Fact]
    public async Task Ignores_the_headers_from_a_peer_that_is_not_a_known_proxy()
    {
        await using var server = await StartAsync(o => o.KnownProxies.Add(IPAddress.Parse("10.0.0.1")));

        var who = await WhoAsync(server, "203.0.113.9", proto: "https");
        Assert.StartsWith("127.0.0.1|http|", who, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Walks_a_chain_of_known_proxies_and_stops_at_the_first_stranger()
    {
        await using var server = await StartAsync(o =>
        {
            o.KnownNetworks.Add(IpAddressRange.Loopback);
            o.ForwardLimit = 5;
        });

        // client-claimed, real client, inner proxy (loopback, trusted).
        var who = await WhoAsync(server, "6.6.6.6, 198.51.100.7, 127.0.0.5");
        Assert.StartsWith("198.51.100.7|", who, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForwardLimit_caps_how_far_back_the_walk_goes()
    {
        await using var server = await StartAsync(o =>
        {
            o.KnownNetworks.Add(IpAddressRange.Loopback);
            o.ForwardLimit = 1;
        });

        var who = await WhoAsync(server, "198.51.100.7, 127.0.0.5");
        Assert.StartsWith("127.0.0.5|", who, StringComparison.Ordinal);
    }

    [Fact]
    public void GetClientIpAddress_reads_the_right_most_entry()
    {
        var context = new HttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Headers.Add("X-Forwarded-For", "6.6.6.6, [2001:db8::1]:443, 203.0.113.9:8080");

        Assert.Equal(IPAddress.Parse("203.0.113.9"), context.GetClientIpAddress(useForwardedHeaders: true));
        Assert.Equal(IPAddress.Loopback, context.GetClientIpAddress());
    }
}
