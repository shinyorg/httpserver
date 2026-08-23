using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Shiny.Net.HttpServer.Mcp;
using Shiny.Net.HttpServer.Security;

namespace Shiny.Net.HttpServer.Tests;

/// <summary>
/// The OAuth discovery half of a remote MCP server: a 401 that says where to authenticate, and a
/// document at the other end of it that names the authorization server.
/// </summary>
public class McpProtectedResourceTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    static Task<TestServer> Protected(Action<McpProtectedResourceOptions>? extra = null)
        => TestServer.StartAsync(
            app =>
            {
                app.UseAuthentication();
                app.UseAuthorization();

                app.MapMcp().RequireAuthorization();
                app.MapMcpProtectedResource();
            },
            builder =>
            {
                builder.Services
                    .AddMcpServer(o => o.ServerInfo = new Implementation { Name = "guarded", Version = "1.0.0" })
                    .WithTools<ProtectedTools>()
                    .WithHttpTransport();

                builder.AddAuthentication();
                builder.AddAuthorization();

                builder.AddMcpProtectedResource(o =>
                {
                    o.AuthorizationServers.Add("https://login.example.com");
                    o.ScopesSupported.Add("mcp:tools");
                    o.ResourceName = "Guarded thermostat";

                    extra?.Invoke(o);
                });
            }
        );

    [Fact]
    public async Task A_denied_request_points_at_the_metadata()
    {
        await using var test = await Protected();

        var response = await test.Client.PostAsync("/mcp", new StringContent("{}"), Token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var challenge = response.Headers.WwwAuthenticate.Single();
        Assert.Equal("Bearer", challenge.Scheme);
        Assert.Contains("/.well-known/oauth-protected-resource/mcp", challenge.Parameter);
    }

    [Fact]
    public async Task The_metadata_names_the_authorization_server()
    {
        await using var test = await Protected();

        var body = await test.Client.GetStringAsync("/.well-known/oauth-protected-resource/mcp", Token);

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        Assert.EndsWith("/mcp", root.GetProperty("resource").GetString());
        Assert.Equal("https://login.example.com", root.GetProperty("authorization_servers")[0].GetString());
        Assert.Equal("mcp:tools", root.GetProperty("scopes_supported")[0].GetString());
        Assert.Equal("header", root.GetProperty("bearer_methods_supported")[0].GetString());
        Assert.Equal("Guarded thermostat", root.GetProperty("resource_name").GetString());
    }

    /// <summary>RFC 9728 lets a client ask at the bare well-known path too.</summary>
    [Fact]
    public async Task Both_well_known_paths_answer()
    {
        await using var test = await Protected();

        Assert.Equal(HttpStatusCode.OK, (await test.Client.GetAsync("/.well-known/oauth-protected-resource", Token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await test.Client.GetAsync("/.well-known/oauth-protected-resource/mcp", Token)).StatusCode);
    }

    [Fact]
    public async Task A_fixed_resource_identifier_overrides_the_request_host()
    {
        await using var test = await Protected(o => o.Resource = "https://thermostat.example.com/mcp");

        var body = await test.Client.GetStringAsync("/.well-known/oauth-protected-resource/mcp", Token);

        using var document = JsonDocument.Parse(body);
        Assert.Equal("https://thermostat.example.com/mcp", document.RootElement.GetProperty("resource").GetString());
    }

    /// <summary>A browser-based client fetches this before it holds any credential at all.</summary>
    [Fact]
    public async Task The_metadata_is_readable_without_credentials_and_cross_origin()
    {
        await using var test = await Protected();

        var response = await test.Client.GetAsync("/.well-known/oauth-protected-resource/mcp", Token);

        Assert.Equal("*", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [Fact]
    public async Task A_document_with_no_authorization_server_is_refused_at_startup()
    {
        var builder = HttpServer.CreateBuilder();
        builder.Options.Port = 0;
        builder.AddMcpProtectedResource(_ => { });

        await using var server = builder.Build();

        var error = Assert.Throws<InvalidOperationException>(() => server.MapMcpProtectedResource());
        Assert.Contains("authorization servers", error.Message);
    }

    [McpServerToolType]
    sealed class ProtectedTools
    {
        [McpServerTool(Name = "temperature")]
        public static string Temperature() => "21";
    }
}
