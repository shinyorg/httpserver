using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Shiny.Net.HttpServer.Jwt;
using Shiny.Net.HttpServer.Security;

namespace Shiny.Net.HttpServer.Tests;

/// <summary>A class role narrowed by a method role.</summary>
[Route("/api/roles")]
[Authorize(Roles = "admin")]
public class StackedRoleEndpoints
{
    /// <summary>Needs admin (class) and viewer (method).</summary>
    [Get("/report")]
    [Authorize(Roles = "viewer")]
    public string Report() => "report";

    /// <summary>Needs admin only, or within the attribute either of two roles.</summary>
    [Get("/either")]
    public string Either() => "either";
}

// Several [Authorize(Roles = …)] used to be merged into one any-of list, so a class that said
// Roles = "admin" and a method that said Roles = "viewer" let a mere viewer in.
public class AuthorizationRoleTests
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

    static string TokenFor(params string[] roles)
    {
        var descriptor = new JwtTokenDescriptor { Issuer = "tests", Subject = "u", Audiences = { "tests" } };
        descriptor.AddRoles(roles);

        using var key = JwtSigningKey.FromSecret(Secret);
        return new JwtTokenGenerator(key).Create(descriptor);
    }

    static async Task<HttpStatusCode> GetAsync(TestServer server, string path, params string[] roles)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TokenFor(roles));

        using var response = await server.Client.SendAsync(request, Token);
        return response.StatusCode;
    }

    [Fact]
    public async Task A_method_role_narrows_a_class_role_on_a_generated_endpoint()
    {
        await using var server = await StartAsync(app => app.MapStackedRoleEndpoints());

        Assert.Equal(HttpStatusCode.Forbidden, await GetAsync(server, "/api/roles/report", "viewer"));
        Assert.Equal(HttpStatusCode.Forbidden, await GetAsync(server, "/api/roles/report", "admin"));
        Assert.Equal(HttpStatusCode.OK, await GetAsync(server, "/api/roles/report", "admin", "viewer"));

        Assert.Equal(HttpStatusCode.OK, await GetAsync(server, "/api/roles/either", "admin"));
        Assert.Equal(HttpStatusCode.Forbidden, await GetAsync(server, "/api/roles/either", "viewer"));
    }

    [Fact]
    public async Task Two_AuthorizeAttributes_with_roles_on_a_raw_route_are_both_required()
    {
        await using var server = await StartAsync(app =>
            app.Map(
                "GET",
                "/raw",
                ctx => ctx.Response.WriteAsync("raw"),
                new AuthorizeAttribute { Roles = "admin, owner" },
                new AuthorizeAttribute { Roles = "viewer" }
            ));

        Assert.Equal(HttpStatusCode.Forbidden, await GetAsync(server, "/raw", "viewer"));
        Assert.Equal(HttpStatusCode.Forbidden, await GetAsync(server, "/raw", "admin"));

        // Within one attribute any listed role will do.
        Assert.Equal(HttpStatusCode.OK, await GetAsync(server, "/raw", "owner", "viewer"));
        Assert.Equal(HttpStatusCode.OK, await GetAsync(server, "/raw", "admin", "viewer"));
    }

    [Fact]
    public async Task Roles_set_directly_on_metadata_are_still_any_of()
    {
        var metadata = new AuthorizationMetadata { Required = true };
        metadata.Roles.Add("a");
        metadata.Roles.Add("b");

        await using var server = await StartAsync(app => app.Map("GET", "/legacy", ctx => ctx.Response.WriteAsync("ok"), metadata));

        Assert.Equal(HttpStatusCode.OK, await GetAsync(server, "/legacy", "b"));
        Assert.Equal(HttpStatusCode.Forbidden, await GetAsync(server, "/legacy", "c"));
    }
}
