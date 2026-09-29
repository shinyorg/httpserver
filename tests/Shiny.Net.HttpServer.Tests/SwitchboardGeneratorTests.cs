using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Shiny.Net.HttpServer.SourceGenerators;

namespace Shiny.Net.HttpServer.Tests;

/// <summary>
/// The switchboard generator's refusals. Everything it accepts is exercised end to end by
/// <see cref="SwitchboardTests"/>; these are the shapes that must fail the build instead.
/// </summary>
public class SwitchboardGeneratorTests
{
    static ImmutableArray<Diagnostic> Run(string source, out string generated)
        => Run(source, new SwitchboardGenerator(), out generated, out _);

    static ImmutableArray<Diagnostic> Run(string source, IIncrementalGenerator generator, out string generated, out ImmutableArray<SyntaxTree> trees)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path))
            .Append(MetadataReference.CreateFromFile(typeof(HttpServer).Assembly.Location))
            .Append(MetadataReference.CreateFromFile(typeof(Switchboard.Switchboard).Assembly.Location))
            .Append(MetadataReference.CreateFromFile(typeof(Switchboard.Client.SwitchboardLine).Assembly.Location));

        var compilation = CSharpCompilation.Create(
            "Fixture",
            [CSharpSyntaxTree.ParseText(source)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable)
        );

        var driver = CSharpGeneratorDriver.Create(generator)
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);

        trees = driver.GetRunResult().GeneratedTrees;
        generated = string.Join("\n", trees.Select(t => t.ToString()));

        // The generated code has to compile too — a dispatcher that does not build is a bug here,
        // not in the app.
        return [.. diagnostics, .. output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)];
    }

    static string[] Ids(string source) => [.. Run(source, out _).Select(d => d.Id).Distinct().Order()];

    const string Usings = """
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using Shiny.Net.HttpServer;
        using Shiny.Net.HttpServer.Switchboard;

        """;

    [Fact]
    public void A_valid_switchboard_compiles_with_no_diagnostics()
    {
        var diagnostics = Run(Usings + """
            public interface IStore;

            public class Board(IStore store, IStore? optional = null) : Switchboard
            {
                public int Add(int a, int b = 2) => a + b;
                public Task Nothing(CancellationToken ct) => Task.CompletedTask;
                public async IAsyncEnumerable<string> Stream(int n) { await Task.Yield(); yield return "x"; }
                [LineMethod("renamed")] public string Named() => "";
                [NotALineMethod] public void Hidden<T>() { }
                public string Services([FromServices] IStore s, CallerContext caller) => caller.LineId;
                public override Task OnConnectedAsync() => Task.CompletedTask;
                public override string ToString() => "";
            }

            public abstract class NotMapped : Switchboard;
            """, out var generated);

        Assert.Empty(diagnostics);
        Assert.Contains("\"Add\", 1, 2", generated);
        Assert.Contains("\"renamed\"", generated);
        Assert.DoesNotContain("\"Hidden\"", generated);
        Assert.DoesNotContain("\"OnConnectedAsync\"", generated);
        Assert.DoesNotContain("\"ToString\"", generated);
        Assert.DoesNotContain("NotMapped", generated);
    }

    [Fact]
    public void Overloads_collide_on_the_wire() => Assert.Equal(["SWB003"], Ids(Usings + """
        public class Board : Switchboard
        {
            public void Send(string text) { }
            public void Send(int number) { }
        }
        """));

    [Fact]
    public void A_generic_switchboard_is_refused() => Assert.Equal(["SWB001"], Ids(Usings + """
        public class Board<T> : Switchboard { public void Go() { } }
        """));

    [Fact]
    public void A_private_nested_switchboard_is_refused() => Assert.Equal(["SWB001"], Ids(Usings + """
        public class Outer { private class Board : Switchboard { public void Go() { } } }
        """));

    [Fact]
    public void A_generic_method_is_refused() => Assert.Equal(["SWB006"], Ids(Usings + """
        public class Board : Switchboard { public T Echo<T>(T value) => value; }
        """));

    [Fact]
    public void Client_streaming_and_ref_parameters_are_refused() => Assert.Equal(["SWB004"], Ids(Usings + """
        public class Board : Switchboard
        {
            public Task Upload(IAsyncEnumerable<int> items) => Task.CompletedTask;
            public void Swap(ref int value) { }
        }
        """));

    [Fact]
    public void A_task_of_a_stream_is_refused() => Assert.Equal(["SWB005"], Ids(Usings + """
        public class Board : Switchboard
        {
            public Task<IAsyncEnumerable<int>> Items() => Task.FromResult<IAsyncEnumerable<int>>(null!);
        }
        """));

    [Fact]
    public void AllowAnonymous_under_a_class_requirement_warns() => Assert.Equal(["SWB007"], Ids(Usings + """
        [Authorize]
        public class Board : Switchboard
        {
            [AllowAnonymous] public string Ping() => "pong";
        }
        """));

    // ---- typed switchboards ----

    [Fact]
    public void A_typed_switchboard_gets_a_client_proxy()
    {
        var diagnostics = Run(Usings + """
            public interface IClient
            {
                Task Notify(string text, CancellationToken ct);
                Task<int> Pick(int max);
            }

            public class Board : Switchboard<IClient> { public Task Go() => Clients.All.Notify("x", default); }
            """, out var generated);

        Assert.Empty(diagnostics);
        Assert.Contains("SwitchboardClientProxies.Register<global::IClient>", generated);
        Assert.Contains("proxy.SendCoreAsync(\"Notify\", new object[] { text }, ct)", generated);
        Assert.Contains("SwitchboardClientResults.InvokeAsync<int>(proxy, \"Pick\"", generated);
    }

    [Fact]
    public void A_client_interface_with_a_property_or_a_non_task_method_is_refused() => Assert.Equal(["SWB008"], Ids(Usings + """
        public interface IClient
        {
            string Name { get; }
            void Fire(string text);
        }

        public class Board : Switchboard<IClient>;
        """));

    [Fact]
    public void A_contract_method_that_clients_cannot_call_warns() => Assert.Equal(["SWB009"], Ids(Usings + """
        public interface IContract
        {
            Task One();
            Task Two();
            Task Three();
        }

        public class Board : Switchboard, IContract
        {
            Task IContract.One() => Task.CompletedTask;
            [NotALineMethod] public Task Two() => Task.CompletedTask;
            [LineMethod("third")] public Task Three() => Task.CompletedTask;
        }
        """));

    // ---- contract export ----

    [Fact]
    public void Exports_a_contract_that_compiles_with_nothing_but_the_bcl()
    {
        var diagnostics = Run(Usings + """
            using System.Text.Json.Serialization;

            namespace Chat.Server
            {
                public enum Mood { Happy = 1, Sad = 2 }
                public sealed record ChatMessage(string Room, string User, [property: JsonPropertyName("body")] string Text, Mood Mood);
                public class RoomInfo { public string Name { get; set; } = ""; public int Members { get; init; } }
                public interface IChatClient
                {
                    Task MessageReceived(ChatMessage message);
                    Task<bool> ConfirmDelete(string room);
                }

                [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
                [JsonSerializable(typeof(ChatMessage))]
                public partial class ServerJson : JsonSerializerContext;

                [ExportContract(Namespace = "Chat.Contracts")]
                public class ChatBoard(object? unused = null) : Switchboard<IChatClient>
                {
                    public Task Send(string room, string text, [FromServices] object services, CallerContext caller) => Task.CompletedTask;
                    public IReadOnlyList<RoomInfo> Rooms() => [];
                    public async IAsyncEnumerable<ChatMessage> History(string room, int take = 20, CancellationToken ct = default) { await Task.Yield(); yield break; }
                    [LineMethod("delete")] public void DeleteRoom(string room) { }
                }
            }
            """, new SwitchboardGenerator(), out _, out var trees);

        // The server's own JSON context has no generated half in this driver, so its errors are expected.
        Assert.DoesNotContain(diagnostics, d => d.Id.StartsWith("SWB", StringComparison.Ordinal));

        var contract = trees.Single(t => t.FilePath.EndsWith("ChatBoard.Contract.g.cs", StringComparison.Ordinal)).ToString();
        Assert.StartsWith("// <auto-generated/>", contract);
        Assert.Contains("#if SHINY_SWITCHBOARD_CONTRACT_EXPORT", contract);

        // What the build target does on the way out.
        var exported = contract
            .Replace("#if SHINY_SWITCHBOARD_CONTRACT_EXPORT", "")
            .Replace("#endif // SHINY_SWITCHBOARD_CONTRACT_EXPORT", "");

        Assert.Contains("public interface IChatBoard", exported);
        Assert.Contains("global::System.Threading.Tasks.Task Send(string room, string text, global::System.Threading.CancellationToken cancellationToken = default);", exported);
        Assert.Contains("Task<global::System.Collections.Generic.IReadOnlyList<global::Chat.Contracts.RoomInfo>> Rooms(", exported);
        Assert.Contains("IAsyncEnumerable<global::Chat.Contracts.ChatMessage> History(string room, int take = (int)(20), global::System.Threading.CancellationToken cancellationToken = default);", exported);
        Assert.Contains(" delete(string room,", exported);
        Assert.Contains("public interface IChatClient", exported);
        Assert.Contains("public sealed record ChatMessage(string Room, string User, [property: global::System.Text.Json.Serialization.JsonPropertyNameAttribute(\"body\")] string Text, global::Chat.Contracts.Mood Mood);", exported);
        Assert.Contains("public enum Mood", exported);
        Assert.Contains("public int Members { get; init; }", exported);
        Assert.Contains("JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)", exported.Replace("global::", ""));
        Assert.Contains("[global::System.Text.Json.Serialization.JsonSerializable(typeof(global::Chat.Contracts.ChatMessage))]", exported);
        Assert.Contains("public partial class ChatBoardContractJsonContext", exported);

        // Compiled alone against the BCL — no server, no switchboard. The JSON context needs the
        // System.Text.Json generator, which this driver does not run, so it is left out here.
        var withoutContext = string.Join("\n", exported.Split('\n').Where(l =>
            !l.Contains("ChatBoardContractJsonContext")
            && !l.Contains("JsonSerializable(")
            && !l.Contains("JsonSourceGenerationOptions(")));
        var bcl = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));

        var client = CSharpCompilation.Create(
            "Client",
            [CSharpSyntaxTree.ParseText(withoutContext)],
            bcl,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );

        Assert.Empty(client.GetDiagnostics().Where(d => d.Severity >= DiagnosticSeverity.Warning));
    }

    [Fact]
    public void A_type_that_cannot_be_exported_warns() => Assert.Equal(["SWB010"], Ids(Usings + """
        public class Base { }
        public class Derived : Base { public int X { get; set; } }

        [ExportContract]
        public class Board : Switchboard { public Derived Get() => new(); }
        """));

    // ---- the client generator ----

    static ImmutableArray<Diagnostic> RunClient(string source, out string generated)
        => Run(source, new SwitchboardClientGenerator(), out generated, out _);

    const string ClientUsings = """
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using Shiny.Net.HttpServer.Switchboard.Client;

        public interface IBoard
        {
            Task Send(string text);
            Task<int> Count(CancellationToken cancellationToken = default);
            IAsyncEnumerable<int> Stream(int n, CancellationToken cancellationToken = default);
        }

        public interface IClient
        {
            Task Received(string from, string text);
            Task<bool> Confirm(string question);
        }

        """;

    [Fact]
    public void The_client_generator_writes_calls_events_and_handler_properties()
    {
        var diagnostics = RunClient(ClientUsings + """
            namespace App
            {
                [SwitchboardClient<IBoard, IClient>]
                public partial class BoardClient;
            }
            """, out var generated);

        Assert.Empty(diagnostics);
        Assert.Contains("partial class BoardClient : global::IBoard, global::System.IAsyncDisposable", generated);
        Assert.Contains("this.Line.InvokeCoreAsync(\"Send\", new object?[] { text }, default)", generated);
        Assert.Contains("this.Line.InvokeCoreAsync<int>(\"Count\", global::System.Array.Empty<object?>(), cancellationToken)", generated);
        Assert.Contains("this.Line.StreamCoreAsync<int>(\"Stream\", new object?[] { n }, cancellationToken)", generated);
        Assert.Contains("public event global::System.Func<string, string, global::System.Threading.Tasks.Task>? Received;", generated);
        Assert.Contains("public global::System.Func<string, global::System.Threading.Tasks.Task<bool>>? Confirm { get; set; }", generated);
        Assert.Contains("public global::System.IDisposable Register(global::IClient receiver)", generated);
    }

    [Fact]
    public void The_client_generator_refuses_a_class_that_is_not_partial()
        => Assert.Equal(["SWBC001"], RunClient(ClientUsings + "[SwitchboardClient<IBoard, IClient>] public class BoardClient;", out _).Select(d => d.Id).Distinct());

    [Fact]
    public void The_client_generator_refuses_colliding_names()
        => Assert.Contains("SWBC003", RunClient("""
            using System.Threading.Tasks;
            using Shiny.Net.HttpServer.Switchboard.Client;

            public interface IBoard { Task Notice(string text); }
            public interface IClient { Task Notice(string text); }

            [SwitchboardClient<IBoard, IClient>] public partial class BoardClient;
            """, out _).Select(d => d.Id));
}
