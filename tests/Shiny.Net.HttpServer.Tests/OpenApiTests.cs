using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shiny.Net.HttpServer.OpenApi;

namespace Shiny.Net.HttpServer.Tests;

// A second endpoint class, kept apart from ThingEndpoints so the document assertions below read
// against a small, stable surface rather than every binding case the binder tests need.

/// <summary>Documented endpoints, used to assert the generated OpenAPI description.</summary>
[Route("/api/docs")]
[ApiTags("documented")]
public class DocumentedEndpoints
{
    /// <summary>Fetches one thing.</summary>
    [Get("/{id:int}")]
    [Produces(200, typeof(Thing))]
    [Produces(404, Description = "Nothing has that id")]
    public IActionResult GetOne(int id) => new OkObjectResult(new Thing(id, "x"));

    /// <summary>Lists things.</summary>
    [Get]
    public IReadOnlyList<Thing> List(int take = 10, string? search = null, Colour? colour = null) => [];

    /// <summary>Creates a thing.</summary>
    [Post]
    [Produces(201, typeof(Thing))]
    public IActionResult Create(CreateThing request) => new CreatedResult("/api/docs/1", new Thing(1, request.Name));

    /// <summary>Matches with or without the trailing segment.</summary>
    [Get("/optional/{page:int?}")]
    public string Optional(int page = 1) => page.ToString();

    /// <summary>Captures the rest of the path.</summary>
    [Get("/files/{*path}")]
    public string Files(string path) => path;

    [ApiExclude]
    [Get("/internal")]
    public string Hidden() => "not in the document";
}

public class OpenApiDocumentTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    static JsonElement BuildDocument(Action<HttpServer> configure, Action<OpenApiOptions>? options = null)
    {
        JsonTypeInfoRegistry.Register(TestJson.Default);

        var server = new HttpServer(new HttpServerOptions { Port = 0 });
        configure(server);

        var apiOptions = new OpenApiOptions { Title = "Test API", Version = "2.1.0" };
        options?.Invoke(apiOptions);

        return JsonDocument.Parse(OpenApiDocumentBuilder.Build(server, apiOptions)).RootElement;
    }

    static JsonElement Documented(Action<OpenApiOptions>? options = null)
        => BuildDocument(app => app.MapDocumentedEndpoints(), options);

    static JsonElement Operation(JsonElement document, string path, string method)
        => document.GetProperty("paths").GetProperty(path).GetProperty(method);

    [Fact]
    public void Writes_the_version_and_info_block()
    {
        var document = Documented();

        Assert.Equal("3.0.3", document.GetProperty("openapi").GetString());
        Assert.Equal("Test API", document.GetProperty("info").GetProperty("title").GetString());
        Assert.Equal("2.1.0", document.GetProperty("info").GetProperty("version").GetString());
    }

    [Fact]
    public void Omits_servers_when_none_were_configured()
        => Assert.False(Documented().TryGetProperty("servers", out _));

    [Fact]
    public void Writes_servers_when_configured()
    {
        var document = Documented(o => o.Servers.Add("https://api.example.com"));

        Assert.Equal(
            "https://api.example.com",
            document.GetProperty("servers")[0].GetProperty("url").GetString()
        );
    }

    [Fact]
    public void Uses_the_route_template_as_the_path_without_constraints()
    {
        var paths = Documented().GetProperty("paths");

        Assert.True(paths.TryGetProperty("/api/docs/{id}", out _));
        Assert.False(paths.TryGetProperty("/api/docs/{id:int}", out _));
    }

    [Fact]
    public void Takes_the_summary_from_the_doc_comment()
        => Assert.Equal(
            "Fetches one thing.",
            Operation(Documented(), "/api/docs/{id}", "get").GetProperty("summary").GetString()
        );

    [Fact]
    public void Writes_a_stable_operation_id()
        => Assert.Equal(
            "DocumentedEndpoints_GetOne",
            Operation(Documented(), "/api/docs/{id}", "get").GetProperty("operationId").GetString()
        );

    [Fact]
    public void Uses_the_class_tag()
        => Assert.Equal(
            "documented",
            Operation(Documented(), "/api/docs/{id}", "get").GetProperty("tags")[0].GetString()
        );

    [Fact]
    public void Describes_a_route_parameter_as_a_required_path_parameter()
    {
        var parameter = Operation(Documented(), "/api/docs/{id}", "get").GetProperty("parameters")[0];

        Assert.Equal("id", parameter.GetProperty("name").GetString());
        Assert.Equal("path", parameter.GetProperty("in").GetString());
        Assert.True(parameter.GetProperty("required").GetBoolean());
        Assert.Equal("integer", parameter.GetProperty("schema").GetProperty("type").GetString());
        Assert.Equal("int32", parameter.GetProperty("schema").GetProperty("format").GetString());
    }

    [Fact]
    public void Describes_query_parameters_and_marks_optional_ones_as_not_required()
    {
        var parameters = Operation(Documented(), "/api/docs", "get").GetProperty("parameters")
            .EnumerateArray()
            .ToDictionary(p => p.GetProperty("name").GetString()!);

        Assert.Equal("query", parameters["take"].GetProperty("in").GetString());
        Assert.False(parameters["take"].GetProperty("required").GetBoolean());
        Assert.False(parameters["search"].GetProperty("required").GetBoolean());
        Assert.Equal("string", parameters["search"].GetProperty("schema").GetProperty("type").GetString());
    }

    [Fact]
    public void Describes_a_nullable_enum_parameter_by_its_underlying_type()
    {
        var parameters = Operation(Documented(), "/api/docs", "get").GetProperty("parameters")
            .EnumerateArray()
            .ToDictionary(p => p.GetProperty("name").GetString()!);

        var schema = parameters["colour"].GetProperty("schema");

        Assert.Equal("string", schema.GetProperty("type").GetString());
        Assert.Equal(
            ["Red", "Green"],
            schema.GetProperty("enum").EnumerateArray().Select(e => e.GetString()!).ToArray()
        );
    }

    [Fact]
    public void Describes_the_request_body()
    {
        var body = Operation(Documented(), "/api/docs", "post").GetProperty("requestBody");

        Assert.True(body.GetProperty("required").GetBoolean());
        Assert.Equal(
            "#/components/schemas/CreateThing",
            body.GetProperty("content").GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString()
        );
    }

    [Fact]
    public void Writes_declared_responses_including_ones_with_no_body()
    {
        var responses = Operation(Documented(), "/api/docs/{id}", "get").GetProperty("responses");

        Assert.Equal(
            "#/components/schemas/Thing",
            responses.GetProperty("200").GetProperty("content").GetProperty("application/json")
                .GetProperty("schema").GetProperty("$ref").GetString()
        );

        Assert.Equal("Nothing has that id", responses.GetProperty("404").GetProperty("description").GetString());
        Assert.False(responses.GetProperty("404").TryGetProperty("content", out _));
    }

    [Fact]
    public void Falls_back_to_the_standard_reason_phrase_for_a_response_with_no_description()
        => Assert.Equal(
            "Created",
            Operation(Documented(), "/api/docs", "post")
                .GetProperty("responses").GetProperty("201").GetProperty("description").GetString()
        );

    [Fact]
    public void Infers_a_200_from_the_return_type_when_nothing_was_declared()
    {
        var schema = Operation(Documented(), "/api/docs", "get")
            .GetProperty("responses").GetProperty("200")
            .GetProperty("content").GetProperty("application/json").GetProperty("schema");

        Assert.Equal("array", schema.GetProperty("type").GetString());
        Assert.Equal("#/components/schemas/Thing", schema.GetProperty("items").GetProperty("$ref").GetString());
    }

    [Fact]
    public void Documents_a_string_return_as_text_plain()
    {
        var response = Operation(Documented(), "/api/docs/files/{path}", "get")
            .GetProperty("responses").GetProperty("200");

        Assert.Equal("string", response.GetProperty("content").GetProperty("text/plain")
            .GetProperty("schema").GetProperty("type").GetString());
    }

    [Fact]
    public void Emits_both_paths_for_a_trailing_optional_parameter()
    {
        // OpenAPI has no way to say a path segment is optional, so the two URLs the route really
        // matches become two paths — and the shorter one does not list the parameter.
        var paths = Documented().GetProperty("paths");

        Assert.True(paths.TryGetProperty("/api/docs/optional/{page}", out var withParameter));
        Assert.True(paths.TryGetProperty("/api/docs/optional", out var without));

        Assert.Contains(
            withParameter.GetProperty("get").GetProperty("parameters").EnumerateArray(),
            p => p.GetProperty("name").GetString() == "page"
        );
        // With nothing left to describe, the parameters array is omitted rather than written empty.
        Assert.False(without.GetProperty("get").TryGetProperty("parameters", out _));
    }

    [Fact]
    public void Documents_a_catch_all_as_a_plain_path_parameter()
    {
        var parameter = Operation(Documented(), "/api/docs/files/{path}", "get").GetProperty("parameters")[0];

        Assert.Equal("path", parameter.GetProperty("name").GetString());
        Assert.Equal("path", parameter.GetProperty("in").GetString());
        Assert.True(parameter.GetProperty("required").GetBoolean());
    }

    [Fact]
    public void Omits_endpoints_marked_ApiExclude()
        => Assert.False(Documented().GetProperty("paths").TryGetProperty("/api/docs/internal", out _));

    [Fact]
    public void Builds_component_schemas_from_the_apps_json_metadata()
    {
        var schemas = Documented().GetProperty("components").GetProperty("schemas");
        var thing = schemas.GetProperty("Thing");

        Assert.Equal("object", thing.GetProperty("type").GetString());

        // camelCase because the app's JsonSerializerContext says so. Reading the schema off the
        // JSON metadata rather than off Type.GetProperties() is what keeps these in step.
        var properties = thing.GetProperty("properties");
        Assert.True(properties.TryGetProperty("id", out _));
        Assert.True(properties.TryGetProperty("name", out _));
        Assert.False(properties.TryGetProperty("Id", out _));
    }

    [Fact]
    public void Includes_raw_routes_with_path_parameters_inferred_from_the_template()
    {
        var document = BuildDocument(app =>
        {
            app.MapGet("/raw/{id:guid}", ctx => ctx.Response.WriteAsync("x"));
            app.MapPost("/raw", ctx => ctx.Response.WriteAsync("x"));
        });

        var parameter = Operation(document, "/raw/{id}", "get").GetProperty("parameters")[0];

        Assert.Equal("id", parameter.GetProperty("name").GetString());
        Assert.Equal("string", parameter.GetProperty("schema").GetProperty("type").GetString());
        Assert.Equal("uuid", parameter.GetProperty("schema").GetProperty("format").GetString());
        Assert.True(document.GetProperty("paths").GetProperty("/raw").TryGetProperty("post", out _));
    }

    [Fact]
    public void Can_omit_routes_that_were_never_described()
    {
        var document = BuildDocument(
            app =>
            {
                app.MapGet("/undescribed", ctx => ctx.Response.WriteAsync("x"));
                app.MapGet("/described", ctx => ctx.Response.WriteAsync("x")).Describe(o => o.Summary = "Described");
            },
            o => o.IncludeUndescribedRoutes = false
        );

        var paths = document.GetProperty("paths");

        Assert.True(paths.TryGetProperty("/described", out _));
        Assert.False(paths.TryGetProperty("/undescribed", out _));
    }

    [Fact]
    public void Describe_augments_a_raw_route()
    {
        var document = BuildDocument(app => app
            .MapGet("/ping", ctx => ctx.Response.WriteAsync("pong"))
            .Describe(o =>
            {
                o.Summary = "Liveness probe";
                o.Tags.Add("ops");
                o.Responses.Add(new ApiResponse { StatusCode = 200, Type = typeof(string), ContentType = "text/plain" });
            }));

        var operation = Operation(document, "/ping", "get");

        Assert.Equal("Liveness probe", operation.GetProperty("summary").GetString());
        Assert.Equal("ops", operation.GetProperty("tags")[0].GetString());
        Assert.True(operation.GetProperty("responses").GetProperty("200")
            .GetProperty("content").TryGetProperty("text/plain", out _));
    }

    [Fact]
    public void Describe_throws_before_any_route_is_mapped()
    {
        var server = new HttpServer(new HttpServerOptions { Port = 0 });
        Assert.Throws<InvalidOperationException>(() => server.Describe(o => o.Summary = "nope"));
    }

    [Fact]
    public void ConfigureOperation_can_apply_a_convention_to_every_endpoint()
    {
        var document = BuildDocument(
            app => app.MapGet("/x", ctx => ctx.Response.WriteAsync("x")),
            o => o.ConfigureOperation = (operation, _) =>
                operation.Responses.Add(new ApiResponse { StatusCode = 401 })
        );

        Assert.True(Operation(document, "/x", "get").GetProperty("responses").TryGetProperty("401", out _));
    }

    [Fact]
    public void Does_not_escape_non_ascii_text()
    {
        var server = new HttpServer(new HttpServerOptions { Port = 0 });
        server.MapGet("/x", ctx => ctx.Response.WriteAsync("x")).Describe(o => o.Summary = "Fetches a widget — quickly");

        Assert.Contains("—", OpenApiDocumentBuilder.BuildJson(server));
    }
}

public class OpenApiSecurityTests
{
    static JsonElement Build(Action<OpenApiOptions>? options = null)
    {
        JsonTypeInfoRegistry.Register(TestJson.Default);

        var server = new HttpServer(new HttpServerOptions { Port = 0 });
        server.MapSecureEndpoints();
        server.MapGet("/open", ctx => ctx.Response.WriteAsync("open"));

        var apiOptions = new OpenApiOptions();
        apiOptions.AddBearerAuthentication();
        options?.Invoke(apiOptions);

        return JsonDocument.Parse(OpenApiDocumentBuilder.Build(server, apiOptions)).RootElement;
    }

    [Fact]
    public void Publishes_the_bearer_scheme()
    {
        var scheme = Build().GetProperty("components").GetProperty("securitySchemes").GetProperty("bearer");

        Assert.Equal("http", scheme.GetProperty("type").GetString());
        Assert.Equal("bearer", scheme.GetProperty("scheme").GetString());
        Assert.Equal("JWT", scheme.GetProperty("bearerFormat").GetString());
    }

    [Fact]
    public void Marks_protected_operations_as_requiring_the_scheme()
    {
        var operation = Build().GetProperty("paths").GetProperty("/api/secure/me").GetProperty("get");

        Assert.True(operation.TryGetProperty("security", out var security));
        Assert.True(security[0].TryGetProperty("bearer", out var scopes));
        Assert.Equal(0, scopes.GetArrayLength());
    }

    [Fact]
    public void Leaves_open_operations_unsecured()
    {
        var paths = Build().GetProperty("paths");

        // Explicitly opted out with [AllowAnonymous], despite the class-level [Authorize].
        Assert.False(paths.GetProperty("/api/secure/ping").GetProperty("get").TryGetProperty("security", out _));
        Assert.False(paths.GetProperty("/open").GetProperty("get").TryGetProperty("security", out _));
    }

    [Fact]
    public void Writes_no_security_when_no_scheme_was_configured()
    {
        JsonTypeInfoRegistry.Register(TestJson.Default);

        var server = new HttpServer(new HttpServerOptions { Port = 0 });
        server.MapSecureEndpoints();

        var document = JsonDocument.Parse(OpenApiDocumentBuilder.Build(server, new OpenApiOptions())).RootElement;

        Assert.False(document.GetProperty("paths").GetProperty("/api/secure/me").GetProperty("get")
            .TryGetProperty("security", out _));
    }
}

public class OpenApiEndpointTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Serves_the_document_over_http()
    {
        await using var server = await TestServer.StartAsync(
            app =>
            {
                app.MapDocumentedEndpoints();
                app.MapOpenApi(configure: o => o.Title = "Served");
            },
            builder => builder.Services.AddSingleton<IThingService, ThingService>()
        );

        var response = await server.Client.GetAsync("/openapi.json", Token);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType?.ToString());

        var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(Token)).RootElement;

        Assert.Equal("Served", document.GetProperty("info").GetProperty("title").GetString());
        Assert.True(document.GetProperty("paths").TryGetProperty("/api/docs/{id}", out _));
    }

    [Fact]
    public async Task The_document_endpoint_does_not_document_itself()
    {
        await using var server = await TestServer.StartAsync(app =>
        {
            app.MapGet("/x", ctx => ctx.Response.WriteAsync("x"));
            app.MapOpenApi();
        });

        var document = JsonDocument.Parse(await server.Client.GetByteArrayAsync("/openapi.json", Token)).RootElement;

        Assert.False(document.GetProperty("paths").TryGetProperty("/openapi.json", out _));
        Assert.True(document.GetProperty("paths").TryGetProperty("/x", out _));
    }

    [Fact]
    public async Task Can_be_served_from_a_custom_path()
    {
        await using var server = await TestServer.StartAsync(app =>
        {
            app.MapGet("/x", ctx => ctx.Response.WriteAsync("x"));
            app.MapOpenApi("/swagger/v1/swagger.json");
        });

        var response = await server.Client.GetAsync("/swagger/v1/swagger.json", Token);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Returns_the_same_document_on_repeated_requests()
    {
        await using var server = await TestServer.StartAsync(app =>
        {
            app.MapGet("/x", ctx => ctx.Response.WriteAsync("x"));
            app.MapOpenApi();
        });

        Assert.Equal(
            await server.Client.GetStringAsync("/openapi.json", Token),
            await server.Client.GetStringAsync("/openapi.json", Token)
        );
    }
}
