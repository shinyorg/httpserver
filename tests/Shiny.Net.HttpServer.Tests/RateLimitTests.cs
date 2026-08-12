using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Shiny.Net.HttpServer.RateLimiting;

namespace Shiny.Net.HttpServer.Tests;

/// <summary>A clock the test moves by hand — real waiting makes for slow, flaky limiter tests.</summary>
sealed class TestTimeProvider : TimeProvider
{
    DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => this.now;

    public void Advance(TimeSpan by) => this.now = this.now.Add(by);
}

public class RateLimitPolicyTests
{
    [Fact]
    public void Fixed_window_refuses_past_the_limit_and_recovers_when_it_rolls()
    {
        var time = new TestTimeProvider();
        var policy = new FixedWindowRateLimitPolicy(2, TimeSpan.FromMinutes(1), time);

        Assert.True(policy.Acquire("a").IsAcquired);
        Assert.True(policy.Acquire("a").IsAcquired);

        var rejected = policy.Acquire("a");
        Assert.False(rejected.IsAcquired);
        Assert.NotNull(rejected.RetryAfter);
        Assert.InRange(rejected.RetryAfter!.Value, TimeSpan.Zero, TimeSpan.FromMinutes(1));

        time.Advance(TimeSpan.FromMinutes(1));
        Assert.True(policy.Acquire("a").IsAcquired);
    }

    [Fact]
    public void Partitions_do_not_share_an_allowance()
    {
        var policy = new FixedWindowRateLimitPolicy(1, TimeSpan.FromMinutes(1), new TestTimeProvider());

        Assert.True(policy.Acquire("a").IsAcquired);
        Assert.False(policy.Acquire("a").IsAcquired);
        Assert.True(policy.Acquire("b").IsAcquired);
    }

    [Fact]
    public void Fixed_window_reports_what_is_left()
    {
        var policy = new FixedWindowRateLimitPolicy(3, TimeSpan.FromMinutes(1), new TestTimeProvider());

        Assert.Equal(2, policy.Acquire("a").Remaining);
        Assert.Equal(1, policy.Acquire("a").Remaining);
        Assert.Equal(0, policy.Acquire("a").Remaining);
    }

    [Fact]
    public void Sliding_window_gives_the_allowance_back_a_segment_at_a_time()
    {
        var time = new TestTimeProvider();
        var policy = new SlidingWindowRateLimitPolicy(4, TimeSpan.FromSeconds(4), segments: 4, time);

        for (var i = 0; i < 4; i++)
            Assert.True(policy.Acquire("a").IsAcquired);

        Assert.False(policy.Acquire("a").IsAcquired);

        // One segment on, the four requests are still inside the window — this is the difference
        // from a fixed window, which would have handed the whole allowance back at the boundary.
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(policy.Acquire("a").IsAcquired);

        time.Advance(TimeSpan.FromSeconds(3));
        Assert.True(policy.Acquire("a").IsAcquired);
    }

    [Fact]
    public void Token_bucket_allows_a_burst_then_settles_into_the_refill_rate()
    {
        var time = new TestTimeProvider();
        var policy = new TokenBucketRateLimitPolicy(3, tokensPerPeriod: 1, TimeSpan.FromSeconds(1), time);

        for (var i = 0; i < 3; i++)
            Assert.True(policy.Acquire("a").IsAcquired);

        Assert.False(policy.Acquire("a").IsAcquired);

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(policy.Acquire("a").IsAcquired);
        Assert.False(policy.Acquire("a").IsAcquired);
    }

    [Fact]
    public void Token_bucket_never_fills_past_capacity()
    {
        var time = new TestTimeProvider();
        var policy = new TokenBucketRateLimitPolicy(2, tokensPerPeriod: 1, TimeSpan.FromSeconds(1), time);

        time.Advance(TimeSpan.FromHours(1));

        Assert.True(policy.Acquire("a").IsAcquired);
        Assert.True(policy.Acquire("a").IsAcquired);
        Assert.False(policy.Acquire("a").IsAcquired);
    }

    [Fact]
    public void Concurrency_counts_requests_in_flight_not_over_time()
    {
        var policy = new ConcurrencyRateLimitPolicy(1, new TestTimeProvider());

        var first = policy.Acquire("a");
        Assert.True(first.IsAcquired);
        Assert.False(policy.Acquire("a").IsAcquired);

        first.Dispose();
        Assert.True(policy.Acquire("a").IsAcquired);
    }

    [Fact]
    public void Disposing_a_lease_twice_does_not_hand_back_two_permits()
    {
        // Over-releasing a concurrency limiter is worse than leaking: it quietly raises the limit.
        var policy = new ConcurrencyRateLimitPolicy(2, new TestTimeProvider());

        var lease = policy.Acquire("a");
        lease.Dispose();
        lease.Dispose();

        Assert.True(policy.Acquire("a").IsAcquired);
        Assert.True(policy.Acquire("a").IsAcquired);
        Assert.False(policy.Acquire("a").IsAcquired);
    }

    [Fact]
    public void Idle_partitions_are_swept_away()
    {
        // Partitioned by IP and left to itself, a limiter on a public server would grow an entry per
        // address that ever knocked.
        var time = new TestTimeProvider();
        var policy = new FixedWindowRateLimitPolicy(1, TimeSpan.FromSeconds(1), time);

        policy.Acquire("a");
        policy.Acquire("b");
        Assert.Equal(2, policy.PartitionCount);

        time.Advance(TimeSpan.FromMinutes(5));
        policy.Sweep();

        Assert.Equal(0, policy.PartitionCount);
    }

    [Fact]
    public void A_partition_holding_permits_is_never_swept()
    {
        var time = new TestTimeProvider();
        var policy = new ConcurrencyRateLimitPolicy(2, time);

        using var held = policy.Acquire("a");

        time.Advance(TimeSpan.FromHours(1));
        policy.Sweep();

        Assert.Equal(1, policy.PartitionCount);
        Assert.True(policy.Acquire("a").IsAcquired);
        Assert.False(policy.Acquire("a").IsAcquired);
    }

    [Fact]
    public void A_swept_partition_starts_over()
    {
        var time = new TestTimeProvider();
        var policy = new FixedWindowRateLimitPolicy(1, TimeSpan.FromSeconds(1), time);

        Assert.True(policy.Acquire("a").IsAcquired);

        time.Advance(TimeSpan.FromMinutes(5));
        policy.Sweep();

        Assert.True(policy.Acquire("a").IsAcquired);
        Assert.False(policy.Acquire("a").IsAcquired);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_limit_below_one_is_rejected(int permitLimit)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new FixedWindowRateLimitPolicy(permitLimit, TimeSpan.FromSeconds(1))
        );
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConcurrencyRateLimitPolicy(permitLimit));
    }
}

public class RateLimitMiddlewareTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    static Task<TestServer> StartAsync(RateLimitPolicy policy)
        => TestServer.StartAsync(app =>
        {
            app.UseRateLimiter(policy);
            app.MapGet("/x", ctx => ctx.Response.WriteAsync("ok"));
        });

    static RateLimitPolicy Global(int permits)
        => new FixedWindowRateLimitPolicy(permits, TimeSpan.FromMinutes(5))
        {
            Partitioner = RateLimitPartitioners.Global
        };

    [Fact]
    public async Task Answers_429_once_the_allowance_is_spent()
    {
        await using var server = await StartAsync(Global(2));

        Assert.Equal(HttpStatusCode.OK, (await server.Client.GetAsync("/x", Token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await server.Client.GetAsync("/x", Token)).StatusCode);

        var throttled = await server.Client.GetAsync("/x", Token);

        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        Assert.NotNull(throttled.Headers.RetryAfter?.Delta);
    }

    [Fact]
    public async Task Reports_the_limit_and_what_is_left()
    {
        await using var server = await StartAsync(Global(2));

        var first = await server.Client.GetAsync("/x", Token);

        Assert.Equal("2", first.Headers.GetValues("X-RateLimit-Limit").Single());
        Assert.Equal("1", first.Headers.GetValues("X-RateLimit-Remaining").Single());
    }

    [Fact]
    public async Task The_handler_never_runs_for_a_throttled_request()
    {
        var calls = 0;

        await using var server = await TestServer.StartAsync(app =>
        {
            app.UseRateLimiter(Global(1));
            app.MapGet("/x", ctx => { calls++; return ctx.Response.WriteAsync("ok"); });
        });

        await server.Client.GetAsync("/x", Token);
        await server.Client.GetAsync("/x", Token);

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Requests_that_match_no_route_are_limited_too()
    {
        // A scanner produces 404s, and a limiter that only covered mapped routes would let every
        // one of them through at full price.
        await using var server = await StartAsync(Global(1));

        Assert.Equal(HttpStatusCode.NotFound, (await server.Client.GetAsync("/nowhere", Token)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await server.Client.GetAsync("/nowhere", Token)).StatusCode);
    }

    [Fact]
    public async Task Partitions_by_header_and_exempts_requests_without_one()
    {
        await using var server = await TestServer.StartAsync(app =>
        {
            app.UseRateLimiter(new FixedWindowRateLimitPolicy(1, TimeSpan.FromMinutes(5))
            {
                Partitioner = RateLimitPartitioners.ByHeader("X-Api-Key")
            });
            app.MapGet("/x", ctx => ctx.Response.WriteAsync("ok"));
        });

        Assert.Equal(HttpStatusCode.OK, (await Send(server, "one")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Send(server, "one")).StatusCode);

        // A different key is a different bucket...
        Assert.Equal(HttpStatusCode.OK, (await Send(server, "two")).StatusCode);

        // ...and a request the partitioner has no key for is none of this policy's business.
        Assert.Equal(HttpStatusCode.OK, (await server.Client.GetAsync("/x", Token)).StatusCode);

        static Task<HttpResponseMessage> Send(TestServer server, string key)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/x");
            request.Headers.Add("X-Api-Key", key);

            return server.Client.SendAsync(request, Token);
        }
    }

    [Fact]
    public async Task An_endpoint_can_name_a_tighter_policy()
    {
        await using var server = await TestServer.StartAsync(
            app =>
            {
                app.UseRateLimiter();
                app.MapGet("/cheap", ctx => ctx.Response.WriteAsync("ok"));
                app.MapPost("/upload", ctx => ctx.Response.WriteAsync("ok")).RequireRateLimiting("uploads");
            },
            builder => builder.Services.AddRateLimiter(o =>
            {
                o.GlobalPolicy = Global(100);
                o.AddPolicy("uploads", new FixedWindowRateLimitPolicy(1, TimeSpan.FromMinutes(5))
                {
                    Partitioner = RateLimitPartitioners.Global
                });
            })
        );

        Assert.Equal(HttpStatusCode.OK, (await server.Client.PostAsync("/upload", null, Token)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await server.Client.PostAsync("/upload", null, Token)).StatusCode);

        // The tighter policy is the endpoint's own; everything else still has its global allowance.
        Assert.Equal(HttpStatusCode.OK, (await server.Client.GetAsync("/cheap", Token)).StatusCode);
    }

    [Fact]
    public async Task An_endpoint_can_opt_out_of_the_global_policy()
    {
        await using var server = await TestServer.StartAsync(
            app =>
            {
                app.UseRateLimiter();
                app.MapGet("/x", ctx => ctx.Response.WriteAsync("ok"));
                app.MapGet("/health", ctx => ctx.Response.WriteAsync("ok")).DisableRateLimiting();
            },
            builder => builder.Services.AddRateLimiter(o => o.GlobalPolicy = Global(1))
        );

        Assert.Equal(HttpStatusCode.OK, (await server.Client.GetAsync("/x", Token)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await server.Client.GetAsync("/x", Token)).StatusCode);

        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.OK, (await server.Client.GetAsync("/health", Token)).StatusCode);
    }

    [Fact]
    public async Task The_rejection_response_can_be_replaced()
    {
        await using var server = await TestServer.StartAsync(
            app =>
            {
                app.UseRateLimiter();
                app.MapGet("/x", ctx => ctx.Response.WriteAsync("ok"));
            },
            builder => builder.Services.AddRateLimiter(o =>
            {
                o.GlobalPolicy = Global(1);
                o.OnRejected = (ctx, lease) =>
                {
                    ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    return ctx.Response.WriteAsync($"slow down, limit is {lease.Limit}");
                };
            })
        );

        await server.Client.GetAsync("/x", Token);
        var response = await server.Client.GetAsync("/x", Token);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("slow down, limit is 1", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task A_concurrency_permit_is_held_for_the_whole_request_and_returned_after_it()
    {
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        await using var server = await TestServer.StartAsync(app =>
        {
            app.UseRateLimiter(new ConcurrencyRateLimitPolicy(1) { Partitioner = RateLimitPartitioners.Global });
            app.MapGet("/slow", async ctx =>
            {
                entered.TrySetResult();
                await release.Task;
                await ctx.Response.WriteAsync("done");
            });
        });

        var inFlight = server.Client.GetAsync("/slow", Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);

        // The permit is still out, so the second request is refused rather than queued.
        Assert.Equal(HttpStatusCode.TooManyRequests, (await server.Client.GetAsync("/slow", Token)).StatusCode);

        release.SetResult();
        Assert.Equal(HttpStatusCode.OK, (await inFlight).StatusCode);

        // ...and once the response is complete the permit is back.
        release = new TaskCompletionSource();
        release.SetResult();
        Assert.Equal(HttpStatusCode.OK, (await server.Client.GetAsync("/slow", Token)).StatusCode);
    }

    [Fact]
    public async Task A_named_policy_that_was_never_registered_says_so()
    {
        await using var server = await TestServer.StartAsync(
            app =>
            {
                app.UseRateLimiter();
                app.MapGet("/x", ctx => ctx.Response.WriteAsync("ok")).RequireRateLimiting("missing");
            },
            builder => builder.Services.AddRateLimiter(o => o.GlobalPolicy = Global(10))
        );

        var response = await server.Client.GetAsync("/x", Token);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("missing", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task UseRateLimiter_without_a_policy_says_so()
    {
        await using var server = await TestServer.StartAsync(app => app.MapGet("/x", ctx => ctx.Response.WriteAsync("ok")));

        var error = Assert.Throws<InvalidOperationException>(() => server.Server.UseRateLimiter());
        Assert.Contains("AddRateLimiter", error.Message);
    }
}
