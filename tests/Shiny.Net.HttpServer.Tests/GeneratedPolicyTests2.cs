using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Shiny.Net.HttpServer.Caching;
using Shiny.Net.HttpServer.Security;
using Shiny.Net.HttpServer.Timeouts;

namespace Shiny.Net.HttpServer.Tests;

// ---------------------------------------------------------------------------
// Timeouts, output caching and antiforgery on generated endpoints. Same
// arrangement as the CORS/rate-limit/IP-filter set: the attribute is read at
// compile time and emitted as route metadata, so the middleware sees exactly
// what it would see from a hand-mapped route.
// ---------------------------------------------------------------------------

[Route("/api/policies2")]
public class Policy2Endpoints
{
    [Get("/slow")]
    [RequestTimeout(100)]
    public async Task<string> Slow(CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        return "never";
    }

    [Get("/named")]
    [RequestTimeout("reports")]
    public async Task<string> Named(CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        return "never";
    }

    [Get("/stream")]
    [DisableRequestTimeout]
    public async Task<string> Stream(CancellationToken ct)
    {
        await Task.Delay(250, ct);
        return "finished";
    }

    [Get("/cached")]
    [OutputCache(Seconds = 60)]
    public string Cached() => Interlocked.Increment(ref Counter).ToString();

    [Get("/uncached")]
    [NoOutputCache]
    public string Uncached() => Interlocked.Increment(ref Counter).ToString();

    [Post("/guarded")]
    [ValidateAntiforgery]
    public string Guarded() => "guarded";

    [Post("/webhook")]
    [DisableAntiforgery]
    public string Webhook() => "accepted";

    public static int Counter;
}

public class GeneratedPolicy2Tests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    static Task<TestServer> StartAsync() => TestServer.StartAsync(
        app =>
        {
            app.UseRequestTimeouts();
            app.UseOutputCache();
            app.UseAntiforgery();
            app.MapPolicy2Endpoints();
        },
        builder =>
        {
            builder.AddRequestTimeouts(o => o.AddPolicy("reports", TimeSpan.FromMilliseconds(100)));
            builder.AddOutputCache();
            builder.AddAntiforgery();
        }
    );

    [Fact]
    public async Task An_inline_timeout_is_enforced()
    {
        await using var test = await StartAsync();

        Assert.Equal(HttpStatusCode.GatewayTimeout, (await test.Client.GetAsync("/api/policies2/slow", Token)).StatusCode);
    }

    [Fact]
    public async Task A_named_timeout_policy_is_enforced()
    {
        await using var test = await StartAsync();

        Assert.Equal(HttpStatusCode.GatewayTimeout, (await test.Client.GetAsync("/api/policies2/named", Token)).StatusCode);
    }

    [Fact]
    public async Task DisableRequestTimeout_lets_a_long_handler_finish()
    {
        await using var test = await StartAsync();

        Assert.Equal("finished", await test.Client.GetStringAsync("/api/policies2/stream", Token));
    }

    [Fact]
    public async Task OutputCache_serves_the_second_request_from_the_cache()
    {
        await using var test = await StartAsync();

        var first = await test.Client.GetStringAsync("/api/policies2/cached", Token);
        var second = await test.Client.GetStringAsync("/api/policies2/cached", Token);

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task NoOutputCache_runs_the_handler_every_time()
    {
        await using var test = await StartAsync();

        var first = await test.Client.GetStringAsync("/api/policies2/uncached", Token);
        var second = await test.Client.GetStringAsync("/api/policies2/uncached", Token);

        Assert.NotEqual(first, second);
    }

    /// <summary>The attribute asks for the check on a caller that carries no cookie at all.</summary>
    [Fact]
    public async Task ValidateAntiforgery_requires_a_token_even_without_cookies()
    {
        await using var test = await StartAsync();

        var response = await test.Client.PostAsync("/api/policies2/guarded", new StringContent("x"), Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DisableAntiforgery_lets_a_cookie_bearing_post_through()
    {
        await using var test = await StartAsync();

        using var handler = new HttpClientHandler { UseCookies = true, CookieContainer = new CookieContainer() };
        using var client = new HttpClient(handler) { BaseAddress = new Uri($"http://127.0.0.1:{test.Port}") };

        // A cookie from somewhere else entirely — enough to make the default rule apply.
        handler.CookieContainer.Add(new Uri($"http://127.0.0.1:{test.Port}"), new Cookie("session", "abc"));

        var response = await client.PostAsync("/api/policies2/webhook", new StringContent("x"), Token);

        Assert.Equal("accepted", await response.Content.ReadAsStringAsync(Token));
    }
}
