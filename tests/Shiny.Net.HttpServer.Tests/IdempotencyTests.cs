using System.Net;
using System.Security.Claims;
using System.Text;
using Shiny.Net.HttpServer.Http3;
using Shiny.Net.HttpServer.Idempotency;
using Shiny.Net.HttpServer.Integrity;

namespace Shiny.Net.HttpServer.Tests;

// ---------------------------------------------------------------------------
// A generated endpoint class opting in with [Idempotent], the same arrangement
// as the other generated-policy tests: the attribute becomes route metadata at
// compile time and the middleware reads it like any hand-mapped route's.
// ---------------------------------------------------------------------------

[Route("/api/orders")]
[Idempotent]
public class IdempotentOrderEndpoints
{
    public static int Placed;

    [Post("/")]
    public string Place() => "order " + Interlocked.Increment(ref Placed);

    [Post("/preview")]
    [DisableIdempotency]
    public string Preview() => "preview " + Interlocked.Increment(ref Placed);

    [Post("/optional")]
    [Idempotent(Required = false)]
    public string Optional() => "optional " + Interlocked.Increment(ref Placed);
}

public class IdempotencyTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>A charge endpoint that counts how many times it really ran.</summary>
    sealed class Ledger
    {
        int charges;

        public int Charges => Volatile.Read(ref this.charges);

        public async ValueTask ChargeAsync(HttpContext ctx)
        {
            var body = await ctx.Request.ReadBodyAsStringAsync(maxLength: 1024 * 1024, cancellationToken: ctx.RequestAborted);
            var number = Interlocked.Increment(ref this.charges);

            ctx.Response.StatusCode = StatusCodes.Status201Created;
            ctx.Response.Headers.Set("X-Charge", number.ToString());
            await ctx.Response.WriteTextAsync($"charge {number} for {body}", cancellationToken: ctx.RequestAborted);
        }
    }

    static Task<TestServer> StartAsync(Action<HttpServer> map, Action<IdempotencyOptions>? configure = null)
        => TestServer.StartAsync(
            app =>
            {
                app.UseIdempotency();
                map(app);
            },
            builder => builder.AddIdempotency(configure)
        );

    static HttpRequestMessage Post(string path, string? key, string body = "10.00")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body) };

        if (key is not null)
            request.Headers.TryAddWithoutValidation(IdempotencyHeaders.IdempotencyKey, key);

        return request;
    }

    [Fact]
    public async Task A_retry_with_the_same_key_gets_the_stored_response_and_the_handler_runs_once()
    {
        var ledger = new Ledger();
        await using var server = await StartAsync(app => app.MapPost("/payments", ledger.ChargeAsync).RequireIdempotencyKey());

        var first = await server.Client.SendAsync(Post("/payments", "key-1"), Token);
        var retry = await server.Client.SendAsync(Post("/payments", "key-1"), Token);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(Token), await retry.Content.ReadAsStringAsync(Token));
        Assert.Equal("1", retry.Headers.GetValues("X-Charge").Single());
        Assert.Equal("true", retry.Headers.GetValues(IdempotencyHeaders.IdempotentReplayed).Single());
        Assert.False(first.Headers.Contains(IdempotencyHeaders.IdempotentReplayed));
        Assert.Equal(1, ledger.Charges);
    }

    [Fact]
    public async Task A_quoted_structured_field_key_is_the_same_key_as_the_bare_one()
    {
        var ledger = new Ledger();
        await using var server = await StartAsync(app => app.MapPost("/payments", ledger.ChargeAsync).RequireIdempotencyKey());

        await server.Client.SendAsync(Post("/payments", "\"8e03978e-40d5-43e8-bc93-6894a57f9324\""), Token);
        var retry = await server.Client.SendAsync(Post("/payments", "8e03978e-40d5-43e8-bc93-6894a57f9324"), Token);

        Assert.True(retry.Headers.Contains(IdempotencyHeaders.IdempotentReplayed));
        Assert.Equal(1, ledger.Charges);
    }

    [Fact]
    public async Task Different_keys_are_different_requests()
    {
        var ledger = new Ledger();
        await using var server = await StartAsync(app => app.MapPost("/payments", ledger.ChargeAsync).RequireIdempotencyKey());

        await server.Client.SendAsync(Post("/payments", "a"), Token);
        await server.Client.SendAsync(Post("/payments", "b"), Token);

        Assert.Equal(2, ledger.Charges);
    }

    [Fact]
    public async Task Reusing_a_key_with_a_different_body_is_refused_with_422()
    {
        var ledger = new Ledger();
        await using var server = await StartAsync(app => app.MapPost("/payments", ledger.ChargeAsync).RequireIdempotencyKey());

        await server.Client.SendAsync(Post("/payments", "key-1", "10.00"), Token);
        var reuse = await server.Client.SendAsync(Post("/payments", "key-1", "99.00"), Token);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, reuse.StatusCode);
        Assert.Equal("application/problem+json", reuse.Content.Headers.ContentType?.MediaType);
        Assert.Equal(1, ledger.Charges);
    }

    [Fact]
    public async Task A_duplicate_that_arrives_while_the_first_is_still_running_gets_409()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = 0;

        await using var server = await StartAsync(app => app.MapPost("/slow", async ctx =>
        {
            Interlocked.Increment(ref runs);
            entered.TrySetResult();
            await release.Task;
            await ctx.Response.WriteTextAsync("done", cancellationToken: ctx.RequestAborted);
        }).RequireIdempotencyKey());

        var first = server.Client.SendAsync(Post("/slow", "key-1"), Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);

        var duplicate = await server.Client.SendAsync(Post("/slow", "key-1"), Token);

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("application/problem+json", duplicate.Content.Headers.ContentType?.MediaType);
        Assert.True(duplicate.Headers.Contains("Retry-After"));

        release.SetResult();
        Assert.Equal(HttpStatusCode.OK, (await first).StatusCode);

        // Once the first has finished, the same retry is answered with its response.
        var later = await server.Client.SendAsync(Post("/slow", "key-1"), Token);
        Assert.Equal("done", await later.Content.ReadAsStringAsync(Token));
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task A_missing_key_on_an_endpoint_that_requires_one_is_a_400_problem()
    {
        var ledger = new Ledger();
        await using var server = await StartAsync(app => app.MapPost("/payments", ledger.ChargeAsync).RequireIdempotencyKey());

        var response = await server.Client.SendAsync(Post("/payments", key: null), Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Idempotency-Key", await response.Content.ReadAsStringAsync(Token));
        Assert.Equal(0, ledger.Charges);
    }

    [Fact]
    public async Task An_empty_or_oversized_key_is_a_400()
    {
        var ledger = new Ledger();
        await using var server = await StartAsync(
            app => app.MapPost("/payments", ledger.ChargeAsync).RequireIdempotencyKey(),
            o => o.MaxKeyLength = 8
        );

        Assert.Equal(HttpStatusCode.BadRequest, (await server.Client.SendAsync(Post("/payments", "\"\""), Token)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await server.Client.SendAsync(Post("/payments", "123456789"), Token)).StatusCode);
        Assert.Equal(0, ledger.Charges);
    }

    [Fact]
    public async Task WithIdempotency_honours_a_key_but_lets_a_request_without_one_through()
    {
        var ledger = new Ledger();
        await using var server = await StartAsync(app => app.MapPost("/payments", ledger.ChargeAsync).WithIdempotency());

        await server.Client.SendAsync(Post("/payments", key: null), Token);
        await server.Client.SendAsync(Post("/payments", key: null), Token);
        await server.Client.SendAsync(Post("/payments", "k"), Token);
        await server.Client.SendAsync(Post("/payments", "k"), Token);

        Assert.Equal(3, ledger.Charges);
    }

    [Fact]
    public async Task A_route_that_did_not_opt_in_ignores_the_key()
    {
        var ledger = new Ledger();
        await using var server = await StartAsync(app => app.MapPost("/payments", ledger.ChargeAsync));

        await server.Client.SendAsync(Post("/payments", "k"), Token);
        await server.Client.SendAsync(Post("/payments", "k"), Token);

        Assert.Equal(2, ledger.Charges);
    }

    [Fact]
    public async Task A_server_error_is_not_stored_so_the_retry_runs_again()
    {
        var runs = 0;
        await using var server = await StartAsync(app => app.MapPost("/flaky", async ctx =>
        {
            if (Interlocked.Increment(ref runs) == 1)
            {
                ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }

            await ctx.Response.WriteTextAsync("ok", cancellationToken: ctx.RequestAborted);
        }).RequireIdempotencyKey());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await server.Client.SendAsync(Post("/flaky", "k"), Token)).StatusCode);

        var retry = await server.Client.SendAsync(Post("/flaky", "k"), Token);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal("ok", await retry.Content.ReadAsStringAsync(Token));

        // ...and the success is what the next retry gets.
        var again = await server.Client.SendAsync(Post("/flaky", "k"), Token);
        Assert.True(again.Headers.Contains(IdempotencyHeaders.IdempotentReplayed));
        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task A_handler_that_throws_releases_the_key()
    {
        var runs = 0;
        await using var server = await StartAsync(app => app.MapPost("/throws", async ctx =>
        {
            if (Interlocked.Increment(ref runs) == 1)
                throw new InvalidOperationException("boom");

            await ctx.Response.WriteTextAsync("ok", cancellationToken: ctx.RequestAborted);
        }).RequireIdempotencyKey());

        Assert.Equal(HttpStatusCode.InternalServerError, (await server.Client.SendAsync(Post("/throws", "k"), Token)).StatusCode);
        Assert.Equal("ok", await (await server.Client.SendAsync(Post("/throws", "k"), Token)).Content.ReadAsStringAsync(Token));
        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task Client_errors_are_stored_by_default_and_the_status_filter_is_configurable()
    {
        var runs = 0;
        RequestDelegate reject = ctx =>
        {
            Interlocked.Increment(ref runs);
            ctx.Response.StatusCode = 402;
            return ValueTask.CompletedTask;
        };

        await using (var server = await StartAsync(app => app.MapPost("/declined", reject).RequireIdempotencyKey()))
        {
            await server.Client.SendAsync(Post("/declined", "k"), Token);
            var retry = await server.Client.SendAsync(Post("/declined", "k"), Token);

            Assert.Equal(HttpStatusCode.PaymentRequired, retry.StatusCode);
            Assert.Equal(1, runs);
        }

        runs = 0;

        await using (var server = await StartAsync(
            app => app.MapPost("/declined", reject).RequireIdempotencyKey(),
            o => o.ShouldStoreStatusCode = status => status is >= 200 and < 300
        ))
        {
            await server.Client.SendAsync(Post("/declined", "k"), Token);
            await server.Client.SendAsync(Post("/declined", "k"), Token);

            Assert.Equal(2, runs);
        }
    }

    [Fact]
    public async Task A_key_is_scoped_to_the_path()
    {
        var ledger = new Ledger();
        await using var server = await StartAsync(app =>
        {
            app.MapPost("/payments", ledger.ChargeAsync).RequireIdempotencyKey();
            app.MapPost("/refunds", ledger.ChargeAsync).RequireIdempotencyKey();
        });

        await server.Client.SendAsync(Post("/payments", "shared"), Token);
        var other = await server.Client.SendAsync(Post("/refunds", "shared"), Token);

        Assert.False(other.Headers.Contains(IdempotencyHeaders.IdempotentReplayed));
        Assert.Equal(2, ledger.Charges);
    }

    [Fact]
    public async Task A_key_is_scoped_to_the_caller_so_one_user_never_sees_another_users_reply()
    {
        var ledger = new Ledger();
        await using var server = await TestServer.StartAsync(
            app =>
            {
                // Stands in for authentication: whoever X-User names is the caller.
                app.Use((ctx, next) =>
                {
                    if (ctx.Request.Headers.GetFirst("X-User") is { } user)
                        ctx.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user)], "test"));

                    return next(ctx);
                });

                app.UseIdempotency();
                app.MapPost("/payments", ledger.ChargeAsync).RequireIdempotencyKey();
            },
            builder => builder.AddIdempotency()
        );

        HttpRequestMessage As(string user)
        {
            var request = Post("/payments", "same-key");
            request.Headers.Add("X-User", user);
            return request;
        }

        await server.Client.SendAsync(As("alice"), Token);
        var bob = await server.Client.SendAsync(As("bob"), Token);
        var aliceAgain = await server.Client.SendAsync(As("alice"), Token);

        Assert.False(bob.Headers.Contains(IdempotencyHeaders.IdempotentReplayed));
        Assert.True(aliceAgain.Headers.Contains(IdempotencyHeaders.IdempotentReplayed));
        Assert.Equal(2, ledger.Charges);
    }

    [Fact]
    public async Task Get_is_never_intercepted_even_on_an_opted_in_route()
    {
        var runs = 0;
        await using var server = await StartAsync(app => app.MapGet("/read", ctx =>
        {
            Interlocked.Increment(ref runs);
            return ctx.Response.WriteTextAsync("x", cancellationToken: ctx.RequestAborted);
        }).RequireIdempotencyKey());

        // No key and not refused: the method is idempotent already.
        Assert.Equal(HttpStatusCode.OK, (await server.Client.GetAsync("/read", Token)).StatusCode);
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task A_stored_response_expires()
    {
        var ledger = new Ledger();
        await using var server = await StartAsync(
            app => app.MapPost("/payments", ledger.ChargeAsync).RequireIdempotencyKey(TimeSpan.FromMilliseconds(150))
        );

        await server.Client.SendAsync(Post("/payments", "k"), Token);
        await Task.Delay(400, Token);
        await server.Client.SendAsync(Post("/payments", "k"), Token);

        Assert.Equal(2, ledger.Charges);
    }

    [Fact]
    public async Task A_request_body_over_the_limit_is_refused_with_413_and_claims_nothing()
    {
        var ledger = new Ledger();
        await using var server = await StartAsync(
            app => app.MapPost("/payments", ledger.ChargeAsync).RequireIdempotencyKey(),
            o => o.MaxRequestBodyBytes = 16
        );

        var tooBig = await server.Client.SendAsync(Post("/payments", "k", new string('x', 100)), Token);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooBig.StatusCode);

        // The corrected retry is not locked out by the refused one.
        var fixedUp = await server.Client.SendAsync(Post("/payments", "k", "small"), Token);
        Assert.Equal(HttpStatusCode.Created, fixedUp.StatusCode);
    }

    [Fact]
    public async Task A_response_too_large_to_store_is_served_and_the_key_released()
    {
        var runs = 0;
        await using var server = await StartAsync(
            app => app.MapPost("/big", ctx =>
            {
                Interlocked.Increment(ref runs);
                return ctx.Response.WriteTextAsync(new string('y', 1000), cancellationToken: ctx.RequestAborted);
            }).RequireIdempotencyKey(),
            o => o.MaxResponseBodyBytes = 100
        );

        var first = await server.Client.SendAsync(Post("/big", "k"), Token);
        Assert.Equal(1000, (await first.Content.ReadAsStringAsync(Token)).Length);

        await server.Client.SendAsync(Post("/big", "k"), Token);
        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task A_group_convention_covers_every_route_and_one_route_can_opt_out()
    {
        var ledger = new Ledger();
        await using var server = await StartAsync(app => app.MapGroup("/api", group =>
        {
            var payments = group.RequireIdempotencyKey();

            payments.MapPost("/pay", ledger.ChargeAsync);
            payments.MapGroup("/nested").MapPost("/pay", ledger.ChargeAsync);
            payments.MapPost("/quote", ledger.ChargeAsync).DisableIdempotency();
        }));

        Assert.Equal(HttpStatusCode.BadRequest, (await server.Client.SendAsync(Post("/api/pay", null), Token)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await server.Client.SendAsync(Post("/api/nested/pay", null), Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await server.Client.SendAsync(Post("/api/quote", null), Token)).StatusCode);
    }

    [Fact]
    public async Task Generated_endpoints_honour_the_attribute()
    {
        await using var server = await StartAsync(app => app.MapIdempotentOrderEndpoints());

        Assert.Equal(HttpStatusCode.BadRequest, (await server.Client.SendAsync(Post("/api/orders", null), Token)).StatusCode);

        var first = await (await server.Client.SendAsync(Post("/api/orders", "g1"), Token)).Content.ReadAsStringAsync(Token);
        var retry = await server.Client.SendAsync(Post("/api/orders", "g1"), Token);

        Assert.Equal(first, await retry.Content.ReadAsStringAsync(Token));
        Assert.True(retry.Headers.Contains(IdempotencyHeaders.IdempotentReplayed));

        // [DisableIdempotency] beats the class, and Required = false lets a keyless call through.
        Assert.Equal(HttpStatusCode.OK, (await server.Client.SendAsync(Post("/api/orders/preview", null), Token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await server.Client.SendAsync(Post("/api/orders/optional", null), Token)).StatusCode);
    }

    [Fact]
    public async Task Http2_replays_the_stored_response()
    {
        var ledger = new Ledger();
        await using var server = await StartAsync(app => app.MapPost("/payments", ledger.ChargeAsync).RequireIdempotencyKey());

        using var client = CreateHttp2Client(server.Port);

        var first = await client.SendAsync(H2(Post("/payments", "h2")), Token);
        var retry = await client.SendAsync(H2(Post("/payments", "h2")), Token);

        Assert.Equal(HttpVersion.Version20, retry.Version);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(Token), await retry.Content.ReadAsStringAsync(Token));
        Assert.True(retry.Headers.Contains(IdempotencyHeaders.IdempotentReplayed));
        Assert.Equal(1, ledger.Charges);
    }

    [Fact]
    public async Task Http2_concurrent_duplicates_on_one_connection_run_the_handler_once()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = 0;

        await using var server = await StartAsync(app => app.MapPost("/slow", async ctx =>
        {
            Interlocked.Increment(ref runs);
            await release.Task;
            await ctx.Response.WriteTextAsync("done", cancellationToken: ctx.RequestAborted);
        }).RequireIdempotencyKey());

        using var client = CreateHttp2Client(server.Port);

        // Multiplexed onto one connection: the case HTTP/1.1 cannot even produce.
        var requests = Enumerable.Range(0, 5).Select(_ => client.SendAsync(H2(Post("/slow", "same")), Token)).ToArray();

        await WaitUntilAsync(() => Volatile.Read(ref runs) == 1);
        await Task.Delay(200, Token);
        release.SetResult();

        var responses = await Task.WhenAll(requests);
        var statuses = responses.Select(r => r.StatusCode).ToList();

        Assert.All(responses, r => Assert.Equal(HttpVersion.Version20, r.Version));
        Assert.Equal(1, runs);
        Assert.Equal(1, statuses.Count(s => s == HttpStatusCode.OK));
        Assert.Equal(4, statuses.Count(s => s == HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task Http3_replays_the_stored_response()
    {
        Assert.SkipUnless(Http3Listener.IsSupported, "QUIC is not supported on this platform.");

        using var certificate = ServerCertificate.Create();
        var ledger = new Ledger();

        var builder = HttpServer.CreateBuilder();
        builder.Options.Address = IPAddress.Loopback;
        builder.Options.Port = 0;
        builder.Options.Https = new HttpsOptions { Certificate = certificate };
        builder.AddIdempotency();
        await using var server = builder.Build();

        server.UseIdempotency();
        server.MapPost("/payments", ledger.ChargeAsync).RequireIdempotencyKey();

        await using var h3 = await server.ListenHttp3Async(o =>
        {
            o.Port = 0;
            o.Certificate = certificate;
        }, Token);

        await server.StartAsync(Token);

        using var client = new HttpClient(CertificatePinning.CreateHandler(certificate))
        {
            BaseAddress = new Uri($"https://127.0.0.1:{h3.BoundEndPoint!.Port}"),
            Timeout = TimeSpan.FromSeconds(30)
        };

        static HttpRequestMessage H3(HttpRequestMessage request)
        {
            request.Version = HttpVersion.Version30;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
            return request;
        }

        var first = await client.SendAsync(H3(Post("/payments", "h3")), Token);
        var retry = await client.SendAsync(H3(Post("/payments", "h3")), Token);

        Assert.Equal(HttpVersion.Version30, retry.Version);
        Assert.Equal(await first.Content.ReadAsStringAsync(Token), await retry.Content.ReadAsStringAsync(Token));
        Assert.True(retry.Headers.Contains(IdempotencyHeaders.IdempotentReplayed));
        Assert.Equal(1, ledger.Charges);
    }

    [Fact]
    public async Task A_request_through_a_tunnel_is_replayed_too()
    {
        var ledger = new Ledger();

        var builder = HttpServer.CreateBuilder();
        builder.Options.Port = 0;
        builder.AddIdempotency();
        await using var server = builder.Build();

        server.UseIdempotency();
        server.MapPost("/payments", ledger.ChargeAsync).RequireIdempotencyKey();

        await using var tunnel = await RelayHarness.StartAsync(server, "idem");

        await tunnel.Client.SendAsync(tunnel.Route(Post("/payments", "tunnelled")), Token);
        var retry = await tunnel.Client.SendAsync(tunnel.Route(Post("/payments", "tunnelled")), Token);

        Assert.True(retry.Headers.Contains(IdempotencyHeaders.IdempotentReplayed));
        Assert.Equal(1, ledger.Charges);
    }

    [Fact]
    public async Task A_body_that_fails_its_content_digest_claims_no_key()
    {
        var ledger = new Ledger();
        await using var server = await TestServer.StartAsync(
            app =>
            {
                app.UseContentDigest();
                app.UseIdempotency();
                app.MapPost("/payments", ledger.ChargeAsync).RequireIdempotencyKey();
            },
            builder =>
            {
                builder.AddContentDigest();
                builder.AddIdempotency();
            }
        );

        HttpRequestMessage Signed(string digestOf)
        {
            var request = Post("/payments", "k", "10.00");
            request.Content!.Headers.TryAddWithoutValidation(DigestFields.ContentDigest, DigestFields.Compute(Encoding.UTF8.GetBytes(digestOf)));
            return request;
        }

        // Truncated in transit: refused, and the key is left free for the intact retry.
        Assert.Equal(HttpStatusCode.BadRequest, (await server.Client.SendAsync(Signed("10.0"), Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await server.Client.SendAsync(Signed("10.00"), Token)).StatusCode);
        Assert.Equal(1, ledger.Charges);
    }

    [Fact]
    public async Task Memory_store_reserves_atomically_and_never_evicts_a_claim_in_flight()
    {
        var store = new MemoryIdempotencyStore(maxBytes: 10);

        Assert.Null(await store.TryReserveAsync("in-flight", "fp", TimeSpan.FromMinutes(1), Token));
        Assert.True((await store.TryReserveAsync("in-flight", "fp", TimeSpan.FromMinutes(1), Token))!.InFlight);

        // Two completed records well over the byte budget: the older goes, the claim stays.
        foreach (var key in new[] { "old", "new" })
        {
            Assert.Null(await store.TryReserveAsync(key, "fp", TimeSpan.FromMinutes(1), Token));
            var response = new IdempotentResponse(200, [], new byte[8], DateTimeOffset.UtcNow);
            await store.CompleteAsync(key, new IdempotencyRecord("fp", response, DateTimeOffset.UtcNow.AddHours(1)), Token);
            await Task.Delay(5, Token);
        }

        Assert.NotNull(await store.TryReserveAsync("in-flight", "fp", TimeSpan.FromMinutes(1), Token));
        Assert.NotNull(await store.TryReserveAsync("new", "fp", TimeSpan.FromMinutes(1), Token));
        Assert.Null(await store.TryReserveAsync("old", "fp", TimeSpan.FromMinutes(1), Token));

        // Releasing a completed record does nothing; releasing a claim frees it.
        await store.ReleaseAsync("new", Token);
        Assert.NotNull(await store.TryReserveAsync("new", "fp", TimeSpan.FromMinutes(1), Token));

        await store.ReleaseAsync("in-flight", Token);
        Assert.Null(await store.TryReserveAsync("in-flight", "fp", TimeSpan.FromMinutes(1), Token));
    }

    [Fact]
    public async Task Memory_store_treats_an_expired_claim_as_abandoned()
    {
        var store = new MemoryIdempotencyStore();

        Assert.Null(await store.TryReserveAsync("k", "fp", TimeSpan.FromMilliseconds(50), Token));
        await Task.Delay(150, Token);

        Assert.Null(await store.TryReserveAsync("k", "fp", TimeSpan.FromMinutes(1), Token));
    }

    [Fact]
    public async Task A_second_AddIdempotency_call_composes_with_the_first()
    {
        var ledger = new Ledger();
        await using var server = await TestServer.StartAsync(
            app =>
            {
                app.UseIdempotency();
                app.MapPost("/payments", ledger.ChargeAsync).RequireIdempotencyKey();
            },
            builder =>
            {
                builder.AddIdempotency(o => o.ReplayedHeaderName = "X-Replayed");
                builder.AddIdempotency(o => o.HeaderName = "X-Key");
            }
        );

        HttpRequestMessage Request()
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/payments") { Content = new StringContent("1") };
            request.Headers.Add("X-Key", "k");
            return request;
        }

        await server.Client.SendAsync(Request(), Token);
        var retry = await server.Client.SendAsync(Request(), Token);

        Assert.Equal("true", retry.Headers.GetValues("X-Replayed").Single());
        Assert.Equal(1, ledger.Charges);
    }

    static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);

        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(25, Token);

        Assert.True(condition());
    }

    /// <summary>A message built by hand ignores the client's default version, so it has to say so itself.</summary>
    static HttpRequestMessage H2(HttpRequestMessage request)
    {
        request.Version = HttpVersion.Version20;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        return request;
    }

    static HttpClient CreateHttp2Client(int port) => new(new SocketsHttpHandler())
    {
        BaseAddress = new Uri($"http://127.0.0.1:{port}"),
        DefaultRequestVersion = HttpVersion.Version20,
        DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        Timeout = TimeSpan.FromSeconds(30)
    };
}
