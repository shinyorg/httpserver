using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace Shiny.Net.HttpServer.Tests;

// ---------------------------------------------------------------------------
// These tests compile against the generator's real output. The endpoint class
// below is generated into route registrations and binders at build time, and
// everything asserted here goes through a real socket — so a regression in the
// emitted code shows up as a failing HTTP response, not a subtle mismatch in a
// string comparison against expected source text.
// ---------------------------------------------------------------------------

[Route("/api/things")]
public class ThingEndpoints(IThingService things)
{
    [Get("/{id:int}")]
    public IActionResult GetById(int id)
        => things.Find(id) is { } thing ? new OkObjectResult(thing) : new NotFoundResult();

    [Get("/optional/{id:int?}")]
    public string Optional(int id = -1) => $"id={id}";

    [Get("/query")]
    public string Query(string name, int count = 3, bool loud = false)
        => loud ? $"{name}x{count}".ToUpperInvariant() : $"{name}x{count}";

    [Get("/nullable")]
    public string Nullable(int? maybe, string? text) => $"maybe={maybe?.ToString() ?? "null"};text={text ?? "null"}";

    [Get("/enum")]
    public string Enum(Colour colour) => colour.ToString();

    [Get("/array")]
    public string Array(int[] ids, string[] tags) => $"{string.Join('-', ids)}|{string.Join('-', tags)}";

    [Get("/header")]
    public string Header([FromHeader(Name = "X-Trace")] string? trace) => trace ?? "(none)";

    [Get("/ambient")]
    public async Task<string> Ambient(HttpContext context, HttpRequest request, CancellationToken cancellationToken)
    {
        await Task.Yield();
        return $"{context.Request.Method} {request.Path} cancellable={cancellationToken.CanBeCanceled}";
    }

    [Get("/catchall/{*rest}")]
    public string CatchAll(string rest) => rest;

    [Post]
    public async Task<IActionResult> Create(CreateThing request, CancellationToken cancellationToken)
    {
        await Task.Yield();
        return new CreatedResult("/api/things/1", things.Add(request.Name));
    }

    [Put("/{id:int}")]
    public IActionResult Replace(int id, CreateThing request) => new OkObjectResult(new Thing(id, request.Name));

    [Delete("/{id:int}")]
    public IActionResult Remove(int id) => new NoContentResult();

    [Get("/void")]
    public void WritesNothing()
    {
    }

    [Get("/direct")]
    public async Task WritesItself(HttpResponse response)
    {
        response.StatusCode = StatusCodes.Status202Accepted;
        await response.WriteAsync("written by hand");
    }

    [Get("/json")]
    public Thing ReturnsAPoco() => new(7, "poco");

    [Get("/service")]
    public string InjectedPerCall([FromServices] IThingService injected) => injected.Name;

    [NonEndpoint]
    [Get("/never")]
    public string NotAnEndpoint() => "unreachable";
}

public enum Colour
{
    Red,
    Green
}

public record Thing(int Id, string Name);

public record CreateThing(string Name);

public interface IThingService
{
    string Name { get; }
    Thing? Find(int id);
    Thing Add(string name);
}

sealed class ThingService : IThingService
{
    readonly Dictionary<int, Thing> things = new() { [1] = new Thing(1, "first") };

    public string Name => "thing-service";

    public Thing? Find(int id) => this.things.GetValueOrDefault(id);

    public Thing Add(string name)
    {
        var thing = new Thing(this.things.Count + 1, name);
        this.things[thing.Id] = thing;
        return thing;
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Thing))]
[JsonSerializable(typeof(IReadOnlyList<Thing>))]
[JsonSerializable(typeof(CreateThing))]
public partial class TestJson : JsonSerializerContext;

public class GeneratedEndpointTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    static Task<TestServer> StartAsync() => TestServer.StartAsync(
        app => app.MapThingEndpoints(),
        builder => builder.Services.AddSingleton<IThingService, ThingService>()
    );

    [Fact]
    public async Task Registers_the_class_prefix_and_method_template()
    {
        await using var server = await StartAsync();
        Assert.Equal("""{"id":1,"name":"first"}""", await server.Client.GetStringAsync("/api/things/1", Token));
    }

    [Fact]
    public async Task Returns_the_result_the_method_returned()
    {
        await using var server = await StartAsync();

        var response = await server.Client.GetAsync("/api/things/99", Token);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Does_not_route_a_value_that_fails_the_constraint()
    {
        await using var server = await StartAsync();

        // {id:int} simply does not match, so this is a 404 from routing — not a 400 from binding.
        var response = await server.Client.GetAsync("/api/things/abc", Token);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Binds_query_parameters_with_defaults()
    {
        await using var server = await StartAsync();

        Assert.Equal("bobx3", await server.Client.GetStringAsync("/api/things/query?name=bob", Token));
        Assert.Equal("bobx9", await server.Client.GetStringAsync("/api/things/query?name=bob&count=9", Token));
        Assert.Equal("BOBX2", await server.Client.GetStringAsync("/api/things/query?name=bob&count=2&loud=true", Token));
    }

    [Fact]
    public async Task Returns_400_for_a_missing_required_query_parameter()
    {
        await using var server = await StartAsync();

        var response = await server.Client.GetAsync("/api/things/query", Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("name", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Returns_400_for_an_unparseable_query_parameter()
    {
        await using var server = await StartAsync();

        var response = await server.Client.GetAsync("/api/things/query?name=bob&count=lots", Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("count", await response.Content.ReadAsStringAsync(Token));
        Assert.Contains("int", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Treats_a_nullable_parameter_as_optional()
    {
        await using var server = await StartAsync();

        Assert.Equal("maybe=null;text=null", await server.Client.GetStringAsync("/api/things/nullable", Token));
        Assert.Equal("maybe=4;text=hi", await server.Client.GetStringAsync("/api/things/nullable?maybe=4&text=hi", Token));
    }

    [Fact]
    public async Task Still_rejects_a_present_but_invalid_nullable_parameter()
    {
        await using var server = await StartAsync();

        var response = await server.Client.GetAsync("/api/things/nullable?maybe=nope", Token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Binds_enums_case_insensitively()
    {
        await using var server = await StartAsync();

        Assert.Equal("Green", await server.Client.GetStringAsync("/api/things/enum?colour=green", Token));
        Assert.Equal(HttpStatusCode.BadRequest, (await server.Client.GetAsync("/api/things/enum?colour=purple", Token)).StatusCode);
    }

    [Fact]
    public async Task Binds_repeated_query_values_into_arrays()
    {
        await using var server = await StartAsync();

        Assert.Equal(
            "1-2-3|a-b",
            await server.Client.GetStringAsync("/api/things/array?ids=1&ids=2&ids=3&tags=a&tags=b", Token)
        );
    }

    [Fact]
    public async Task Binds_headers()
    {
        await using var server = await StartAsync();

        Assert.Equal("(none)", await server.Client.GetStringAsync("/api/things/header", Token));

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/things/header");
        request.Headers.Add("X-Trace", "abc");
        Assert.Equal("abc", await (await server.Client.SendAsync(request, Token)).Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Supplies_ambient_context_types()
    {
        await using var server = await StartAsync();

        Assert.Equal(
            "GET /api/things/ambient cancellable=True",
            await server.Client.GetStringAsync("/api/things/ambient", Token)
        );
    }

    [Fact]
    public async Task Binds_a_catch_all_route_token()
    {
        await using var server = await StartAsync();
        Assert.Equal("a/b/c.txt", await server.Client.GetStringAsync("/api/things/catchall/a/b/c.txt", Token));
    }

    [Fact]
    public async Task Binds_a_json_body_on_a_verb_that_carries_one()
    {
        await using var server = await StartAsync();

        var response = await server.Client.PostAsJsonAsync("/api/things", new CreateThing("posted"), Token);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("/api/things/1", response.Headers.Location?.ToString());
        Assert.Contains("posted", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Binds_a_route_value_and_a_body_together()
    {
        await using var server = await StartAsync();

        var response = await server.Client.PutAsJsonAsync("/api/things/5", new CreateThing("replaced"), Token);

        Assert.Equal("""{"id":5,"name":"replaced"}""", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Returns_400_for_a_malformed_body()
    {
        await using var server = await StartAsync();

        var content = new StringContent("{not json", Encoding.UTF8, "application/json");
        var response = await server.Client.PostAsync("/api/things", content, Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Returns_400_for_a_missing_body()
    {
        await using var server = await StartAsync();

        var response = await server.Client.PostAsync("/api/things", new StringContent(""), Token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Completes_a_void_endpoint_with_an_empty_200()
    {
        await using var server = await StartAsync();

        var response = await server.Client.GetAsync("/api/things/void", Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, response.Content.Headers.ContentLength);
    }

    [Fact]
    public async Task Leaves_a_handler_that_wrote_the_response_itself_alone()
    {
        await using var server = await StartAsync();

        var response = await server.Client.GetAsync("/api/things/direct", Token);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("written by hand", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Serializes_a_returned_value_as_json()
    {
        await using var server = await StartAsync();

        var response = await server.Client.GetAsync("/api/things/json", Token);

        Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Equal("""{"id":7,"name":"poco"}""", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Injects_services_into_parameters_and_constructors()
    {
        await using var server = await StartAsync();
        Assert.Equal("thing-service", await server.Client.GetStringAsync("/api/things/service", Token));
    }

    [Fact]
    public async Task Skips_methods_marked_NonEndpoint()
    {
        await using var server = await StartAsync();

        var response = await server.Client.GetAsync("/api/things/never", Token);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Matches_an_optional_route_token()
    {
        await using var server = await StartAsync();

        Assert.Equal("id=8", await server.Client.GetStringAsync("/api/things/optional/8", Token));
        Assert.Equal("id=-1", await server.Client.GetStringAsync("/api/things/optional", Token));
    }

    [Fact]
    public async Task Resolves_the_endpoint_class_from_the_container_when_it_was_registered()
    {
        var constructed = 0;

        await using var server = await TestServer.StartAsync(
            app => app.MapThingEndpoints(),
            builder =>
            {
                builder.Services.AddSingleton<IThingService, ThingService>();
                builder.Services.AddScoped(sp =>
                {
                    Interlocked.Increment(ref constructed);
                    return new ThingEndpoints(sp.GetRequiredService<IThingService>());
                });
            }
        );

        await server.Client.GetStringAsync("/api/things/1", Token);
        Assert.Equal(1, constructed);
    }
}

public class JsonTypeInfoRegistryTests
{
    [Fact]
    public void Resolves_a_registered_type()
    {
        JsonTypeInfoRegistry.Register(TestJson.Default);
        Assert.True(JsonTypeInfoRegistry.TryGet<Thing>(out _));
    }

    [Fact]
    public void Reports_an_unregistered_type_with_an_actionable_message()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => JsonTypeInfoRegistry.GetRequired<Uri>());

        Assert.Contains("JsonSerializable", ex.Message);
        Assert.Contains(nameof(Uri), ex.Message);
    }

    [Fact]
    public void Registering_the_same_context_twice_is_a_no_op()
    {
        JsonTypeInfoRegistry.Register(TestJson.Default);
        JsonTypeInfoRegistry.Register(TestJson.Default);

        Assert.True(JsonTypeInfoRegistry.TryGet(typeof(Thing), out _));
    }
}
