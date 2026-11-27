using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Shiny.Net.HttpServer.Mcp;

/// <summary>
/// One MCP session: a transport, the server pumping messages off it, and the background task that
/// keeps the pump running between requests.
/// <para>
/// The background task is the point. <c>McpServer.RunAsync</c> has to outlive the HTTP request that
/// created it — the initialize POST returns in milliseconds and the session lives for as long as
/// the client keeps using it — so it gets its own cancellation source rather than the request's
/// <c>RequestAborted</c>, which fires the moment that first response completes.
/// </para>
/// </summary>
sealed class McpHttpSession : IAsyncDisposable
{
    readonly CancellationTokenSource stopping = new();
    readonly ILogger logger;
    Task? pump;
    long lastActivity = Environment.TickCount64;
    int inFlight;
    int disposed;

    public McpHttpSession(string id, StreamableHttpServerTransport transport, McpServer server, ILogger logger)
    {
        this.Id = id;
        this.Transport = transport;
        this.Server = server;
        this.logger = logger;
    }

    public string Id { get; }

    public StreamableHttpServerTransport Transport { get; }

    public McpServer Server { get; }

    /// <summary>Starts the message pump. Called once, immediately after construction.</summary>
    public void Start()
        => this.pump = Task.Run(
            async () =>
            {
                try
                {
                    await this.Server.RunAsync(this.stopping.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Normal: the session was disposed.
                }
                catch (Exception ex)
                {
                    this.logger.LogError(ex, "MCP session {SessionId} faulted", this.Id);
                }
            },
            CancellationToken.None
        );

    /// <summary>Marks the session as in use, deferring idle collection.</summary>
    public void Touch() => Volatile.Write(ref this.lastActivity, Environment.TickCount64);

    // Requests in flight, or negative once the session is closing: the sweeper claiming an idle session
    // and a request entering it are one compare-and-swap on the same field, so exactly one of them wins.
    // Looking the session up and then entering it as two steps let the sweeper dispose it in between,
    // and the request then ran against a disposed transport.

    /// <summary>
    /// Registers a request as in flight against this session. False when the session is closing — the
    /// caller treats it as gone, the same as an id it never knew.
    /// </summary>
    public bool TryEnter()
    {
        var current = Volatile.Read(ref this.inFlight);

        while (current >= 0)
        {
            var seen = Interlocked.CompareExchange(ref this.inFlight, current + 1, current);
            if (seen == current)
            {
                this.Touch();
                return true;
            }

            current = seen;
        }

        return false;
    }

    /// <summary>Releases a request registered by <see cref="TryEnter"/>.</summary>
    public void Exit()
    {
        // Touched before the count drops, so a sweeper that sees no request in flight also sees this one
        // as just finished rather than as long quiet.
        this.Touch();
        Interlocked.Decrement(ref this.inFlight);
    }

    /// <summary>
    /// Claims the session for reclaiming if nothing has touched it for <paramref name="timeout"/> and no
    /// request is in flight. Once this returns true no request can enter it, so the caller disposes it.
    /// <para>
    /// Measured against <see cref="Environment.TickCount64"/> rather than the wall clock, so a device
    /// sleeping, waking, or having its time corrected by NTP cannot make sessions expire early or never.
    /// A session with a request still open is never idle, however long it has been quiet. An SSE stream
    /// sitting silently for an hour waiting for the server to say something is the healthy case, not an
    /// abandoned session, and reclaiming it would cut the client off mid-conversation.
    /// </para>
    /// </summary>
    public bool TryClaimIdle(TimeSpan timeout)
    {
        if (!this.QuietFor(timeout))
            return false;

        if (Interlocked.CompareExchange(ref this.inFlight, int.MinValue, 0) != 0)
            return false;

        // A whole request can start and finish between the look at the clock and the claim; it touched
        // the session on the way, so look again, and hand the session back if it was just used.
        if (this.QuietFor(timeout))
            return true;

        Interlocked.CompareExchange(ref this.inFlight, 0, int.MinValue);
        return false;
    }

    bool QuietFor(TimeSpan timeout)
        => Environment.TickCount64 - Volatile.Read(ref this.lastActivity) > (long)timeout.TotalMilliseconds;

    /// <summary>Refuses every request from now on. Those already in flight finish against the closing session.</summary>
    void MarkClosing()
    {
        var current = Volatile.Read(ref this.inFlight);

        while (current >= 0)
        {
            var seen = Interlocked.CompareExchange(ref this.inFlight, current + int.MinValue, current);
            if (seen == current)
                return;

            current = seen;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) == 1)
            return;

        this.MarkClosing();

        // Order matters: disposing the transport completes its message channel, which is what lets
        // RunAsync return on its own. Cancelling first would be the blunt version of the same thing
        // and would abandon anything mid-flight, so it is the fallback rather than the mechanism.
        await this.Transport.DisposeAsync().ConfigureAwait(false);
        await this.stopping.CancelAsync().ConfigureAwait(false);

        if (this.pump is { } running)
        {
            try
            {
                await running.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.logger.LogDebug(ex, "MCP session {SessionId} pump ended with an error", this.Id);
            }
        }

        await this.Server.DisposeAsync().ConfigureAwait(false);
        this.stopping.Dispose();
    }
}
