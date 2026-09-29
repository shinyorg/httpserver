using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.Switchboard;

// Outside the Shiny.Net.HttpServer namespace on purpose: in there, the name "Switchboard" resolves to
// the Shiny.Net.HttpServer.Switchboard namespace before any using directive is consulted. App code
// lives in its own namespace and never meets this.
namespace SwitchboardFixtures;

/// <summary>Records lifecycle callbacks so a test can wait for one.</summary>
public sealed class LifecycleProbe
{
    readonly Channel<string> events = Channel.CreateUnbounded<string>();

    public void Record(string value) => this.events.Writer.TryWrite(value);

    /// <summary>Holds <c>TestBoard.Slow</c> between its first and second item.</summary>
    public TaskCompletionSource StreamGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<string> WaitForAsync(Func<string, bool> match, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        while (true)
        {
            var value = await this.events.Reader.ReadAsync(timeout.Token);
            if (match(value))
                return value;
        }
    }
}

public sealed record Note(string Title, int Priority);

[JsonSerializable(typeof(Note))]
public partial class SwitchboardFixtureJson : JsonSerializerContext;

public class TestBoard(LifecycleProbe probe) : Switchboard
{
    public override Task OnConnectedAsync()
    {
        probe.Record($"connected:{this.Context.LineId}");
        return this.Clients.Caller.SendAsync("Welcome", this.Context.LineId);
    }

    public override Task OnReconnectedAsync()
    {
        probe.Record($"reconnected:{this.Context.LineId}");
        return Task.CompletedTask;
    }

    public override Task OnDisconnectedAsync(DisconnectContext disconnect)
    {
        probe.Record($"disconnected:{this.Context.LineId}:{disconnect.Reason}:{disconnect.Message}");
        return Task.CompletedTask;
    }

    public int Add(int a, int b) => a + b;

    public async Task<string> Echo(string text)
    {
        await Task.Yield();
        return text;
    }

    public void Nothing()
    {
    }

    public Note Bump(Note note) => note with { Priority = note.Priority + 1 };

    public Task Broadcast(string text) => this.Clients.All.SendAsync("Said", this.Context.LineId, text);

    public Task SayToOthers(string text) => this.Clients.Others.SendAsync("Said", this.Context.LineId, text);

    public Task Join(string group) => this.Groups.AddToGroupAsync(this.Context.LineId, group);

    public Task SayToGroup(string group, string text) => this.Clients.Group(group).SendAsync("Said", this.Context.LineId, text);

    public async IAsyncEnumerable<int> Count(int to, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var i = 1; i <= to; i++)
        {
            await Task.Yield();
            yield return i;
        }
    }

    public void Fail() => throw new SwitchboardException("nope");

    public void Crash() => throw new InvalidOperationException("secret detail");

    public void HangUpOnMe() => this.Context.HangUp("bye");

    [LineMethod("greet")]
    public string Greeting(string name = "world") => $"hello {name}";

    [Authorize(Roles = "admin")]
    public string AdminOnly() => "admin";

    public string WhoAmI() => this.Context.UserIdentifier ?? "(anonymous)";

    public int Counter()
    {
        var next = this.Context.Items.TryGetValue("n", out var value) ? (int)value! + 1 : 1;
        this.Context.Items["n"] = next;
        return next;
    }

    public string Scoped([FromServices] LifecycleProbe injected, CallerContext caller)
        => ReferenceEquals(injected, probe) ? caller.LineId : "different";

    [NotALineMethod]
    public void Hidden()
    {
    }

    public void Record(int value)
    {
        if (!this.Context.Items.TryGetValue("recorded", out var list))
            this.Context.Items["recorded"] = list = new List<int>();

        ((List<int>)list!).Add(value);
    }

    public int[] Recorded()
        => this.Context.Items.TryGetValue("recorded", out var list) ? [.. (List<int>)list!] : [];

    public Task<string> Ask(string question)
        => this.Clients.Caller.InvokeAsync<string>("Answer", [question]);

    public async IAsyncEnumerable<int> Slow([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return 1;
        await probe.StreamGate.Task.WaitAsync(cancellationToken);
        yield return 2;
    }
}

/// <summary>Records every call and lifecycle callback, and refuses one method by name.</summary>
public sealed class RecordingFilter(LifecycleProbe probe) : ISwitchboardFilter
{
    public async ValueTask InvokeMethodAsync(SwitchboardInvocationContext context, Func<SwitchboardInvocationContext, ValueTask> next)
    {
        if (context.MethodName == "Nothing" && context.Caller.UserIdentifier == "blocked")
            throw new SwitchboardException("Blocked by a filter");

        probe.Record($"filter:before:{context.MethodName}");
        await next(context);
        probe.Record($"filter:after:{context.MethodName}");
    }

    public Task OnConnectedAsync(SwitchboardLifecycleContext context, Func<SwitchboardLifecycleContext, Task> next)
    {
        probe.Record($"filter:connected:{context.Caller.LineId}");
        return next(context);
    }
}

// ---- typed ----

public interface ITypedBoard
{
    Task Post(string text);

    Task<int> Add(int a, int b);

    IAsyncEnumerable<int> Count(int to, CancellationToken cancellationToken = default);

    Task<string> AskCaller(string question);

    Task AskEveryone(string question);
}

public interface ITypedClient
{
    Task Posted(string from, string text);

    Task Pinged();

    Task<string> Answer(string question);
}

public class TypedBoard : Switchboard<ITypedClient>, ITypedBoard
{
    public Task Post(string text) => this.Clients.All.Posted(this.Context.LineId, text);

    public Task<int> Add(int a, int b) => Task.FromResult(a + b);

    public async IAsyncEnumerable<int> Count(int to, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (var i = 1; i <= to; i++)
        {
            await Task.Yield();
            yield return i;
        }
    }

    public Task<string> AskCaller(string question) => this.Clients.Caller.Answer(question);

    // A group cannot answer: this must fail rather than hang.
    public Task AskEveryone(string question) => this.Clients.All.Answer(question);
}

// Fully qualified: server and client each have a SwitchboardException, and this file sees both.
[Shiny.Net.HttpServer.Switchboard.Client.SwitchboardClient<ITypedBoard, ITypedClient>]
public partial class TypedBoardClient;

[Authorize]
public class SecureBoard : Switchboard
{
    public string Ping() => "pong";
}
