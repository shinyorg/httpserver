using Shiny.Net.HttpServer.Switchboard.Internal;

namespace Shiny.Net.HttpServer.Switchboard;

/// <summary>
/// Works a switchboard from outside it — an endpoint, a background service, a view model when the
/// server runs on the device. Everything a switchboard can do to its lines except the parts that are
/// relative to a caller, because out here there is none.
/// <code>
/// public class OrderShipped(IOperator&lt;ChatBoard&gt; board)
/// {
///     public Task Handle(Order order)
///         => board.Clients.User(order.CustomerId).SendAsync("OrderShipped", order.Id);
/// }
/// </code>
/// Registered by <c>AddSwitchboard()</c> for every switchboard type; inject it like anything else.
/// </summary>
public interface IOperator<TBoard> where TBoard : Switchboard
{
    /// <summary>
    /// Every line on the switchboard. <c>Caller</c>, <c>Others</c> and <c>OthersInGroup</c> throw:
    /// there is no caller outside a switchboard call.
    /// </summary>
    ISwitchboardCallerClients Clients { get; }

    IGroupManager Groups { get; }

    ILineManager Lines { get; }
}

sealed class Operator<TBoard>(SwitchboardRuntime runtime) : IOperator<TBoard> where TBoard : Switchboard
{
    BoardRuntime? board;

    // Resolved on first use rather than in the constructor, so an operator can be injected into a
    // singleton that is built before the switchboard is mapped.
    BoardRuntime Board => this.board ??= runtime.GetBoard(SwitchboardRegistry.GetRequired(typeof(TBoard)));

    public ISwitchboardCallerClients Clients => new SwitchboardClients(this.Board, null);

    public IGroupManager Groups => this.Board.Groups;

    public ILineManager Lines => this.Board.Lines;
}
