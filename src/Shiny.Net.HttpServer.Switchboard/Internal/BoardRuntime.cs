using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shiny.Net.HttpServer.Security;

namespace Shiny.Net.HttpServer.Switchboard.Internal;

/// <summary>
/// Everything live for one switchboard type: its lines, by id and by token, and the work of opening,
/// closing and sweeping them. Every path that ends a line goes through <see cref="Close"/>, so the
/// client is told, the token dies, and <c>OnDisconnectedAsync</c> runs — exactly once, whichever
/// path got there first.
/// </summary>
sealed class BoardRuntime : IAsyncDisposable
{
    static readonly ClaimsPrincipal Anonymous = new(new ClaimsIdentity());

    readonly ConcurrentDictionary<string, Line> byId = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, Line> byToken = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<Line, Task> finalizing = new();
    readonly CancellationTokenSource stopping = new();
    readonly IServiceProvider services;
    readonly IUserIdProvider userIds;
    readonly ILogger logger;
    // Open and DisposeAsync take this, so a line is either published before shutdown starts — and closed
    // by it — or refused; never added after DisposeAsync has looked, to outlive the board.
    readonly object lifetime = new();
    readonly ISwitchboardFilter[] filters;
    readonly string name;
    Task? sweeper;
    int disposed;

    public BoardRuntime(
        SwitchboardDescriptor descriptor,
        SwitchboardOptions options,
        IServiceProvider services,
        IUserIdProvider userIds,
        ILogger? logger
    )
    {
        this.Descriptor = descriptor;
        this.Options = options;
        this.services = services;
        this.userIds = userIds;
        this.logger = logger ?? NullLogger.Instance;
        this.ClassAuthorization = SwitchboardAuthorization.Fold(descriptor.Metadata);
        this.filters = [.. services.GetServices<ISwitchboardFilter>()];
        this.name = descriptor.BoardType.Name;
        this.Lines = new LineManager(this);
        this.Groups = new GroupManager(this);
    }

    public SwitchboardDescriptor Descriptor { get; }

    public SwitchboardOptions Options { get; }

    public AuthorizationMetadata? ClassAuthorization { get; }

    public ILineManager Lines { get; }

    public string Name => this.name;

    public IReadOnlyList<ISwitchboardFilter> Filters => this.filters;

    public IGroupManager Groups { get; }

    public int Count => this.byId.Count;

    public ICollection<Line> AllLines() => this.byId.Values;

    public Line? FindLine(string lineId) => this.byId.GetValueOrDefault(lineId);

    public Line? FindByToken(string token) => this.byToken.GetValueOrDefault(token);

    /// <summary>
    /// Creates and tracks a new line for a connect request. Null when <see cref="SwitchboardOptions.MaxLines"/>
    /// is reached. The line comes back with its whole invocation gate held (see <see cref="Line.HoldGateForConnect"/>);
    /// the caller releases it once <c>OnConnectedAsync</c> has run, or the connect has failed.
    /// </summary>
    public Line? Open(HttpContext context)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref this.disposed) != 0, this);

        // Racy by the width of the check, which is fine: the limit is a guard rail, not an
        // accounting boundary, and making it exact would put a lock on every connect.
        if (this.byId.Count >= this.Options.MaxLines)
        {
            this.logger.LogWarning(
                "Refusing a new line on {Board}: {Count} already open",
                this.Descriptor.BoardType.Name,
                this.byId.Count
            );
            return null;
        }

        var user = context.User ?? Anonymous;
        var line = new Line(
            Line.NewId(),
            user,
            this.userIds.GetUserId(user),
            this.Options.ReplayBufferSize,
            this.Options.MaxBufferedMessagesPerLine,
            this.Options.MaximumParallelInvocationsPerLine,
            context.Request.Protocol,
            context.Connection.RemoteIpAddress
        );

        // Before the line is published: nothing can be waiting on the gate yet, so holding it cannot block.
        line.HoldGateForConnect();

        lock (this.lifetime)
        {
            ObjectDisposedException.ThrowIf(this.disposed != 0, this);

            this.byId[line.Id] = line;
            this.byToken[line.Token] = line;
            this.EnsureSweeper();
        }

        SwitchboardMetrics.LineOpened(this.name);

        this.logger.LogDebug("Line {LineId} opened on {Board} ({Count} open)", line.Id, this.Descriptor.BoardType.Name, this.byId.Count);
        return line;
    }

    public void Broadcast(string eventName, string data, Func<Line, bool> filter)
    {
        foreach (var line in this.byId.Values)
        {
            if (filter(line))
                this.Send(line, eventName, data);
        }
    }

    public void Send(Line line, string eventName, string data)
    {
        if (line.Send(eventName, data, out var overflowed) && overflowed)
            this.OnOverflow(line);
    }

    void OnOverflow(Line line)
    {
        SwitchboardMetrics.LineOverflowed(this.name);

        this.logger.LogWarning(
            "Line {LineId} fell {Limit} messages behind; cutting its stream so it resumes",
            line.Id,
            this.Options.MaxBufferedMessagesPerLine
        );

        // With no resume window there is nothing to come back to, so a line that cannot keep up is a
        // line that is gone.
        if (this.Options.ResumeWindow <= TimeSpan.Zero)
            this.Close(line, DisconnectReason.Dropped, "The client was not keeping up", allowReconnect: false);
    }

    /// <summary>
    /// Ends a line: tells its client (when a stream is open), invalidates the token, and runs
    /// <c>OnDisconnectedAsync</c> in the background. Returns false when it was already closed.
    /// </summary>
    public bool Close(Line line, DisconnectReason reason, string? message, bool allowReconnect, Exception? exception = null)
    {
        var data = JsonSerializer.Serialize(new ClosePayload(message, allowReconnect), SwitchboardProtocolJson.Default.ClosePayload);

        if (!line.TryClose(data))
            return false;

        this.OnClosed(line, reason, message, exception);
        return true;
    }

    /// <summary>The bookkeeping after a line has been marked closed, by whichever path won.</summary>
    void OnClosed(Line line, DisconnectReason reason, string? message, Exception? exception)
    {
        this.byId.TryRemove(line.Id, out _);
        this.byToken.TryRemove(line.Token, out _);
        line.CancelClosed();
        SwitchboardMetrics.LineClosed(this.name, reason);

        this.logger.LogDebug("Line {LineId} closed on {Board}: {Reason}", line.Id, this.Descriptor.BoardType.Name, reason);

        // Not awaited by the caller. A switchboard method that hangs up on its own line holds the
        // line's invocation gate, and waiting here for the gate would wait for itself.
        var finalize = Task.Run(() => this.FinalizeAsync(line, new DisconnectContext(reason, message, exception)));
        this.finalizing[line] = finalize;
        _ = finalize.ContinueWith(
            _ => this.finalizing.TryRemove(line, out Task? _),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
    }

    async Task FinalizeAsync(Line line, DisconnectContext disconnect)
    {
        // OnDisconnectedAsync never overlaps a call or OnConnectedAsync on the same line: every in-flight
        // call has seen LineClosed by now, and this waits for them to let go of the gate — and for a stream,
        // which let go of it once its headers were out, to finish its body — briefly, because a call that
        // ignores cancellation must not be able to hold up the disconnect forever.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var acquired = await line.AcquireWholeGateAsync(timeout.Token).ConfigureAwait(false);
        var settled = acquired == line.MaxParallelInvocations;

        if (settled)
        {
            try
            {
                await line.CallsDrained().WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                settled = false;
            }
        }

        if (!settled)
            this.logger.LogWarning("Line {LineId} still had a call running 10 seconds after closing", line.Id);

        try
        {
            await using var scope = this.services.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
            await this.RunLifecycleAsync(line, null, scope.ServiceProvider, Lifecycle.Disconnected, disconnect).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "OnDisconnectedAsync failed for line {LineId}", line.Id);
        }
        finally
        {
            if (acquired > 0)
                line.Gate.Release(acquired);
        }
    }

    /// <summary>
    /// Creates a switchboard instance for <paramref name="line"/>, runs one lifecycle callback on it
    /// through the filters, and disposes it.
    /// </summary>
    public async Task RunLifecycleAsync(
        Line line,
        HttpContext? httpContext,
        IServiceProvider scope,
        Lifecycle lifecycle,
        DisconnectContext? disconnect = null
    )
    {
        var board = this.Descriptor.Factory(scope);
        board.Attach(new CallerContext(line, this, httpContext, httpContext?.User ?? Anonymous), this);

        try
        {
            var context = new SwitchboardLifecycleContext(board, scope);

            switch (lifecycle)
            {
                case Lifecycle.Connected:
                {
                    Func<SwitchboardLifecycleContext, Task> next = static c => c.Switchboard.OnConnectedAsync();
                    for (var i = this.filters.Length - 1; i >= 0; i--)
                    {
                        var (filter, inner) = (this.filters[i], next);
                        next = c => filter.OnConnectedAsync(c, inner);
                    }

                    await next(context).ConfigureAwait(false);
                    break;
                }

                case Lifecycle.Reconnected:
                {
                    Func<SwitchboardLifecycleContext, Task> next = static c => c.Switchboard.OnReconnectedAsync();
                    for (var i = this.filters.Length - 1; i >= 0; i--)
                    {
                        var (filter, inner) = (this.filters[i], next);
                        next = c => filter.OnReconnectedAsync(c, inner);
                    }

                    await next(context).ConfigureAwait(false);
                    break;
                }

                default:
                {
                    Func<SwitchboardLifecycleContext, DisconnectContext, Task> next = static (c, d) => c.Switchboard.OnDisconnectedAsync(d);
                    for (var i = this.filters.Length - 1; i >= 0; i--)
                    {
                        var (filter, inner) = (this.filters[i], next);
                        next = (c, d) => filter.OnDisconnectedAsync(c, d, inner);
                    }

                    await next(context, disconnect!).ConfigureAwait(false);
                    break;
                }
            }
        }
        finally
        {
            await DisposeBoardAsync(board).ConfigureAwait(false);
        }
    }

    public static async ValueTask DisposeBoardAsync(Switchboard board)
    {
        if (board is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        else if (board is IDisposable disposable)
            disposable.Dispose();
    }

    // ---- client results ----

    /// <summary>Asks one line a question and waits for the client's answer.</summary>
    public async Task<T> InvokeClientAsync<T>(string lineId, string method, object?[] args, CancellationToken cancellationToken)
    {
        var line = this.FindLine(lineId) ?? throw new LineClosedException($"There is no line '{lineId}'.");

        // Resolved before anything is sent, so a type with no metadata fails here rather than after
        // the client has already been asked.
        var typeInfo = SwitchboardJson.Get<T>();

        if (line.RegisterResult(this.Options.MaxPendingClientResults) is not var (id, answer))
            throw new InvalidOperationException(
                $"Line '{lineId}' already has {this.Options.MaxPendingClientResults} unanswered calls waiting on it."
            );

        try
        {
            this.Send(line, SwitchboardProtocol.InvokeEvent, InvocationPayload.Write(method, args, id));

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(this.Options.ClientResultTimeout);

            JsonElement result;
            try
            {
                result = await answer.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                SwitchboardMetrics.ClientResult(this.name, "timeout");
                throw new TimeoutException($"The client did not answer '{method}' within {this.Options.ClientResultTimeout}.");
            }

            SwitchboardMetrics.ClientResult(this.name, "ok");
            return result.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                ? default!
                : result.Deserialize(typeInfo)!;
        }
        catch (SwitchboardException)
        {
            SwitchboardMetrics.ClientResult(this.name, "error");
            throw;
        }
        catch (LineClosedException)
        {
            SwitchboardMetrics.ClientResult(this.name, "closed");
            throw;
        }
        finally
        {
            line.RemoveResult(id);
        }
    }

    // ---- resume window ----

    // Called under the lifetime lock, so DisposeAsync either sees the sweeper or stops one from starting.
    void EnsureSweeper()
    {
        // Started with the first line rather than at construction: an app that maps a switchboard and
        // never gets a connection should not be running a timer for it.
        if (this.sweeper is not null || this.Options.ResumeWindow <= TimeSpan.Zero)
            return;

        var token = this.stopping.Token;
        this.sweeper = Task.Run(() => this.SweepLoopAsync(token), CancellationToken.None);
    }

    async Task SweepLoopAsync(CancellationToken cancellationToken)
    {
        var window = this.Options.ResumeWindow;
        var interval = TimeSpan.FromTicks(Math.Clamp(window.Ticks / 4, TimeSpan.FromMilliseconds(25).Ticks, TimeSpan.FromSeconds(5).Ticks));
        var windowMs = (long)window.TotalMilliseconds;

        using var timer = new PeriodicTimer(interval);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                // Tick count rather than wall clock, so a device sleeping, waking, or having its time
                // corrected cannot make a line expire early or never.
                var now = Environment.TickCount64;

                // The line decides under its own lock, so a resume landing between a look at DetachedAt and
                // the close cannot be closed out from under its client.
                foreach (var line in this.byId.Values)
                {
                    if (line.TryCloseDetached(now, windowMs))
                        this.OnClosed(line, DisconnectReason.Dropped, null, null);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "The switchboard resume sweeper stopped unexpectedly");
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? sweeping;

        lock (this.lifetime)
        {
            if (this.disposed != 0)
                return;

            this.disposed = 1;
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

        foreach (var line in this.byId.Values)
            this.Close(line, DisconnectReason.ServerShutdown, "The server is shutting down", allowReconnect: true);

        try
        {
            await Task.WhenAll(this.finalizing.Values).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            this.logger.LogWarning("Switchboard {Board} shut down with disconnect callbacks still running", this.Descriptor.BoardType.Name);
        }

        this.stopping.Dispose();
    }
}

enum Lifecycle
{
    Connected,
    Reconnected,
    Disconnected
}

/// <summary>Every switchboard on a server, one <see cref="BoardRuntime"/> per type.</summary>
sealed class SwitchboardRuntime(
    SwitchboardOptions options,
    IServiceProvider services,
    IUserIdProvider userIds,
    ILoggerFactory? loggerFactory = null
) : IAsyncDisposable
{
    readonly ConcurrentDictionary<Type, BoardRuntime> boards = new();

    public SwitchboardOptions Options => options;

    public BoardRuntime GetBoard(SwitchboardDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        return this.boards.GetOrAdd(
            descriptor.BoardType,
            static (_, state) => new BoardRuntime(
                state.descriptor,
                state.options,
                state.services,
                state.userIds,
                state.loggerFactory?.CreateLogger("Shiny.Net.HttpServer.Switchboard." + state.descriptor.BoardType.Name)
            ),
            (descriptor, options, services, userIds, loggerFactory)
        );
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var board in this.boards.Values)
            await board.DisposeAsync().ConfigureAwait(false);
    }
}
