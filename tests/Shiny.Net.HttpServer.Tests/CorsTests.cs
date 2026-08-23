using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Shiny.Net.HttpServer.Cors;

namespace Shiny.Net.HttpServer.Tests;

public class CorsTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    const string Origin = "https://app.example.com";

    static Task<TestServer> StartAsync(Action<CorsPolicyBuilder> policy)
        => TestServer.StartAsync(app =>
        {
            app.UseCors(policy);
            app.MapGet("/data", ctx => ctx.Response.WriteAsync("payload"));
            app.MapPost("/data", ctx => ctx.Response.WriteAsync("created"));
        });

    static HttpRequestMessage Preflight(string path, string method, string origin = Origin, string? headers = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, path);
        request.Headers.Add(HeaderNames.Origin, origin);
        request.Headers.Add(HeaderNames.AccessControlRequestMethod, method);

        if (headers is not null)
            request.Headers.Add(HeaderNames.AccessControlRequestHeaders, headers);

        return request;
    }

    static HttpRequestMessage CrossOrigin(HttpMethod method, string path, string origin = Origin)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add(HeaderNames.Origin, origin);

        return request;
    }

    [Fact]
    public async Task Answers_a_preflight_without_reaching_the_router()
    {
        // OPTIONS is not mapped anywhere here. Left to the router this would be a 405 with no CORS
        // headers, and the browser would never send the real request.
        await using var server = await StartAsync(p => p.WithOrigins(Origin).AllowAnyHeader().AllowAnyMethod());

        var response = await server.Client.SendAsync(Preflight("/data", "POST", headers: "X-Api-Key"), Token);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(Origin, response.Headers.GetValues(HeaderNames.AccessControlAllowOrigin).Single());
        Assert.Equal("POST", response.Headers.GetValues(HeaderNames.AccessControlAllowMethods).Single());
        Assert.Equal("X-Api-Key", response.Headers.GetValues(HeaderNames.AccessControlAllowHeaders).Single());
    }

    [Fact]
    public async Task Preflight_from_an_unlisted_origin_gets_no_headers_at_all()
    {
        await using var server = await StartAsync(p => p.WithOrigins(Origin).AllowAnyHeader().AllowAnyMethod());

        var response = await server.Client.SendAsync(Preflight("/data", "POST", origin: "https://evil.example"), Token);

        // A bare 204: the browser reads the absence of a permission slip as a refusal. Emitting a
        // partial answer would be worse than emitting none.
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(response.Headers.Contains(HeaderNames.AccessControlAllowOrigin));
        Assert.False(response.Headers.Contains(HeaderNames.AccessControlAllowMethods));
    }

    [Fact]
    public async Task Preflight_for_a_disallowed_method_is_refused()
    {
        await using var server = await StartAsync(p => p.WithOrigins(Origin).WithMethods("GET").AllowAnyHeader());

        var response = await server.Client.SendAsync(Preflight("/data", "DELETE"), Token);

        Assert.False(response.Headers.Contains(HeaderNames.AccessControlAllowOrigin));
    }

    [Fact]
    public async Task Preflight_asking_for_an_unlisted_header_is_refused_entirely()
    {
        await using var server = await StartAsync(
            p => p.WithOrigins(Origin).AllowAnyMethod().WithHeaders("X-Api-Key")
        );

        var approved = await server.Client.SendAsync(Preflight("/data", "POST", headers: "x-api-key"), Token);
        Assert.True(approved.Headers.Contains(HeaderNames.AccessControlAllowOrigin));

        // One unapproved header in the list poisons the whole preflight — approving the rest would
        // let the browser send the one nobody vetted.
        var refused = await server.Client.SendAsync(
            Preflight("/data", "POST", headers: "X-Api-Key, X-Smuggled"),
            Token
        );
        Assert.False(refused.Headers.Contains(HeaderNames.AccessControlAllowHeaders));
    }

    [Fact]
    public async Task Preflight_max_age_is_emitted_in_seconds()
    {
        await using var server = await StartAsync(
            p => p.WithOrigins(Origin).AllowAnyMethod().AllowAnyHeader().SetPreflightMaxAge(TimeSpan.FromMinutes(10))
        );

        var response = await server.Client.SendAsync(Preflight("/data", "GET"), Token);

        Assert.Equal("600", response.Headers.GetValues(HeaderNames.AccessControlMaxAge).Single());
    }

    [Fact]
    public async Task Stamps_a_simple_request_and_varies_by_origin()
    {
        await using var server = await StartAsync(p => p.WithOrigins(Origin).AllowAnyMethod().AllowAnyHeader());

        var response = await server.Client.SendAsync(CrossOrigin(HttpMethod.Get, "/data"), Token);

        Assert.Equal("payload", await response.Content.ReadAsStringAsync(Token));
        Assert.Equal(Origin, response.Headers.GetValues(HeaderNames.AccessControlAllowOrigin).Single());
        Assert.Contains(HeaderNames.Origin, response.Headers.GetValues(HeaderNames.Vary));
    }

    [Fact]
    public async Task A_request_without_an_origin_is_not_a_cors_request()
    {
        await using var server = await StartAsync(p => p.WithOrigins(Origin).AllowAnyMethod().AllowAnyHeader());

        var response = await server.Client.GetAsync("/data", Token);

        Assert.False(response.Headers.Contains(HeaderNames.AccessControlAllowOrigin));
        Assert.Equal("payload", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task An_unlisted_origin_still_gets_its_response_just_without_permission()
    {
        // CORS is enforced by the browser, not the server. The request runs; the browser is simply
        // never told it may hand the result to script.
        await using var server = await StartAsync(p => p.WithOrigins(Origin).AllowAnyMethod().AllowAnyHeader());

        var response = await server.Client.SendAsync(CrossOrigin(HttpMethod.Get, "/data", "https://evil.example"), Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("payload", await response.Content.ReadAsStringAsync(Token));
        Assert.False(response.Headers.Contains(HeaderNames.AccessControlAllowOrigin));
    }

    [Fact]
    public async Task Any_origin_emits_a_wildcard()
    {
        await using var server = await StartAsync(p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());

        var response = await server.Client.SendAsync(CrossOrigin(HttpMethod.Get, "/data", "https://anywhere.example"), Token);

        Assert.Equal("*", response.Headers.GetValues(HeaderNames.AccessControlAllowOrigin).Single());
        Assert.False(response.Headers.Contains(HeaderNames.Vary));
    }

    [Fact]
    public async Task Credentials_echo_the_origin_rather_than_a_wildcard()
    {
        await using var server = await StartAsync(
            p => p.WithOrigins(Origin).AllowAnyMethod().AllowAnyHeader().AllowCredentials()
        );

        var response = await server.Client.SendAsync(CrossOrigin(HttpMethod.Get, "/data"), Token);

        Assert.Equal(Origin, response.Headers.GetValues(HeaderNames.AccessControlAllowOrigin).Single());
        Assert.Equal("true", response.Headers.GetValues(HeaderNames.AccessControlAllowCredentials).Single());
    }

    [Fact]
    public void Any_origin_with_credentials_is_rejected_when_the_policy_is_built()
    {
        // A browser refuses this combination, so the failure would otherwise show up as "CORS is
        // broken" long after the line that caused it.
        var error = Assert.Throws<InvalidOperationException>(
            () => CorsPolicy.Create(p => p.AllowAnyOrigin().AllowCredentials())
        );

        Assert.Contains("AllowAnyOrigin", error.Message);
        Assert.Contains("AllowCredentials", error.Message);
    }

    [Fact]
    public void A_policy_that_allows_no_origins_is_rejected()
        => Assert.Throws<InvalidOperationException>(() => CorsPolicy.Create(p => p.AllowAnyHeader()));

    [Fact]
    public async Task Exposed_headers_are_listed()
    {
        await using var server = await StartAsync(
            p => p.WithOrigins(Origin).AllowAnyMethod().AllowAnyHeader().WithExposedHeaders("X-Total-Count", "X-Page")
        );

        var response = await server.Client.SendAsync(CrossOrigin(HttpMethod.Get, "/data"), Token);

        Assert.Equal(
            "X-Total-Count, X-Page",
            response.Headers.GetValues(HeaderNames.AccessControlExposeHeaders).Single()
        );
    }

    [Fact]
    public async Task A_trailing_slash_on_a_configured_origin_still_matches()
    {
        // Copy-pasted from a browser address bar, an origin usually arrives with a slash on the end.
        await using var server = await StartAsync(
            p => p.WithOrigins("https://app.example.com/").AllowAnyMethod().AllowAnyHeader()
        );

        var response = await server.Client.SendAsync(CrossOrigin(HttpMethod.Get, "/data"), Token);

        Assert.Equal(Origin, response.Headers.GetValues(HeaderNames.AccessControlAllowOrigin).Single());
    }

    [Fact]
    public async Task A_predicate_decides_what_a_list_cannot()
    {
        await using var server = await StartAsync(
            p => p.SetIsOriginAllowed(o => o.EndsWith(".example.com", StringComparison.OrdinalIgnoreCase))
                  .AllowAnyMethod()
                  .AllowAnyHeader()
        );

        var allowed = await server.Client.SendAsync(CrossOrigin(HttpMethod.Get, "/data", "https://tenant.example.com"), Token);
        Assert.True(allowed.Headers.Contains(HeaderNames.AccessControlAllowOrigin));

        var refused = await server.Client.SendAsync(CrossOrigin(HttpMethod.Get, "/data", "https://tenant.evil.com"), Token);
        Assert.False(refused.Headers.Contains(HeaderNames.AccessControlAllowOrigin));
    }

    [Fact]
    public async Task An_endpoint_can_name_its_own_policy()
    {
        await using var server = await TestServer.StartAsync(
            app =>
            {
                app.UseCors();
                app.MapGet("/private", ctx => ctx.Response.WriteAsync("private"));
                app.MapGet("/public", ctx => ctx.Response.WriteAsync("public")).RequireCors("public");
            },
            builder => builder.AddCors(o =>
            {
                o.AddDefaultPolicy(p => p.WithOrigins(Origin).AllowAnyMethod().AllowAnyHeader());
                o.AddPolicy("public", p => p.AllowAnyOrigin().AllowAnyMethod());
            })
        );

        var stranger = "https://anywhere.example";

        var privateResponse = await server.Client.SendAsync(CrossOrigin(HttpMethod.Get, "/private", stranger), Token);
        Assert.False(privateResponse.Headers.Contains(HeaderNames.AccessControlAllowOrigin));

        var publicResponse = await server.Client.SendAsync(CrossOrigin(HttpMethod.Get, "/public", stranger), Token);
        Assert.Equal("*", publicResponse.Headers.GetValues(HeaderNames.AccessControlAllowOrigin).Single());
    }

    [Fact]
    public async Task An_endpoint_policy_applies_to_its_preflight_too()
    {
        // The preflight arrives as OPTIONS, so the endpoint it is really about is the one the
        // Access-Control-Request-Method names.
        await using var server = await TestServer.StartAsync(
            app =>
            {
                app.UseCors();
                app.MapPost("/public", ctx => ctx.Response.WriteAsync("public")).RequireCors("public");
            },
            builder => builder.AddCors(o =>
            {
                o.AddDefaultPolicy(p => p.WithOrigins(Origin).AllowAnyMethod().AllowAnyHeader());
                o.AddPolicy("public", p => p.AllowAnyOrigin().AllowAnyMethod());
            })
        );

        var response = await server.Client.SendAsync(Preflight("/public", "POST", origin: "https://anywhere.example"), Token);

        Assert.Equal("*", response.Headers.GetValues(HeaderNames.AccessControlAllowOrigin).Single());
    }

    [Fact]
    public async Task An_endpoint_can_opt_out_of_the_default_policy()
    {
        await using var server = await TestServer.StartAsync(
            app =>
            {
                app.UseCors();
                app.MapGet("/data", ctx => ctx.Response.WriteAsync("payload")).DisableCors();
            },
            builder => builder.AddCors(
                o => o.AddDefaultPolicy(p => p.WithOrigins(Origin).AllowAnyMethod().AllowAnyHeader())
            )
        );

        var response = await server.Client.SendAsync(CrossOrigin(HttpMethod.Get, "/data"), Token);

        Assert.False(response.Headers.Contains(HeaderNames.AccessControlAllowOrigin));
    }

    [Fact]
    public async Task A_preflight_for_a_path_that_matches_nothing_is_still_answered()
    {
        await using var server = await StartAsync(p => p.WithOrigins(Origin).AllowAnyMethod().AllowAnyHeader());

        var response = await server.Client.SendAsync(Preflight("/nowhere", "GET"), Token);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(Origin, response.Headers.GetValues(HeaderNames.AccessControlAllowOrigin).Single());
    }

    [Fact]
    public async Task Vary_is_appended_rather_than_replacing_what_the_handler_set()
    {
        await using var server = await TestServer.StartAsync(app =>
        {
            app.UseCors(p => p.WithOrigins(Origin).AllowAnyMethod().AllowAnyHeader());
            app.MapGet("/data", ctx =>
            {
                ctx.Response.Headers.Set(HeaderNames.Vary, HeaderNames.AcceptEncoding);
                return ctx.Response.WriteAsync("payload");
            });
        });

        var response = await server.Client.SendAsync(CrossOrigin(HttpMethod.Get, "/data"), Token);
        var vary = response.Headers.GetValues(HeaderNames.Vary).ToArray();

        Assert.Contains(HeaderNames.AcceptEncoding, vary);
        Assert.Contains(HeaderNames.Origin, vary);
    }

    [Fact]
    public async Task UseCors_without_a_policy_says_so_rather_than_doing_nothing()
    {
        await using var server = await TestServer.StartAsync(app => app.MapGet("/x", ctx => ctx.Response.WriteAsync("ok")));

        var error = Assert.Throws<InvalidOperationException>(() => server.Server.UseCors());
        Assert.Contains("AddCors", error.Message);
    }
}
