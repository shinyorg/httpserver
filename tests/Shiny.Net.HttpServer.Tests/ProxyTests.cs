using System.Net;
using Shiny.Net.HttpServer.Proxy;

namespace Shiny.Net.HttpServer.Tests;

public class ProxyTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    static Task<TestServer> Upstream() => TestServer.StartAsync(server =>
    {
        server.MapGet("/hello", ctx => ctx.Response.WriteTextAsync("upstream says hello", cancellationToken: ctx.RequestAborted));
        server.MapGet("/echo-path", ctx => ctx.Response.WriteTextAsync(
            ctx.Request.Path + (ctx.Request.QueryString ?? ""),
            cancellationToken: ctx.RequestAborted
        ));
        server.MapGet("/deep/nested/thing", ctx => ctx.Response.WriteTextAsync("deep", cancellationToken: ctx.RequestAborted));
        server.MapGet("/headers", ctx => ctx.Response.WriteTextAsync(
            ctx.Request.Headers.GetFirst("X-Forwarded-For") + "|" + ctx.Request.Headers.GetFirst("X-Test"),
            cancellationToken: ctx.RequestAborted
        ));
        server.MapPost("/echo-body", async ctx =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync(ctx.RequestAborted);

            await ctx.Response.WriteTextAsync("got:" + body, cancellationToken: ctx.RequestAborted);
        });
        server.MapGet("/teapot", ctx =>
        {
            ctx.Response.StatusCode = 418;
            ctx.Response.Headers.Set("X-Upstream", "yes");

            return ctx.Response.WriteTextAsync("short and stout", cancellationToken: ctx.RequestAborted);
        });
    });

    [Fact]
    public async Task Forwards_a_request_and_returns_the_answer()
    {
        await using var upstream = await Upstream();
        await using var edge = await TestServer.StartAsync(server =>
            server.MapProxy("/api/{*path}", $"http://127.0.0.1:{upstream.Port}"));

        Assert.Equal("upstream says hello", await edge.Client.GetStringAsync("/api/hello", Token));
    }

    [Fact]
    public async Task Carries_the_remaining_path_and_the_query()
    {
        await using var upstream = await Upstream();
        await using var edge = await TestServer.StartAsync(server =>
            server.MapProxy("/api/{*path}", $"http://127.0.0.1:{upstream.Port}"));

        Assert.Equal("/echo-path?a=1&b=2", await edge.Client.GetStringAsync("/api/echo-path?a=1&b=2", Token));
        Assert.Equal("deep", await edge.Client.GetStringAsync("/api/deep/nested/thing", Token));
    }

    [Fact]
    public async Task Passes_the_status_and_headers_back()
    {
        await using var upstream = await Upstream();
        await using var edge = await TestServer.StartAsync(server =>
            server.MapProxy("/api/{*path}", $"http://127.0.0.1:{upstream.Port}"));

        var response = await edge.Client.GetAsync("/api/teapot", Token);

        Assert.Equal(418, (int)response.StatusCode);
        Assert.Equal("yes", response.Headers.GetValues("X-Upstream").Single());
        Assert.Equal("short and stout", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Streams_a_request_body_through()
    {
        await using var upstream = await Upstream();
        await using var edge = await TestServer.StartAsync(server =>
            server.MapProxy("/api/{*path}", $"http://127.0.0.1:{upstream.Port}"));

        var response = await edge.Client.PostAsync("/api/echo-body", new StringContent("payload"), Token);

        Assert.Equal("got:payload", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Tells_the_upstream_who_the_caller_was()
    {
        await using var upstream = await Upstream();
        await using var edge = await TestServer.StartAsync(server =>
            server.MapProxy("/api/{*path}", $"http://127.0.0.1:{upstream.Port}"));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/headers");
        request.Headers.Add("X-Test", "kept");

        var response = await edge.Client.SendAsync(request, Token);

        Assert.Equal("127.0.0.1|kept", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Can_rewrite_the_outbound_request()
    {
        await using var upstream = await Upstream();
        await using var edge = await TestServer.StartAsync(server => server.MapProxy(
            "/legacy/{*path}",
            $"http://127.0.0.1:{upstream.Port}",
            o => o.RewriteUri = ctx => new Uri($"http://127.0.0.1:{upstream.Port}/hello")
        ));

        Assert.Equal("upstream says hello", await edge.Client.GetStringAsync("/legacy/anything", Token));
    }

    /// <summary>An upstream that is not there is a 502: our request failed, not the caller's.</summary>
    [Fact]
    public async Task An_unreachable_upstream_is_a_502()
    {
        await using var edge = await TestServer.StartAsync(server =>
            server.MapProxy("/api/{*path}", "http://127.0.0.1:1"));

        Assert.Equal(HttpStatusCode.BadGateway, (await edge.Client.GetAsync("/api/anything", Token)).StatusCode);
    }

    [Fact]
    public async Task An_upstream_that_will_not_answer_is_a_504()
    {
        await using var upstream = await TestServer.StartAsync(server =>
            server.MapGet("/hang", async ctx => await Task.Delay(Timeout.Infinite, ctx.RequestAborted)));

        await using var edge = await TestServer.StartAsync(server => server.MapProxy(
            "/api/{*path}",
            $"http://127.0.0.1:{upstream.Port}",
            o => o.Timeout = TimeSpan.FromMilliseconds(150)
        ));

        Assert.Equal(HttpStatusCode.GatewayTimeout, (await edge.Client.GetAsync("/api/hang", Token)).StatusCode);
    }
}
