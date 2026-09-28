using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Shiny.Net.HttpServer.OpenApi;
using Shiny.Net.HttpServer.Security;
using Shiny.Net.HttpServer.Versioning;

namespace Shiny.Net.HttpServer.Tests;

public class ScalarTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    // The configuration is the object literal handed to createApiReference; reading it back as JSON
    // is the assertion that it is valid JSON at all.
    static JsonElement Configuration(string page)
    {
        const string start = "Scalar.createApiReference('#app', ";
        var from = page.IndexOf(start, StringComparison.Ordinal) + start.Length;
        var to = page.IndexOf(");</script>", from, StringComparison.Ordinal);
        return JsonDocument.Parse(page[from..to]).RootElement.Clone();
    }

    [Fact]
    public async Task Serves_the_reference_pointing_at_the_document()
    {
        await using var server = await TestServer.StartAsync(app =>
        {
            app.MapGet("/ping", ctx => ctx.Response.WriteTextAsync("pong"));
            app.MapOpenApi();
            app.MapScalarApiReference();
        });

        using var response = await server.Client.GetAsync("/scalar", Token);
        var page = await response.Content.ReadAsStringAsync(Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains($"<script src=\"{ScalarOptions.DefaultScriptUrl}\"></script>", page);
        Assert.Equal("/openapi.json", Configuration(page).GetProperty("url").GetString());
    }

    [Fact]
    public async Task Is_left_out_of_the_document_it_describes()
    {
        await using var server = await TestServer.StartAsync(app =>
        {
            app.MapGet("/ping", ctx => ctx.Response.WriteTextAsync("pong"));
            app.MapOpenApi();
            app.MapScalarApiReference("/docs");
        });

        var document = await server.Client.GetStringAsync("/openapi.json", Token);

        Assert.Contains("\"/ping\"", document);
        Assert.DoesNotContain("\"/docs\"", document);
    }

    [Fact]
    public void Writes_only_the_settings_that_were_set()
    {
        var page = HttpServerScalarExtensions.BuildScalarPage(new HttpServer(), new ScalarOptions
        {
            Theme = ScalarTheme.BluePlanet,
            Layout = ScalarLayout.Classic,
            ForceColorMode = ScalarColorMode.Dark,
            HideModels = true,
            DefaultHttpClient = ("csharp", "httpclient"),
            PreferredSecurityScheme = "bearer"
        });
        var config = Configuration(page);

        Assert.Equal("bluePlanet", config.GetProperty("theme").GetString());
        Assert.Equal("classic", config.GetProperty("layout").GetString());
        Assert.Equal("dark", config.GetProperty("forceDarkModeState").GetString());
        Assert.True(config.GetProperty("hideModels").GetBoolean());
        Assert.Equal("csharp", config.GetProperty("defaultHttpClient").GetProperty("targetKey").GetString());
        Assert.Equal("httpclient", config.GetProperty("defaultHttpClient").GetProperty("clientKey").GetString());
        Assert.Equal("bearer", config.GetProperty("authentication").GetProperty("preferredSecurityScheme").GetString());

        // Unset means Scalar's default, so it is absent rather than written as false.
        Assert.False(config.TryGetProperty("darkMode", out _));
        Assert.False(config.TryGetProperty("showSidebar", out _));
    }

    [Fact]
    public void Additional_configuration_is_written_as_given_and_wins()
    {
        var options = new ScalarOptions { Theme = ScalarTheme.Moon };
        options.AdditionalConfiguration["theme"] = "mars";
        options.AdditionalConfiguration["hiddenClients"] = new JsonArray("unirest");

        var config = Configuration(HttpServerScalarExtensions.BuildScalarPage(new HttpServer(), options));

        Assert.Equal("mars", config.GetProperty("theme").GetString());
        Assert.Equal("unirest", config.GetProperty("hiddenClients")[0].GetString());
        Assert.Single(config.EnumerateObject(), p => p.Name == "theme");
    }

    [Fact]
    public void Nothing_configured_can_break_out_of_the_page()
    {
        var page = HttpServerScalarExtensions.BuildScalarPage(new HttpServer(), new ScalarOptions
        {
            Title = "</title><script>alert(1)</script>",
            CustomCss = "</script><script>alert(2)</script>",
            ScriptUrl = "https://cdn.example/x.js\"><script>alert(3)</script>"
        });

        Assert.DoesNotContain("alert(1)</script>", page);
        Assert.DoesNotContain("</script><script>alert(2)", page);
        Assert.DoesNotContain("\"><script>alert(3)", page);

        // The CSS survives intact once the JSON is read back — escaped, not dropped.
        Assert.Equal("</script><script>alert(2)</script>", Configuration(page).GetProperty("customCss").GetString());
    }

    [Fact]
    public void Lists_one_document_per_api_version_newest_first()
    {
        var server = new HttpServer();
        server.MapGet("/users", _ => default).HasApiVersion(1.0);
        server.MapGet("/users", _ => default).HasApiVersion(2.0);
        server.MapGet("/orders", _ => default).HasApiVersion(1.5);

        var page = HttpServerScalarExtensions.BuildScalarPage(
            server,
            new ScalarOptions { DocumentUrl = "/openapi/{documentName}.json" }
        );
        var sources = Configuration(page).GetProperty("sources").EnumerateArray().ToList();

        Assert.Equal(
            ["/openapi/v2.json", "/openapi/v1.5.json", "/openapi/v1.json"],
            sources.Select(s => s.GetProperty("url").GetString())
        );
        Assert.Equal("v2", sources[0].GetProperty("title").GetString());
        Assert.True(sources[0].GetProperty("default").GetBoolean());
        Assert.All(sources.Skip(1), s => Assert.False(s.TryGetProperty("default", out _)));
    }

    [Fact]
    public void A_per_version_url_with_no_versioned_endpoints_says_so()
    {
        var server = new HttpServer();
        server.MapGet("/users", _ => default);

        var ex = Assert.Throws<InvalidOperationException>(() => HttpServerScalarExtensions.BuildScalarPage(
            server,
            new ScalarOptions { DocumentUrl = "/openapi/{documentName}.json" }
        ));

        Assert.Contains("API version", ex.Message);
    }

    [Fact]
    public void Explicit_documents_replace_the_document_url()
    {
        var options = new ScalarOptions()
            .AddDocument("/openapi.json", "Public", "public", isDefault: true)
            .AddDocument("https://internal.example/openapi.json", "Internal");

        var sources = Configuration(HttpServerScalarExtensions.BuildScalarPage(new HttpServer(), options))
            .GetProperty("sources")
            .EnumerateArray()
            .ToList();

        Assert.Equal(2, sources.Count);
        Assert.Equal("public", sources[0].GetProperty("slug").GetString());
        Assert.Equal("https://internal.example/openapi.json", sources[1].GetProperty("url").GetString());
    }

    [Fact]
    public async Task Can_be_protected_like_any_other_route()
    {
        await using var server = await TestServer.StartAsync(
            app =>
            {
                app.UseAuthentication();
                app.UseAuthorization();
                app.MapOpenApi();
                app.MapScalarApiReference().RequireAuthorization();
            },
            builder =>
            {
                builder.AddAuthentication().AddBasic(o => o.AddUser("ada", "secret"));
                builder.AddAuthorization();
            }
        );

        using var response = await server.Client.GetAsync("/scalar", Token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
