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
    // Create and DisposeAsync take this, so a session is either tracked before shutdown starts — and
    // disposed by it — or never tracked; not added after DisposeAsync has looked, to run on forever.
    readonly object lifetime = new();
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

    /// <summary>
    /// Finds a session and registers a request against it, as one step: false when there is no such
    /// session or it is being reclaimed. On true, the caller calls <see cref="McpHttpSession.Exit"/>.
    /// </summary>
    public bool TryAcquire(string sessionId, out McpHttpSession session)
        => this.sessions.TryGetValue(sessionId, out session!) && session.TryEnter();

    /// <summary>
    /// Creates a tracked session with a request already registered against it (the initialize that asked
    /// for it), or null when <see cref="McpHttpOptions.MaxSessions"/> is reached.
    /// </summary>
    public async ValueTask<McpHttpSession?> CreateAsync()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref this.disposed), this);

        // Racy against concurrent initializes by exactly the width of the check, which is fine: the
        // limit is a guard rail, not an accounting boundary, and paying for a lock on every
        // initialize to make it exact would be the wrong trade.
        if (this.sessions.Count >= this.Options.MaxSessions)
        {
            this.logger.LogWarning("Refusing a new MCP session: {Count} already open", this.sessions.Count);
            return null;
        }

        var session = this.CreateCore(stateless: false);
        session.TryEnter();

        bool tracked;
        lock (this.lifetime)
        {
            tracked = !this.disposed;
            if (tracked)
            {
                this.sessions[session.Id] = session;
                this.EnsureSweeper();
            }
        }

        if (!tracked)
        {
            // Shutdown started while this was being built; nothing would ever dispose it.
            session.Exit();
            await session.DisposeAsync().ConfigureAwait(false);
            throw new ObjectDisposedException(nameof(McpHttpSessionManager));
        }

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
    public McpHttpSession CreateTransient()
    {
        var session = this.CreateCore(stateless: true);

        // Nobody else can see it, so this cannot be refused.
        session.TryEnter();
        return session;
    }

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

    // Called under the lifetime lock, so DisposeAsync either sees the sweeper or stops one from starting.
    void EnsureSweeper()
    {
        // Started on the first session rather than at construction: an app that registers the
        // endpoint and never receives a request should not be running a timer loop for it.
        if (this.sweeper is not null)
            return;

        var token = this.stopping.Token;
        this.sweeper = Task.Run(() => this.SweepLoopAsync(token), CancellationToken.None);
    }

    async Task SweepLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(this.Options.SessionSweepInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                // Claiming is what makes it safe: a claimed session refuses every request from then on, and
                // the removal is of this exact session, never one that replaced it under the same id.
                foreach (var entry in this.sessions)
                {
                    if (entry.Value.TryClaimIdle(this.Options.IdleSessionTimeout) && this.sessions.TryRemove(entry))
                    {
                        this.logger.LogInformation("Reclaiming idle MCP session {SessionId}", entry.Key);
                        await entry.Value.DisposeAsync().ConfigureAwait(false);
                    }
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
        Task? sweeping;

        lock (this.lifetime)
        {
            if (this.disposed)
                return;

            this.disposed = true;
            sweeping = this.sweeper;
        }

        await this.stopping.CancelAsync().ConfigureAwait(false);

        if (sweeping is { } running)
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
