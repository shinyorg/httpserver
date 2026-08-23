using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Shiny.Net.HttpServer.Security;

namespace Shiny.Net.HttpServer.Tests;

public class AntiforgeryTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    static Task<TestServer> Server(Action<HttpServer>? extra = null)
        => TestServer.StartAsync(
            server =>
            {
                server.UseAntiforgery();
                server.MapGet("/token", ctx =>
                {
                    var tokens = ctx.GetRequiredService<IAntiforgery>().GetTokens(ctx);
                    return ctx.Response.WriteTextAsync(tokens.RequestToken, cancellationToken: ctx.RequestAborted);
                });
                server.Map(HttpMethods.Post, "/save", ctx => ctx.Response.WriteTextAsync("saved", cancellationToken: ctx.RequestAborted));

                extra?.Invoke(server);
            },
            builder => builder.AddAntiforgery()
        );

    [Fact]
    public async Task A_cookie_bearing_post_without_a_token_is_refused()
    {
        await using var test = await Server();

        using var handler = new HttpClientHandler { UseCookies = true, CookieContainer = new CookieContainer() };
        using var client = new HttpClient(handler) { BaseAddress = new Uri($"http://127.0.0.1:{test.Port}") };

        await client.GetStringAsync("/token", Token);

        var response = await client.PostAsync("/save", new StringContent("x"), Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_issued_token_is_accepted()
    {
        await using var test = await Server();

        using var handler = new HttpClientHandler { UseCookies = true, CookieContainer = new CookieContainer() };
        using var client = new HttpClient(handler) { BaseAddress = new Uri($"http://127.0.0.1:{test.Port}") };

        var token = await client.GetStringAsync("/token", Token);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/save") { Content = new StringContent("x") };
        request.Headers.Add("X-CSRF-TOKEN", token);

        var response = await client.SendAsync(request, Token);

        Assert.Equal("saved", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task A_forged_token_is_refused()
    {
        await using var test = await Server();

        using var handler = new HttpClientHandler { UseCookies = true, CookieContainer = new CookieContainer() };
        using var client = new HttpClient(handler) { BaseAddress = new Uri($"http://127.0.0.1:{test.Port}") };

        await client.GetStringAsync("/token", Token);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/save") { Content = new StringContent("x") };
        request.Headers.Add("X-CSRF-TOKEN", "0.notasignature");

        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(request, Token)).StatusCode);
    }

    /// <summary>
    /// A caller with no cookies has no ambient credential to abuse, so there is nothing for CSRF to
    /// ride on and nothing to check.
    /// </summary>
    [Fact]
    public async Task A_cookieless_post_goes_through()
    {
        await using var test = await Server();

        var response = await test.Client.PostAsync("/save", new StringContent("x"), Token);

        Assert.Equal("saved", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task A_get_is_never_checked()
    {
        await using var test = await Server(server => server.MapGet("/read", ctx =>
            ctx.Response.WriteTextAsync("read", cancellationToken: ctx.RequestAborted)));

        using var handler = new HttpClientHandler { UseCookies = true, CookieContainer = new CookieContainer() };
        using var client = new HttpClient(handler) { BaseAddress = new Uri($"http://127.0.0.1:{test.Port}") };

        await client.GetStringAsync("/token", Token);

        Assert.Equal("read", await client.GetStringAsync("/read", Token));
    }

    [Fact]
    public async Task An_endpoint_can_opt_out()
    {
        await using var test = await Server(server => server
            .Map(HttpMethods.Post, "/webhook", ctx => ctx.Response.WriteTextAsync("accepted", cancellationToken: ctx.RequestAborted))
            .DisableAntiforgery()
        );

        using var handler = new HttpClientHandler { UseCookies = true, CookieContainer = new CookieContainer() };
        using var client = new HttpClient(handler) { BaseAddress = new Uri($"http://127.0.0.1:{test.Port}") };

        await client.GetStringAsync("/token", Token);

        var response = await client.PostAsync("/webhook", new StringContent("x"), Token);

        Assert.Equal("accepted", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task An_endpoint_can_opt_in_even_without_cookies()
    {
        await using var test = await Server(server => server
            .Map(HttpMethods.Post, "/sensitive", ctx => ctx.Response.WriteTextAsync("done", cancellationToken: ctx.RequestAborted))
            .ValidateAntiforgery()
        );

        var response = await test.Client.PostAsync("/sensitive", new StringContent("x"), Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>A token is only good against the key that signed it, which is what lets two servers share one.</summary>
    [Fact]
    public async Task A_token_from_a_different_key_does_not_validate()
    {
        var other = new Antiforgery(new AntiforgeryOptions { Key = Enumerable.Repeat((byte)9, 64).ToArray() });

        await using var test = await TestServer.StartAsync(
            server => server.MapGet("/check", ctx =>
            {
                var mine = ctx.GetRequiredService<IAntiforgery>();
                var tokens = mine.GetTokens(ctx);

                // The cookie has only been written to the response, so the check reads it from
                // there: this is one request, not two.
                return ctx.Response.WriteTextAsync(
                    $"{mine.ValidateToken(ctx, tokens.RequestToken)},{other.ValidateToken(ctx, tokens.RequestToken)}",
                    cancellationToken: ctx.RequestAborted
                );
            }),
            builder => builder.AddAntiforgery(o => o.Key = new byte[64])
        );

        using var handler = new HttpClientHandler { UseCookies = true, CookieContainer = new CookieContainer() };
        using var client = new HttpClient(handler) { BaseAddress = new Uri($"http://127.0.0.1:{test.Port}") };

        // First call issues the cookie; the second one has it, so both validations see it.
        await client.GetStringAsync("/check", Token);

        Assert.Equal("True,False", await client.GetStringAsync("/check", Token));
    }
}
