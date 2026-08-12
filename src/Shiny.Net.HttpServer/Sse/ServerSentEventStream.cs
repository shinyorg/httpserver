using System.Text;

namespace Shiny.Net.HttpServer.Sse;

/// <summary>
/// An open <c>text/event-stream</c> response.
/// <para>
/// Every send is flushed immediately. Buffering is the enemy here — an event the client receives
/// three minutes late because it was waiting for a full chunk is worse than useless, and it is the
/// single most common reason SSE "does not work".
/// </para>
/// </summary>
public sealed class ServerSentEventStream
{
    readonly HttpContext context;
    readonly StringBuilder builder = new(256);

    ServerSentEventStream(HttpContext context) => this.context = context;

    /// <summary>Cancelled when the client disconnects, which is how an SSE loop knows to stop.</summary>
    public CancellationToken Aborted => this.context.RequestAborted;

    /// <summary>
    /// The client's <c>Last-Event-ID</c>, when it is reconnecting. Present exactly when the client
    /// is asking to resume from a known point.
    /// </summary>
    public string? LastEventId { get; private init; }

    /// <summary>
    /// Sets the headers and flushes them, so the client's <c>onopen</c> fires before the first
    /// event rather than with it.
    /// </summary>
    public static async ValueTask<ServerSentEventStream> StartAsync(
        HttpContext context,
        TimeSpan? retry = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(context);

        var response = context.Response;
        response.ContentType = "text/event-stream";

        // No Content-Length, so the response is chunked and stays open. The cache and buffering
        // headers are for the intermediaries: a proxy that buffers an event stream breaks it.
        response.Headers[HeaderNames.CacheControl] = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";

        var stream = new ServerSentEventStream(context)
        {
            LastEventId = context.Request.Headers.GetFirst("Last-Event-ID")
        };

        await response.StartAsync(cancellationToken).ConfigureAwait(false);

        if (retry is { } interval)
            await stream.SendAsync(new ServerSentEvent { Retry = interval }, cancellationToken).ConfigureAwait(false);

        return stream;
    }

    /// <summary>Sends one event and flushes it.</summary>
    public async ValueTask SendAsync(ServerSentEvent message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        this.builder.Clear();
        message.WriteTo(this.builder);

        await this.WriteAsync(this.builder.ToString(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends a plain data event.</summary>
    public ValueTask SendAsync(string data, CancellationToken cancellationToken = default)
        => this.SendAsync(ServerSentEvent.Message(data), cancellationToken);

    /// <summary>Sends a named event.</summary>
    public ValueTask SendAsync(string eventName, string data, CancellationToken cancellationToken = default)
        => this.SendAsync(ServerSentEvent.Named(eventName, data), cancellationToken);

    /// <summary>Sends a comment-only event to keep the connection warm.</summary>
    public ValueTask SendHeartbeatAsync(CancellationToken cancellationToken = default)
        => this.SendAsync(ServerSentEvent.Heartbeat(), cancellationToken);

    async ValueTask WriteAsync(string payload, CancellationToken cancellationToken)
    {
        var writer = this.context.Response.BodyWriter;
        var byteCount = Encoding.UTF8.GetByteCount(payload);

        var span = writer.GetSpan(byteCount);
        var written = Encoding.UTF8.GetBytes(payload, span);
        writer.Advance(written);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            this.context.RequestAborted
        );

        await writer.FlushAsync(linked.Token).ConfigureAwait(false);
    }
}

/// <summary>Serving Server-Sent Events.</summary>
public static class ServerSentEventExtensions
{
    /// <summary>
    /// Opens an event stream and hands it to <paramref name="produce"/>, which runs until it
    /// returns or the client disconnects.
    /// <code>
    /// app.MapGet("/events", ctx => ctx.SendEventsAsync(async stream =>
    /// {
    ///     while (!stream.Aborted.IsCancellationRequested)
    ///     {
    ///         await stream.SendAsync($"tick {DateTime.UtcNow:O}");
    ///         await Task.Delay(1000, stream.Aborted);
    ///     }
    /// }));
    /// </code>
    /// </summary>
    public static async ValueTask SendEventsAsync(
        this HttpContext context,
        Func<ServerSentEventStream, Task> produce,
        TimeSpan? retry = null
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(produce);

        var stream = await StartAsync(context, retry).ConfigureAwait(false);

        try
        {
            await produce(stream).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The client went away mid-stream. That is how an event stream normally ends, not a
            // fault worth turning into a 500 nobody can receive.
        }
    }

    /// <summary>
    /// Streams an <see cref="IAsyncEnumerable{T}"/> of events until it completes or the client
    /// disconnects — the whole endpoint, when the source is already async.
    /// </summary>
    public static async ValueTask SendEventsAsync(
        this HttpContext context,
        IAsyncEnumerable<ServerSentEvent> events,
        TimeSpan? retry = null
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(events);

        var stream = await StartAsync(context, retry).ConfigureAwait(false);

        try
        {
            await foreach (var message in events.WithCancellation(context.RequestAborted).ConfigureAwait(false))
                await stream.SendAsync(message, context.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
        }
    }

    static ValueTask<ServerSentEventStream> StartAsync(HttpContext context, TimeSpan? retry)
        => ServerSentEventStream.StartAsync(context, retry, context.RequestAborted);
}

/// <summary>An <see cref="IResult"/> that streams events, for handlers that return results.</summary>
public sealed class ServerSentEventResult(Func<ServerSentEventStream, Task> produce, TimeSpan? retry = null)
    : IActionResult
{
    public ValueTask ExecuteAsync(HttpContext context) => context.SendEventsAsync(produce, retry);
}
