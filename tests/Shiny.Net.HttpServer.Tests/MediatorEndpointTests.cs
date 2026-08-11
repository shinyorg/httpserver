using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shiny.Mediator;
using Shiny.Net.HttpServer.Mediator;
using Shiny.Net.HttpServer.OpenApi;
using Shiny.Net.HttpServer.Security;

namespace Shiny.Net.HttpServer.Tests;

// ---------------------------------------------------------------------------
// These compile against the mediator generator's real output. The handlers below
// are turned into route registrations and binders at build time, and every
// assertion goes through a real socket — so a regression in the emitted binding
// shows up as a failing HTTP response rather than a string comparison against
// expected source.
// ---------------------------------------------------------------------------

public record Gadget(int Id, string Name);

public record GetGadget(int Id) : IRequest<Gadget>;

public record ListGadgets(string? Search, int Take = 10) : IRequest<IReadOnlyList<Gadget>>;

public record CreateGadget(string Name) : IRequest<Gadget>;

public record RenameGadget : IRequest<Gadget>
{
    public int Id { get; set; }

    public string Name { get; set; } = "";
}

public record DeleteGadget(int Id) : ICommand;

public record ArchiveGadget(int Id) : ICommand;

public record WatchGadgets(int Count) : IStreamRequest<Gadget>;

public enum Sharpness { Blunt, Keen }

public record MeasureGadget(Sharpness Sharpness, int? Depth, string[] Tags) : IRequest<string>;

[MediatorHttpGroup("/api/gadgets", Tags = ["Gadgets"], Summary = "Gadget operations")]
public class GadgetHandlers :
    IRequestHandler<GetGadget, Gadget>,
    IRequestHandler<ListGadgets, IReadOnlyList<Gadget>>,
    IRequestHandler<CreateGadget, Gadget>,
    IRequestHandler<RenameGadget, Gadget>,
    IRequestHandler<MeasureGadget, string>,
    ICommandHandler<DeleteGadget>,
    ICommandHandler<ArchiveGadget>,
    IStreamRequestHandler<WatchGadgets, Gadget>
{
    public static readonly List<string> Sent = [];

    [MediatorHttpGet("/{id:int}", OperationId = "GetGadget", Summary = "One gadget")]
    public Task<Gadget> Handle(GetGadget request, IMediatorContext context, CancellationToken ct)
        => Task.FromResult(new Gadget(request.Id, "gadget-" + request.Id));

    [MediatorHttpGet("/")]
    public Task<IReadOnlyList<Gadget>> Handle(ListGadgets request, IMediatorContext context, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<Gadget>>(
            Enumerable
                .Range(1, request.Take)
                .Select(i => new Gadget(i, (request.Search ?? "gadget") + "-" + i))
                .ToList()
        );

    [MediatorHttpPost("/")]
    public Task<Gadget> Handle(CreateGadget request, IMediatorContext context, CancellationToken ct)
        => Task.FromResult(new Gadget(99, request.Name));

    // The route token has to win over whatever the body claimed.
    [MediatorHttpPut("/{id:int}")]
    public Task<Gadget> Handle(RenameGadget request, IMediatorContext context, CancellationToken ct)
        => Task.FromResult(new Gadget(request.Id, request.Name));

    [MediatorHttpGet("/measure")]
    public Task<string> Handle(MeasureGadget request, IMediatorContext context, CancellationToken ct)
        => Task.FromResult(
            $"{request.Sharpness}|{request.Depth?.ToString() ?? "null"}|{string.Join('-', request.Tags)}"
        );

    [MediatorHttpDelete("/{id:int}")]
    public Task Handle(DeleteGadget command, IMediatorContext context, CancellationToken ct)
    {
        Sent.Add("delete-" + command.Id);
        return Task.CompletedTask;
    }

    [MediatorHttpDelete("/{id:int}/archive", SuccessStatusCode = 202)]
    public Task Handle(ArchiveGadget command, IMediatorContext context, CancellationToken ct)
    {
        Sent.Add("archive-" + command.Id);
        return Task.CompletedTask;
    }

    [MediatorHttpGet("/watch/{count:int}", EventName = "gadget")]
    public async IAsyncEnumerable<Gadget> Handle(
        WatchGadgets request,
        IMediatorContext context,
        [EnumeratorCancellation] CancellationToken ct
    )
    {
        for (var i = 1; i <= request.Count; i++)
        {
            await Task.Yield();
            yield return new Gadget(i, "watched-" + i);
        }
    }
}

[MediatorHttpGroup("/api/secure", RequiresAuthorization = true)]
public class SecureGadgetHandlers :
    IRequestHandler<SecretGadget, Gadget>,
    IRequestHandler<OpenGadget, Gadget>
{
    [MediatorHttpGet("/secret")]
    public Task<Gadget> Handle(SecretGadget request, IMediatorContext context, CancellationToken ct)
        => Task.FromResult(new Gadget(1, "secret"));

    [MediatorHttpGet("/open", AllowAnonymous = true)]
    public Task<Gadget> Handle(OpenGadget request, IMediatorContext context, CancellationToken ct)
        => Task.FromResult(new Gadget(2, "open"));
}

public record SecretGadget : IRequest<Gadget>;

public record OpenGadget : IRequest<Gadget>;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Gadget))]
[JsonSerializable(typeof(IReadOnlyList<Gadget>))]
[JsonSerializable(typeof(CreateGadget))]
[JsonSerializable(typeof(RenameGadget))]
[JsonSerializable(typeof(string))]
public partial class MediatorTestJson : JsonSerializerContext;

public class MediatorEndpointTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    static Task<TestServer> StartAsync() => TestServer.StartAsync(
        app => app.MapGeneratedMediatorEndpoints(),
        builder => builder.Services
            // Shiny.Mediator's stream pipeline includes a timer-refresh middleware that takes an
            // IConfiguration. A bare container has none, so a stream request would fail on its
            // first item — after the event-stream headers had already gone out.
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddShinyMediator(_ => { })
            .AddScoped<IRequestHandler<GetGadget, Gadget>, GadgetHandlers>()
            .AddScoped<IRequestHandler<ListGadgets, IReadOnlyList<Gadget>>, GadgetHandlers>()
            .AddScoped<IRequestHandler<CreateGadget, Gadget>, GadgetHandlers>()
            .AddScoped<IRequestHandler<RenameGadget, Gadget>, GadgetHandlers>()
            .AddScoped<IRequestHandler<MeasureGadget, string>, GadgetHandlers>()
            .AddScoped<ICommandHandler<DeleteGadget>, GadgetHandlers>()
            .AddScoped<ICommandHandler<ArchiveGadget>, GadgetHandlers>()
            .AddScoped<IStreamRequestHandler<WatchGadgets, Gadget>, GadgetHandlers>()
    );

    [Fact]
    public async Task Binds_a_route_token_into_the_contract()
    {
        await using var server = await StartAsync();

        Assert.Equal(
            """{"id":7,"name":"gadget-7"}""",
            await server.Client.GetStringAsync("/api/gadgets/7", Token)
        );
    }

    /// <summary>
    /// The group prefix and the member template combine the same way <c>[Route]</c> does — the two
    /// generators share the template parser precisely so this cannot drift.
    /// </summary>
    [Fact]
    public async Task Combines_the_group_prefix_with_the_endpoint_template()
    {
        await using var server = await StartAsync();

        var gadgets = await server.Client.GetFromJsonAsync<Gadget[]>("/api/gadgets?take=2", Token);

        Assert.NotNull(gadgets);
        Assert.Equal(2, gadgets!.Length);
    }

    [Fact]
    public async Task Binds_query_members_with_their_declared_defaults()
    {
        await using var server = await StartAsync();

        var defaulted = await server.Client.GetFromJsonAsync<Gadget[]>("/api/gadgets", Token);
        Assert.Equal(10, defaulted!.Length);

        var searched = await server.Client.GetFromJsonAsync<Gadget[]>("/api/gadgets?search=widget&take=1", Token);
        Assert.Equal("widget-1", searched!.Single().Name);
    }

    [Fact]
    public async Task Binds_enums_nullables_and_arrays_from_the_query_string()
    {
        await using var server = await StartAsync();

        // A request result is written as JSON, so a string result arrives quoted.
        Assert.Equal(
            "\"Keen|3|a-b\"",
            await server.Client.GetStringAsync("/api/gadgets/measure?sharpness=Keen&depth=3&tags=a&tags=b", Token)
        );

        Assert.Equal(
            "\"Blunt|null|\"",
            await server.Client.GetStringAsync("/api/gadgets/measure?sharpness=Blunt", Token)
        );
    }

    [Fact]
    public async Task Returns_400_when_a_member_cannot_be_bound()
    {
        await using var server = await StartAsync();

        var response = await server.Client.GetAsync("/api/gadgets/measure?sharpness=NotAThing", Token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Reads_the_whole_contract_from_the_body_on_a_post()
    {
        await using var server = await StartAsync();

        var response = await server.Client.PostAsync(
            "/api/gadgets",
            new StringContent("""{"name":"from-body"}""", Encoding.UTF8, "application/json"),
            Token
        );

        response.EnsureSuccessStatusCode();
        Assert.Equal("""{"id":99,"name":"from-body"}""", await response.Content.ReadAsStringAsync(Token));
    }

    /// <summary>
    /// The URL says which resource is being addressed, so a body that disagrees does not get to win.
    /// </summary>
    [Fact]
    public async Task Applies_a_route_token_over_a_body_bound_contract()
    {
        await using var server = await StartAsync();

        var response = await server.Client.PutAsync(
            "/api/gadgets/42",
            new StringContent("""{"id":-1,"name":"renamed"}""", Encoding.UTF8, "application/json"),
            Token
        );

        response.EnsureSuccessStatusCode();
        Assert.Equal("""{"id":42,"name":"renamed"}""", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Answers_a_command_with_204_and_no_body()
    {
        await using var server = await StartAsync();
        GadgetHandlers.Sent.Clear();

        var response = await server.Client.DeleteAsync("/api/gadgets/5", Token);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Contains("delete-5", GadgetHandlers.Sent);
    }

    [Fact]
    public async Task Honours_a_custom_success_status_code()
    {
        await using var server = await StartAsync();

        var response = await server.Client.DeleteAsync("/api/gadgets/6/archive", Token);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task Streams_a_stream_request_as_server_sent_events()
    {
        await using var server = await StartAsync();

        using var response = await server.Client.GetAsync(
            "/api/gadgets/watch/3",
            HttpCompletionOption.ResponseHeadersRead,
            Token
        );

        response.EnsureSuccessStatusCode();
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        // Read frames as they arrive rather than waiting for the body to end: an event stream is
        // not obliged to close, and this test is about what comes down it.
        await using var body = await response.Content.ReadAsStreamAsync(Token);
        using var reader = new StreamReader(body);

        var seen = new StringBuilder();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        while (!seen.ToString().Contains("watched-3", StringComparison.Ordinal))
        {
            var line = await reader.ReadLineAsync(timeout.Token);
            if (line is null)
                break;

            seen.AppendLine(line);
        }

        var text = seen.ToString();

        Assert.Contains("event:gadget", text);
        Assert.Contains("""{"id":1,"name":"watched-1"}""", text);
        Assert.Contains("""{"id":3,"name":"watched-3"}""", text);
    }

    [Fact]
    public async Task Puts_the_group_summary_and_tags_into_the_openapi_document()
    {
        await using var server = await StartAsync();

        var document = OpenApiDocumentBuilder.BuildJson(server.Server);

        Assert.Contains("\"Gadgets\"", document);
        Assert.Contains("GetGadget", document);
        Assert.Contains("/api/gadgets/{id}", document);
    }
}

/// <summary>
/// Authorization is emitted as endpoint metadata rather than checked inside the handler, so a
/// denied request never reaches the mediator at all.
/// </summary>
public class MediatorAuthorizationTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    static Task<TestServer> StartAsync() => TestServer.StartAsync(
        app =>
        {
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapSecureGadgetHandlersMediatorEndpoints();
        },
        builder => builder.Services
            .AddShinyMediator(_ => { })
            .AddScoped<IRequestHandler<SecretGadget, Gadget>, SecureGadgetHandlers>()
            .AddScoped<IRequestHandler<OpenGadget, Gadget>, SecureGadgetHandlers>()
            .AddAuthentication()
            .AddBasic((Action<BasicAuthenticationOptions>)(o =>
            {
                o.Realm = "test";
                o.AddUser("u", "p");
            }))
            .Services
            .AddAuthorization()
    );

    [Fact]
    public async Task Refuses_an_unauthenticated_caller_on_a_protected_group()
    {
        await using var server = await StartAsync();

        var response = await server.Client.GetAsync("/api/secure/secret", Token);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Lets_an_endpoint_opt_out_of_its_group()
    {
        await using var server = await StartAsync();

        var response = await server.Client.GetAsync("/api/secure/open", Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
