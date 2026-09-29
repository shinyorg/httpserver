using System.Security.Claims;
using Shiny.Net.HttpServer.Switchboard.Internal;

namespace Shiny.Net.HttpServer.Switchboard;

/// <summary>
/// A class whose public methods clients can call, and which can call back out to any connected
/// client — the thing SignalR calls a hub.
/// <code>
/// public class ChatBoard(IRoomStore rooms) : Switchboard
/// {
///     public async Task SendMessage(string room, string text)
///         => await Clients.Group(room).SendAsync("MessageReceived", Context.UserIdentifier, text);
/// }
///
/// builder.AddSwitchboard();
/// app.MapSwitchboard&lt;ChatBoard&gt;("/chat");
/// </code>
/// <para>
/// One instance is created per call and per lifecycle callback, in the request's service scope, so
/// a scoped dependency is the same instance the rest of the request sees. Nothing survives on the
/// instance between calls: per-line state goes in <see cref="CallerContext.Items"/>.
/// </para>
/// <para>
/// The source generator shipped with this package finds every non-abstract subclass and writes its
/// dispatcher — argument binding, authorization and result writing — at compile time. There is no
/// reflection anywhere on the call path.
/// </para>
/// </summary>
public abstract class Switchboard
{
    CallerContext? context;
    ISwitchboardCallerClients? clients;
    BoardRuntime? runtime;

    /// <summary>The line that made this call, and who is on it.</summary>
    public CallerContext Context => this.context ?? throw NotAttached();

    /// <summary>Every line on this switchboard, addressable one at a time, by group, by user, or all at once.</summary>
    public ISwitchboardCallerClients Clients => this.clients ?? throw NotAttached();

    /// <summary>Group membership for lines on this switchboard.</summary>
    public IGroupManager Groups => this.runtime?.Groups ?? throw NotAttached();

    /// <summary>The lines themselves: who is connected, and hanging up on them.</summary>
    public ILineManager Lines => this.runtime?.Lines ?? throw NotAttached();

    /// <summary>
    /// Runs once for a new line, after the client has been told its line id — so
    /// <c>Clients.Caller</c> works in here. Throwing closes the line as <see cref="DisconnectReason.Faulted"/>.
    /// </summary>
    public virtual Task OnConnectedAsync() => Task.CompletedTask;

    /// <summary>
    /// Runs when a dropped line comes back inside the resume window. The line id, groups and
    /// <see cref="CallerContext.Items"/> are intact, and the messages it missed have been replayed.
    /// </summary>
    public virtual Task OnReconnectedAsync() => Task.CompletedTask;

    /// <summary>
    /// Runs once when a line is gone for good. <paramref name="disconnect"/> says why — the user
    /// closing it is not the same event as a phone losing signal, and apps usually care which.
    /// </summary>
    public virtual Task OnDisconnectedAsync(DisconnectContext disconnect) => Task.CompletedTask;

    internal void Attach(CallerContext context, BoardRuntime runtime)
    {
        this.context = context;
        this.runtime = runtime;
        this.clients = new SwitchboardClients(runtime, context.LineId);
    }

    internal static InvalidOperationException NotAttached() => new(
        "This switchboard instance is not attached to a line. Switchboards are created by the " +
        "runtime for each call; use IOperator<TBoard> to reach clients from anywhere else."
    );
}

/// <summary>
/// A switchboard whose clients are called through an interface rather than by method name.
/// <code>
/// public interface IChatClient
/// {
///     Task MessageReceived(ChatMessage message);
///     Task&lt;bool&gt; ConfirmDelete(string room);      // a client result
/// }
///
/// public class ChatBoard : Switchboard&lt;IChatClient&gt;
/// {
///     public Task Send(string room, string text)
///         => Clients.Group(room).MessageReceived(new(room, Context.UserIdentifier!, text));
/// }
/// </code>
/// The implementation of <typeparamref name="TClient"/> is generated at compile time. A method
/// returning <c>Task&lt;T&gt;</c> waits for the client's answer, which only a single line can give:
/// on <c>Caller</c> or <c>Line(id)</c> it works, on a group or everyone it throws.
/// </summary>
public abstract class Switchboard<TClient> : Switchboard where TClient : class
{
    ISwitchboardClients<TClient>? typed;

    /// <summary>Every line on this switchboard, called through <typeparamref name="TClient"/>.</summary>
    public new ISwitchboardClients<TClient> Clients
        => this.typed ??= new TypedSwitchboardClients<TClient>(base.Clients);
}

/// <summary>The line behind the current call.</summary>
public sealed class CallerContext
{
    readonly Line line;
    readonly BoardRuntime runtime;

    internal CallerContext(Line line, BoardRuntime runtime, HttpContext? httpContext, ClaimsPrincipal user)
    {
        this.line = line;
        this.runtime = runtime;
        this.HttpContext = httpContext;
        this.User = user;
    }

    /// <summary>Public, stable for the life of the line (including resumes). What <c>Clients.Line(id)</c> addresses.</summary>
    public string LineId => this.line.Id;

    /// <summary>Who is calling. For an invocation, the principal of that request.</summary>
    public ClaimsPrincipal User { get; }

    /// <summary>The user this line belongs to, from <see cref="IUserIdProvider"/>. What <c>Clients.User(id)</c> addresses.</summary>
    public string? UserIdentifier => this.line.UserId;

    /// <summary>
    /// State that lives as long as the line, resumes included.
    /// <para>
    /// Calls from one line run one at a time by default, so this needs no locking. Raise
    /// <see cref="SwitchboardOptions.MaximumParallelInvocationsPerLine"/> and it becomes shared
    /// between concurrent calls.
    /// </para>
    /// </summary>
    public IDictionary<object, object?> Items => this.line.Items;

    /// <summary>
    /// Fires when the line closes for good. A drop inside the resume window does not fire it — the
    /// line is expected back.
    /// </summary>
    public CancellationToken LineClosed => this.line.Closed;

    /// <summary>
    /// The request currently executing: the connect request for <c>OnConnectedAsync</c>, the
    /// invocation's POST for a method call. Null in <c>OnDisconnectedAsync</c>, which usually runs
    /// after every request on the line has ended.
    /// </summary>
    public HttpContext? HttpContext { get; }

    /// <summary>
    /// Hangs up on this line. The client is told why, and — unless <paramref name="allowReconnect"/>
    /// — will not reconnect: its line token stops working at once.
    /// </summary>
    public void HangUp(string? reason = null, bool allowReconnect = false)
        => this.runtime.Close(this.line, DisconnectReason.HungUp, reason, allowReconnect);
}

/// <summary>Why a line closed, as seen by the server.</summary>
public enum DisconnectReason
{
    /// <summary>The client closed it on purpose.</summary>
    ClientClosed,

    /// <summary>The stream broke and the line did not come back inside the resume window.</summary>
    Dropped,

    /// <summary>The server hung up: <c>Context.HangUp</c> or one of the <see cref="ILineManager"/> hang-ups.</summary>
    HungUp,

    /// <summary>The server is stopping. Clients are told they may come back.</summary>
    ServerShutdown,

    /// <summary>Something went wrong in the line itself — <c>OnConnectedAsync</c> threw, for one.</summary>
    Faulted
}

/// <summary>What <see cref="Switchboard.OnDisconnectedAsync"/> is told.</summary>
public sealed record DisconnectContext(DisconnectReason Reason, string? Message, Exception? Exception);

/// <summary>
/// An error meant for the client. Its message always reaches the caller, whatever
/// <see cref="SwitchboardOptions.EnableDetailedErrors"/> says — every other exception is reported
/// as a generic failure, because an exception message is not written with a stranger in mind.
/// </summary>
public class SwitchboardException : Exception
{
    public SwitchboardException(string message) : base(message)
    {
    }

    public SwitchboardException(string message, Exception? innerException) : base(message, innerException)
    {
    }
}

/// <summary>Publishes a switchboard method under a different name than the C# one.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class LineMethodAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

/// <summary>Keeps a public method on a switchboard from being callable by clients.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class NotALineMethodAttribute : Attribute;

/// <summary>
/// Decides which user a line belongs to, which is what <c>Clients.User</c> and
/// <c>HangUpUserAsync</c> address. The default is the principal's name.
/// </summary>
public interface IUserIdProvider
{
    string? GetUserId(ClaimsPrincipal user);
}

sealed class DefaultUserIdProvider : IUserIdProvider
{
    public string? GetUserId(ClaimsPrincipal user) => user.Identity?.Name;
}
