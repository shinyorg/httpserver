namespace Shiny.Net.HttpServer;

/// <summary>
/// Why the server moved to a state.
/// <para>
/// <see cref="HttpServerState"/> on its own answers "is it up", which is enough to draw a toggle and
/// nothing else. The question an app actually gets asked — "it stopped, why?" — has no answer without
/// this: a server that the user switched off and a server whose listener died underneath it both
/// report <see cref="HttpServerState.Stopped"/>, and only one of them is a bug.
/// </para>
/// </summary>
public enum HttpServerStateReason
{
    /// <summary>The app called <c>StartAsync</c> or <c>StopAsync</c>. The expected reason, and the only one that is never a fault.</summary>
    Requested,

    /// <summary>
    /// Part of a <c>RestartAsync</c>. Present on the stop half as well as the start half, so a handler
    /// watching for <see cref="HttpServerState.Stopped"/> can tell "down" from "down for a moment" and
    /// leave its notification, advertisement or UI alone until the start lands.
    /// </summary>
    Restarting,

    /// <summary>A rebind driven by the machine's addresses changing — the phone moved networks.</summary>
    NetworkChanged,

    /// <summary>
    /// The listener could not be bound, and the retries are spent. The server is stopped and will not
    /// come back on its own. <see cref="HttpServerStateChange.Exception"/> carries the bind failure.
    /// </summary>
    BindFailed,

    /// <summary>
    /// The accept loop ended or threw while the server still believed it was running — the listening
    /// socket was disposed, aborted, or failed to accept often enough to count as dead.
    /// <see cref="HttpServerStateChange.Exception"/> carries the cause. Whether the server comes back
    /// is <see cref="HttpServerOptions.RecoverFromListenerFaults"/>.
    /// </summary>
    ListenerFaulted,

    /// <summary>The server was disposed. Nothing follows this one.</summary>
    Disposed
}

/// <summary>
/// One state transition, with the reason it happened and — when something went wrong — the exception
/// that caused it.
/// <para>
/// Raised by <see cref="HttpServer.StateTransitioned"/> and kept in
/// <see cref="HttpServer.LastStateChange"/> for a consumer that was not subscribed when it happened.
/// </para>
/// </summary>
/// <param name="State">The state the server moved to.</param>
/// <param name="Reason">Why it moved.</param>
/// <param name="Exception">The failure behind the move, when there was one. Null for every ordinary transition.</param>
public sealed record HttpServerStateChange(
    HttpServerState State,
    HttpServerStateReason Reason,
    Exception? Exception = null
);

/// <summary>
/// The listener stopped accepting while the server still believed it was running.
/// <para>
/// This is the exception behind <see cref="HttpServerStateReason.ListenerFaulted"/>. It exists as its
/// own type because the underlying cause is often nothing at all — a listener that returns "no more
/// connections" because its socket was disposed out from under it produces no exception to report, and
/// "the server went down and here is nothing" is precisely the silence this whole class of bug hid in.
/// </para>
/// </summary>
public sealed class HttpServerListenerException : Exception
{
    public HttpServerListenerException(string message)
        : base(message)
    {
    }

    public HttpServerListenerException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
