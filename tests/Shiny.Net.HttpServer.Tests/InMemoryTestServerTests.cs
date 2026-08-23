using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Shiny.Net.HttpServer.Testing;

namespace Shiny.Net.HttpServer.Tests;

/// <summary>
/// The in-memory harness, tested for the property that makes it worth shipping: it goes through the
/// same parser, router, middleware and framing as a socket does. A harness that quietly bypassed
/// any of those would let a test pass for code that fails on a real connection.
/// </summary>
public class InMemoryTestServerTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Serves_a_request_with_no_port_bound()
    {
        await using var app = TestHttpServer.Create(server =>
            server.MapGet("/ping", ctx => ctx.Response.WriteTextAsync("pong", cancellationToken: ctx.RequestAborted)));

        Assert.Equal("pong", await app.Client.GetStringAsync("/ping", Token));
        Assert.False(app.Server.IsRunning);
        Assert.Null(app.Server.ListenUrl);
    }

    [Fact]
    public async Task Routes_and_binds_exactly_as_a_socket_would()
    {
        await using var app = TestHttpServer.Create(server =>
            server.MapGet("/users/{id:int}", ctx => ctx.Response.WriteTextAsync(
                "user " + ctx.Request.RouteValues["id"],
                cancellationToken: ctx.RequestAborted
            )));

        Assert.Equal("user 42", await app.Client.GetStringAsync("/users/42", Token));
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.GetAsync("/users/abc", Token)).StatusCode);
    }

    [Fact]
    public async Task Middleware_runs()
    {
        await using var app = TestHttpServer.Create(server =>
        {
            server.Use(async (ctx, next) =>
            {
                ctx.Response.Headers.Set("X-Traced", "yes");
                await next(ctx);
            });
            server.MapGet("/", ctx => ctx.Response.WriteTextAsync("ok", cancellationToken: ctx.RequestAborted));
        });

        var response = await app.Client.GetAsync("/", Token);

        Assert.Equal("yes", response.Headers.GetValues("X-Traced").Single());
    }

    [Fact]
    public async Task Carries_a_request_body_and_a_response_body()
    {
        await using var app = TestHttpServer.Create(server => server.MapPost("/echo", async ctx =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync(ctx.RequestAborted);

            await ctx.Response.WriteTextAsync("got " + body, cancellationToken: ctx.RequestAborted);
        }));

        var response = await app.Client.PostAsync("/echo", new StringContent("a payload"), Token);

        Assert.Equal("got a payload", await response.Content.ReadAsStringAsync(Token));
    }

    /// <summary>The client reuses one connection, which means the server's keep-alive path is exercised.</summary>
    [Fact]
    public async Task Reuses_the_connection_across_requests()
    {
        var connectionIds = new List<string>();

        await using var app = TestHttpServer.Create(server => server.MapGet("/who", ctx =>
        {
            lock (connectionIds)
                connectionIds.Add(ctx.Connection.ConnectionId);

            return ctx.Response.WriteTextAsync("ok", cancellationToken: ctx.RequestAborted);
        }));

        await app.Client.GetStringAsync("/who", Token);
        await app.Client.GetStringAsync("/who", Token);
        await app.Client.GetStringAsync("/who", Token);

        Assert.Equal(3, connectionIds.Count);
        Assert.Single(connectionIds.Distinct());
    }

    [Fact]
    public async Task A_chunked_response_is_framed_correctly()
    {
        await using var app = TestHttpServer.Create(server => server.MapGet("/stream", async ctx =>
        {
            ctx.Response.ContentType = "text/plain";
            await ctx.Response.StartAsync(ctx.RequestAborted);

            for (var i = 0; i < 3; i++)
            {
                await ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes($"chunk{i};"), ctx.RequestAborted);
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
            }
        }));

        Assert.Equal("chunk0;chunk1;chunk2;", await app.Client.GetStringAsync("/stream", Token));
    }

    [Fact]
    public async Task Services_registered_on_the_builder_reach_the_handler()
    {
        await using var app = TestHttpServer.Create(
            server => server.MapGet("/clock", ctx => ctx.Response.WriteTextAsync(
                ctx.GetRequiredService<IFrozenClock>().Now,
                cancellationToken: ctx.RequestAborted
            )),
            builder => builder.Services.AddSingleton<IFrozenClock>(new FrozenClock("2026-08-23"))
        );

        Assert.Equal("2026-08-23", await app.Client.GetStringAsync("/clock", Token));
        Assert.NotNull(app.Services.GetService<IFrozenClock>());
    }

    [Fact]
    public async Task Two_clients_are_two_callers()
    {
        await using var app = TestHttpServer.Create(server => server.MapGet("/count", ctx =>
            ctx.Response.WriteTextAsync(ctx.Connection.ConnectionId, cancellationToken: ctx.RequestAborted)));

        using var second = app.CreateClient();

        var first = await app.Client.GetStringAsync("/count", Token);
        var other = await second.GetStringAsync("/count", Token);

        Assert.NotEqual(first, other);
    }

    /// <summary>Exceptions surface, which is the whole point of a test harness.</summary>
    [Fact]
    public async Task A_handler_that_throws_produces_a_500_with_the_detail()
    {
        await using var app = TestHttpServer.Create(server =>
            server.MapGet("/boom", _ => throw new InvalidOperationException("the handler failed")));

        var response = await app.Client.GetAsync("/boom", Token);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("the handler failed", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Speaks_http2_when_asked()
    {
        await using var app = TestHttpServer.Create(
            server => server.MapGet("/version", ctx => ctx.Response.WriteTextAsync(
                ctx.Request.Protocol,
                cancellationToken: ctx.RequestAborted
            )),
            useHttp2: true
        );

        var response = await app.Client.GetAsync("/version", Token);

        Assert.Equal(2, response.Version.Major);
        Assert.Equal("HTTP/2", await response.Content.ReadAsStringAsync(Token));
    }

    interface IFrozenClock
    {
        string Now { get; }
    }

    sealed class FrozenClock(string now) : IFrozenClock
    {
        public string Now { get; } = now;
    }
}
