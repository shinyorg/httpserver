using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Shiny.Net.HttpServer.Security;

namespace Shiny.Net.HttpServer.Tests;

// ---------------------------------------------------------------------------
// The single-endpoint form: verb on the class, one handler. Everything the
// controller path does — binding, DI, results, [Authorize], OpenAPI — works
// unchanged, because it is the same generator code with the grouping removed.
// ---------------------------------------------------------------------------

/// <summary>Returns one thing by id.</summary>
[Get("/minimal/things/{id:int}")]
public class GetThingEndpoint(IThingService things) : IHttpEndpoint
{
    public IActionResult Handle(int id)
        => things.Find(id) is { } thing ? new OkObjectResult(thing) : new NotFoundResult();
}

/// <summary>Creates a thing.</summary>
[Post("/minimal/things")]
public class CreateThingEndpoint(IThingService things) : IHttpEndpoint
{
    public async Task<IActionResult> HandleAsync(CreateThing request, CancellationToken cancellationToken)
    {
        await Task.Yield();
        return new CreatedResult("/minimal/things/1", things.Add(request.Name));
    }
}

/// <summary>Answers on two verbs from one class.</summary>
[Get("/minimal/multi")]
[Post("/minimal/multi")]
public class MultiVerbEndpoint : IHttpEndpoint
{
    public string Handle(HttpContext context) => context.Request.Method;
}

[Get("/minimal/query")]
public class QueryEndpoint : IHttpEndpoint
{
    public string Handle(string name, int count = 2) => $"{name}:{count}";
}

[Get("/minimal/secure")]
[Authorize]
public class SecureMinimalEndpoint : IHttpEndpoint
{
    public string Handle() => "secret";
}

public class MinimalEndpointTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    static Task<TestServer> StartAsync() => TestServer.StartAsync(
        app =>
        {
            app.MapGetThingEndpoint();
            app.MapCreateThingEndpoint();
            app.MapMultiVerbEndpoint();
            app.MapQueryEndpoint();
        },
        builder => builder.Services.AddSingleton<IThingService, ThingService>()
    );

    [Fact]
    public async Task Maps_the_class_level_route()
    {
        await using var server = await StartAsync();
        Assert.Equal("""{"id":1,"name":"first"}""", await server.Client.GetStringAsync("/minimal/things/1", Token));
    }

    [Fact]
    public async Task Returns_the_handler_result()
    {
        await using var server = await StartAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await server.Client.GetAsync("/minimal/things/99", Token)).StatusCode);
    }

    [Fact]
    public async Task Binds_a_json_body()
    {
        await using var server = await StartAsync();

        var response = await server.Client.PostAsJsonAsync("/minimal/things", new CreateThing("minimal"), Token);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Contains("minimal", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Binds_query_parameters_with_defaults()
    {
        await using var server = await StartAsync();

        Assert.Equal("bob:2", await server.Client.GetStringAsync("/minimal/query?name=bob", Token));
        Assert.Equal("bob:7", await server.Client.GetStringAsync("/minimal/query?name=bob&count=7", Token));
        Assert.Equal(HttpStatusCode.BadRequest, (await server.Client.GetAsync("/minimal/query", Token)).StatusCode);
    }

    [Fact]
    public async Task Supports_several_verbs_on_one_class()
    {
        await using var server = await StartAsync();

        Assert.Equal("GET", await server.Client.GetStringAsync("/minimal/multi", Token));

        var posted = await server.Client.PostAsync("/minimal/multi", new StringContent(""), Token);
        Assert.Equal("POST", await posted.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Is_registered_by_the_assembly_wide_call_alongside_controllers()
    {
        await using var server = await TestServer.StartAsync(
            app => app.MapShinyNetHttpServerTestsEndpoints(),
            builder => builder.Services.AddSingleton<IThingService, ThingService>()
        );

        // One call brings up minimal endpoints and [Route] controllers together.
        Assert.Equal("""{"id":1,"name":"first"}""", await server.Client.GetStringAsync("/minimal/things/1", Token));
        Assert.Equal("""{"id":1,"name":"first"}""", await server.Client.GetStringAsync("/api/things/1", Token));
    }

    [Fact]
    public async Task Honours_Authorize_on_a_minimal_endpoint()
    {
        await using var server = await TestServer.StartAsync(
            app =>
            {
                app.UseAuthentication();
                app.UseAuthorization();
                app.MapSecureMinimalEndpoint();
            },
            builder =>
            {
                builder.Services.AddAuthentication();
                builder.Services.AddAuthorization();
            }
        );

        Assert.Equal(HttpStatusCode.Unauthorized, (await server.Client.GetAsync("/minimal/secure", Token)).StatusCode);
    }
}

public class EndpointModuleTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    sealed class AdminModule : IEndpointModule
    {
        public void Map(IEndpointRouteBuilder endpoints)
        {
            endpoints.MapGet("/stats", ctx => ctx.Response.WriteAsync("stats"));
            endpoints.MapPost("/reset", ctx => ctx.Response.WriteAsync("reset"));
        }
    }

    sealed class PublicModule : IEndpointModule
    {
        public void Map(IEndpointRouteBuilder endpoints)
            => endpoints.MapGet("/about", ctx => ctx.Response.WriteAsync("about"));
    }

    [Fact]
    public async Task Mounts_a_module_under_a_prefix()
    {
        await using var server = await TestServer.StartAsync(app => app.MapModule(new AdminModule(), "/admin"));

        Assert.Equal("stats", await server.Client.GetStringAsync("/admin/stats", Token));
        Assert.Equal(
            "reset",
            await (await server.Client.PostAsync("/admin/reset", new StringContent(""), Token)).Content.ReadAsStringAsync(Token)
        );
    }

    [Fact]
    public async Task Mounts_a_module_at_the_root()
    {
        await using var server = await TestServer.StartAsync(app => app.MapModule(new PublicModule()));
        Assert.Equal("about", await server.Client.GetStringAsync("/about", Token));
    }

    [Fact]
    public async Task Unmounts_a_whole_module_while_running()
    {
        await using var server = await TestServer.StartAsync(app =>
        {
            app.MapModule(new AdminModule(), "/admin");
            app.MapModule(new PublicModule());
        });

        Assert.Equal("stats", await server.Client.GetStringAsync("/admin/stats", Token));

        var removed = server.Server.UnmapModule<AdminModule>();

        Assert.Equal(2, removed);
        Assert.Equal(HttpStatusCode.NotFound, (await server.Client.GetAsync("/admin/stats", Token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await server.Client.GetAsync("/admin/reset", Token)).StatusCode);

        // The other module is untouched.
        Assert.Equal("about", await server.Client.GetStringAsync("/about", Token));
    }

    [Fact]
    public async Task Re_mounts_a_module_after_unmounting_it()
    {
        await using var server = await TestServer.StartAsync(app => app.MapModule(new AdminModule(), "/admin"));

        server.Server.UnmapModule<AdminModule>();
        server.Server.MapModule(new AdminModule(), "/admin");

        Assert.Equal("stats", await server.Client.GetStringAsync("/admin/stats", Token));
    }

    [Fact]
    public async Task Mounts_every_module_in_the_container()
    {
        await using var server = await TestServer.StartAsync(
            app => app.MapModules(),
            builder =>
            {
                builder.Services.AddSingleton<IEndpointModule, AdminModule>();
                builder.Services.AddSingleton<IEndpointModule, PublicModule>();
            }
        );

        Assert.Equal("stats", await server.Client.GetStringAsync("/stats", Token));
        Assert.Equal("about", await server.Client.GetStringAsync("/about", Token));
    }

    [Fact]
    public async Task Groups_routes_under_a_shared_prefix()
    {
        await using var server = await TestServer.StartAsync(app => app.MapGroup("/api/v2", api =>
        {
            api.MapGet("/ping", ctx => ctx.Response.WriteAsync("pong"));

            api.MapGroup("/nested").MapGet("/deep", ctx => ctx.Response.WriteAsync("deep"));
        }));

        Assert.Equal("pong", await server.Client.GetStringAsync("/api/v2/ping", Token));
        Assert.Equal("deep", await server.Client.GetStringAsync("/api/v2/nested/deep", Token));
    }

    [Fact]
    public async Task Attaches_metadata_through_the_builder()
    {
        await using var server = await TestServer.StartAsync(
            app =>
            {
                app.UseAuthorization();
                app.MapGroup("", api => api
                    .MapGet("/guarded", ctx => ctx.Response.WriteAsync("guarded"))
                    .RequireAuthorization()
                    .WithSummary("Guarded route"));
            },
            builder =>
            {
                builder.Services.AddAuthentication();
                builder.Services.AddAuthorization();
            }
        );

        Assert.Equal(HttpStatusCode.Unauthorized, (await server.Client.GetAsync("/guarded", Token)).StatusCode);

        var endpoint = server.Server.Router.Endpoints.Single();
        Assert.True(endpoint.GetMetadata<Security.AuthorizationMetadata>()?.Required);
        Assert.Equal("Guarded route", endpoint.GetMetadata<OpenApi.ApiOperation>()?.Summary);
    }
}
