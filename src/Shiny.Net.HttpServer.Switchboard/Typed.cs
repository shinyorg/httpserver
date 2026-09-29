using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Shiny.Net.HttpServer.Switchboard.Internal;

namespace Shiny.Net.HttpServer.Switchboard;

/// <summary>
/// Where generated <c>TClient</c> implementations register, keyed by the client interface. Generated
/// code calls this from a module initializer; nothing else should.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class SwitchboardClientProxies
{
    static readonly ConcurrentDictionary<Type, Delegate> Factories = new();

    public static void Register<TClient>(Func<IClientProxy, TClient> factory) where TClient : class
    {
        ArgumentNullException.ThrowIfNull(factory);
        Factories[typeof(TClient)] = factory;
    }

    public static Func<IClientProxy, TClient> Get<TClient>() where TClient : class
    {
        if (Factories.TryGetValue(typeof(TClient), out var factory))
            return (Func<IClientProxy, TClient>)factory;

        // Same reasoning as SwitchboardRegistry: typeof() does not run a module initializer.
        RuntimeHelpers.RunModuleConstructor(typeof(TClient).Module.ModuleHandle);

        return Factories.TryGetValue(typeof(TClient), out factory)
            ? (Func<IClientProxy, TClient>)factory
            : throw new InvalidOperationException(
                $"No generated implementation of '{typeof(TClient).FullName}' was found. The Switchboard " +
                "generator writes one for the TClient of every Switchboard<TClient> it compiles — check " +
                "the build for SWB diagnostics against the interface."
            );
    }
}

/// <summary>
/// Calls a generated <c>TClient</c> proxy that must answer: it throws when the target is more than
/// one line. Generated code calls this.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class SwitchboardClientResults
{
    public static Task<T> InvokeAsync<T>(IClientProxy proxy, string method, object?[] args, CancellationToken cancellationToken)
        => proxy is ISingleLineProxy single
            ? single.InvokeAsync<T>(method, args, cancellationToken)
            : Task.FromException<T>(new NotSupportedException(
                $"'{method}' returns a value, and only a single line can answer one. Call it on Clients.Caller " +
                "or Clients.Line(id), not on a group, a user, or everyone."
            ));
}

sealed class TypedSwitchboardClients<TClient>(ISwitchboardClients<IClientProxy> inner) : ISwitchboardClients<TClient>
    where TClient : class
{
    readonly Func<IClientProxy, TClient> create = SwitchboardClientProxies.Get<TClient>();

    public TClient All => this.create(inner.All);

    public TClient AllExcept(params IReadOnlyList<string> lineIds) => this.create(inner.AllExcept(lineIds));

    public TClient Caller => this.create(inner.Caller);

    public TClient Others => this.create(inner.Others);

    public TClient Line(string lineId) => this.create(inner.Line(lineId));

    public TClient Lines(params IReadOnlyList<string> lineIds) => this.create(inner.Lines(lineIds));

    public TClient Group(string group) => this.create(inner.Group(group));

    public TClient Groups(params IReadOnlyList<string> groups) => this.create(inner.Groups(groups));

    public TClient GroupExcept(string group, params IReadOnlyList<string> excludedLineIds)
        => this.create(inner.GroupExcept(group, excludedLineIds));

    public TClient OthersInGroup(string group) => this.create(inner.OthersInGroup(group));

    public TClient User(string userId) => this.create(inner.User(userId));

    public TClient Users(params IReadOnlyList<string> userIds) => this.create(inner.Users(userIds));
}

/// <summary>
/// The operator for a typed switchboard: its clients are called through <typeparamref name="TClient"/>.
/// <code>
/// public class OrderShipped(IOperator&lt;ChatBoard, IChatClient&gt; board)
/// {
///     public Task Handle(Order order) => board.Clients.User(order.CustomerId).Notice($"Order {order.Id} shipped");
/// }
/// </code>
/// </summary>
public interface IOperator<TBoard, TClient>
    where TBoard : Switchboard<TClient>
    where TClient : class
{
    ISwitchboardClients<TClient> Clients { get; }

    IGroupManager Groups { get; }

    ILineManager Lines { get; }
}

sealed class Operator<TBoard, TClient>(SwitchboardRuntime runtime) : IOperator<TBoard, TClient>
    where TBoard : Switchboard<TClient>
    where TClient : class
{
    BoardRuntime? board;

    BoardRuntime Board => this.board ??= runtime.GetBoard(SwitchboardRegistry.GetRequired(typeof(TBoard)));

    public ISwitchboardClients<TClient> Clients => new TypedSwitchboardClients<TClient>(new SwitchboardClients(this.Board, null));

    public IGroupManager Groups => this.Board.Groups;

    public ILineManager Lines => this.Board.Lines;
}
