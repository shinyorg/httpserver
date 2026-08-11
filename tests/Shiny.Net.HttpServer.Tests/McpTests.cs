using System.ComponentModel;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Shiny.Net.HttpServer.Mcp;

namespace Shiny.Net.HttpServer.Tests;

/// <summary>
/// The MCP endpoint, exercised end to end.
/// <para>
/// The interop tests drive it with the MCP SDK's own client rather than hand-rolled JSON, because
/// the only claim worth making here is "a real MCP client can talk to this", and a test that
/// invents its own idea of the protocol cannot make it. The raw HTTP tests below cover the parts
/// the client never exercises: the failure paths.
/// </para>
/// </summary>
public class McpTests
{
    // ---- Interop, through the SDK client ----

    [Fact]
    public async Task Client_Connects_And_Reads_ServerInfo()
    {
        await using var server = await StartMcpServerAsync();
        await using var client = await ConnectAsync(server);

        Assert.Equal("shiny-test", client.ServerInfo.Name);
        Assert.NotNull(client.ServerCapabilities.Tools);
    }

    [Fact]
    public async Task Client_Lists_Tools()
    {
        await using var server = await StartMcpServerAsync();
        await using var client = await ConnectAsync(server);

        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(tools, t => t.Name == "echo");
        Assert.Contains(tools, t => t.Name == "add");
    }

    [Fact]
    public async Task Client_Calls_A_Tool()
    {
        await using var server = await StartMcpServerAsync();
        await using var client = await ConnectAsync(server);

        var result = await client.CallToolAsync(
            "echo",
            new Dictionary<string, object?> { ["text"] = "hello" },
            cancellationToken: TestContext.Current.CancellationToken
        );

        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        Assert.Equal("echo: hello", text.Text);
    }

    [Fact]
    public async Task Tool_Resolves_Services_From_The_Container()
    {
        await using var server = await StartMcpServerAsync();
        await using var client = await ConnectAsync(server);

        // The tool takes an IGreeter, which only the server's container can supply. That it runs at
        // all is the assertion: it proves the MCP server was created with the HTTP server's
        // provider rather than an empty one.
        var result = await client.CallToolAsync(
            "greet",
            new Dictionary<string, object?> { ["name"] = "world" },
            cancellationToken: TestContext.Current.CancellationToken
        );

        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        Assert.Equal("Hello, world", text.Text);
    }

    [Fact]
    public async Task Clients_That_Skip_The_Handshake_Cost_No_Session()
    {
        await using var server = await StartMcpServerAsync();

        await using var first = await ConnectAsync(server);
        await using var second = await ConnectAsync(server);

        var firstTools = await first.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        var secondTools = await second.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(firstTools.Count, secondTools.Count);

        // The SDK client connects through server/discover and never initializes, so every request
        // is answered on its own. Two connected clients, nothing held on the server.
        Assert.Equal(0, SessionCount(server));
    }

    [Fact]
    public async Task Initializing_Opens_A_Tracked_Session()
    {
        await using var server = await StartMcpServerAsync();

        var first = await InitializeAsync(server);
        var second = await InitializeAsync(server);

        Assert.NotEqual(first, second);
        Assert.Equal(2, SessionCount(server));
    }

    // ---- The failure paths, over raw HTTP ----

    [Fact]
    public async Task Initialize_Returns_A_Session_Id()
    {
        await using var server = await StartMcpServerAsync();

        using var response = await server.Client.SendAsync(
            Initialize(),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.TryGetValues(McpHttpHandler.SessionIdHeader, out var ids));
        Assert.NotEmpty(Assert.Single(ids!));

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("\"protocolVersion\"", body);
    }

    [Fact]
    public async Task Notification_Only_Post_Is_Accepted_With_No_Body()
    {
        await using var server = await StartMcpServerAsync();
        var sessionId = await InitializeAsync(server);

        using var response = await server.Client.SendAsync(
            Rpc("""{"jsonrpc":"2.0","method":"notifications/initialized"}""", sessionId),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(0, response.Content.Headers.ContentLength);
    }

    [Fact]
    public async Task Post_Without_A_Session_Id_Is_Served_Statelessly()
    {
        await using var server = await StartMcpServerAsync();

        using var response = await server.Client.SendAsync(
            Rpc("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}"""),
            TestContext.Current.CancellationToken
        );

        // No handshake, no session, still answered — this is the flow every SDK 2.x client uses,
        // and rejecting it (which the spec's older session rules read as permitting) locks them
        // all out.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"echo\"", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, SessionCount(server));
    }

    [Fact]
    public async Task Unknown_Session_Is_A_404()
    {
        await using var server = await StartMcpServerAsync();

        using var response = await server.Client.SendAsync(
            Rpc("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", "not-a-session"),
            TestContext.Current.CancellationToken
        );

        // 404 is what tells a client to start over with a fresh initialize rather than give up.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_Ends_The_Session()
    {
        await using var server = await StartMcpServerAsync();
        var sessionId = await InitializeAsync(server);

        using var request = new HttpRequestMessage(HttpMethod.Delete, "/mcp");
        request.Headers.Add(McpHttpHandler.SessionIdHeader, sessionId);

        using var deleted = await server.Client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        using var reused = await server.Client.SendAsync(
            Rpc("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", sessionId),
            TestContext.Current.CancellationToken
        );
        Assert.Equal(HttpStatusCode.NotFound, reused.StatusCode);
    }

    [Fact]
    public async Task Unparseable_Body_Is_A_Parse_Error()
    {
        await using var server = await StartMcpServerAsync();

        using var response = await server.Client.SendAsync(
            Rpc("not json at all"),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("-32700", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Accept_Must_Cover_Both_Media_Types()
    {
        await using var server = await StartMcpServerAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.ParseAdd("application/json");

        using var response = await server.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotAcceptable, response.StatusCode);
    }

    [Fact]
    public async Task Session_Limit_Is_Enforced()
    {
        await using var server = await StartMcpServerAsync(o => o.MaxSessions = 1);

        using var first = await server.Client.SendAsync(Initialize(), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var second = await server.Client.SendAsync(Initialize(), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
    }

    // ---- Origin policy ----

    [Fact]
    public async Task Unnamed_Browser_Origin_Is_Refused()
    {
        await using var server = await StartMcpServerAsync();

        var request = Initialize();
        request.Headers.Add("Origin", "https://evil.example");

        using var response = await server.Client.SendAsync(request, TestContext.Current.CancellationToken);

        // The DNS-rebinding case: a page the user happened to visit, talking to a server on their
        // own machine. Nothing else about the request looks wrong.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Named_Origin_Is_Allowed_And_Echoed()
    {
        await using var server = await StartMcpServerAsync(o => o.AllowedOrigins.Add("https://inspector.example"));

        var request = Initialize();
        request.Headers.Add("Origin", "https://inspector.example");

        using var response = await server.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("https://inspector.example", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.Contains(
            McpHttpHandler.SessionIdHeader,
            Assert.Single(response.Headers.GetValues("Access-Control-Expose-Headers"))
        );
    }

    [Fact]
    public async Task Preflight_Advertises_The_Protocol_Headers()
    {
        await using var server = await StartMcpServerAsync(o => o.AllowedOrigins.Add("https://inspector.example"));

        using var request = new HttpRequestMessage(HttpMethod.Options, "/mcp");
        request.Headers.Add("Origin", "https://inspector.example");

        using var response = await server.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var allowed = Assert.Single(response.Headers.GetValues("Access-Control-Allow-Headers"));
        Assert.Contains(McpHttpHandler.SessionIdHeader, allowed);
        Assert.Contains(McpHttpHandler.ProtocolVersionHeader, allowed);
    }

    // ---- The server-to-client stream ----

    [Fact]
    public async Task Get_Opens_The_Stream()
    {
        await using var server = await StartMcpServerAsync();
        var sessionId = await InitializeAsync(server);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/mcp");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.Add(McpHttpHandler.SessionIdHeader, sessionId);

        // Headers only: the body never ends on its own, which is the point of the stream.
        using var response = await server.Client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Get_Is_405_When_The_Stream_Is_Off()
    {
        await using var server = await StartMcpServerAsync(o => o.AllowServerToClientStream = false);
        var sessionId = await InitializeAsync(server);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/mcp");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.Add(McpHttpHandler.SessionIdHeader, sessionId);

        using var response = await server.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Contains("POST", response.Content.Headers.Allow);
    }

    // ---- Wiring ----

    [Fact]
    public async Task MapMcp_Without_Registration_Says_What_To_Add()
    {
        var builder = HttpServer.CreateBuilder();
        builder.Options.Port = 0;

        await using var server = builder.Build();

        var ex = Assert.Throws<InvalidOperationException>(() => server.MapMcp());
        Assert.Contains("WithHttpTransport", ex.Message);
    }

    // ---- Trimming: the JSON context a trimmed app needs ----
    //
    // These stand in for what only happens once the app is trimmed. The test host is not trimmed and
    // its reflection resolver would describe any type asked of it, so a tool that fails on a phone
    // passes here. Handing the MCP server a JsonSerializerOptions whose only resolver is a
    // source-generated context reproduces the same absence on purpose: a type the context does not
    // cover cannot be described, exactly as if the trimmer had taken the reflection path away.

    [Fact]
    public async Task MapMcp_Rejects_A_Tool_Whose_Types_The_Json_Context_Omits()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => StartPocoServerAsync(EmptyToolJson.Default.Options)
        );

        // Names the type that could not be described, and what to do about it.
        Assert.Contains(nameof(PocoQuery), ex.Message);
        Assert.Contains("JsonSerializerContext", ex.Message);
        Assert.IsType<NotSupportedException>(ex.InnerException);
    }

    [Fact]
    public async Task A_Tool_Covered_By_The_Json_Context_Round_Trips()
    {
        await using var server = await StartPocoServerAsync(PocoToolJson.Default.Options);
        await using var client = await ConnectAsync(server);

        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(tools, t => t.Name == "query");

        var result = await client.CallToolAsync(
            "query",
            new Dictionary<string, object?>
            {
                ["query"] = JsonSerializer.SerializeToElement(new PocoQuery("kitchen", 2), PocoToolJson.Default.PocoQuery)
            },
            cancellationToken: TestContext.Current.CancellationToken
        );

        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        Assert.Contains("kitchen", text.Text);
    }

    [Fact]
    public async Task Primitive_Tools_Need_No_Json_Context()
    {
        // The whole reason the check is worth having is that it stays out of the way of the common
        // case. Every tool on McpTestTools takes and returns primitives, so the default options
        // describe them all and MapMcp() is silent.
        await using var server = await StartMcpServerAsync();
        await using var client = await ConnectAsync(server);

        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(tools, t => t.Name == "echo");
    }

    [Fact]
    public async Task An_Unrelated_Failure_Is_Not_Dressed_Up_As_A_Json_Problem()
    {
        // The guard only claims the one fault it can recognise. A server with no transport
        // registered still gets told that, rather than advice about JsonSerializerContext.
        var builder = HttpServer.CreateBuilder();
        builder.Options.Port = 0;
        builder.Services
            .AddMcpServer(o => o.ServerInfo = new Implementation { Name = "shiny-test", Version = "1.0.0" })
            .WithTools<PocoTools>(EmptyToolJson.Default.Options);

        await using var server = builder.Build();

        var ex = Assert.Throws<InvalidOperationException>(() => server.MapMcp());
        Assert.Contains("WithHttpTransport", ex.Message);
        Assert.DoesNotContain("JsonSerializerContext", ex.Message);
    }

    // ---- Helpers ----

    static Task<TestServer> StartPocoServerAsync(JsonSerializerOptions toolJson)
        => TestServer.StartAsync(
            app => app.MapMcp(),
            builder => builder.Services
                .AddMcpServer(o => o.ServerInfo = new Implementation { Name = "shiny-test", Version = "1.0.0" })
                .WithTools<PocoTools>(toolJson)
                .WithHttpTransport()
        );

    static Task<TestServer> StartMcpServerAsync(Action<McpHttpOptions>? configure = null)
        => TestServer.StartAsync(
            app => app.MapMcp(),
            builder =>
            {
                builder.Services.AddSingleton<IGreeter, McpGreeter>();
                builder.Services
                    .AddMcpServer(o => o.ServerInfo = new Implementation { Name = "shiny-test", Version = "1.0.0" })
                    .WithTools<McpTestTools>()
                    .WithHttpTransport(configure);
            }
        );

    static async Task<McpClient> ConnectAsync(TestServer server)
    {
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri($"http://127.0.0.1:{server.Port}/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp
            }
        );

        return await McpClient.CreateAsync(transport, cancellationToken: TestContext.Current.CancellationToken);
    }

    static int SessionCount(TestServer server)
        => server.Server.Services!.GetRequiredService<McpHttpSessionManager>().Count;

    static HttpRequestMessage Initialize() => Rpc(
        """
        {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"raw","version":"1.0.0"}}}
        """
    );

    static async Task<string> InitializeAsync(TestServer server)
    {
        using var response = await server.Client.SendAsync(Initialize(), TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        return response.Headers.GetValues(McpHttpHandler.SessionIdHeader).Single();
    }

    static HttpRequestMessage Rpc(string json, string? sessionId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        request.Headers.Accept.ParseAdd("application/json, text/event-stream");

        if (sessionId is not null)
            request.Headers.Add(McpHttpHandler.SessionIdHeader, sessionId);

        return request;
    }
}

// Not static: WithTools<T>() takes a type argument, and a static class cannot be one.
[McpServerToolType]
public sealed class McpTestTools
{
    [McpServerTool(Name = "echo"), Description("Echoes the text back.")]
    public static string Echo(string text) => $"echo: {text}";

    [McpServerTool(Name = "add"), Description("Adds two numbers.")]
    public static int Add(int a, int b) => a + b;

    [McpServerTool(Name = "greet"), Description("Greets someone using an injected service.")]
    public static string Greet(IGreeter greeter, string name) => greeter.Greet(name);
}

/// <summary>A tool that trades in something other than primitives, which is what needs describing.</summary>
public sealed record PocoQuery(string Sensor, int Take);

public sealed record PocoReading(string Sensor, double Value);

[McpServerToolType]
public sealed class PocoTools
{
    [McpServerTool(Name = "query"), Description("Queries readings.")]
    public static IReadOnlyList<PocoReading> Query(PocoQuery query)
        => [.. Enumerable.Range(0, query.Take).Select(i => new PocoReading(query.Sensor, 20 + i))];
}

/// <summary>Covers the tool's types, which is what a trimmed app has to supply.</summary>
[JsonSerializable(typeof(PocoQuery))]
[JsonSerializable(typeof(PocoReading))]
[JsonSerializable(typeof(IReadOnlyList<PocoReading>))]
public partial class PocoToolJson : JsonSerializerContext;

/// <summary>Covers nothing, standing in for the types a trimmer removed.</summary>
[JsonSerializable(typeof(string))]
public partial class EmptyToolJson : JsonSerializerContext;

public interface IGreeter
{
    string Greet(string name);
}

sealed class McpGreeter : IGreeter
{
    public string Greet(string name) => $"Hello, {name}";
}
