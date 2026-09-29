namespace Shiny.Net.HttpServer.Switchboard;

/// <summary>Settings shared by every switchboard on a server.</summary>
public sealed class SwitchboardOptions
{
    /// <summary>
    /// How often an idle stream gets a comment line. Keeps proxies from closing a quiet connection,
    /// and lets a client tell a dead link from a quiet one. Zero turns heartbeats off.
    /// </summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long a line whose stream dropped is held for the client to come back with the same id,
    /// groups and <c>Items</c>, and the messages it missed. Zero closes a line the moment its stream
    /// ends.
    /// </summary>
    public TimeSpan ResumeWindow { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Messages kept per line for replay on resume. A client that missed more than this is told so,
    /// rather than silently handed a gap.
    /// </summary>
    public int ReplayBufferSize { get; set; } = 256;

    /// <summary>
    /// Messages that may be queued for one attached line before it counts as not keeping up. A line
    /// that falls this far behind has its stream cut; its client resumes and is replayed what the
    /// replay buffer still holds. Unbounded per-client memory is the thing this is designed against.
    /// </summary>
    public int MaxBufferedMessagesPerLine { get; set; } = 1024;

    /// <summary>
    /// How many calls from one line run at once. One — the default — runs them in the order they
    /// arrive and makes <c>Context.Items</c> safe without locking.
    /// </summary>
    public int MaximumParallelInvocationsPerLine { get; set; } = 1;

    /// <summary>
    /// How long the server waits for a client to answer a call that expects a result, before
    /// failing it with a <see cref="TimeoutException"/>.
    /// </summary>
    public TimeSpan ClientResultTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Unanswered client-result calls one line may have at once. Past it, new ones fail immediately.</summary>
    public int MaxPendingClientResults { get; set; } = 16;

    /// <summary>
    /// With one call at a time per line, how long a call waits for an earlier-numbered call that has
    /// not arrived yet before running anyway. Calls are separate HTTP requests and can overtake each
    /// other; this is what puts them back in the order the client sent them.
    /// </summary>
    public TimeSpan InvocationOrderingTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>A guard rail on open lines per switchboard. New connects past it get a 503.</summary>
    public int MaxLines { get; set; } = 10_000;

    /// <summary>
    /// Sends unhandled exception messages to clients. Off by default: an exception message is written
    /// for a developer, not for whoever is on the other end of the line. <see cref="SwitchboardException"/>
    /// messages are always sent.
    /// </summary>
    public bool EnableDetailedErrors { get; set; }
}
