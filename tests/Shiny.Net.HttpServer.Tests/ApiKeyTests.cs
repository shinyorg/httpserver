using System.Net;
using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Shiny.Net.HttpServer.Security;

namespace Shiny.Net.HttpServer.Tests;

public class ApiKeyAuthenticationTests
{
    const string Key = "s3cr3t-ingest-key";

    static Task<TestServer> StartAsync(Action<ApiKeyOptions> configure) => TestServer.StartAsync(
        app =>
        {
            app.UseAuthentication();
            app.UseAuthorization();

            app.MapGet("/who", ctx => ctx.Response.WriteTextAsync(ctx.User.Identity?.Name ?? "anonymous"))
                .RequireAuthorization();

            app.MapGet("/open", ctx => ctx.Response.WriteTextAsync(ctx.User.Identity?.Name ?? "anonymous"));

            app.MapGet("/writers", ctx => ctx.Response.WriteTextAsync("wrote"))
                .RequireAuthorization("writers-only");
        },
        builder =>
        {
            builder.Services.AddAuthentication().AddApiKey(configure);
            builder.Services.AddAuthorization(o => o.AddPolicy("writers-only", p => p.RequireRole("writer")));
        }
    );

    [Fact]
    public async Task Identifies_a_caller_from_the_header()
    {
        await using var server = await StartAsync(o => o.AddKey(Key, "ingest-service", "writer"));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/who");
        request.Headers.Add("X-API-Key", Key);

        var response = await server.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ingest-service", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Accepts_the_key_on_the_authorization_header()
    {
        await using var server = await StartAsync(o => o.AddKey(Key, "ingest-service"));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/who");
        request.Headers.TryAddWithoutValidation("Authorization", "ApiKey " + Key);

        var response = await server.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal("ingest-service", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>Off by default: a query string ends up in logs, history and Referer headers.</summary>
    [Fact]
    public async Task Ignores_a_key_in_the_query_string_unless_enabled()
    {
        await using var server = await StartAsync(o => o.AddKey(Key, "ingest-service"));

        var denied = await server.Client.GetAsync($"/who?api_key={Key}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
    }

    [Fact]
    public async Task Accepts_a_key_in_the_query_string_when_enabled()
    {
        await using var server = await StartAsync(o =>
        {
            o.QueryParameterName = "api_key";
            o.AddKey(Key, "ingest-service");
        });

        var response = await server.Client.GetAsync($"/who?api_key={Key}", TestContext.Current.CancellationToken);

        Assert.Equal("ingest-service", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Rejects_an_unknown_key()
    {
        await using var server = await StartAsync(o => o.AddKey(Key, "ingest-service"));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/who");
        request.Headers.Add("X-API-Key", "not-the-key");

        var response = await server.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        // The challenge names the scheme, so a client knows what kind of credential to go and get.
        Assert.Contains("ApiKey", response.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Leaves_a_request_with_no_key_anonymous()
    {
        await using var server = await StartAsync(o => o.AddKey(Key, "ingest-service"));

        Assert.Equal("anonymous", await server.Client.GetStringAsync("/open", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Carries_roles_into_authorization()
    {
        await using var server = await StartAsync(o =>
        {
            o.AddKey(Key, "ingest-service", "writer");
            o.AddKey("reader-key", "reporting", "reader");
        });

        using var writer = new HttpRequestMessage(HttpMethod.Get, "/writers");
        writer.Headers.Add("X-API-Key", Key);

        using var reader = new HttpRequestMessage(HttpMethod.Get, "/writers");
        reader.Headers.Add("X-API-Key", "reader-key");

        Assert.Equal(
            HttpStatusCode.OK,
            (await server.Client.SendAsync(writer, TestContext.Current.CancellationToken)).StatusCode
        );

        // Known caller, still not allowed: 403, not 401.
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await server.Client.SendAsync(reader, TestContext.Current.CancellationToken)).StatusCode
        );
    }

    [Fact]
    public async Task Falls_back_to_the_validator_for_a_key_that_is_not_configured()
    {
        await using var server = await StartAsync(o => o.ValidateAsync = (key, _) => new ValueTask<ClaimsPrincipal?>(
            key == "from-the-database"
                ? new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "db-user")], "ApiKey"))
                : null
        ));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/who");
        request.Headers.Add("X-API-Key", "from-the-database");

        Assert.Equal(
            "db-user",
            await (await server.Client.SendAsync(request, TestContext.Current.CancellationToken))
                .Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task Supports_a_custom_header_name()
    {
        await using var server = await StartAsync(o =>
        {
            o.HeaderName = "X-Device-Token";
            o.AddKey(Key, "device-1");
        });

        using var request = new HttpRequestMessage(HttpMethod.Get, "/who");
        request.Headers.Add("X-Device-Token", Key);

        Assert.Equal(
            "device-1",
            await (await server.Client.SendAsync(request, TestContext.Current.CancellationToken))
                .Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
        );
    }

    /// <summary>A scheme with nothing to accept would silently reject every request.</summary>
    [Fact]
    public void Refuses_to_register_with_no_keys_and_no_validator()
    {
        var services = new ServiceCollection();

        Assert.Throws<InvalidOperationException>(() => services.AddAuthentication().AddApiKey(_ => { }));
    }
}
