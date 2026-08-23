using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Shiny.Net.HttpServer.Timeouts;

namespace Shiny.Net.HttpServer.Tests;

public class RequestTimeoutTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_handler_that_overruns_answers_504()
    {
        await using var test = await TestServer.StartAsync(server =>
        {
            server.UseRequestTimeouts(TimeSpan.FromMilliseconds(100));
            server.MapGet("/slow", async ctx => await Task.Delay(Timeout.Infinite, ctx.RequestAborted));
        });

        var response = await test.Client.GetAsync("/slow", Token);

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
    }

    [Fact]
    public async Task A_handler_that_finishes_in_time_is_untouched()
    {
        await using var test = await TestServer.StartAsync(server =>
        {
            server.UseRequestTimeouts(TimeSpan.FromSeconds(30));
            server.MapGet("/fast", ctx => ctx.Response.WriteTextAsync("done", cancellationToken: ctx.RequestAborted));
        });

        Assert.Equal("done", await test.Client.GetStringAsync("/fast", Token));
    }

    /// <summary>An endpoint whose job is to stay open opts out, and the default policy does not reach it.</summary>
    [Fact]
    public async Task An_endpoint_can_opt_out()
    {
        await using var test = await TestServer.StartAsync(server =>
        {
            server.UseRequestTimeouts(TimeSpan.FromMilliseconds(100));
            server
                .MapGet("/stream", async ctx =>
                {
                    await Task.Delay(300, ctx.RequestAborted);
                    await ctx.Response.WriteTextAsync("still here", cancellationToken: ctx.RequestAborted);
                })
                .DisableRequestTimeout();
        });

        Assert.Equal("still here", await test.Client.GetStringAsync("/stream", Token));
    }

    [Fact]
    public async Task A_route_can_ask_for_its_own_timeout()
    {
        await using var test = await TestServer.StartAsync(server =>
        {
            server.UseRequestTimeouts(TimeSpan.FromSeconds(30));
            server
                .MapGet("/impatient", async ctx => await Task.Delay(Timeout.Infinite, ctx.RequestAborted))
                .WithRequestTimeout(TimeSpan.FromMilliseconds(100));
        });

        Assert.Equal(HttpStatusCode.GatewayTimeout, (await test.Client.GetAsync("/impatient", Token)).StatusCode);
    }

    [Fact]
    public async Task A_named_policy_can_carry_its_own_status_code()
    {
        await using var test = await TestServer.StartAsync(
            server =>
            {
                server.UseRequestTimeouts();
                server
                    .MapGet("/report", async ctx => await Task.Delay(Timeout.Infinite, ctx.RequestAborted))
                    .WithRequestTimeout("reports");
            },
            builder => builder.AddRequestTimeouts(o => o.AddPolicy(
                "reports",
                new RequestTimeoutPolicy(TimeSpan.FromMilliseconds(100))
                {
                    StatusCode = StatusCodes.Status503ServiceUnavailable
                }
            ))
        );

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await test.Client.GetAsync("/report", Token)).StatusCode);
    }

    [Fact]
    public async Task A_policy_can_write_its_own_response()
    {
        await using var test = await TestServer.StartAsync(
            server =>
            {
                server.UseRequestTimeouts();
                server.MapGet("/slow", async ctx => await Task.Delay(Timeout.Infinite, ctx.RequestAborted));
            },
            builder => builder.AddRequestTimeouts(o => o.DefaultPolicy = new RequestTimeoutPolicy(TimeSpan.FromMilliseconds(100))
            {
                OnTimeout = ctx => ctx.Response.WriteTextAsync("try a smaller range", cancellationToken: CancellationToken.None)
            })
        );

        var response = await test.Client.GetAsync("/slow", Token);

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Equal("try a smaller range", await response.Content.ReadAsStringAsync(Token));
    }

    /// <summary>A handler that ignores its token runs to completion, and is still reported as late.</summary>
    [Fact]
    public async Task An_uncooperative_handler_is_still_reported()
    {
        await using var test = await TestServer.StartAsync(server =>
        {
            server.UseRequestTimeouts(TimeSpan.FromMilliseconds(50));
            server.MapGet("/stubborn", async _ => await Task.Delay(200, CancellationToken.None));
        });

        Assert.Equal(HttpStatusCode.GatewayTimeout, (await test.Client.GetAsync("/stubborn", Token)).StatusCode);
    }

    /// <summary>
    /// The timeout token must not outlive the request that owned it: the next request on the same
    /// keep-alive connection gets a fresh one, or a cancelled token would follow it.
    /// </summary>
    [Fact]
    public async Task A_timeout_does_not_follow_the_connection_to_the_next_request()
    {
        await using var test = await TestServer.StartAsync(server =>
        {
            server.UseRequestTimeouts(TimeSpan.FromMilliseconds(100));
            server.MapGet("/slow", async ctx => await Task.Delay(Timeout.Infinite, ctx.RequestAborted));
            server
                .MapGet("/fast", async ctx =>
                {
                    await Task.Delay(250, ctx.RequestAborted);
                    await ctx.Response.WriteTextAsync("fine", cancellationToken: ctx.RequestAborted);
                })
                .DisableRequestTimeout();
        });

        Assert.Equal(HttpStatusCode.GatewayTimeout, (await test.Client.GetAsync("/slow", Token)).StatusCode);
        Assert.Equal("fine", await test.Client.GetStringAsync("/fast", Token));
    }
}
