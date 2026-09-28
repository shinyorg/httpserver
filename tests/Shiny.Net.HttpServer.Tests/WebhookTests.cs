using System.Net;
using System.Security.Cryptography;
using System.Text;
using Shiny.Net.HttpServer.Testing;
using Shiny.Net.HttpServer.Webhooks;

namespace Shiny.Net.HttpServer.Tests;

/// <summary>
/// The verifiers against the worked examples the providers publish. Matching a vector computed by
/// the same code under test proves only that the code agrees with itself; these came from the
/// senders' documentation, so passing them means a real delivery verifies.
/// </summary>
public class WebhookVerifierTests
{
    // docs.github.com — "Validating webhook deliveries", testing the webhook payload validation.
    const string GitHubSecret = "It's a Secret to Everybody";
    const string GitHubBody = "Hello, World!";
    const string GitHubSignature = "sha256=757107ea0eb2509fc211221cce984b8a37570b6d7586c22c46f4379c8b043e17";

    // standard-webhooks spec / reference libraries.
    const string StandardSecret = "whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw";
    const string StandardId = "msg_p5jXN8AQM9LWM0D4loKWxJek";
    const long StandardTimestamp = 1614265330;
    const string StandardBody = "{\"test\": 2432232314}";
    const string StandardSignature = "v1,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE=";

    // api.slack.com — "Verifying requests from Slack", the worked example.
    const string SlackSecret = "8f742231b10e8888abcd99yyyzzz85a5";
    const long SlackTimestamp = 1531420618;
    const string SlackBody = "token=xyzz0WbapA4vBCDEFasx0q6G&team_id=T1DC2JH3J&team_domain=testteamnow&channel_id=G8PSS9T3V&channel_name=foobar&user_id=U2CERLKJA&user_name=roadrunner&command=%2Fwebhook-collect&text=&response_url=https%3A%2F%2Fhooks.slack.com%2Fcommands%2FT1DC2JH3J%2F397700885554%2F96rGlfmibIGlgcZRskXaIFfN&trigger_id=398738663015.47445629121.803a0bc887a14d10d2c447fce8b6703c";
    const string SlackSignature = "v0=a2114d57b48eac39b9ad189dd8316235a7b4a8d21a10bd27519666489c69b503";

    static HeaderDictionary Headers(params (string Name, string Value)[] values)
    {
        var headers = new HeaderDictionary();
        foreach (var (name, value) in values)
            headers.Set(name, value);

        return headers;
    }

    static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);

    static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public void GitHub_accepts_the_documented_example()
    {
        var result = WebhookSignature.GitHub(GitHubSecret).Verify(
            Headers(("X-Hub-Signature-256", GitHubSignature), ("X-GitHub-Delivery", "d-1"), ("X-GitHub-Event", "push")),
            Utf8(GitHubBody),
            Now
        );

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal("d-1", result.DeliveryId);
        Assert.Equal("push", result.EventType);
    }

    [Fact]
    public void GitHub_accepts_an_uppercase_hex_signature()
        => Assert.True(WebhookSignature.GitHub(GitHubSecret).Verify(
            Headers(("X-Hub-Signature-256", "sha256=" + GitHubSignature[7..].ToUpperInvariant())),
            Utf8(GitHubBody),
            Now
        ).Succeeded);

    [Theory]
    [InlineData("sha256=0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("757107ea0eb2509fc211221cce984b8a37570b6d7586c22c46f4379c8b043e17")]
    [InlineData("sha256=not-hex")]
    [InlineData("sha256=")]
    [InlineData("")]
    public void GitHub_refuses_a_wrong_or_malformed_signature(string signature)
        => Assert.False(WebhookSignature.GitHub(GitHubSecret).Verify(
            Headers(("X-Hub-Signature-256", signature)),
            Utf8(GitHubBody),
            Now
        ).Succeeded);

    [Fact]
    public void GitHub_refuses_a_body_that_changed_by_one_byte()
        => Assert.False(WebhookSignature.GitHub(GitHubSecret).Verify(
            Headers(("X-Hub-Signature-256", GitHubSignature)),
            Utf8(GitHubBody + " "),
            Now
        ).Succeeded);

    [Fact]
    public void Any_configured_secret_verifies_during_rotation()
        => Assert.True(WebhookSignature.GitHub("the-new-secret", GitHubSecret).Verify(
            Headers(("X-Hub-Signature-256", GitHubSignature)),
            Utf8(GitHubBody),
            Now
        ).Succeeded);

    [Fact]
    public void Standard_webhooks_accepts_the_spec_example()
    {
        var result = WebhookSignature.StandardWebhooks(StandardSecret).Verify(
            Headers(("webhook-id", StandardId), ("webhook-timestamp", StandardTimestamp.ToString()), ("webhook-signature", StandardSignature)),
            Utf8(StandardBody),
            DateTimeOffset.FromUnixTimeSeconds(StandardTimestamp + 10)
        );

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal(StandardId, result.DeliveryId);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(StandardTimestamp), result.Timestamp);
    }

    [Fact]
    public void Standard_webhooks_accepts_the_svix_header_names()
        => Assert.True(WebhookSignature.StandardWebhooks(StandardSecret).Verify(
            Headers(("svix-id", StandardId), ("svix-timestamp", StandardTimestamp.ToString()), ("svix-signature", StandardSignature)),
            Utf8(StandardBody),
            DateTimeOffset.FromUnixTimeSeconds(StandardTimestamp)
        ).Succeeded);

    [Fact]
    public void Standard_webhooks_accepts_a_secret_without_its_prefix()
        => Assert.True(WebhookSignature.StandardWebhooks(StandardSecret["whsec_".Length..]).Verify(
            Headers(("webhook-id", StandardId), ("webhook-timestamp", StandardTimestamp.ToString()), ("webhook-signature", StandardSignature)),
            Utf8(StandardBody),
            DateTimeOffset.FromUnixTimeSeconds(StandardTimestamp)
        ).Succeeded);

    [Fact]
    public void Standard_webhooks_finds_the_valid_signature_among_several()
    {
        // What a sender mid-rotation sends: one signature per active key, plus an asymmetric one
        // this verifier does not speak.
        var header = "v1,AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA= v1a,c29tZXRoaW5n " + StandardSignature;

        Assert.True(WebhookSignature.StandardWebhooks(StandardSecret).Verify(
            Headers(("webhook-id", StandardId), ("webhook-timestamp", StandardTimestamp.ToString()), ("webhook-signature", header)),
            Utf8(StandardBody),
            DateTimeOffset.FromUnixTimeSeconds(StandardTimestamp)
        ).Succeeded);
    }

    [Theory]
    [InlineData(-301)]
    [InlineData(301)]
    public void Standard_webhooks_refuses_a_timestamp_outside_the_tolerance(int skewSeconds)
    {
        var result = WebhookSignature.StandardWebhooks(StandardSecret).Verify(
            Headers(("webhook-id", StandardId), ("webhook-timestamp", StandardTimestamp.ToString()), ("webhook-signature", StandardSignature)),
            Utf8(StandardBody),
            DateTimeOffset.FromUnixTimeSeconds(StandardTimestamp + skewSeconds)
        );

        Assert.False(result.Succeeded);
        Assert.Contains("tolerance", result.FailureReason);
    }

    [Fact]
    public void Standard_webhooks_signature_covers_the_id()
        => Assert.False(WebhookSignature.StandardWebhooks(StandardSecret).Verify(
            Headers(("webhook-id", "msg_somebody_else"), ("webhook-timestamp", StandardTimestamp.ToString()), ("webhook-signature", StandardSignature)),
            Utf8(StandardBody),
            DateTimeOffset.FromUnixTimeSeconds(StandardTimestamp)
        ).Succeeded);

    [Fact]
    public void Standard_webhooks_refuses_a_secret_that_is_not_base64_at_construction()
        => Assert.Throws<ArgumentException>(() => WebhookSignature.StandardWebhooks("whsec_not base64!"));

    [Fact]
    public void Slack_accepts_the_documented_example()
    {
        var result = WebhookSignature.Slack(SlackSecret).Verify(
            Headers(("X-Slack-Signature", SlackSignature), ("X-Slack-Request-Timestamp", SlackTimestamp.ToString())),
            Utf8(SlackBody),
            DateTimeOffset.FromUnixTimeSeconds(SlackTimestamp + 60)
        );

        Assert.True(result.Succeeded, result.FailureReason);
    }

    [Fact]
    public void Slack_refuses_a_replayed_request()
        => Assert.False(WebhookSignature.Slack(SlackSecret).Verify(
            Headers(("X-Slack-Signature", SlackSignature), ("X-Slack-Request-Timestamp", SlackTimestamp.ToString())),
            Utf8(SlackBody),
            DateTimeOffset.FromUnixTimeSeconds(SlackTimestamp + 3600)
        ).Succeeded);

    [Fact]
    public void Slack_signature_covers_the_timestamp()
        => Assert.False(WebhookSignature.Slack(tolerance: null, SlackSecret).Verify(
            Headers(("X-Slack-Signature", SlackSignature), ("X-Slack-Request-Timestamp", (SlackTimestamp + 1).ToString())),
            Utf8(SlackBody),
            Now
        ).Succeeded);

    static string StripeHeader(string secret, long timestamp, string body)
    {
        var hash = HMACSHA256.HashData(Utf8(secret), Utf8($"{timestamp}.{body}"));
        return $"t={timestamp},v1={Convert.ToHexStringLower(hash)}";
    }

    [Fact]
    public void Stripe_accepts_a_signed_event_and_reads_its_id_and_type()
    {
        const string body = "{\"id\":\"evt_123\",\"object\":\"event\",\"data\":{\"object\":{\"id\":\"pi_1\",\"type\":\"nested\"}},\"type\":\"payment_intent.succeeded\"}";
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

        var result = WebhookSignature.Stripe("whsec_test").Verify(
            Headers(("Stripe-Signature", StripeHeader("whsec_test", now.ToUnixTimeSeconds(), body))),
            Utf8(body),
            now
        );

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal("evt_123", result.DeliveryId);
        Assert.Equal("payment_intent.succeeded", result.EventType);
    }

    [Fact]
    public void Stripe_accepts_any_of_several_v1_signatures()
    {
        const string body = "{\"id\":\"evt_1\"}";
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

        // Stripe sends one v1 per active secret while one is being rolled, plus a legacy v0.
        var valid = StripeHeader("whsec_new", now.ToUnixTimeSeconds(), body);
        var header = $"t={now.ToUnixTimeSeconds()},v1={new string('a', 64)},{valid.Split(',')[1]},v0={new string('b', 64)}";

        Assert.True(WebhookSignature.Stripe("whsec_new").Verify(Headers(("Stripe-Signature", header)), Utf8(body), now).Succeeded);
    }

    [Fact]
    public void Stripe_refuses_a_stale_timestamp()
    {
        const string body = "{}";
        var signedAt = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

        var result = WebhookSignature.Stripe("whsec_test").Verify(
            Headers(("Stripe-Signature", StripeHeader("whsec_test", signedAt.ToUnixTimeSeconds(), body))),
            Utf8(body),
            signedAt.AddMinutes(6)
        );

        Assert.False(result.Succeeded);
    }

    [Fact]
    public void Stripe_tolerance_can_be_widened()
    {
        const string body = "{}";
        var signedAt = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

        Assert.True(WebhookSignature.Stripe(TimeSpan.FromMinutes(10), "whsec_test").Verify(
            Headers(("Stripe-Signature", StripeHeader("whsec_test", signedAt.ToUnixTimeSeconds(), body))),
            Utf8(body),
            signedAt.AddMinutes(6)
        ).Succeeded);
    }

    [Fact]
    public void Stripe_refuses_a_header_without_a_timestamp()
        => Assert.False(WebhookSignature.Stripe("whsec_test").Verify(
            Headers(("Stripe-Signature", "v1=" + new string('a', 64))),
            Utf8("{}"),
            Now
        ).Succeeded);

    [Theory]
    [InlineData(WebhookHmacAlgorithm.Sha1, WebhookSignatureEncoding.Base64, null)]
    [InlineData(WebhookHmacAlgorithm.Sha256, WebhookSignatureEncoding.Base64, null)]
    [InlineData(WebhookHmacAlgorithm.Sha512, WebhookSignatureEncoding.Hex, "sha512=")]
    public void Generic_hmac_verifies_every_algorithm_and_encoding(WebhookHmacAlgorithm algorithm, WebhookSignatureEncoding encoding, string? prefix)
    {
        var body = Utf8("{\"order\":42}");
        var key = Utf8("shpss_secret");
        var hash = algorithm switch
        {
            WebhookHmacAlgorithm.Sha1 => HMACSHA1.HashData(key, body),
            WebhookHmacAlgorithm.Sha256 => HMACSHA256.HashData(key, body),
            _ => HMACSHA512.HashData(key, body)
        };
        var signature = prefix + (encoding == WebhookSignatureEncoding.Hex ? Convert.ToHexStringLower(hash) : Convert.ToBase64String(hash));

        var verifier = WebhookSignature.Hmac(o =>
        {
            o.Name = "shop";
            o.HeaderName = "X-Signature";
            o.Algorithm = algorithm;
            o.Encoding = encoding;
            o.Prefix = prefix;
            o.Secrets.Add("shpss_secret");
            o.DeliveryIdHeader = "X-Id";
        });

        var result = verifier.Verify(Headers(("X-Signature", signature), ("X-Id", "abc")), body, Now);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal("abc", result.DeliveryId);
        Assert.Equal("shop", verifier.Name);
    }

    [Fact]
    public void Generic_hmac_needs_a_header_and_a_secret()
    {
        Assert.Throws<ArgumentException>(() => WebhookSignature.Hmac(o => o.Secrets.Add("s")));
        Assert.Throws<ArgumentException>(() => WebhookSignature.Hmac(o => o.HeaderName = "X-Sig"));
    }

    [Fact]
    public void Signer_reproduces_the_spec_example()
    {
        var signer = WebhookSigner.StandardWebhooks(StandardSecret);

        Assert.Equal(
            StandardSignature,
            signer.Sign(StandardId, DateTimeOffset.FromUnixTimeSeconds(StandardTimestamp), Utf8(StandardBody))
        );
    }

    [Fact]
    public void Generated_secrets_round_trip_through_the_verifier()
    {
        var secret = WebhookSigner.GenerateSecret();
        var signer = WebhookSigner.StandardWebhooks(secret);
        var body = Utf8("{\"type\":\"device.paired\"}");
        var at = DateTimeOffset.UtcNow;

        var result = WebhookSignature.StandardWebhooks(secret).Verify(
            Headers(("webhook-id", "msg_1"), ("webhook-timestamp", at.ToUnixTimeSeconds().ToString()), ("webhook-signature", signer.Sign("msg_1", at, body))),
            body,
            at
        );

        Assert.StartsWith("whsec_", secret);
        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal("device.paired", result.EventType);
    }
}

public class WebhookDeliveryStoreTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Refuses_a_key_it_already_holds()
    {
        var store = new InMemoryWebhookDeliveryStore();

        Assert.True(await store.TryAddAsync("github:1", Token));
        Assert.False(await store.TryAddAsync("github:1", Token));
        Assert.True(await store.TryAddAsync("github:2", Token));
    }

    [Fact]
    public async Task Stays_within_its_capacity_dropping_the_oldest()
    {
        var store = new InMemoryWebhookDeliveryStore(capacity: 3);

        for (var i = 0; i < 10; i++)
            await store.TryAddAsync("k" + i, Token);

        Assert.Equal(3, store.Count);
        Assert.True(await store.TryAddAsync("k0", Token));
        Assert.False(await store.TryAddAsync("k9", Token));
    }

    [Fact]
    public async Task Forgets_keys_after_the_retention_window()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = new InMemoryWebhookDeliveryStore(retention: TimeSpan.FromHours(1), timeProvider: clock);

        await store.TryAddAsync("a", Token);
        clock.Advance(TimeSpan.FromMinutes(61));

        Assert.True(await store.TryAddAsync("a", Token));
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public async Task A_removed_key_can_be_added_again()
    {
        var store = new InMemoryWebhookDeliveryStore();

        await store.TryAddAsync("a", Token);
        await store.RemoveAsync("a", Token);

        Assert.True(await store.TryAddAsync("a", Token));
    }
}

// ---------------------------------------------------------------------------
// Generated endpoints: [RequireWebhookSignature] becomes WebhookMetadata, and a
// WebhookContext parameter binds the verified delivery.
// ---------------------------------------------------------------------------

[Route("/gen-hooks")]
public class GeneratedHookEndpoints
{
    [Post("/github")]
    [RequireWebhookSignature("github")]
    public string GitHub(WebhookContext webhook) => $"{webhook.EventType}:{webhook.ReadAsString()}";

    [Post("/raw")]
    [RequireWebhookSignature("github")]
    public async Task<string> Raw(HttpRequest request, CancellationToken ct)
        => "raw:" + await request.ReadBodyAsStringAsync(cancellationToken: ct);
}

public class WebhookEndpointTests
{
    const string Secret = "It's a Secret to Everybody";

    static CancellationToken Token => TestContext.Current.CancellationToken;

    static HttpRequestMessage GitHubDelivery(string path, string body, string? secret = Secret, string delivery = "d-1", string @event = "push")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        if (secret is not null)
        {
            var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body));
            request.Headers.TryAddWithoutValidation("X-Hub-Signature-256", "sha256=" + Convert.ToHexStringLower(hash));
        }

        request.Headers.TryAddWithoutValidation("X-GitHub-Delivery", delivery);
        request.Headers.TryAddWithoutValidation("X-GitHub-Event", @event);
        return request;
    }

    static HttpRequestMessage Http2(HttpRequestMessage request)
    {
        // DefaultRequestVersion only applies to requests the client builds itself.
        request.Version = HttpVersion.Version20;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        return request;
    }

    static void MapGitHub(HttpServer app, Action<WebhookOptions>? configure = null, List<string>? seen = null)
        => app.MapWebhook("/hooks/github", WebhookSignature.GitHub(Secret), async (WebhookContext ctx) =>
        {
            seen?.Add(ctx.DeliveryId!);

            // Both views of the body: the verified bytes, and the request stream the ordinary API reads.
            var viaStream = await ctx.Request.ReadBodyAsStringAsync(cancellationToken: ctx.RequestAborted);
            await ctx.Response.WriteTextAsync($"{ctx.EventType}|{ctx.ReadAsString()}|{viaStream}", cancellationToken: ctx.RequestAborted);
        }, configure);

    static HttpClient Http2Client(int port) => new(new SocketsHttpHandler())
    {
        BaseAddress = new Uri($"http://127.0.0.1:{port}"),
        DefaultRequestVersion = HttpVersion.Version20,
        DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        Timeout = TimeSpan.FromSeconds(30)
    };

    [Fact]
    public async Task Http1_runs_the_handler_for_a_valid_delivery_with_the_body_readable_both_ways()
    {
        await using var server = await TestServer.StartAsync(app => MapGitHub(app));

        var response = await server.Client.SendAsync(GitHubDelivery("/hooks/github", "{\"ref\":\"main\"}"), Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("push|{\"ref\":\"main\"}|{\"ref\":\"main\"}", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Http1_refuses_a_forged_delivery_with_a_problem_that_names_no_check()
    {
        var seen = new List<string>();
        await using var server = await TestServer.StartAsync(app => MapGitHub(app, seen: seen));

        var response = await server.Client.SendAsync(GitHubDelivery("/hooks/github", "{}", secret: "guess"), Token);
        var body = await response.Content.ReadAsStringAsync(Token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("could not be verified", body);
        Assert.DoesNotContain("secret", body, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(seen);
    }

    [Fact]
    public async Task Http1_refuses_an_unsigned_delivery()
    {
        await using var server = await TestServer.StartAsync(app => MapGitHub(app));

        var response = await server.Client.SendAsync(GitHubDelivery("/hooks/github", "{}", secret: null), Token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Http2_verifies_exactly_as_http1_does()
    {
        await using var server = await TestServer.StartAsync(app => MapGitHub(app));
        using var client = Http2Client(server.Port);

        var ok = await client.SendAsync(Http2(GitHubDelivery("/hooks/github", "{\"ref\":\"h2\"}")), Token);
        var forged = await client.SendAsync(Http2(GitHubDelivery("/hooks/github", "{}", secret: "guess")), Token);

        Assert.Equal(HttpVersion.Version20, ok.Version);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("push|{\"ref\":\"h2\"}|{\"ref\":\"h2\"}", await ok.Content.ReadAsStringAsync(Token));
        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
    }

    [Fact]
    public async Task Http2_verifies_a_body_that_spans_many_frames()
    {
        // Larger than the 16KB default frame, so the body arrives in pieces and has to be
        // reassembled exactly before the signature can match.
        var large = "{\"blob\":\"" + new string('x', 100_000) + "\"}";

        await using var server = await TestServer.StartAsync(app =>
            app.MapWebhook("/hooks/github", WebhookSignature.GitHub(Secret), async (WebhookContext ctx) =>
                await ctx.Response.WriteTextAsync(ctx.Body.Length.ToString(), cancellationToken: ctx.RequestAborted)));

        using var client = Http2Client(server.Port);
        var response = await client.SendAsync(Http2(GitHubDelivery("/hooks/github", large)), Token);

        Assert.Equal(HttpVersion.Version20, response.Version);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(large.Length.ToString(), await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task In_memory_transport_verifies_too()
    {
        // The in-memory harness serves a stream the way a tunnel provider does, so this is the
        // tunnelled path: no socket, same pipeline.
        await using var app = TestHttpServer.Create(server => MapGitHub(server));

        var ok = await app.Client.SendAsync(GitHubDelivery("/hooks/github", "{}"), Token);
        var forged = await app.Client.SendAsync(GitHubDelivery("/hooks/github", "{}", secret: "nope"), Token);

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
    }

    [Fact]
    public async Task Refuses_a_body_over_the_limit_with_413()
    {
        await using var server = await TestServer.StartAsync(app => MapGitHub(app, o => o.MaxBodySize = 64));

        var response = await server.Client.SendAsync(GitHubDelivery("/hooks/github", new string('x', 65)), Token);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task A_redelivery_is_acknowledged_without_running_the_handler_again()
    {
        var seen = new List<string>();
        await using var server = await TestServer.StartAsync(app => MapGitHub(app, o => o.SuppressDuplicates(), seen));

        var first = await server.Client.SendAsync(GitHubDelivery("/hooks/github", "{}", delivery: "abc"), Token);
        var second = await server.Client.SendAsync(GitHubDelivery("/hooks/github", "{}", delivery: "abc"), Token);
        var other = await server.Client.SendAsync(GitHubDelivery("/hooks/github", "{}", delivery: "def"), Token);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        Assert.Equal(["abc", "def"], seen);
    }

    [Fact]
    public async Task A_forged_delivery_does_not_poison_the_duplicate_store()
    {
        var seen = new List<string>();
        await using var server = await TestServer.StartAsync(app => MapGitHub(app, o => o.SuppressDuplicates(), seen));

        await server.Client.SendAsync(GitHubDelivery("/hooks/github", "{}", secret: "forged", delivery: "abc"), Token);
        var real = await server.Client.SendAsync(GitHubDelivery("/hooks/github", "{}", delivery: "abc"), Token);

        Assert.Equal(HttpStatusCode.OK, real.StatusCode);
        Assert.Equal(["abc"], seen);
    }

    [Fact]
    public async Task A_delivery_the_handler_failed_is_processed_when_retried()
    {
        var attempts = 0;

        await using var server = await TestServer.StartAsync(app =>
            app.MapWebhook(
                "/hooks/github",
                WebhookSignature.GitHub(Secret),
                (WebhookContext ctx) => Task.FromResult(++attempts == 1 ? Results.StatusCode(503) : Results.Ok()),
                o => o.SuppressDuplicates()
            ));

        var failed = await server.Client.SendAsync(GitHubDelivery("/hooks/github", "{}", delivery: "retry-me"), Token);
        var retried = await server.Client.SendAsync(GitHubDelivery("/hooks/github", "{}", delivery: "retry-me"), Token);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task A_stale_standard_webhook_is_refused_by_the_configured_clock()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var secret = WebhookSigner.GenerateSecret();
        var signer = WebhookSigner.StandardWebhooks(secret, clock);
        var handled = 0;

        await using var server = await TestServer.StartAsync(app =>
            app.MapWebhook("/hooks/std", WebhookSignature.StandardWebhooks(secret), (WebhookContext _) =>
            {
                handled++;
                return Task.CompletedTask;
            }, o => o.TimeProvider = clock));

        HttpRequestMessage Signed()
        {
            var body = "{\"type\":\"ping\"}"u8.ToArray();
            var request = new HttpRequestMessage(HttpMethod.Post, "/hooks/std") { Content = new ByteArrayContent(body) };
            signer.Apply(request, "msg_1", body);
            return request;
        }

        var fresh = await server.Client.SendAsync(Signed(), Token);

        var stale = Signed();
        clock.Advance(TimeSpan.FromMinutes(10));
        var replayed = await server.Client.SendAsync(stale, Token);

        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, replayed.StatusCode);
        Assert.Equal(1, handled);
    }

    [Fact]
    public async Task A_named_verifier_is_resolved_from_the_registered_options()
    {
        await using var server = await TestServer.StartAsync(
            app => app.MapWebhook("/hooks/github", "github", (WebhookContext ctx) => Task.FromResult(Results.Text(ctx.Verifier))),
            builder => builder.AddWebhooks(o => o.AddVerifier("github", WebhookSignature.GitHub(Secret)))
        );

        var response = await server.Client.SendAsync(GitHubDelivery("/hooks/github", "{}"), Token);

        Assert.Equal("github", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Mapping_an_unregistered_verifier_fails_at_startup_not_per_request()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => TestServer.StartAsync(
            app => app.MapWebhook("/hooks/x", "missing", (WebhookContext _) => Task.CompletedTask)
        ));
    }

    [Fact]
    public async Task A_custom_rejection_replaces_the_problem_response()
    {
        await using var server = await TestServer.StartAsync(app => MapGitHub(app, o => o.OnRejected = (ctx, _) =>
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return ValueTask.CompletedTask;
        }));

        var response = await server.Client.SendAsync(GitHubDelivery("/hooks/github", "{}", secret: "forged"), Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Middleware_verifies_a_mapped_route_that_requires_a_signature()
    {
        await using var server = await TestServer.StartAsync(
            app =>
            {
                app.UseWebhookVerification();
                app.MapPost("/hooks/plain", async ctx =>
                {
                    var webhook = ctx.GetWebhook();
                    await ctx.Response.WriteTextAsync(webhook.DeliveryId + ":" + await ctx.Request.ReadBodyAsStringAsync(cancellationToken: ctx.RequestAborted), cancellationToken: ctx.RequestAborted);
                }).RequireWebhookSignature("github");
                app.MapPost("/open", ctx => ctx.Response.WriteTextAsync("open", cancellationToken: ctx.RequestAborted));
            },
            builder => builder.AddWebhooks(o => o.AddVerifier("github", WebhookSignature.GitHub(Secret)))
        );

        var ok = await server.Client.SendAsync(GitHubDelivery("/hooks/plain", "{\"a\":1}", delivery: "x1"), Token);
        var forged = await server.Client.SendAsync(GitHubDelivery("/hooks/plain", "{}", secret: "no"), Token);
        var open = await server.Client.PostAsync("/open", new StringContent("hi"), Token);

        Assert.Equal("x1:{\"a\":1}", await ok.Content.ReadAsStringAsync(Token));
        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
        Assert.Equal("open", await open.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Middleware_and_map_webhook_together_verify_once()
    {
        var seen = new List<string>();
        await using var server = await TestServer.StartAsync(
            app =>
            {
                app.UseWebhookVerification();
                MapGitHub(app, seen: seen);
            },
            builder => builder.AddWebhooks(o => o.SuppressDuplicates())
        );

        var response = await server.Client.SendAsync(GitHubDelivery("/hooks/github", "{}", delivery: "once"), Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["once"], seen);
    }

    [Fact]
    public async Task Generated_endpoints_are_verified_and_bind_the_delivery()
    {
        await using var server = await TestServer.StartAsync(
            app =>
            {
                app.UseWebhookVerification();
                app.MapGeneratedHookEndpoints();
            },
            builder => builder.AddWebhooks(o => o.AddVerifier("github", WebhookSignature.GitHub(Secret)))
        );

        var ok = await server.Client.SendAsync(GitHubDelivery("/gen-hooks/github", "{\"n\":1}", @event: "issues"), Token);
        var raw = await server.Client.SendAsync(GitHubDelivery("/gen-hooks/raw", "{\"n\":2}"), Token);
        var forged = await server.Client.SendAsync(GitHubDelivery("/gen-hooks/github", "{}", secret: "forged"), Token);

        Assert.Equal("issues:{\"n\":1}", await ok.Content.ReadAsStringAsync(Token));
        Assert.Equal("raw:{\"n\":2}", await raw.Content.ReadAsStringAsync(Token));
        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
    }

    [Fact]
    public async Task A_generated_endpoint_that_takes_the_delivery_fails_closed_without_the_middleware()
    {
        await using var server = await TestServer.StartAsync(
            app => app.MapGeneratedHookEndpoints(),
            builder => builder.AddWebhooks(o => o.AddVerifier("github", WebhookSignature.GitHub(Secret)))
        );

        var response = await server.Client.SendAsync(GitHubDelivery("/gen-hooks/github", "{}"), Token);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public void Generated_endpoints_carry_the_webhook_metadata()
    {
        var server = new HttpServer();
        server.MapGeneratedHookEndpoints();

        var endpoint = server.Router.Endpoints.Single(e => e.Template.RawText == "/gen-hooks/github");

        Assert.Equal("github", endpoint.GetMetadata<WebhookMetadata>()?.VerifierName);
    }
}
