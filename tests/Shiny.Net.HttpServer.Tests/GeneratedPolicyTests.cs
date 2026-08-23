using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Shiny.Net.HttpServer.Cors;
using Shiny.Net.HttpServer.RateLimiting;
using Shiny.Net.HttpServer.Security;

namespace Shiny.Net.HttpServer.Tests;

// ---------------------------------------------------------------------------
// CORS, rate limiting and IP filtering on generated endpoints. The attributes
// below are read at compile time and emitted as route metadata, so what is
// asserted here is the same path a hand-mapped route takes through each
// middleware — no reflection, and nothing the middleware has to discover.
// ---------------------------------------------------------------------------

[Route("/api/policies")]
[EnableCors("tenant")]
public class PolicyEndpoints
{
    [Get("/inherited")]
    public string Inherited() => "inherited";

    [Get("/own")]
    [EnableCors("public")]
    public string Own() => "own";

    [Get("/none")]
    [DisableCors]
    public string None() => "none";

    [Post("/upload")]
    [EnableRateLimiting("uploads")]
    public string Upload() => "uploaded";

    [Get("/health")]
    [DisableRateLimiting]
    [DisableCors]
    public string Health() => "ok";

    [Get("/admin")]
    [RequireIpFilter("admin")]
    public string Admin() => "admin";

    [Get("/open")]
    [AllowAnyIp]
    [DisableCors]
    public string Open() => "open";
}

public class GeneratedPolicyTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    const string Origin = "https://app.example.com";

    static HttpRequestMessage CrossOrigin(string path, string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(HeaderNames.Origin, origin);

        return request;
    }

    static Task<TestServer> StartAsync() => TestServer.StartAsync(
        app =>
        {
            app.UseCors();
            app.UseRateLimiter();
            app.UseIpFilter();
            app.MapPolicyEndpoints();
        },
        builder =>
        {
            builder.AddCors(o =>
            {
                o.AddDefaultPolicy(p => p.WithOrigins("https://default.example").AllowAnyMethod().AllowAnyHeader());
                o.AddPolicy("tenant", p => p.WithOrigins(Origin).AllowAnyMethod().AllowAnyHeader());
                o.AddPolicy("public", p => p.AllowAnyOrigin().AllowAnyMethod());
            });

            builder.AddRateLimiter(o =>
            {
                o.GlobalPolicy = new FixedWindowRateLimitPolicy(100, TimeSpan.FromMinutes(5))
                {
                    Partitioner = RateLimitPartitioners.Global
                };
                o.AddPolicy("uploads", new FixedWindowRateLimitPolicy(1, TimeSpan.FromMinutes(5))
                {
                    Partitioner = RateLimitPartitioners.Global
                });
            });

            builder.AddIpFilter(o =>
            {
                o.SetDefaultPolicy(p => p.AllowLoopback());
                o.AddPolicy("admin", p => p.Allow("10.0.0.0/8"));
            });
        }
    );

    [Fact]
    public async Task A_class_level_cors_policy_covers_every_method_on_it()
    {
        await using var server = await StartAsync();

        var response = await server.Client.SendAsync(CrossOrigin("/api/policies/inherited", Origin), Token);

        Assert.Equal(Origin, response.Headers.GetValues(HeaderNames.AccessControlAllowOrigin).Single());
    }

    [Fact]
    public async Task A_method_level_cors_policy_replaces_the_class_one()
    {
        await using var server = await StartAsync();

        var response = await server.Client.SendAsync(CrossOrigin("/api/policies/own", "https://anywhere.example"), Token);

        Assert.Equal("*", response.Headers.GetValues(HeaderNames.AccessControlAllowOrigin).Single());
    }

    [Fact]
    public async Task DisableCors_beats_the_class_attribute_and_the_default_policy()
    {
        await using var server = await StartAsync();

        var response = await server.Client.SendAsync(CrossOrigin("/api/policies/none", Origin), Token);

        Assert.Equal("none", await response.Content.ReadAsStringAsync(Token));
        Assert.False(response.Headers.Contains(HeaderNames.AccessControlAllowOrigin));
    }

    [Fact]
    public async Task An_endpoint_rate_limit_attribute_is_enforced()
    {
        await using var server = await StartAsync();

        Assert.Equal(HttpStatusCode.OK, (await server.Client.PostAsync("/api/policies/upload", null, Token)).StatusCode);
        Assert.Equal(
            HttpStatusCode.TooManyRequests,
            (await server.Client.PostAsync("/api/policies/upload", null, Token)).StatusCode
        );

        // The tight policy belongs to that one endpoint; everything else still has the global one.
        Assert.Equal("ok", await server.Client.GetStringAsync("/api/policies/health", Token));
    }

    [Fact]
    public async Task An_endpoint_ip_filter_attribute_is_enforced()
    {
        await using var server = await StartAsync();

        // Loopback satisfies the default policy but not the admin one.
        Assert.Equal("inherited", await server.Client.GetStringAsync("/api/policies/inherited", Token));
        Assert.Equal(HttpStatusCode.Forbidden, (await server.Client.GetAsync("/api/policies/admin", Token)).StatusCode);
    }

    [Fact]
    public async Task AllowAnyIp_exempts_a_generated_endpoint()
    {
        await using var server = await TestServer.StartAsync(
            app =>
            {
                app.UseIpFilter();
                app.MapPolicyEndpoints();
            },
            builder => builder.AddIpFilter(o =>
            {
                o.SetDefaultPolicy(p => p.Allow("10.0.0.0/8"));
                o.AddPolicy("admin", p => p.Allow("10.0.0.0/8"));
            })
        );

        Assert.Equal(HttpStatusCode.Forbidden, (await server.Client.GetAsync("/api/policies/inherited", Token)).StatusCode);
        Assert.Equal("open", await server.Client.GetStringAsync("/api/policies/open", Token));
    }
}
