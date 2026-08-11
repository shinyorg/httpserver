using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace Shiny.Net.HttpServer.Mcp;

/// <summary>
/// Owns every live MCP session for one server: creates them, hands them back by id, and reclaims
/// the ones whose clients wandered off.
/// <para>
/// Registered as a singleton, so its lifetime is the container's. A session survives across
/// requests and across connections — which is the entire difference between MCP over HTTP and a
/// request/response API, and the reason this class exists at all.
/// </para>
/// </summary>
sealed class McpHttpSessionManager : IAsyncDisposable
{
    readonly ConcurrentDictionary<string, McpHttpSession> sessions = new(StringComparer.Ordinal);
    readonly CancellationTokenSource stopping = new();
    readonly IServiceProvider services;
    readonly IOptionsFactory<McpServerOptions> serverOptions;
    readonly ILoggerFactory loggerFactory;
    readonly ILogger<McpHttpSessionManager> logger;
    Task? sweeper;
    bool disposed;

    public McpHttpSessionManager(
        IOptions<McpHttpOptions> options,
        IOptionsFactory<McpServerOptions> serverOptions,
        IServiceProvider services,
        ILoggerFactory? loggerFactory = null
    )
    {
        this.Options = options.Value;
        this.serverOptions = serverOptions;
        this.services = services;
        this.loggerFactory = loggerFactory ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;
        this.logger = this.loggerFactory.CreateLogger<McpHttpSessionManager>();
    }

    public McpHttpOptions Options { get; }

    public int Count => this.sessions.Count;

    public bool TryGet(string sessionId, out McpHttpSession session)
        => this.sessions.TryGetValue(sessionId, out session!);

    /// <summary>
    /// Creates a tracked session, or null when <see cref="McpHttpOptions.MaxSessions"/> is reached.
    /// </summary>
    public McpHttpSession? Create()
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        // Racy against concurrent initializes by exactly the width of the check, which is fine: the
        // limit is a guard rail, not an accounting boundary, and paying for a lock on every
        // initialize to make it exact would be the wrong trade.
        if (this.sessions.Count >= this.Options.MaxSessions)
        {
            this.logger.LogWarning("Refusing a new MCP session: {Count} already open", this.sessions.Count);
            return null;
        }

        var session = this.CreateCore(stateless: false);
        this.sessions[session.Id] = session;
        this.EnsureSweeper();

        this.logger.LogDebug("MCP session {SessionId} opened ({Count} open)", session.Id, this.sessions.Count);
        return session;
    }

    /// <summary>
    /// Creates a server that lives exactly as long as the caller keeps it: one request, one server,
    /// nothing remembered afterwards.
    /// <para>
    /// Always stateless, whatever <see cref="McpHttpOptions.Stateless"/> says, because the transport
    /// only skips the "you must initialize first" rule in stateless mode — and a request that
    /// arrives without a session has by definition not initialized.
    /// </para>
    /// </summary>
    public McpHttpSession CreateTransient() => this.CreateCore(stateless: true);

    McpHttpSession CreateCore(bool stateless)
    {
        // Random rather than sequential, and 128 bits of it. A session id is a bearer token in
        // everything but name: anyone holding one can speak as that client.
        var sessionId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

        var transport = new StreamableHttpServerTransport(this.loggerFactory)
        {
            Stateless = stateless,
            SessionId = sessionId
        };

        // A fresh options instance per session: McpServerOptions carries per-session state (the
        // negotiated client info and capabilities), so sharing one across sessions would let one
        // client's handshake leak into another's.
        var server = McpServer.Create(
            transport,
            this.serverOptions.Create(Microsoft.Extensions.Options.Options.DefaultName),
            this.loggerFactory,
            this.services
        );

        var session = new McpHttpSession(sessionId, transport, server, this.logger);
        session.Start();

        return session;
    }

    public async ValueTask<bool> RemoveAsync(string sessionId)
    {
        if (!this.sessions.TryRemove(sessionId, out var session))
            return false;

        await session.DisposeAsync().ConfigureAwait(false);
        this.logger.LogDebug("MCP session {SessionId} closed ({Count} open)", sessionId, this.sessions.Count);

        return true;
    }

    void EnsureSweeper()
    {
        // Started on the first session rather than at construction: an app that registers the
        // endpoint and never receives a request should not be running a timer loop for it.
        if (this.sweeper is not null)
            return;

        lock (this.sessions)
            this.sweeper ??= Task.Run(() => this.SweepLoopAsync(this.stopping.Token), CancellationToken.None);
    }

    async Task SweepLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(this.Options.SessionSweepInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                foreach (var entry in this.sessions)
                {
                    if (!entry.Value.IsIdle(this.Options.IdleSessionTimeout))
                        continue;

                    this.logger.LogInformation("Reclaiming idle MCP session {SessionId}", entry.Key);
                    await this.RemoveAsync(entry.Key).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "The MCP session sweeper stopped unexpectedly");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (this.disposed)
            return;

        this.disposed = true;

        await this.stopping.CancelAsync().ConfigureAwait(false);

        if (this.sweeper is { } running)
        {
            try
            {
                await running.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        foreach (var entry in this.sessions)
        {
            if (this.sessions.TryRemove(entry.Key, out var session))
                await session.DisposeAsync().ConfigureAwait(false);
        }

        this.stopping.Dispose();
    }
}
