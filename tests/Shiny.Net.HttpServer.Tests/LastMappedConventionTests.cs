using System.Net;
using System.Text;
using Shiny.Net.HttpServer.Caching;
using Shiny.Net.HttpServer.Cors;
using Shiny.Net.HttpServer.Jwt;
using Shiny.Net.HttpServer.OpenApi;
using Shiny.Net.HttpServer.RateLimiting;
using Shiny.Net.HttpServer.Routing;
using Shiny.Net.HttpServer.Security;
using Shiny.Net.HttpServer.Timeouts;

namespace Shiny.Net.HttpServer.Tests;

/// <summary>Two open routes, mapped in one generated call, for chaining a convention onto.</summary>
[Route("/api/pair")]
public class PairEndpoints
{
    /// <summary>First.</summary>
    [Get("/first")]
    public string First() => "first";

    /// <summary>Second.</summary>
    [Post("/second")]
    public string Second() => "second";
}

// A convention chained onto the server — .RequireAuthorization(), .RequireRateLimiting("x") — used to
// apply to the last route in the table. After MapGroup / MapModule / a generated Map{Class}() that is
// one route of several: MapGroup("/admin", …).RequireAuthorization() protected the last admin route and
// left the others anonymous.
public class LastMappedConventionTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    static readonly byte[] Secret = Encoding.UTF8.GetBytes("tests-32-byte-secret-value!!!!!!");

    static Task<TestServer> StartAsync(Action<HttpServer> configure) => TestServer.StartAsync(
        app =>
        {
            app.UseAuthentication();
            app.UseAuthorization();
            configure(app);
        },
        builder =>
        {
            builder.AddAuthentication().AddJwtBearer(o =>
            {
                o.Issuer = "tests";
                o.Audience = "tests";
                o.SigningKey = JwtSigningKey.FromSecret(Secret);
            });
            builder.AddAuthorization();
        }
    );

    [Fact]
    public async Task RequireAuthorization_after_MapGroup_protects_every_route_in_the_group()
    {
        await using var server = await StartAsync(app =>
            app.MapGroup("/admin", admin =>
            {
                admin.MapGet("/users", ctx => ctx.Response.WriteAsync("users"));
                admin.MapPost("/delete-all", ctx => ctx.Response.WriteAsync("deleted"));
                admin.MapGet("/last", ctx => ctx.Response.WriteAsync("last"));
            })
            .RequireAuthorization()
        );

        using var users = await server.Client.GetAsync("/admin/users", Token);
        Assert.Equal(HttpStatusCode.Unauthorized, users.StatusCode);

        using var delete = await server.Client.PostAsync("/admin/delete-all", null, Token);
        Assert.Equal(HttpStatusCode.Unauthorized, delete.StatusCode);

        using var last = await server.Client.GetAsync("/admin/last", Token);
        Assert.Equal(HttpStatusCode.Unauthorized, last.StatusCode);
    }

    [Fact]
    public async Task RequireAuthorization_after_a_generated_Map_call_protects_every_route_it_mapped()
    {
        await using var server = await StartAsync(app => app.MapPairEndpoints().RequireAuthorization());

        using var first = await server.Client.GetAsync("/api/pair/first", Token);
        Assert.Equal(HttpStatusCode.Unauthorized, first.StatusCode);

        using var second = await server.Client.PostAsync("/api/pair/second", null, Token);
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
    }

    [Fact]
    public async Task RequireAuthorization_after_MapModule_protects_every_route_in_the_module()
    {
        await using var server = await StartAsync(app => app.MapModule(new TwoRouteModule()).RequireAuthorization());

        using var a = await server.Client.GetAsync("/module/a", Token);
        Assert.Equal(HttpStatusCode.Unauthorized, a.StatusCode);

        using var b = await server.Client.GetAsync("/module/b", Token);
        Assert.Equal(HttpStatusCode.Unauthorized, b.StatusCode);
    }

    [Fact]
    public async Task A_route_that_allows_anonymous_inside_a_protected_group_stays_open()
    {
        await using var server = await StartAsync(app =>
            app.MapGroup("/g", g =>
            {
                g.MapGet("/open", ctx => ctx.Response.WriteAsync("open")).AllowAnonymous();
                g.MapGet("/closed", ctx => ctx.Response.WriteAsync("closed"));
            })
            .RequireAuthorization()
        );

        using var open = await server.Client.GetAsync("/g/open", Token);
        Assert.Equal(HttpStatusCode.OK, open.StatusCode);

        using var closed = await server.Client.GetAsync("/g/closed", Token);
        Assert.Equal(HttpStatusCode.Unauthorized, closed.StatusCode);
    }

    [Fact]
    public async Task A_convention_on_a_single_route_still_applies_only_to_it()
    {
        await using var server = await StartAsync(app =>
        {
            app.MapGet("/before", ctx => ctx.Response.WriteAsync("before"));
            app.MapGet("/protected", ctx => ctx.Response.WriteAsync("protected")).RequireAuthorization();
        });

        using var before = await server.Client.GetAsync("/before", Token);
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        using var protectedRoute = await server.Client.GetAsync("/protected", Token);
        Assert.Equal(HttpStatusCode.Unauthorized, protectedRoute.StatusCode);
    }

    [Fact]
    public async Task Every_server_level_convention_covers_every_route_of_a_group()
    {
        await using var server = new HttpServer();

        server.MapGroup("/g", g =>
            {
                g.MapGet("/a", Noop);
                g.MapPost("/b", Noop);
            })
            .RequireAuthorization("p")
            .RequireRateLimiting("rl")
            .RequireIpFilter("ip")
            .RequireCors("cors")
            .ValidateAntiforgery()
            .WithRequestTimeout(TimeSpan.FromSeconds(5))
            .CacheOutput(TimeSpan.FromSeconds(5))
            .WithRequestSizeLimit(10)
            .Describe(o => o.Summary = "described");

        Assert.Equal(2, server.Router.Endpoints.Count);

        foreach (var endpoint in server.Router.Endpoints)
        {
            Assert.Equal(["p"], endpoint.GetMetadata<AuthorizationMetadata>()!.Policies);
            Assert.Equal("rl", endpoint.GetMetadata<RateLimitMetadata>()?.PolicyName);
            Assert.Equal("ip", endpoint.GetMetadata<IpFilterMetadata>()?.PolicyName);
            Assert.Equal("cors", endpoint.GetMetadata<CorsMetadata>()?.PolicyName);
            Assert.True(endpoint.GetMetadata<AntiforgeryMetadata>()?.Required);
            Assert.Equal(TimeSpan.FromSeconds(5), endpoint.GetMetadata<RequestTimeoutMetadata>()?.Timeout);
            Assert.Equal(TimeSpan.FromSeconds(5), endpoint.GetMetadata<OutputCacheMetadata>()?.Duration);
            Assert.Equal(10, endpoint.GetMetadata<RequestSizeLimitAttribute>()?.Bytes);
            Assert.Equal("described", endpoint.GetMetadata<ApiOperation>()?.Summary);
        }
    }

    [Fact]
    public async Task Inside_a_group_a_server_level_convention_touches_only_the_route_just_mapped()
    {
        await using var server = new HttpServer();

        server.MapGroup("/g", g =>
        {
            g.MapGet("/a", Noop);
            server.MapGet("/outside", Noop).AllowAnonymous();
            g.MapGet("/b", Noop);
        });

        Assert.True(server.Router.Find("GET", "/outside")!.GetMetadata<AuthorizationMetadata>()!.AllowAnonymous);
        Assert.Null(server.Router.Find("GET", "/g/a")!.GetMetadata<AuthorizationMetadata>());

        // ...and the group as a whole is what the call after it sees.
        Assert.Equal(3, server.Router.LastMapped.Count);
    }

    [Fact]
    public async Task A_convention_after_an_empty_group_applies_to_nothing()
    {
        await using var server = new HttpServer();

        server.MapGet("/earlier", Noop);
        server.MapGroup("/empty", _ => { }).RequireAuthorization();

        Assert.Null(server.Router.Find("GET", "/earlier")!.GetMetadata<AuthorizationMetadata>());
    }

    [Fact]
    public async Task A_convention_before_anything_is_mapped_throws()
    {
        await using var server = new HttpServer();
        Assert.Throws<InvalidOperationException>(() => server.RequireAuthorization());
    }

    static ValueTask Noop(HttpContext context) => default;

    sealed class TwoRouteModule : IEndpointModule
    {
        public void Map(IEndpointRouteBuilder endpoints)
        {
            var module = endpoints.MapGroup("/module");
            module.MapGet("/a", ctx => ctx.Response.WriteAsync("a"));
            module.MapGet("/b", ctx => ctx.Response.WriteAsync("b"));
        }
    }
}
