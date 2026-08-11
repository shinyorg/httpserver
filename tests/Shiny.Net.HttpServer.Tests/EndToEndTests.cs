using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.DependencyInjection;

namespace Shiny.Net.HttpServer.Tests;

public class EndToEndTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Serves_a_single_delegate_with_no_routes()
    {
        await using var server = await TestServer.StartAsync(
            app => app.OnRequest(ctx => ctx.Response.WriteAsync("hello"))
        );

        Assert.Equal("hello", await server.Client.GetStringAsync("/anything", Token));
    }

    [Fact]
    public async Task Routes_by_method_and_template()
    {
        await using var server = await TestServer.StartAsync(app =>
        {
            app.OnGet("/ping", ctx => ctx.Response.WriteAsync("pong"));
            app.OnPost("/ping", ctx => ctx.Response.WriteAsync("posted"));
            app.OnGet("/users/{id:int}", ctx => ctx.Response.WriteAsync($"user {ctx.Request.RouteValues["id"]}"));
        });

        Assert.Equal("pong", await server.Client.GetStringAsync("/ping", Token));
        Assert.Equal("user 42", await server.Client.GetStringAsync("/users/42", Token));

        var posted = await server.Client.PostAsync("/ping", new StringContent(""), Token);
        Assert.Equal("posted", await posted.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Returns_404_when_no_route_matches_and_there_is_no_fallback()
    {
        await using var server = await TestServer.StartAsync(app => app.OnGet("/ping", ctx => ctx.Response.WriteAsync("pong")));

        var response = await server.Client.GetAsync("/nope", Token);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Falls_through_to_OnRequest_when_no_route_matches()
    {
        await using var server = await TestServer.StartAsync(app =>
        {
            app.OnGet("/ping", ctx => ctx.Response.WriteAsync("pong"));
            app.OnRequest(ctx => ctx.Response.WriteAsync("fallback"));
        });

        Assert.Equal("fallback", await server.Client.GetStringAsync("/nope", Token));
    }

    [Fact]
    public async Task Returns_405_with_an_Allow_header()
    {
        await using var server = await TestServer.StartAsync(app => app.OnGet("/ping", ctx => ctx.Response.WriteAsync("pong")));

        var response = await server.Client.PostAsync("/ping", new StringContent(""), Token);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);

        // Allow is a content header as far as HttpClient is concerned, even on a bodyless response.
        Assert.Contains("GET", response.Content.Headers.Allow);
    }

    [Fact]
    public async Task Answers_HEAD_from_the_GET_handler_without_a_body()
    {
        await using var server = await TestServer.StartAsync(app => app.OnGet("/ping", ctx => ctx.Response.WriteAsync("pong")));

        var response = await server.Client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/ping"), Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(4, response.Content.Headers.ContentLength);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(Token));
    }

    [Fact]
    public async Task Reads_a_content_length_body()
    {
        await using var server = await TestServer.StartAsync(app => app.OnPost("/echo", async ctx =>
        {
            var body = await ctx.Request.ReadBodyAsStringAsync(cancellationToken: ctx.RequestAborted);
            await ctx.Response.WriteAsync($"echo:{body}");
        }));

        var response = await server.Client.PostAsync("/echo", new StringContent("payload"), Token);
        Assert.Equal("echo:payload", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Reads_a_chunked_body()
    {
        await using var server = await TestServer.StartAsync(app => app.OnPost("/echo", async ctx =>
        {
            Assert.True(ctx.Request.IsChunked);
            var body = await ctx.Request.ReadBodyAsStringAsync(cancellationToken: ctx.RequestAborted);
            await ctx.Response.WriteAsync($"chunked:{body.Length}");
        }));

        // A StreamContent with no known length is what makes HttpClient use chunked encoding.
        var content = new StreamContent(new MemoryStream(Encoding.ASCII.GetBytes(new string('x', 5000))));
        content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");

        var request = new HttpRequestMessage(HttpMethod.Post, "/echo") { Content = content };
        request.Headers.TransferEncodingChunked = true;

        var response = await server.Client.SendAsync(request, Token);
        Assert.Equal("chunked:5000", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Streams_a_response_of_unknown_length_as_chunked()
    {
        await using var server = await TestServer.StartAsync(app => app.OnGet("/stream", async ctx =>
        {
            ctx.Response.ContentType = "text/plain";
            await ctx.Response.StartAsync(ctx.RequestAborted);

            for (var i = 0; i < 3; i++)
            {
                await ctx.Response.BodyWriter.WriteAsync(Encoding.ASCII.GetBytes($"chunk{i};"), ctx.RequestAborted);
                await ctx.Response.BodyWriter.FlushAsync(ctx.RequestAborted);
            }
        }));

        var response = await server.Client.GetAsync("/stream", Token);

        Assert.Contains("chunked", response.Headers.TransferEncoding.ToString());
        Assert.Equal("chunk0;chunk1;chunk2;", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Reuses_a_keep_alive_connection_for_several_requests()
    {
        await using var server = await TestServer.StartAsync(app => app.OnGet("/n", ctx =>
            ctx.Response.WriteAsync(ctx.Connection.ConnectionId)));

        var first = await server.Client.GetStringAsync("/n", Token);
        var second = await server.Client.GetStringAsync("/n", Token);
        var third = await server.Client.GetStringAsync("/n", Token);

        // Same connection id across all three: the socket really was reused, not re-established.
        Assert.Equal(first, second);
        Assert.Equal(second, third);
    }

    [Fact]
    public async Task Serves_pipelined_requests_on_one_connection()
    {
        await using var server = await TestServer.StartAsync(app => app.OnGet("/ping", ctx => ctx.Response.WriteAsync("pong")));

        var raw = await server.SendRawAsync(
            "GET /ping HTTP/1.1\r\nHost: localhost\r\n\r\n" +
            "GET /ping HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n",
            expectedResponses: 2
        );

        Assert.Equal(2, raw.Split("HTTP/1.1 200").Length - 1);
    }

    [Fact]
    public async Task Rejects_an_HTTP_1_1_request_with_no_Host_header()
    {
        await using var server = await TestServer.StartAsync(app => app.OnGet("/ping", ctx => ctx.Response.WriteAsync("pong")));

        var raw = await server.SendRawAsync("GET /ping HTTP/1.1\r\n\r\n");
        Assert.Contains("400", raw);
    }

    [Fact]
    public async Task Rejects_a_request_with_both_chunked_and_content_length()
    {
        await using var server = await TestServer.StartAsync(app => app.OnPost("/x", ctx => ctx.Response.WriteAsync("ok")));

        var raw = await server.SendRawAsync(
            "POST /x HTTP/1.1\r\nHost: localhost\r\nTransfer-Encoding: chunked\r\nContent-Length: 5\r\n\r\n0\r\n\r\n"
        );

        Assert.Contains("400", raw);
    }

    [Fact]
    public async Task Rejects_duplicate_Host_headers()
    {
        await using var server = await TestServer.StartAsync(app => app.OnGet("/x", ctx => ctx.Response.WriteAsync("ok")));

        var raw = await server.SendRawAsync("GET /x HTTP/1.1\r\nHost: a\r\nHost: b\r\n\r\n");
        Assert.Contains("400", raw);
    }

    [Fact]
    public async Task Turns_an_unhandled_exception_into_a_500()
    {
        await using var server = await TestServer.StartAsync(
            app => app.OnGet("/boom", _ => throw new InvalidOperationException("deliberate")),
            builder => builder.Options.HideExceptionDetails = true
        );

        var response = await server.Client.GetAsync("/boom", Token);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("deliberate", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Runs_middleware_around_everything()
    {
        var order = new List<string>();

        await using var server = await TestServer.StartAsync(app =>
        {
            app.Use(async (ctx, next) => { order.Add("outer-in"); await next(ctx); order.Add("outer-out"); });
            app.Use(async (ctx, next) => { order.Add("inner-in"); await next(ctx); order.Add("inner-out"); });
            app.OnGet("/x", ctx => { order.Add("handler"); return ctx.Response.WriteAsync("ok"); });
        });

        await server.Client.GetStringAsync("/x", Token);

        Assert.Equal(["outer-in", "inner-in", "handler", "inner-out", "outer-out"], order);
    }

    [Fact]
    public async Task Middleware_can_short_circuit_the_pipeline()
    {
        await using var server = await TestServer.StartAsync(app =>
        {
            app.Use(async (ctx, next) =>
            {
                if (ctx.Request.Headers.GetFirst("X-Api-Key") is null)
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await ctx.Response.WriteAsync("no key");
                    return;
                }
                await next(ctx);
            });
            app.OnGet("/secret", ctx => ctx.Response.WriteAsync("classified"));
        });

        var denied = await server.Client.GetAsync("/secret", Token);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

        var request = new HttpRequestMessage(HttpMethod.Get, "/secret");
        request.Headers.Add("X-Api-Key", "letmein");
        var allowed = await server.Client.SendAsync(request, Token);
        Assert.Equal("classified", await allowed.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Handles_many_concurrent_requests()
    {
        await using var server = await TestServer.StartAsync(app => app.OnGet("/slow", async ctx =>
        {
            await Task.Delay(20, ctx.RequestAborted);
            await ctx.Response.WriteAsync("done");
        }));

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 50).Select(_ => server.Client.GetStringAsync("/slow", Token))
        );

        Assert.All(responses, r => Assert.Equal("done", r));
    }

    [Fact]
    public async Task Sets_cookies_and_reads_them_back()
    {
        await using var server = await TestServer.StartAsync(app =>
        {
            app.OnGet("/set", ctx =>
            {
                ctx.Response.Cookies.Append("session", "abc123");
                return ctx.Response.WriteAsync("set");
            });
            app.OnGet("/read", ctx => ctx.Response.WriteAsync(ctx.Request.Cookies["session"] ?? "(none)"));
        });

        var set = await server.Client.GetAsync("/set", Token);
        Assert.Contains(set.Headers.GetValues("Set-Cookie"), v => v.StartsWith("session=abc123"));

        var request = new HttpRequestMessage(HttpMethod.Get, "/read");
        request.Headers.Add("Cookie", "session=abc123");
        Assert.Equal("abc123", await (await server.Client.SendAsync(request, Token)).Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Shuts_down_gracefully_while_a_request_is_in_flight()
    {
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        await using var server = await TestServer.StartAsync(app => app.OnGet("/hold", async ctx =>
        {
            started.SetResult();
            await release.Task;
            await ctx.Response.WriteAsync("finished");
        }));

        var request = server.Client.GetStringAsync("/hold", Token);
        await started.Task;

        var stop = server.Server.StopAsync(Token);
        release.SetResult();

        Assert.Equal("finished", await request);
        await stop;
    }
}

public class DependencyInjectionTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    sealed class Marker
    {
        public Guid Id { get; } = Guid.NewGuid();
    }

    sealed class TrackedDisposable : IAsyncDisposable
    {
        public static int DisposeCount;

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref DisposeCount);
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task Resolves_the_same_scoped_instance_within_one_request()
    {
        await using var server = await TestServer.StartAsync(
            app => app.OnGet("/scope", ctx =>
            {
                var a = ctx.GetRequiredService<Marker>();
                var b = ctx.GetRequiredService<Marker>();
                return ctx.Response.WriteAsync(ReferenceEquals(a, b) ? "same" : "different");
            }),
            builder => builder.Services.AddScoped<Marker>()
        );

        Assert.Equal("same", await server.Client.GetStringAsync("/scope", Token));
    }

    [Fact]
    public async Task Resolves_a_different_scoped_instance_per_request()
    {
        await using var server = await TestServer.StartAsync(
            app => app.OnGet("/id", ctx => ctx.Response.WriteAsync(ctx.GetRequiredService<Marker>().Id.ToString())),
            builder => builder.Services.AddScoped<Marker>()
        );

        var first = await server.Client.GetStringAsync("/id", Token);
        var second = await server.Client.GetStringAsync("/id", Token);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task Shares_a_singleton_across_requests()
    {
        await using var server = await TestServer.StartAsync(
            app => app.OnGet("/id", ctx => ctx.Response.WriteAsync(ctx.GetRequiredService<Marker>().Id.ToString())),
            builder => builder.Services.AddSingleton<Marker>()
        );

        Assert.Equal(
            await server.Client.GetStringAsync("/id", Token),
            await server.Client.GetStringAsync("/id", Token)
        );
    }

    [Fact]
    public async Task Disposes_scoped_IAsyncDisposable_services_when_the_request_ends()
    {
        TrackedDisposable.DisposeCount = 0;

        await using var server = await TestServer.StartAsync(
            app => app.OnGet("/x", ctx =>
            {
                ctx.GetRequiredService<TrackedDisposable>();
                return ctx.Response.WriteAsync("ok");
            }),
            builder => builder.Services.AddScoped<TrackedDisposable>()
        );

        await server.Client.GetStringAsync("/x", Token);
        await server.Client.GetStringAsync("/x", Token);

        Assert.Equal(2, TrackedDisposable.DisposeCount);
    }

    [Fact]
    public async Task Works_without_a_container_at_all()
    {
        var server = new HttpServer(new HttpServerOptions { Port = 0, Address = IPAddress.Loopback });
        server.OnGet("/x", ctx => ctx.Response.WriteAsync(ctx.GetService<Marker>() is null ? "none" : "resolved"));

        await using (server)
        {
            await server.StartAsync(Token);

            using var client = new HttpClient { BaseAddress = new Uri(server.ListenUrl!) };
            Assert.Equal("none", await client.GetStringAsync("/x", Token));
        }
    }
}
