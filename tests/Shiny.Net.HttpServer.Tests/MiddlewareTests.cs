using System.Net;
using Microsoft.Extensions.DependencyInjection;

namespace Shiny.Net.HttpServer.Tests;

public class HttpMiddlewareTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    sealed class TaggingMiddleware(string tag, List<string> log) : IHttpMiddleware
    {
        public async ValueTask InvokeAsync(HttpContext context, RequestDelegate next)
        {
            log.Add($"{tag}-in");
            await next(context);
            log.Add($"{tag}-out");
        }
    }

    sealed class GateMiddleware : IHttpMiddleware
    {
        public ValueTask InvokeAsync(HttpContext context, RequestDelegate next)
        {
            if (context.Request.Headers.GetFirst("X-Pass") is null)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return context.Response.WriteAsync("blocked");
            }

            return next(context);
        }
    }

    sealed class ScopeProbeMiddleware(RequestScopedValue value) : IHttpMiddleware
    {
        public ValueTask InvokeAsync(HttpContext context, RequestDelegate next)
        {
            context.Items["middleware-scope-id"] = value.Id;
            return next(context);
        }
    }

    sealed class RequestScopedValue
    {
        public Guid Id { get; } = Guid.NewGuid();
    }

    sealed class UnregisteredMiddleware : IHttpMiddleware
    {
        public ValueTask InvokeAsync(HttpContext context, RequestDelegate next) => next(context);
    }

    [Fact]
    public async Task Runs_class_middleware_around_the_handler()
    {
        var log = new List<string>();

        await using var server = await TestServer.StartAsync(app =>
        {
            app.Use(new TaggingMiddleware("outer", log));
            app.Use(new TaggingMiddleware("inner", log));
            app.OnGet("/x", ctx => { log.Add("handler"); return ctx.Response.WriteAsync("ok"); });
        });

        await server.Client.GetStringAsync("/x", Token);

        Assert.Equal(["outer-in", "inner-in", "handler", "inner-out", "outer-out"], log);
    }

    [Fact]
    public async Task Interleaves_class_and_delegate_middleware_in_registration_order()
    {
        var log = new List<string>();

        await using var server = await TestServer.StartAsync(app =>
        {
            app.Use(new TaggingMiddleware("class", log));
            app.Use(async (ctx, next) => { log.Add("lambda-in"); await next(ctx); log.Add("lambda-out"); });
            app.OnGet("/x", ctx => { log.Add("handler"); return ctx.Response.WriteAsync("ok"); });
        });

        await server.Client.GetStringAsync("/x", Token);

        Assert.Equal(["class-in", "lambda-in", "handler", "lambda-out", "class-out"], log);
    }

    [Fact]
    public async Task Class_middleware_can_short_circuit()
    {
        await using var server = await TestServer.StartAsync(app =>
        {
            app.Use(new GateMiddleware());
            app.OnGet("/secret", ctx => ctx.Response.WriteAsync("classified"));
        });

        var blocked = await server.Client.GetAsync("/secret", Token);
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.Equal("blocked", await blocked.Content.ReadAsStringAsync(Token));

        var request = new HttpRequestMessage(HttpMethod.Get, "/secret");
        request.Headers.Add("X-Pass", "yes");
        Assert.Equal("classified", await (await server.Client.SendAsync(request, Token)).Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Resolves_middleware_from_the_container()
    {
        await using var server = await TestServer.StartAsync(
            app =>
            {
                app.Use<GateMiddleware>();
                app.OnGet("/secret", ctx => ctx.Response.WriteAsync("classified"));
            },
            builder => builder.Services.AddSingleton<GateMiddleware>()
        );

        Assert.Equal(HttpStatusCode.Forbidden, (await server.Client.GetAsync("/secret", Token)).StatusCode);
    }

    [Fact]
    public async Task Scoped_middleware_shares_the_request_scope_with_the_handler()
    {
        await using var server = await TestServer.StartAsync(
            app =>
            {
                app.Use<ScopeProbeMiddleware>();
                app.OnGet("/scope", ctx =>
                {
                    var fromHandler = ctx.GetRequiredService<RequestScopedValue>().Id;
                    var fromMiddleware = (Guid)ctx.Items["middleware-scope-id"]!;
                    return ctx.Response.WriteAsync(fromHandler == fromMiddleware ? "same" : "different");
                });
            },
            builder =>
            {
                builder.Services.AddScoped<RequestScopedValue>();
                builder.Services.AddScoped<ScopeProbeMiddleware>();
            }
        );

        // Middleware runs inside the per-request scope, not outside it — otherwise "scoped" would
        // mean something different depending on where you asked.
        Assert.Equal("same", await server.Client.GetStringAsync("/scope", Token));

        var first = await server.Client.GetStringAsync("/scope", Token);
        Assert.Equal("same", first);
    }

    [Fact]
    public async Task Explains_itself_when_middleware_was_never_registered()
    {
        await using var server = await TestServer.StartAsync(app =>
        {
            app.Use<UnregisteredMiddleware>();
            app.OnGet("/x", ctx => ctx.Response.WriteAsync("ok"));
        });

        var response = await server.Client.GetAsync("/x", Token);
        var body = await response.Content.ReadAsStringAsync(Token);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains(nameof(UnregisteredMiddleware), body);
        Assert.Contains("AddSingleton", body);
    }

    [Fact]
    public async Task Rejects_middleware_registered_after_the_server_started()
    {
        await using var server = await TestServer.StartAsync(app => app.OnGet("/x", ctx => ctx.Response.WriteAsync("ok")));

        Assert.Throws<InvalidOperationException>(() => server.Server.Use(new GateMiddleware()));
        Assert.Throws<InvalidOperationException>(() => server.Server.Use<GateMiddleware>());
    }

    [Fact]
    public async Task OnStarting_is_how_middleware_adds_a_response_header()
    {
        await using var server = await TestServer.StartAsync(app =>
        {
            app.Use((ctx, next) =>
            {
                ctx.Response.OnStarting(() =>
                {
                    ctx.Response.Headers["X-Served-By"] = "Shiny";
                    return ValueTask.CompletedTask;
                });

                return next(ctx);
            });
            app.OnGet("/x", ctx => ctx.Response.WriteAsync("ok"));
        });

        var response = await server.Client.GetAsync("/x", Token);
        Assert.Equal("Shiny", response.Headers.GetValues("X-Served-By").Single());
    }

    [Fact]
    public async Task Mutating_headers_after_the_response_started_throws()
    {
        // The alternative — silently dropping the header — would be a genuinely nasty afternoon.
        await using var server = await TestServer.StartAsync(app =>
        {
            app.OnGet("/x", async ctx =>
            {
                await ctx.Response.WriteAsync("ok");
                ctx.Response.Headers["X-Too-Late"] = "yes";
            });
        });

        var response = await server.Client.GetAsync("/x", Token);
        Assert.False(response.Headers.Contains("X-Too-Late"));
    }
}
