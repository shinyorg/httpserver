using System.Text.Json;

namespace Shiny.Net.HttpServer.Switchboard.Client;

/// <summary>Where a <see cref="SwitchboardLine"/> is in its life.</summary>
public enum LineState
{
    Disconnected,
    Connecting,
    Connected,

    /// <summary>The stream dropped and the line is trying to resume — or, failing that, to open a new one.</summary>
    Reconnecting
}

/// <summary>Why a line closed, as seen by the client.</summary>
public enum LineCloseReason
{
    /// <summary><see cref="SwitchboardLine.StopAsync"/> was called.</summary>
    ClientClosed,

    /// <summary>The server closed the line, allowing a reconnect, and none was made or none succeeded.</summary>
    ServerClosed,

    /// <summary>The server hung up and does not want this client back. The line never reconnects.</summary>
    HungUp,

    /// <summary>The stream dropped and automatic reconnect is not turned on.</summary>
    Dropped,

    /// <summary>The stream dropped and the retry policy gave up.</summary>
    RetriesExhausted,

    /// <summary>The line failed in a way retrying cannot fix — refused credentials, a protocol error.</summary>
    Faulted
}

public sealed class LineStateChangedEventArgs(LineState previous, LineState current) : EventArgs
{
    public LineState Previous { get; } = previous;

    public LineState Current { get; } = current;
}

public sealed class LineReconnectingEventArgs(Exception? exception, int attempt) : EventArgs
{
    /// <summary>What broke the stream, or what made the last attempt fail. Null when the server simply ended it.</summary>
    public Exception? Exception { get; } = exception;

    /// <summary>1 for the first attempt.</summary>
    public int Attempt { get; } = attempt;
}

public sealed class LineReconnectedEventArgs(string lineId, bool newLine, bool missedMessages) : EventArgs
{
    public string LineId { get; } = lineId;

    /// <summary>
    /// True when the old line could not be resumed and this is a new one: a new id, no groups, no
    /// server-side state. Rejoin whatever the app had joined.
    /// </summary>
    public bool NewLine { get; } = newLine;

    /// <summary>
    /// True when the line resumed but the server could not replay everything it missed. Reload
    /// whatever state the app keeps from those messages.
    /// </summary>
    public bool MissedMessages { get; } = missedMessages;
}

public sealed class LineClosedEventArgs(LineCloseReason reason, string? message, Exception? exception) : EventArgs
{
    public LineCloseReason Reason { get; } = reason;

    /// <summary>What the server said when it closed the line, when it said anything.</summary>
    public string? Message { get; } = message;

    public Exception? Exception { get; } = exception;
}

/// <summary>
/// An error from the switchboard: the server refused a call, its method threw a
/// <c>SwitchboardException</c>, or the call failed in a way the server reported.
/// </summary>
public class SwitchboardException : Exception
{
    public SwitchboardException(string message, int? statusCode = null, Exception? innerException = null)
        : base(message, innerException)
        => this.StatusCode = statusCode;

    /// <summary>The HTTP status the server answered with, when there was one.</summary>
    public int? StatusCode { get; }
}

/// <summary>Decides whether, and when, a dropped line tries again.</summary>
public interface IRetryPolicy
{
    /// <summary>How long to wait before the next attempt, or null to give up.</summary>
    TimeSpan? NextRetryDelay(RetryContext context);
}

/// <param name="PreviousRetryCount">Attempts already made in this reconnect.</param>
/// <param name="ElapsedTime">Time since the line dropped.</param>
/// <param name="RetryReason">What made the last attempt, or the drop itself, fail.</param>
public sealed record RetryContext(int PreviousRetryCount, TimeSpan ElapsedTime, Exception? RetryReason);

/// <summary>
/// Waits through a fixed list of delays, then gives up. Non-zero delays get ±20% jitter, so a server
/// that restarts is not hit by every client in the same instant.
/// </summary>
public sealed class DefaultRetryPolicy(IReadOnlyList<TimeSpan> delays) : IRetryPolicy
{
    /// <summary>0, 2, 10 and 30 seconds.</summary>
    public static IReadOnlyList<TimeSpan> DefaultDelays { get; } =
        [TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

    public DefaultRetryPolicy() : this(DefaultDelays)
    {
    }

    public TimeSpan? NextRetryDelay(RetryContext context)
    {
        if (context.PreviousRetryCount >= delays.Count)
            return null;

        var delay = delays[context.PreviousRetryCount];
        if (delay <= TimeSpan.Zero)
            return TimeSpan.Zero;

        var jitter = 0.8 + (Random.Shared.NextDouble() * 0.4);
        return TimeSpan.FromTicks((long)(delay.Ticks * jitter));
    }
}

/// <summary>
/// The arguments of one call from the server, read on demand with the line's JSON metadata. What a
/// <see cref="SwitchboardLine.Handle(string, Func{LineArguments, Task})"/> handler receives.
/// </summary>
public sealed class LineArguments
{
    readonly SwitchboardLine line;

    internal LineArguments(SwitchboardLine line, string method, JsonElement arguments)
    {
        this.line = line;
        this.Method = method;
        this.Raw = arguments;
        this.Count = arguments.ValueKind == JsonValueKind.Array ? arguments.GetArrayLength() : 0;
    }

    /// <summary>The method the server called.</summary>
    public string Method { get; }

    public int Count { get; }

    /// <summary>The arguments as the server sent them.</summary>
    public JsonElement Raw { get; }

    /// <summary>Reads argument <paramref name="index"/>; a missing trailing argument reads as the default.</summary>
    public T Get<T>(int index)
    {
        if (index >= this.Count)
            return default!;

        var element = this.Raw[index];
        return element.ValueKind == JsonValueKind.Null
            ? default!
            : element.Deserialize(this.line.Json.Get<T>())!;
    }
}

/// <summary>Configures how a <see cref="SwitchboardLine"/> reaches the server.</summary>
public sealed class SwitchboardLineOptions
{
    /// <summary>Called before every request; a non-null result is sent as <c>Authorization: Bearer …</c>.</summary>
    public Func<Task<string?>>? AccessTokenProvider { get; set; }

    /// <summary>Headers added to every request — an API key, say.</summary>
    public IDictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Wraps or replaces the handler the line creates — certificate pinning, a self-signed dev certificate, a proxy.</summary>
    public Func<HttpMessageHandler, HttpMessageHandler>? HttpMessageHandlerFactory { get; set; }

    /// <summary>
    /// An <see cref="HttpClient"/> to use instead of creating one. The line does not dispose it.
    /// <see cref="HttpMessageHandlerFactory"/> is ignored when this is set.
    /// </summary>
    public HttpClient? HttpClient { get; set; }

    /// <summary>
    /// The HTTP version to ask for. HTTP/2 by default, falling back to HTTP/1.1: on HTTP/2 the event
    /// stream and every call share one connection.
    /// </summary>
    public Version HttpVersion { get; set; } = System.Net.HttpVersion.Version20;

    public HttpVersionPolicy HttpVersionPolicy { get; set; } = HttpVersionPolicy.RequestVersionOrLower;

    /// <summary>
    /// How long the stream may be silent before the line counts as dropped. Defaults to twice the
    /// server's heartbeat interval. Relying on TCP to notice is how mobile clients sit "connected" on a
    /// dead link for minutes.
    /// </summary>
    public TimeSpan? ServerTimeout { get; set; }
}
