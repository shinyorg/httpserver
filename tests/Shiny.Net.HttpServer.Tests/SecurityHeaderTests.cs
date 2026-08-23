using System.Net;
using Shiny.Net.HttpServer.Security;

namespace Shiny.Net.HttpServer.Tests;

public class SecurityHeaderTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Applies_the_strict_defaults()
    {
        await using var test = await TestServer.StartAsync(server =>
        {
            server.UseSecurityHeaders();
            server.MapGet("/", ctx => ctx.Response.WriteTextAsync("hi", cancellationToken: ctx.RequestAborted));
        });

        var response = await test.Client.GetAsync("/", Token);

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("same-origin", response.Headers.GetValues("Cross-Origin-Resource-Policy").Single());
        Assert.False(response.Headers.Contains("Content-Security-Policy"));
    }

    [Fact]
    public async Task A_handler_that_set_its_own_keeps_it()
    {
        await using var test = await TestServer.StartAsync(server =>
        {
            server.UseSecurityHeaders(o => o.ContentSecurityPolicy = SecurityHeaderOptions.SelfOnlyContentSecurityPolicy);
            server.MapGet("/embedded", ctx =>
            {
                ctx.Response.Headers.Set("X-Frame-Options", "SAMEORIGIN");
                return ctx.Response.WriteTextAsync("hi", cancellationToken: ctx.RequestAborted);
            });
        });

        var response = await test.Client.GetAsync("/embedded", Token);

        Assert.Equal("SAMEORIGIN", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("default-src 'self'", response.Headers.GetValues("Content-Security-Policy").Single());
    }

    /// <summary>HSTS on a cleartext connection is a header a browser is entitled to ignore, and a footgun on a LAN.</summary>
    [Fact]
    public async Task Hsts_is_withheld_over_cleartext()
    {
        await using var test = await TestServer.StartAsync(server =>
        {
            server.UseSecurityHeaders(o => o.Hsts = new HstsOptions { MaxAge = TimeSpan.FromDays(30) });
            server.MapGet("/", ctx => ctx.Response.WriteTextAsync("hi", cancellationToken: ctx.RequestAborted));
        });

        var response = await test.Client.GetAsync("/", Token);

        Assert.False(response.Headers.Contains("Strict-Transport-Security"));
    }

    [Fact]
    public void Hsts_renders_its_directives()
    {
        var value = new HstsOptions
        {
            MaxAge = TimeSpan.FromDays(365),
            IncludeSubDomains = true,
            Preload = true
        }.ToHeaderValue();

        Assert.Equal("max-age=31536000; includeSubDomains; preload", value);
    }

    [Fact]
    public async Task Redirects_cleartext_to_the_tls_endpoint()
    {
        await using var test = await TestServer.StartAsync(
            server =>
            {
                server.UseHttpsRedirection(httpsPort: 8443);
                server.MapGet("/thing", ctx => ctx.Response.WriteTextAsync("hi", cancellationToken: ctx.RequestAborted));
            }
        );

        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{test.Port}")
        };

        var response = await client.GetAsync("/thing?a=1", Token);

        Assert.Equal(HttpStatusCode.TemporaryRedirect, response.StatusCode);
        Assert.Equal("https://127.0.0.1:8443/thing?a=1", response.Headers.Location?.ToString());
    }

    /// <summary>With nothing to redirect to, serving the request beats sending the caller in a circle.</summary>
    [Fact]
    public async Task Serves_the_request_when_there_is_no_tls_endpoint()
    {
        await using var test = await TestServer.StartAsync(server =>
        {
            server.UseHttpsRedirection();
            server.MapGet("/thing", ctx => ctx.Response.WriteTextAsync("served", cancellationToken: ctx.RequestAborted));
        });

        Assert.Equal("served", await test.Client.GetStringAsync("/thing", Token));
    }
}
