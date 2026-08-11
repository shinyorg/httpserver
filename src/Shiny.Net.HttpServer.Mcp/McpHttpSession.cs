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

    /// <summary>Registers a request as in flight against this session.</summary>
    public void Enter()
    {
        Interlocked.Increment(ref this.inFlight);
        this.Touch();
    }

    /// <summary>Releases a request registered by <see cref="Enter"/>.</summary>
    public void Exit()
    {
        Interlocked.Decrement(ref this.inFlight);
        this.Touch();
    }

    /// <summary>
    /// Whether nothing has touched this session for <paramref name="timeout"/>. Measured against
    /// <see cref="Environment.TickCount64"/> rather than the wall clock, so a device sleeping,
    /// waking, or having its time corrected by NTP cannot make sessions expire early or never.
    /// <para>
    /// A session with a request still open is never idle, however long it has been quiet. An SSE
    /// stream sitting silently for an hour waiting for the server to say something is the healthy
    /// case, not an abandoned session, and reclaiming it would cut the client off mid-conversation.
    /// </para>
    /// </summary>
    public bool IsIdle(TimeSpan timeout)
        => Volatile.Read(ref this.inFlight) == 0 &&
           Environment.TickCount64 - Volatile.Read(ref this.lastActivity) > (long)timeout.TotalMilliseconds;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) == 1)
            return;

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
