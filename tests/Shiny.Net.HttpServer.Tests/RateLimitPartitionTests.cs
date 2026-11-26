using System.Net;
using Shiny.Net.HttpServer.RateLimiting;

namespace Shiny.Net.HttpServer.Tests;

public class RateLimitPartitionTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    static Task<TestServer> StartAsync() => TestServer.StartAsync(app =>
    {
        app.UseRateLimiter(new FixedWindowRateLimitPolicy(2, TimeSpan.FromMinutes(5))
        {
            Partitioner = RateLimitPartitioners.ByIpAddressAndPath
        });

        app.MapPost("/login", ctx => ctx.Response.WriteAsync("ok"));
        app.MapGet("/users/{id}", ctx => ctx.Response.WriteAsync("ok"));
        app.MapGet("/other", ctx => ctx.Response.WriteAsync("ok"));
    });

    // The router is case-insensitive and ignores a trailing slash, so all three reach the same handler.
    // Keyed on the raw path, each spelling had an allowance of its own.
    [Fact]
    public async Task Respelling_the_path_does_not_buy_a_fresh_allowance()
    {
        await using var server = await StartAsync();

        Assert.Equal(HttpStatusCode.OK, (await server.Client.PostAsync("/login", null, Token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await server.Client.PostAsync("/LOGIN", null, Token)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await server.Client.PostAsync("/login/", null, Token)).StatusCode);

        // A different endpoint still has its own.
        Assert.Equal(HttpStatusCode.OK, (await server.Client.GetAsync("/other", Token)).StatusCode);
    }

    [Fact]
    public async Task A_parameterised_route_is_one_allowance()
    {
        await using var server = await StartAsync();

        Assert.Equal(HttpStatusCode.OK, (await server.Client.GetAsync("/users/1", Token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await server.Client.GetAsync("/users/2", Token)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await server.Client.GetAsync("/users/3", Token)).StatusCode);
    }

    [Fact]
    public async Task An_unrouted_path_is_normalised_the_way_the_router_compares()
    {
        await using var server = await StartAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await server.Client.GetAsync("/missing", Token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await server.Client.GetAsync("/MISSING/", Token)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await server.Client.GetAsync("/Missing", Token)).StatusCode);
    }

    static string? KeyFor(string address)
    {
        var context = new HttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(address);
        return RateLimitPartitioners.ByIpAddress(context);
    }

    // A host is routinely given a whole /64 and can rotate through it at will.
    [Fact]
    public void IPv6_callers_are_counted_per_64()
    {
        Assert.Equal(KeyFor("2001:db8:1:2::1"), KeyFor("2001:db8:1:2:ffff:ffff:ffff:fffe"));
        Assert.NotEqual(KeyFor("2001:db8:1:2::1"), KeyFor("2001:db8:1:3::1"));
    }

    [Fact]
    public void IPv4_callers_are_counted_per_address_even_when_mapped()
    {
        Assert.Equal("192.0.2.1", KeyFor("192.0.2.1"));
        Assert.Equal("192.0.2.1", KeyFor("::ffff:192.0.2.1"));
        Assert.NotEqual(KeyFor("192.0.2.1"), KeyFor("192.0.2.2"));
    }
}
