using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Shiny.Net.HttpServer.Caching;

namespace Shiny.Net.HttpServer.Tests;

public class ConditionalRequestTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    static Task<TestServer> EtagServer(string etag = "v1")
        => TestServer.StartAsync(server => server.MapGet("/item", async ctx =>
        {
            if (await ctx.TryCompleteConditionalAsync(etag, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)))
                return;

            await ctx.Response.WriteTextAsync("the body", cancellationToken: ctx.RequestAborted);
        }));

    [Fact]
    public async Task Serves_the_body_and_the_validators_when_nothing_was_asked()
    {
        await using var test = await EtagServer();

        var response = await test.Client.GetAsync("/item", Token);

        Assert.Equal("\"v1\"", response.Headers.ETag?.ToString());
        Assert.Equal("the body", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Answers_304_to_a_matching_if_none_match()
    {
        await using var test = await EtagServer();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/item");
        request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue("\"v1\""));

        var response = await test.Client.SendAsync(request, Token);

        Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
        Assert.Equal("\"v1\"", response.Headers.ETag?.ToString());
        Assert.Empty(await response.Content.ReadAsStringAsync(Token));
    }

    /// <summary>A weak tag and a strong one are the same entity for a conditional GET.</summary>
    [Fact]
    public async Task Compares_weakly()
    {
        await using var test = await EtagServer();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/item");
        request.Headers.TryAddWithoutValidation("If-None-Match", "W/\"v1\"");

        Assert.Equal(HttpStatusCode.NotModified, (await test.Client.SendAsync(request, Token)).StatusCode);
    }

    [Fact]
    public async Task Serves_the_body_when_the_tag_moved_on()
    {
        await using var test = await EtagServer("v2");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/item");
        request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue("\"v1\""));

        var response = await test.Client.SendAsync(request, Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("the body", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Answers_304_to_an_if_modified_since_that_covers_it()
    {
        await using var test = await TestServer.StartAsync(server => server.MapGet("/item", async ctx =>
        {
            if (await ctx.TryCompleteConditionalAsync(etag: null, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)))
                return;

            await ctx.Response.WriteTextAsync("body", cancellationToken: ctx.RequestAborted);
        }));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/item");
        request.Headers.IfModifiedSince = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

        Assert.Equal(HttpStatusCode.NotModified, (await test.Client.SendAsync(request, Token)).StatusCode);
    }

    /// <summary>A failed If-Match is a 412: the caller's write was against a version that has moved.</summary>
    [Fact]
    public async Task A_stale_if_match_is_refused_with_412()
    {
        await using var test = await TestServer.StartAsync(server => server.MapPut("/item", async ctx =>
        {
            if (await ctx.TryCompleteConditionalAsync("v2"))
                return;

            await ctx.Response.WriteTextAsync("saved", cancellationToken: ctx.RequestAborted);
        }));

        using var request = new HttpRequestMessage(HttpMethod.Put, "/item") { Content = new StringContent("x") };
        request.Headers.IfMatch.Add(new EntityTagHeaderValue("\"v1\""));

        Assert.Equal(HttpStatusCode.PreconditionFailed, (await test.Client.SendAsync(request, Token)).StatusCode);
    }

    [Fact]
    public async Task A_current_if_match_goes_through()
    {
        await using var test = await TestServer.StartAsync(server => server.MapPut("/item", async ctx =>
        {
            if (await ctx.TryCompleteConditionalAsync("v2"))
                return;

            await ctx.Response.WriteTextAsync("saved", cancellationToken: ctx.RequestAborted);
        }));

        using var request = new HttpRequestMessage(HttpMethod.Put, "/item") { Content = new StringContent("x") };
        request.Headers.IfMatch.Add(new EntityTagHeaderValue("\"v2\""));

        var response = await test.Client.SendAsync(request, Token);

        Assert.Equal("saved", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public void Entity_tags_are_stable_and_quoted()
    {
        var first = EntityTag.FromContent("the same bytes");
        var second = EntityTag.FromContent("the same bytes");

        Assert.Equal(first, second);
        Assert.StartsWith("\"", first);
        Assert.NotEqual(first, EntityTag.FromContent("different bytes"));
    }
}

public class OutputCacheTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    static Task<TestServer> CountingServer(Action<HttpServer> map, Action<OutputCacheOptions>? configure = null)
        => TestServer.StartAsync(
            server =>
            {
                server.UseOutputCache();
                map(server);
            },
            builder => builder.AddOutputCache(configure)
        );

    [Fact]
    public async Task A_second_request_never_reaches_the_handler()
    {
        var calls = 0;

        await using var test = await CountingServer(server => server
            .MapGet("/list", ctx =>
            {
                Interlocked.Increment(ref calls);
                return ctx.Response.WriteTextAsync("payload", cancellationToken: ctx.RequestAborted);
            })
            .CacheOutput(TimeSpan.FromMinutes(1))
        );

        Assert.Equal("payload", await test.Client.GetStringAsync("/list", Token));
        Assert.Equal("payload", await test.Client.GetStringAsync("/list", Token));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task A_cache_hit_carries_an_age()
    {
        await using var test = await CountingServer(server => server
            .MapGet("/list", ctx => ctx.Response.WriteTextAsync("payload", cancellationToken: ctx.RequestAborted))
            .CacheOutput(TimeSpan.FromMinutes(1))
        );

        await test.Client.GetAsync("/list", Token);
        var second = await test.Client.GetAsync("/list", Token);

        Assert.True(second.Headers.Age.HasValue);
    }

    [Fact]
    public async Task Different_queries_are_different_responses()
    {
        var calls = 0;

        await using var test = await CountingServer(server => server
            .MapGet("/search", ctx =>
            {
                Interlocked.Increment(ref calls);
                return ctx.Response.WriteTextAsync(ctx.Request.Query["q"].ToString(), cancellationToken: ctx.RequestAborted);
            })
            .CacheOutput(TimeSpan.FromMinutes(1))
        );

        Assert.Equal("one", await test.Client.GetStringAsync("/search?q=one", Token));
        Assert.Equal("two", await test.Client.GetStringAsync("/search?q=two", Token));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task An_expired_entry_runs_the_handler_again()
    {
        var calls = 0;

        await using var test = await CountingServer(server => server
            .MapGet("/list", ctx =>
            {
                Interlocked.Increment(ref calls);
                return ctx.Response.WriteTextAsync("payload", cancellationToken: ctx.RequestAborted);
            })
            .CacheOutput(TimeSpan.FromMilliseconds(80))
        );

        await test.Client.GetStringAsync("/list", Token);
        await Task.Delay(200, Token);
        await test.Client.GetStringAsync("/list", Token);

        Assert.Equal(2, calls);
    }

    /// <summary>A POST is never stored: caching one turns a submit button into a replay.</summary>
    [Fact]
    public async Task Unsafe_methods_are_never_cached()
    {
        var calls = 0;

        await using var test = await CountingServer(server => server
            .Map(HttpMethods.Post, "/submit", ctx =>
            {
                Interlocked.Increment(ref calls);
                return ctx.Response.WriteTextAsync("done", cancellationToken: ctx.RequestAborted);
            })
            .CacheOutput(TimeSpan.FromMinutes(1))
        );

        await test.Client.PostAsync("/submit", new StringContent(""), Token);
        await test.Client.PostAsync("/submit", new StringContent(""), Token);

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task A_response_with_a_cookie_is_not_stored()
    {
        var calls = 0;

        await using var test = await CountingServer(server => server
            .MapGet("/session", ctx =>
            {
                Interlocked.Increment(ref calls);
                ctx.Response.Cookies.Append("who", "someone");

                return ctx.Response.WriteTextAsync("hello", cancellationToken: ctx.RequestAborted);
            })
            .CacheOutput(TimeSpan.FromMinutes(1))
        );

        await test.Client.GetStringAsync("/session", Token);
        await test.Client.GetStringAsync("/session", Token);

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task An_authenticated_request_is_not_served_from_a_shared_entry()
    {
        var calls = 0;

        await using var test = await CountingServer(server => server
            .MapGet("/me", ctx =>
            {
                Interlocked.Increment(ref calls);
                return ctx.Response.WriteTextAsync("mine", cancellationToken: ctx.RequestAborted);
            })
            .CacheOutput(TimeSpan.FromMinutes(1))
        );

        using var first = new HttpRequestMessage(HttpMethod.Get, "/me");
        first.Headers.TryAddWithoutValidation("Authorization", "Bearer one");
        await test.Client.SendAsync(first, Token);

        using var second = new HttpRequestMessage(HttpMethod.Get, "/me");
        second.Headers.TryAddWithoutValidation("Authorization", "Bearer two");
        await test.Client.SendAsync(second, Token);

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task An_endpoint_can_opt_out_of_the_default_policy()
    {
        var calls = 0;

        await using var test = await CountingServer(
            server => server
                .MapGet("/live", ctx =>
                {
                    Interlocked.Increment(ref calls);
                    return ctx.Response.WriteTextAsync("now", cancellationToken: ctx.RequestAborted);
                })
                .NoOutputCache(),
            o => o.DefaultPolicy = new OutputCachePolicy(TimeSpan.FromMinutes(1))
        );

        await test.Client.GetStringAsync("/live", Token);
        await test.Client.GetStringAsync("/live", Token);

        Assert.Equal(2, calls);
    }

    /// <summary>Streamed responses flush their own headers, and a buffer in front of one would hang it.</summary>
    [Fact]
    public async Task A_streamed_response_is_passed_through()
    {
        await using var test = await CountingServer(server => server
            .MapGet("/stream", async ctx =>
            {
                ctx.Response.ContentType = "text/plain";
                await ctx.Response.StartAsync(ctx.RequestAborted);
                await ctx.Response.WriteTextAsync("streamed", cancellationToken: ctx.RequestAborted);
            })
            .CacheOutput(TimeSpan.FromMinutes(1))
        );

        Assert.Equal("streamed", await test.Client.GetStringAsync("/stream", Token));
    }

    [Fact]
    public async Task A_body_past_the_limit_is_served_but_not_stored()
    {
        var calls = 0;
        var payload = new string('x', 5000);

        await using var test = await CountingServer(
            server => server
                .MapGet("/big", ctx =>
                {
                    Interlocked.Increment(ref calls);
                    return ctx.Response.WriteTextAsync(payload, cancellationToken: ctx.RequestAborted);
                })
                .CacheOutput(TimeSpan.FromMinutes(1)),
            o => o.MaxBodyBytes = 1024
        );

        Assert.Equal(payload, await test.Client.GetStringAsync("/big", Token));
        Assert.Equal(payload, await test.Client.GetStringAsync("/big", Token));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task A_revalidating_client_gets_a_304_straight_from_the_cache()
    {
        await using var test = await CountingServer(server => server
            .MapGet("/item", ctx =>
            {
                ctx.Response.SetETag("v1");
                return ctx.Response.WriteTextAsync("payload", cancellationToken: ctx.RequestAborted);
            })
            .CacheOutput(TimeSpan.FromMinutes(1))
        );

        await test.Client.GetStringAsync("/item", Token);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/item");
        request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue("\"v1\""));

        Assert.Equal(HttpStatusCode.NotModified, (await test.Client.SendAsync(request, Token)).StatusCode);
    }

    [Fact]
    public async Task The_memory_store_stays_inside_its_budget()
    {
        var store = new MemoryOutputCacheStore(maxBytes: 1000);
        var now = DateTimeOffset.UtcNow;

        for (var i = 0; i < 10; i++)
        {
            await store.SetAsync(
                "key" + i,
                new OutputCacheEntry(200, [], new byte[300], now.AddSeconds(i), now.AddMinutes(5)),
                Token
            );
        }

        Assert.True(store.SizeInBytes <= 1000);

        // The oldest went first, so the newest is still there.
        Assert.NotNull(await store.GetAsync("key9", Token));
        Assert.Null(await store.GetAsync("key0", Token));
    }
}
