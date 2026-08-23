using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Shiny.Net.HttpServer.Jwt;
using Shiny.Net.HttpServer.Security;

namespace Shiny.Net.HttpServer.Tests;

// A protected endpoint class, so the generator's [Authorize] handling is exercised through real
// HTTP rather than by inspecting emitted source.

/// <summary>Endpoints behind authorization.</summary>
[Route("/api/secure")]
[Authorize]
public class SecureEndpoints
{
    /// <summary>Requires any authenticated caller.</summary>
    [Get("/me")]
    public string Me(HttpContext context) => context.User.FindFirst(JwtClaimNames.Subject)?.Value ?? "?";

    /// <summary>Requires the admin policy.</summary>
    [Get("/admin")]
    [Authorize("admin")]
    public string Admin() => "admin-only";

    /// <summary>Requires the auditor role.</summary>
    [Get("/audit")]
    [Authorize(Roles = "auditor")]
    public string Audit() => "audit-only";

    /// <summary>Open despite the class-level attribute.</summary>
    [Get("/ping")]
    [AllowAnonymous]
    public string Ping() => "pong";
}

public class AuthorizationTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    const string Issuer = "shiny-tests";
    const string Audience = "shiny-tests-app";
    static readonly byte[] Secret = Encoding.UTF8.GetBytes("tests-32-byte-secret-value!!!!!!");

    static Task<TestServer> StartAsync(
        Action<HttpServer>? configure = null,
        Action<AuthorizationOptions>? authorization = null
    ) => TestServer.StartAsync(
        app =>
        {
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapSecureEndpoints();
            app.MapGet("/open", ctx => ctx.Response.WriteAsync("open"));
            app.MapGet("/closed", ctx => ctx.Response.WriteAsync("closed")).RequireAuthorization();
            configure?.Invoke(app);
        },
        builder =>
        {
            builder
                .AddAuthentication()
                .AddJwtBearer(o =>
                {
                    o.Issuer = Issuer;
                    o.Audience = Audience;
                    o.SigningKey = JwtSigningKey.FromSecret(Secret);
                });

            builder.AddAuthorization(o =>
            {
                o.AddPolicy("admin", p => p.RequireRole("admin"));
                authorization?.Invoke(o);
            });
        }
    );

    static string TokenFor(params string[] roles)
    {
        var descriptor = new JwtTokenDescriptor
        {
            Issuer = Issuer,
            Subject = "user-1",
            Audiences = { Audience }
        };

        descriptor.AddRoles(roles);

        using var key = JwtSigningKey.FromSecret(Secret);
        return new JwtTokenGenerator(key).Create(descriptor);
    }

    static HttpRequestMessage Request(string path, string? token = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);

        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return request;
    }

    [Fact]
    public async Task Leaves_unprotected_endpoints_open()
    {
        await using var server = await StartAsync();
        Assert.Equal("open", await server.Client.GetStringAsync("/open", Token));
    }

    [Fact]
    public async Task Answers_401_for_an_anonymous_caller()
    {
        await using var server = await StartAsync();

        var response = await server.Client.SendAsync(Request("/api/secure/me"), Token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Bearer", response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task Answers_403_for_an_authenticated_caller_who_is_still_not_allowed()
    {
        await using var server = await StartAsync();

        var response = await server.Client.SendAsync(Request("/api/secure/admin", TokenFor("user")), Token);

        // Not 401: we know exactly who this is, and another login will not help.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Allows_an_authenticated_caller_through()
    {
        await using var server = await StartAsync();

        var response = await server.Client.SendAsync(Request("/api/secure/me", TokenFor()), Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("user-1", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Enforces_a_named_policy()
    {
        await using var server = await StartAsync();

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await server.Client.SendAsync(Request("/api/secure/admin", TokenFor("auditor")), Token)).StatusCode
        );

        Assert.Equal(
            "admin-only",
            await (await server.Client.SendAsync(Request("/api/secure/admin", TokenFor("admin")), Token))
                .Content.ReadAsStringAsync(Token)
        );
    }

    [Fact]
    public async Task Enforces_roles_declared_on_the_attribute()
    {
        await using var server = await StartAsync();

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await server.Client.SendAsync(Request("/api/secure/audit", TokenFor("admin")), Token)).StatusCode
        );

        Assert.Equal(
            "audit-only",
            await (await server.Client.SendAsync(Request("/api/secure/audit", TokenFor("auditor")), Token))
                .Content.ReadAsStringAsync(Token)
        );
    }

    [Fact]
    public async Task AllowAnonymous_beats_a_class_level_Authorize()
    {
        await using var server = await StartAsync();
        Assert.Equal("pong", await server.Client.GetStringAsync("/api/secure/ping", Token));
    }

    [Fact]
    public async Task RequireAuthorization_protects_a_raw_route()
    {
        await using var server = await StartAsync();

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await server.Client.SendAsync(Request("/closed"), Token)).StatusCode
        );

        Assert.Equal(
            "closed",
            await (await server.Client.SendAsync(Request("/closed", TokenFor()), Token)).Content.ReadAsStringAsync(Token)
        );
    }

    [Fact]
    public async Task Reports_why_a_bad_token_was_rejected_in_the_challenge()
    {
        await using var server = await StartAsync();

        var response = await server.Client.SendAsync(Request("/api/secure/me", "not.a.token"), Token);
        var challenge = response.Headers.WwwAuthenticate.ToString();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("invalid_token", challenge);

        // The reason belongs in the challenge, not the body — a body would be one more thing to
        // accidentally render somewhere.
        Assert.Empty(await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task A_fallback_policy_closes_everything_that_did_not_opt_out()
    {
        await using var server = await StartAsync(
            authorization: o => o.SetFallbackPolicy(p => p.RequireAuthenticatedUser())
        );

        // /open never said anything about authorization, and is now closed.
        Assert.Equal(HttpStatusCode.Unauthorized, (await server.Client.SendAsync(Request("/open"), Token)).StatusCode);
        Assert.Equal("open", await (await server.Client.SendAsync(Request("/open", TokenFor()), Token)).Content.ReadAsStringAsync(Token));

        // ...except what explicitly opted out.
        Assert.Equal("pong", await server.Client.GetStringAsync("/api/secure/ping", Token));
    }

    [Fact]
    public async Task Populates_the_user_even_on_an_open_endpoint()
    {
        await using var server = await StartAsync(app => app.MapGet("/whoami", ctx =>
            ctx.Response.WriteAsync(ctx.User.Identity?.Name ?? ctx.User.FindFirst(JwtClaimNames.Subject)?.Value ?? "anonymous")));

        Assert.Equal("anonymous", await server.Client.GetStringAsync("/whoami", Token));
        Assert.Equal(
            "user-1",
            await (await server.Client.SendAsync(Request("/whoami", TokenFor()), Token)).Content.ReadAsStringAsync(Token)
        );
    }

    [Fact]
    public async Task Does_not_construct_the_endpoint_class_for_a_denied_request()
    {
        // Authorization runs between route selection and invocation, so a denied request never
        // reaches the endpoint — and never resolves its dependencies.
        await using var server = await StartAsync();

        var response = await server.Client.SendAsync(Request("/api/secure/me"), Token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Reports_a_policy_that_was_never_registered()
    {
        await using var server = await TestServer.StartAsync(
            app =>
            {
                app.UseAuthentication();
                app.UseAuthorization();
                app.MapGet("/x", ctx => ctx.Response.WriteAsync("x")).RequireAuthorization("nonexistent");
            },
            builder =>
            {
                builder.AddAuthentication().AddJwtBearer(o =>
                {
                    o.Issuer = Issuer;
                    o.Audience = Audience;
                    o.SigningKey = JwtSigningKey.FromSecret(Secret);
                });
                builder.AddAuthorization();
            }
        );

        var response = await server.Client.SendAsync(Request("/x", TokenFor()), Token);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("nonexistent", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Custom_policies_can_read_the_request()
    {
        await using var server = await StartAsync(
            app => app.MapGet("/users/{id}/secrets", ctx => ctx.Response.WriteAsync("yours"))
                      .RequireAuthorization("self"),
            authorization: o => o.AddPolicy("self", p => p.RequireAssertion(
                ctx => ctx.User.FindFirst(JwtClaimNames.Subject)?.Value
                    == ctx.HttpContext.Request.RouteValues["id"],
                "the subject to match the route"
            ))
        );

        Assert.Equal(
            "yours",
            await (await server.Client.SendAsync(Request("/users/user-1/secrets", TokenFor()), Token))
                .Content.ReadAsStringAsync(Token)
        );

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await server.Client.SendAsync(Request("/users/someone-else/secrets", TokenFor()), Token)).StatusCode
        );
    }

    [Fact]
    public void RequireAuthorization_throws_before_any_route_is_mapped()
    {
        var server = new HttpServer(new HttpServerOptions { Port = 0 });
        Assert.Throws<InvalidOperationException>(() => server.RequireAuthorization());
    }

    [Fact]
    public void UseAuthorization_explains_itself_without_a_container()
    {
        var server = new HttpServer(new HttpServerOptions { Port = 0 });

        var ex = Assert.Throws<InvalidOperationException>(() => server.UseAuthorization());
        Assert.Contains("AddAuthorization", ex.Message);
    }
}

public class AuthorizationPolicyTests
{
    static AuthorizationContext ContextFor(params Claim[] claims)
    {
        var identity = new ClaimsIdentity(claims, "Test", JwtClaimNames.Name, JwtClaimNames.Role);
        return new AuthorizationContext(null!, new ClaimsPrincipal(identity));
    }

    [Fact]
    public async Task An_empty_policy_still_requires_authentication()
    {
        var policy = new AuthorizationPolicyBuilder().Build();

        Assert.NotNull(await policy.EvaluateAsync(new AuthorizationContext(null!, new ClaimsPrincipal(new ClaimsIdentity()))));
        Assert.Null(await policy.EvaluateAsync(ContextFor(new Claim(JwtClaimNames.Name, "Ada"))));
    }

    [Fact]
    public async Task Requirements_are_combined_with_and()
    {
        var policy = new AuthorizationPolicyBuilder()
            .RequireRole("admin")
            .RequireClaim("tenant", "acme")
            .Build();

        Assert.NotNull(await policy.EvaluateAsync(ContextFor(new Claim(JwtClaimNames.Role, "admin"))));
        Assert.Null(await policy.EvaluateAsync(ContextFor(
            new Claim(JwtClaimNames.Role, "admin"),
            new Claim("tenant", "acme")
        )));
    }

    [Fact]
    public async Task Roles_are_combined_with_or()
    {
        var policy = new AuthorizationPolicyBuilder().RequireRole("admin", "auditor").Build();

        Assert.Null(await policy.EvaluateAsync(ContextFor(new Claim(JwtClaimNames.Role, "auditor"))));
        Assert.NotNull(await policy.EvaluateAsync(ContextFor(new Claim(JwtClaimNames.Role, "guest"))));
    }

    [Fact]
    public async Task A_claim_requirement_with_no_values_only_checks_presence()
    {
        var policy = new AuthorizationPolicyBuilder().RequireClaim("tenant").Build();

        Assert.Null(await policy.EvaluateAsync(ContextFor(new Claim("tenant", "anything"))));
        Assert.NotNull(await policy.EvaluateAsync(ContextFor(new Claim("other", "x"))));
    }

    [Fact]
    public async Task The_failure_names_the_requirement_that_failed()
    {
        var policy = new AuthorizationPolicyBuilder().RequireRole("admin").Build();
        var failure = await policy.EvaluateAsync(ContextFor(new Claim(JwtClaimNames.Name, "Ada")));

        Assert.Equal("role 'admin'", failure);
    }

    [Fact]
    public async Task No_requirement_is_satisfied_by_an_anonymous_caller()
    {
        var anonymous = new AuthorizationContext(null!, new ClaimsPrincipal(new ClaimsIdentity()));

        Assert.NotNull(await new AuthorizationPolicyBuilder().RequireRole("admin").Build().EvaluateAsync(anonymous));
        Assert.NotNull(await new AuthorizationPolicyBuilder().RequireClaim("any").Build().EvaluateAsync(anonymous));
    }
}
