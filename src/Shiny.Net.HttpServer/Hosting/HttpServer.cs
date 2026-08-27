using System.Buffers;
using System.Security.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shiny.Net.HttpServer.Http1;
using Shiny.Net.HttpServer.Http2;
using Shiny.Net.HttpServer.Routing;
using Shiny.Net.HttpServer.Transports;

namespace Shiny.Net.HttpServer;

/// <summary>
/// The server. Bind an address, describe how to answer requests, run.
/// <para>
/// There are three ways to answer a request and they compose in one app:
/// <list type="bullet">
/// <item><see cref="OnRequest(RequestDelegate)"/> — one delegate handles everything that no route claimed.</item>
/// <item><see cref="MapGet(string, RequestDelegate)"/> and friends — raw handlers behind a route template.</item>
/// <item>Generated endpoint classes — strongly typed, constructor-injected, registered by the source generator.</item>
/// </list>
/// </para>
/// <para>
/// A container is optional. Construct with one and every request gets its own
/// <c>IServiceScope</c>, exactly as in ASP.NET Core; construct without one and everything still
/// works, minus <c>ctx.RequestServices</c>.
/// </para>
/// </summary>
public sealed class HttpServer : IAsyncDisposable
{
    readonly List<MiddlewareDelegate> middleware = [];
    readonly List<MiddlewareDelegate> afterRouting = [];
    readonly ILoggerFactory loggerFactory;
    readonly ILogger<HttpServer> logger;
    readonly SemaphoreSlim? connectionLimit;
    readonly SemaphoreSlim lifecycle = new(1, 1);
    readonly HashSet<Task> connections = [];
    readonly object connectionsLock = new();

    // Replaced on every start rather than reused: cancelling a CancellationTokenSource is permanent,
    // so a shared one would leave a restarted server with an accept loop that exits immediately.
    // Superseded sources are left to the GC — disposing one that an in-flight ServeAsync is still
    // linked to would turn a normal shutdown into an ObjectDisposedException.
    CancellationTokenSource stopping = new();

    // Set the moment a stop begins, before the listeners are unbound. The stopping token cannot do
    // this job: it also cancels the in-flight connections a graceful stop is trying to drain, so it
    // is cancelled *after* the unbind — and in that window a listener returning "no more
    // connections" because we unbound it is indistinguishable from one that died on its own.
    volatile bool stopRequested;

    NetworkChangeWatcher? networkWatcher;
    RequestDelegate? terminalHandler;
    RequestDelegate? pipeline;
    IReadOnlyList<IConnectionListener> listeners = [];
    Task? acceptLoops;
    bool disposed;

    public HttpServer(
        HttpServerOptions? options = null,
        IServiceProvider? services = null,
        ILoggerFactory? loggerFactory = null
    )
    {
        this.Options = options ?? new HttpServerOptions();
        this.Services = services;
        this.loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        this.logger = this.loggerFactory.CreateLogger<HttpServer>();

        if (this.Options.MaxConcurrentConnections is { } max)
            this.connectionLimit = new SemaphoreSlim(max, max);
    }

    /// <summary>
    /// Entry point for a server that owns its own container: configure, then <c>Build()</c>. An app
    /// that already has a container calls <c>services.AddShinyHttpServer(...)</c> instead and gets
    /// the same builder.
    /// </summary>
    public static ShinyHttpServerBuilder CreateBuilder()
        => new(new Microsoft.Extensions.DependencyInjection.ServiceCollection(), ownsContainer: true);

    public HttpServerOptions Options { get; }

    /// <summary>The root provider, or null when the server was built without a container.</summary>
    public IServiceProvider? Services { get; }

    /// <summary>The route table. Generated endpoint registrations add to this.</summary>
    public Router Router { get; } = new();

    /// <summary>
    /// The URL actually being served, available once started. Reflects the real port when
    /// <see cref="HttpServerOptions.Port"/> was 0.
    /// <para>
    /// With several endpoints configured this is the first of them; see <see cref="ListenUrls"/>
    /// for all of them.
    /// </para>
    /// </summary>
    public string? ListenUrl => this.listeners.Count > 0 ? this.listeners[0].ListenDescription : null;

    /// <summary>
    /// Every URL being served, in the order the endpoints were configured. Empty until started.
    /// </summary>
    public IReadOnlyList<string> ListenUrls => [.. this.listeners.Select(x => x.ListenDescription)];

    public bool IsRunning => this.State == HttpServerState.Running;

    /// <summary>
    /// Connections currently being served, tunnelled ones included. This is connections, not
    /// requests: one keep-alive connection serving a hundred requests counts once, and an HTTP/2
    /// connection with a dozen concurrent streams also counts once.
    /// </summary>
    public int ActiveConnections
    {
        get
        {
            lock (this.connectionsLock)
                return this.connections.Count;
        }
    }

    // ---- Tier 0: one delegate ----

    /// <summary>
    /// Handles every request that no mapped route claimed. With no routes registered, that is
    /// every request — which is the whole point of this overload.
    /// <code>
    /// server.OnRequest(ctx => ctx.Response.WriteTextAsync("hello"));
    /// </code>
    /// Calling it a second time replaces the handler rather than chaining; use
    /// <see cref="Use(MiddlewareDelegate)"/> to run things in sequence.
    /// </summary>
    public HttpServer OnRequest(RequestDelegate handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        this.ThrowIfStarted();

        this.terminalHandler = handler;
        return this;
    }

    /// <summary>
    /// <see cref="OnRequest(RequestDelegate)"/> for handlers that return <see cref="Task"/>, which
    /// is what an <c>async</c> lambda calling into most libraries naturally produces.
    /// </summary>
    public HttpServer OnRequest(Func<HttpContext, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return this.OnRequest(ctx => new ValueTask(handler(ctx)));
    }

    // ---- Tier 1: routes ----

    public HttpServer Map(string method, string pattern, RequestDelegate handler, params object[]? metadata)
    {
        this.MapRoute(method, pattern, handler, metadata);
        return this;
    }

    /// <summary>
    /// Maps a route and hands back the endpoint, so it can be removed again later.
    /// <para>
    /// Routes are not frozen when the server starts: the route table is swapped atomically, so one
    /// added now is reachable on the very next request and one removed stops matching immediately.
    /// Middleware is a different matter — that pipeline is composed once.
    /// </para>
    /// </summary>
    public RouteEndpoint MapRoute(string method, string pattern, RequestDelegate handler, params object[]? metadata)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(handler);
        ObjectDisposedException.ThrowIf(this.disposed, this);

        var endpoint = new RouteEndpoint(handler, method.ToUpperInvariant(), RouteTemplate.Parse(pattern), metadata);
        this.Router.Add(endpoint);

        return endpoint;
    }

    /// <summary>Removes a route. Returns false when nothing was registered for it.</summary>
    public bool Unmap(string method, string pattern) => this.Router.Remove(method, pattern);

    /// <summary>Removes a route by the endpoint <see cref="MapRoute"/> returned.</summary>
    public bool Unmap(RouteEndpoint endpoint) => this.Router.Remove(endpoint);

    /// <summary>Removes every route matching a predicate. Returns how many went.</summary>
    public int UnmapAll(Func<RouteEndpoint, bool> predicate) => this.Router.RemoveAll(predicate);

    /// <summary>Removes every route. The <see cref="OnRequest(RequestDelegate)"/> handler stays.</summary>
    public HttpServer ClearRoutes()
    {
        this.Router.Clear();
        return this;
    }

    public HttpServer MapGet(string pattern, RequestDelegate handler) => this.Map(HttpMethods.Get, pattern, handler);

    public HttpServer MapPost(string pattern, RequestDelegate handler) => this.Map(HttpMethods.Post, pattern, handler);

    public HttpServer MapPut(string pattern, RequestDelegate handler) => this.Map(HttpMethods.Put, pattern, handler);

    public HttpServer MapDelete(string pattern, RequestDelegate handler) => this.Map(HttpMethods.Delete, pattern, handler);

    public HttpServer MapPatch(string pattern, RequestDelegate handler) => this.Map(HttpMethods.Patch, pattern, handler);

    // ---- Tier 2: middleware ----

    /// <summary>
    /// Adds middleware. Runs in registration order, wrapping routing and the
    /// <see cref="OnRequest(RequestDelegate)"/> handler, same as ASP.NET Core.
    /// </summary>
    public HttpServer Use(MiddlewareDelegate middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);
        this.ThrowIfStarted();

        this.middleware.Add(middleware);
        return this;
    }

    /// <summary>
    /// Middleware in its more readable form: receive the context and the rest of the pipeline.
    /// <code>
    /// server.Use(async (ctx, next) => { var sw = Stopwatch.StartNew(); await next(ctx); Log(sw.Elapsed); });
    /// </code>
    /// </summary>
    public HttpServer Use(Func<HttpContext, RequestDelegate, ValueTask> middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);
        return this.Use(next => ctx => middleware(ctx, next));
    }

    /// <summary>
    /// Adds an already-constructed <see cref="IHttpMiddleware"/>. The same instance serves every
    /// request, so it must be thread-safe and hold no per-request state.
    /// </summary>
    public HttpServer Use(IHttpMiddleware middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);
        return this.Use(next => ctx => middleware.InvokeAsync(ctx, next));
    }

    /// <summary>
    /// Adds an <see cref="IHttpMiddleware"/> resolved from the container.
    /// <para>
    /// Resolution happens per request, from the request's own scope, so a middleware registered
    /// <c>Scoped</c> gets the same instance as everything else handling that request and one
    /// registered <c>Singleton</c> costs a dictionary lookup. Constructing it here instead would
    /// mean reflection, which is the one thing this server does not do.
    /// </para>
    /// </summary>
    public HttpServer Use<TMiddleware>() where TMiddleware : class, IHttpMiddleware
    {
        this.ThrowIfStarted();
        return this.Use(Resolve<TMiddleware>());
    }

    /// <summary>
    /// Adds middleware that runs <em>after</em> the router has chosen an endpoint, wrapping only the
    /// endpoint's own invocation.
    /// <para>
    /// The difference from <see cref="Use(MiddlewareDelegate)"/> is <c>ctx.Endpoint</c>: here it is
    /// populated, so the middleware can read the endpoint's metadata and decide accordingly. That is
    /// what authorization needs — <c>[Authorize]</c> is a property of the endpoint, and there is no
    /// endpoint yet before routing has run. Requests that matched nothing skip this stage entirely
    /// and go straight to the 404 or 405.
    /// </para>
    /// </summary>
    public HttpServer UseAfterRouting(MiddlewareDelegate middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);
        this.ThrowIfStarted();

        this.afterRouting.Add(middleware);
        return this;
    }

    /// <summary><see cref="UseAfterRouting(MiddlewareDelegate)"/> in the readable two-argument form.</summary>
    public HttpServer UseAfterRouting(Func<HttpContext, RequestDelegate, ValueTask> middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);
        return this.UseAfterRouting(next => ctx => middleware(ctx, next));
    }

    /// <summary><see cref="UseAfterRouting(MiddlewareDelegate)"/> with an already-constructed instance.</summary>
    public HttpServer UseAfterRouting(IHttpMiddleware middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);
        return this.UseAfterRouting(next => ctx => middleware.InvokeAsync(ctx, next));
    }

    /// <summary><see cref="UseAfterRouting(MiddlewareDelegate)"/> resolved from the request scope.</summary>
    public HttpServer UseAfterRouting<TMiddleware>() where TMiddleware : class, IHttpMiddleware
    {
        this.ThrowIfStarted();
        return this.UseAfterRouting(Resolve<TMiddleware>());
    }

    static MiddlewareDelegate Resolve<TMiddleware>() where TMiddleware : class, IHttpMiddleware
        => next => ctx =>
        {
            var middleware = ctx.RequestServices.GetService(typeof(TMiddleware)) as TMiddleware
                ?? throw new InvalidOperationException(
                    $"No service is registered for middleware '{typeof(TMiddleware).Name}'. " +
                    $"Register it (services.AddSingleton<{typeof(TMiddleware).Name}>()) or pass an " +
                    "instance to Use(IHttpMiddleware) instead."
                );

            return middleware.InvokeAsync(ctx, next);
        };

    // ---- Lifecycle ----
    //
    // Start and stop are ordinary operations here, not just startup and shutdown. An app with a
    // "share over Wi-Fi" toggle flips this switch repeatedly over one process lifetime, so the
    // transitions are serialized, idempotent, and leave the server genuinely restartable.

    /// <summary>Where the server is in its lifecycle. Changes are announced by <see cref="StateChanged"/>.</summary>
    public HttpServerState State { get; private set; } = HttpServerState.Stopped;

    /// <summary>
    /// The transition that put the server in <see cref="State"/>, reason and cause included. Null
    /// until the server first moves.
    /// <para>
    /// The same information <see cref="StateTransitioned"/> raises, kept for whoever was not
    /// subscribed at the time — a crash reporter assembling context, a diagnostics screen the user
    /// opens after the fact, a background task that woke up to find the server down.
    /// </para>
    /// </summary>
    public HttpServerStateChange? LastStateChange { get; private set; }

    /// <summary>
    /// Raised on every state transition, on the thread that caused it. Useful for binding a UI to
    /// the server without polling.
    /// <para>
    /// Says what happened and not why; <see cref="StateTransitioned"/> carries the reason and the
    /// exception. A handler that throws is caught and logged rather than taking the transition — or
    /// the server — down with it.
    /// </para>
    /// </summary>
    public event EventHandler<HttpServerState>? StateChanged;

    /// <summary>
    /// The same transitions as <see cref="StateChanged"/>, each carrying why it happened and the
    /// exception behind it when there was one.
    /// <para>
    /// This is the event to subscribe to when the question is "the server stopped, was that us?".
    /// <see cref="HttpServerStateReason.Requested"/> means the app asked;
    /// <see cref="HttpServerStateReason.BindFailed"/> and
    /// <see cref="HttpServerStateReason.ListenerFaulted"/> mean it fell over, and
    /// <see cref="HttpServerStateChange.Exception"/> says what with.
    /// </para>
    /// </summary>
    public event EventHandler<HttpServerStateChange>? StateTransitioned;

    /// <summary>
    /// Raised when the machine's IP addresses change while the server is running — a phone moving
    /// between Wi-Fi networks, a hotspot coming up, cellular taking over.
    /// <para>
    /// Raised whether or not <see cref="HttpServerOptions.RebindOnNetworkChange"/> is on, and after
    /// the rebind when it is, so a handler always sees the addresses the server is actually on. The
    /// obvious things to do with it: re-render the QR code, re-announce the mDNS advertisement,
    /// update the "reachable at" line in the UI.
    /// </para>
    /// </summary>
    public event EventHandler<IReadOnlyList<System.Net.IPAddress>>? NetworkAddressesChanged;

    /// <summary>
    /// Binds the listener and begins accepting. Returns as soon as the server is listening.
    /// <para>
    /// Idempotent: starting an already-running server does nothing rather than throwing, because
    /// the caller is often a button and a double tap is not a bug.
    /// </para>
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        await this.lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await this.StartCoreAsync(cancellationToken, HttpServerStateReason.Requested).ConfigureAwait(false);
        }
        finally
        {
            this.lifecycle.Release();
        }
    }

    /// <summary>
    /// Stops accepting, then waits for in-flight requests to finish. Connections still running when
    /// <paramref name="cancellationToken"/> fires are aborted. Idempotent.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await this.lifecycle.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await this.StopCoreAsync(cancellationToken, HttpServerStateReason.Requested).ConfigureAwait(false);
        }
        finally
        {
            this.lifecycle.Release();
        }
    }

    /// <summary>
    /// Stops and starts again as one operation, picking up any change to <see cref="Options"/> —
    /// a new port, or TLS that was configured after the fact.
    /// <para>
    /// Routes and middleware are not re-read: the pipeline is composed once and stays composed.
    /// </para>
    /// <para>
    /// The start half retries — see <see cref="HttpServerOptions.StartRetryAttempts"/>. A restart is
    /// the one operation where a failure leaves the server worse off than not having tried: it was
    /// running a moment ago, and a bind refused because the network is half up or the old port is
    /// still in TIME_WAIT would otherwise leave it stopped for good. If the retries are spent this
    /// still throws, and the transition to <see cref="HttpServerState.Stopped"/> carries
    /// <see cref="HttpServerStateReason.BindFailed"/> with the exception attached.
    /// </para>
    /// </summary>
    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        await this.lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await this.RestartCoreAsync(cancellationToken, HttpServerStateReason.Restarting).ConfigureAwait(false);
        }
        finally
        {
            this.lifecycle.Release();
        }
    }

    /// <summary>
    /// Starts the server and runs until <paramref name="cancellationToken"/> is signalled, then
    /// shuts down gracefully. The one-liner for a console host.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        await this.StartAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal exit path — the caller cancelled.
        }

        await this.StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (this.disposed)
            return;

        this.disposed = true;

        await this.lifecycle.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await this.StopCoreAsync(CancellationToken.None, HttpServerStateReason.Disposed).ConfigureAwait(false);
        }
        finally
        {
            this.lifecycle.Release();
        }

        this.stopping.Dispose();
        this.lifecycle.Dispose();
        this.connectionLimit?.Dispose();
    }

    async Task RestartCoreAsync(CancellationToken cancellationToken, HttpServerStateReason reason)
    {
        await this.StopCoreAsync(cancellationToken, reason).ConfigureAwait(false);
        await this.StartCoreAsync(cancellationToken, reason).ConfigureAwait(false);
    }

    async Task StartCoreAsync(CancellationToken cancellationToken, HttpServerStateReason reason)
    {
        if (this.acceptLoops is not null)
        {
            this.logger.LogDebug("Start requested while already listening on {Url}", this.ListenUrl);
            return;
        }

        this.SetState(HttpServerState.Starting, reason);

        // A start the app asked for reports its failure straight back to the caller, which is the
        // loudest signal available and the one a "share over Wi-Fi" toggle already handles; retrying
        // behind its back would only make the button appear stuck. A start the *server* asked for —
        // the second half of a restart, a rebind after the addresses moved, a recovery from a dead
        // listener — has nobody to throw to, and that is the one that used to leave a device
        // silently unreachable until some unrelated event happened along.
        var attempts = reason == HttpServerStateReason.Requested
            ? 1
            : Math.Max(1, this.Options.StartRetryAttempts);

        for (var attempt = 1; ; attempt++)
        {
            TimeSpan retryIn;
            try
            {
                await this.BindAndBeginAcceptingAsync(cancellationToken).ConfigureAwait(false);
                this.SetState(HttpServerState.Running, reason);
                return;
            }
            catch (OperationCanceledException)
            {
                // Not a fault: the caller gave up, or a stop landed while the bind was in flight.
                // The server is where it was asked to be, so the reason stays what it was.
                this.SetState(HttpServerState.Stopped, reason);
                throw;
            }
            catch (Exception ex) when (attempt < attempts)
            {
                retryIn = Backoff(this.Options.StartRetryDelay, this.Options.StartRetryMaxDelay, attempt);
                this.logger.LogWarning(
                    ex,
                    "Failed to start the server ({Reason}); attempt {Attempt} of {Attempts}, retrying in {Delay}",
                    reason,
                    attempt,
                    attempts,
                    retryIn
                );
            }
            catch (Exception ex)
            {
                // The end of the line. Logged at Error and not Warning on purpose: from here the
                // server is down and nothing in this library will bring it back, so this line is the
                // one that has to survive a production log filter.
                this.logger.LogError(
                    ex,
                    "The server failed to start after {Attempts} attempt(s) ({Reason}) and is now stopped; it will not come back on its own",
                    attempts,
                    reason
                );

                this.SetState(HttpServerState.Stopped, HttpServerStateReason.BindFailed, ex);
                throw;
            }

            try
            {
                await Task.Delay(retryIn, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                this.SetState(HttpServerState.Stopped, reason);
                throw;
            }
        }
    }

    /// <summary>
    /// One start attempt: bind every endpoint, start accepting, arrange for the accept loops to be
    /// watched. Throws on failure with nothing left bound, which is what makes it safe to retry.
    /// <para>
    /// Split out from <see cref="StartCoreAsync"/> so the retry loop lives above the state machine
    /// rather than inside it — a server that is on its third attempt is still
    /// <see cref="HttpServerState.Starting"/>, not flickering between Starting and Stopped once per
    /// attempt and telling every subscriber about it.
    /// </para>
    /// </summary>
    async Task BindAndBeginAcceptingAsync(CancellationToken cancellationToken)
    {
        var bound = new List<IConnectionListener>();
        try
        {
            this.EnsurePipeline();
            this.stopping = new CancellationTokenSource();
            this.stopRequested = false;

            var endpoints = this.Options.ResolveEndpoints();
            if (endpoints.Count == 0)
                throw new InvalidOperationException("The server has no endpoints to listen on.");

            for (var i = 0; i < endpoints.Count; i++)
            {
                var connectionListener = this.CreateListener(endpoints[i], i);

                // Bound one at a time so a partial failure — the second port already in use —
                // is caught here and unwound, rather than leaving the server half listening.
                await connectionListener.BindAsync(cancellationToken).ConfigureAwait(false);
                bound.Add(connectionListener);
            }

            this.listeners = bound;

            var token = this.stopping.Token;
            var loops = Task.WhenAll(
                bound.Select(x => Task.Run(() => this.AcceptLoopAsync(x, token), CancellationToken.None))
            );

            this.acceptLoops = loops;

            // Nothing awaits the accept loops until StopCoreAsync, and on a server that has quietly
            // stopped listening that call may never come — so a faulted loop sat unobserved until
            // the finalizer thread surfaced it through TaskScheduler.UnobservedTaskException, if
            // ever. This continuation is the only thing between a dead listener and a server that
            // goes on reporting Running with nothing behind it.
            _ = loops.ContinueWith(
                this.OnListeningEnded,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default
            );

            foreach (var connectionListener in bound)
                this.logger.LogInformation("Listening on {Url}", connectionListener.ListenDescription);

            this.StartWatchingTheNetwork();
        }
        catch
        {
            // A failed attempt must not leave the endpoints that did bind holding their ports, nor
            // an accept-loop task that the next attempt would mistake for a running server.
            this.acceptLoops = null;

            foreach (var connectionListener in bound)
            {
                try
                {
                    await connectionListener.UnbindAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    this.logger.LogDebug(ex, "Failed to unbind {Url} while unwinding a failed start", connectionListener.ListenDescription);
                }
            }

            this.listeners = [];
            throw;
        }
    }

    async Task StopCoreAsync(CancellationToken cancellationToken, HttpServerStateReason reason, Exception? cause = null)
    {
        if (this.acceptLoops is null)
            return;

        // Before anything is unbound, so the accept loops can tell an ordinary shutdown from a
        // listener disappearing underneath them.
        this.stopRequested = true;

        this.SetState(HttpServerState.Stopping, reason, cause);
        this.logger.LogInformation("Shutting down");

        this.networkWatcher?.Dispose();
        this.networkWatcher = null;

        foreach (var connectionListener in this.listeners)
            await connectionListener.UnbindAsync(cancellationToken).ConfigureAwait(false);

        await this.stopping.CancelAsync().ConfigureAwait(false);

        try
        {
            await this.acceptLoops.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected: cancelling the accept loop is how we stop.
        }
        catch (Exception ex)
        {
            // A loop that faulted has already been reported by OnListeningEnded — this await is here
            // to drain it, not to discover it. Letting it out would turn a stop the app asked for
            // into a throw from StopAsync, over a listener that was going away regardless.
            this.logger.LogDebug(ex, "An accept loop had already faulted when the server was stopped");
        }

        Task[] inFlight;
        lock (this.connectionsLock)
            inFlight = [.. this.connections];

        if (inFlight.Length > 0)
        {
            var drained = Task.WhenAll(inFlight);
            var completed = await Task
                .WhenAny(drained, Task.Delay(Timeout.Infinite, cancellationToken))
                .ConfigureAwait(false);

            if (completed != drained)
                this.logger.LogWarning("{Count} connection(s) still active at shutdown; abandoning them", inFlight.Length);
        }

        this.acceptLoops = null;
        this.listeners = [];
        this.SetState(HttpServerState.Stopped, reason, cause);
    }

    void SetState(HttpServerState state, HttpServerStateReason reason, Exception? exception = null)
    {
        if (this.State == state)
            return;

        this.State = state;

        var change = new HttpServerStateChange(state, reason, exception);
        this.LastStateChange = change;

        // The level says who caused it. A transition the app asked for is one the app already knows
        // about, so it stays at Debug; one the server decided on its own — a rebind, a recovery, a
        // listener that died — is the line whoever is reading a production log actually needs. An
        // attached exception makes it an error whatever the reason was.
        if (exception is not null)
            this.logger.LogError(exception, "Server state -> {State} ({Reason})", state, reason);
        else if (reason == HttpServerStateReason.Requested)
            this.logger.LogDebug("Server state -> {State} ({Reason})", state, reason);
        else
            this.logger.LogInformation("Server state -> {State} ({Reason})", state, reason);

        this.Raise(this.StateChanged, state, nameof(this.StateChanged));
        this.Raise(this.StateTransitioned, change, nameof(this.StateTransitioned));
    }

    /// <summary>
    /// Raises an event without letting a subscriber's failure become the server's.
    /// <para>
    /// These run on the lifecycle thread, inside <see cref="StartCoreAsync"/>'s own catch, so a UI
    /// handler that threw used to be caught there — which unwound a start that had already succeeded
    /// and took the server down over something drawing a button.
    /// </para>
    /// <para>
    /// Subscribers are invoked one at a time rather than through the multicast delegate, because
    /// <c>Invoke</c> stops at the first one that throws and silently skips the rest: a broken UI
    /// binding would also stop the mDNS advertiser and the background-execution task from ever
    /// hearing that the server moved. The array this allocates is paid for at most a handful of
    /// times per start/stop cycle, which is not a rate worth optimising.
    /// </para>
    /// </summary>
    void Raise<TArgs>(EventHandler<TArgs>? handlers, TArgs args, string eventName)
    {
        if (handlers is null)
            return;

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<TArgs>)handler).Invoke(this, args);
            }
            catch (Exception ex)
            {
                this.logger.LogError(ex, "A {Event} handler threw and was ignored", eventName);
            }
        }
    }

    IConnectionListener CreateListener(HttpServerEndpoint endpoint, int index)
        => this.ListenerFactory?.Invoke(this.Options, endpoint, index)
            ?? new SocketConnectionListener(
                this.Options,
                endpoint,
                this.loggerFactory.CreateLogger<SocketConnectionListener>(),
                index
            );

    /// <summary>
    /// Test seam. Everything this class does about resilience is a reaction to a listener
    /// misbehaving, and a real socket listener cannot be made to abort, return null, or fail to
    /// accept on demand — the only behaviour worth testing is the behaviour the OS will not perform
    /// to order.
    /// </summary>
    internal Func<HttpServerOptions, HttpServerEndpoint, int, IConnectionListener>? ListenerFactory { get; set; }

    /// <summary>
    /// Doubling backoff, clamped. Computed in ticks from the attempt number rather than by repeated
    /// addition so a long-lived failure cannot shift its way to a negative delay.
    /// </summary>
    static TimeSpan Backoff(TimeSpan initial, TimeSpan max, int attempt)
    {
        if (initial <= TimeSpan.Zero)
            return TimeSpan.Zero;

        var ticks = initial.Ticks * (1L << Math.Min(attempt - 1, 16));
        return TimeSpan.FromTicks(Math.Min(ticks, Math.Max(max.Ticks, initial.Ticks)));
    }

    /// <summary>
    /// Serves one already-established connection, returning when it closes. The listener is not
    /// involved, which is what lets a tunnel hand the server connections that arrived from
    /// somewhere else entirely — the request path cannot tell the difference.
    /// <para>
    /// Usable without <see cref="StartAsync"/>: a phone app reachable only through a tunnel never
    /// binds a local port at all.
    /// </para>
    /// </summary>
    public async Task ServeAsync(IConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ObjectDisposedException.ThrowIf(this.disposed, this);

        this.EnsurePipeline();

        // Tunnelled connections count against MaxConcurrentConnections like any other, and — just
        // as importantly — take the slot that ServeConnectionAsync unconditionally gives back.
        if (this.connectionLimit is not null)
            await this.connectionLimit.WaitAsync(cancellationToken).ConfigureAwait(false);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(this.stopping.Token, cancellationToken);
        var task = this.ServeConnectionAsync(connection, linked.Token);

        // Tracked so a graceful shutdown drains tunnelled requests exactly like local ones.
        lock (this.connectionsLock)
            this.connections.Add(task);

        try
        {
            await task.ConfigureAwait(false);
        }
        finally
        {
            lock (this.connectionsLock)
                this.connections.Remove(task);
        }
    }

    // ---- Internals ----

    /// <summary>
    /// The composed pipeline, for a transport that runs alongside the TCP listener — HTTP/3 has its
    /// own socket and its own connection type, but must serve the same routes and middleware.
    /// </summary>
    internal RequestDelegate BuildPipelineForTransport() => this.EnsurePipeline();

    RequestDelegate EnsurePipeline()
    {
        // Double-checked because ServeAsync can be called concurrently by a tunnel before (or
        // instead of) StartAsync, and the pipeline must be composed exactly once.
        if (this.pipeline is { } existing)
            return existing;

        lock (this.connectionsLock)
            return this.pipeline ??= this.BuildPipeline();
    }

    /// <summary>
    /// Composes middleware around routing around the terminal handler. Built once at start so the
    /// per-request path is a plain delegate call with nothing to look up.
    /// </summary>
    RequestDelegate BuildPipeline()
    {
        var terminal = this.terminalHandler ?? NotFound;

        // The innermost stage: invoke whatever the router selected. After-routing middleware wraps
        // this and nothing else, so it sees a populated ctx.Endpoint and never runs for a request
        // that matched no route.
        RequestDelegate invoke = static ctx => ctx.Endpoint!.RequestDelegate(ctx);

        for (var i = this.afterRouting.Count - 1; i >= 0; i--)
            invoke = this.afterRouting[i](invoke);

        // Always composed in, even with an empty table. Skipping it would be a micro-optimisation
        // that quietly made every route added after startup unreachable; walking an empty trie is
        // two null checks.
        RequestDelegate app = new RoutingMiddleware(this.Router, terminal, invoke).InvokeAsync;

        for (var i = this.middleware.Count - 1; i >= 0; i--)
            app = this.middleware[i](app);

        return app;
    }

    /// <summary>
    /// The terminal handler when nothing matched.
    /// <para>
    /// Deliberately does not flush. Starting the response here would put the headers on the wire
    /// before any middleware had unwound, which makes a 404 the one status no middleware can act on
    /// — and giving it a body is exactly what problem details exists to do. The connection writes
    /// the head when it completes the response, so an untouched 404 costs nothing either way.
    /// </para>
    /// </summary>
    static ValueTask NotFound(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        context.Response.ContentLength = 0;

        return default;
    }

    /// <summary>
    /// Accepts until told to stop — and, crucially, never ends quietly for any other reason. Every
    /// exit that is not a stop leaves through <see cref="HttpServerListenerException"/>, which
    /// <see cref="OnListeningEnded"/> turns into a logged fault and a rebind.
    /// </summary>
    async Task AcceptLoopAsync(IConnectionListener connectionListener, CancellationToken cancellationToken)
    {
        // Consecutive, and reset by every connection accepted: see AcceptRetryAttempts for why the
        // loop counts failures instead of classifying them.
        var consecutiveFailures = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            if (this.connectionLimit is not null)
            {
                try
                {
                    await this.connectionLimit.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            IConnection? connection = null;
            Exception? failure = null;
            try
            {
                connection = await connectionListener.AcceptAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Shutting down.
            }
            catch (Exception ex)
            {
                // Anything the listener did not handle itself: a transient SocketException, an
                // interface torn down mid-accept, descriptor exhaustion. This used to escape and
                // fault the accept-loop task, where nobody was looking.
                failure = ex;
            }

            if (connection is not null)
            {
                consecutiveFailures = 0;
                this.TrackConnection(connection, cancellationToken);
                continue;
            }

            // Nothing was accepted, so the slot taken above goes back before anything below returns
            // or throws. The alternative is a server that loses one connection slot per transient
            // accept failure until it can accept nothing at all.
            this.connectionLimit?.Release();

            if (cancellationToken.IsCancellationRequested || this.stopRequested)
                return;

            if (failure is null)
            {
                // The listener said "no more connections" while nobody had asked it to stop: its
                // socket was disposed or aborted underneath us. This is the exit that used to be a
                // bare `return` — the server went on reporting Running with no listener behind it,
                // which from the outside is a toggle that reads "on" and a port that refuses
                // everything, and toggling it off and on changes nothing because it already is on.
                throw new HttpServerListenerException(
                    $"The listener on {connectionListener.ListenDescription} stopped accepting while the server was still running."
                );
            }

            if (++consecutiveFailures > this.Options.AcceptRetryAttempts)
            {
                throw new HttpServerListenerException(
                    $"Accepting on {connectionListener.ListenDescription} failed {consecutiveFailures} times in a row; treating the listener as dead.",
                    failure
                );
            }

            var retryIn = Backoff(this.Options.AcceptRetryDelay, this.Options.AcceptRetryMaxDelay, consecutiveFailures);
            this.logger.LogWarning(
                failure,
                "Failed to accept on {Url}; failure {Attempt} of {Attempts}, retrying in {Delay}",
                connectionListener.ListenDescription,
                consecutiveFailures,
                this.Options.AcceptRetryAttempts,
                retryIn
            );

            try
            {
                await Task.Delay(retryIn, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Runs when every accept loop has finished, however it finished. The whole point is the case
    /// where nobody asked them to.
    /// </summary>
    void OnListeningEnded(Task loops)
    {
        // The ordinary path. Note this cannot be decided from the stopping token alone:
        // StopCoreAsync unbinds the listeners *before* it cancels — cancelling first would abort the
        // very requests it is trying to drain — and in that window a normal shutdown is
        // indistinguishable from a listener that died.
        if (this.stopRequested || this.disposed)
            return;

        // Reading .Exception is also what marks the fault observed, so this must happen whatever we
        // decide to do next. A loop that ended without throwing gets a stand-in, because "the server
        // went down and here is no exception at all" is exactly the silence being fixed.
        var cause = loops.Exception?.GetBaseException()
            ?? new HttpServerListenerException("The accept loop ended while the server was still running.");

        this.logger.LogError(cause, "The server stopped listening while it believed it was running");

        // Off this thread: the continuation runs synchronously on whichever thread completed the
        // last accept loop, and what follows takes the lifecycle lock and rebinds a socket.
        _ = Task.Run(() => this.RecoverFromListenerFaultAsync(loops, cause), CancellationToken.None);
    }

    async Task RecoverFromListenerFaultAsync(Task loops, Exception cause)
    {
        try
        {
            await this.lifecycle.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Disposed while this was queued. There is nothing left to recover.
            return;
        }

        try
        {
            // Identity rather than state: between the loops ending and this running, a stop may have
            // completed and a start may have put fresh listeners behind a fresh task. Acting on the
            // old generation then would tear down a server that is perfectly healthy.
            if (this.disposed || !ReferenceEquals(this.acceptLoops, loops))
                return;

            // Taken down first and honestly — a consumer sees Stopped with the fault attached — and
            // only then brought back. Reporting Running throughout a rebind would be the same lie
            // that made this bug invisible in the first place.
            await this.StopCoreAsync(CancellationToken.None, HttpServerStateReason.ListenerFaulted, cause).ConfigureAwait(false);

            if (!this.Options.RecoverFromListenerFaults)
            {
                this.logger.LogError(
                    cause,
                    "The server is stopped and {Option} is off, so it will not come back on its own",
                    nameof(HttpServerOptions.RecoverFromListenerFaults)
                );
                return;
            }

            await this.StartCoreAsync(CancellationToken.None, HttpServerStateReason.ListenerFaulted).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // StartCoreAsync has already spent its retries and reported the failure with the reason
            // attached. There is no caller above this to rethrow to — this method *is* the top of
            // the stack — so it stops here rather than becoming an unhandled exception.
            this.logger.LogError(ex, "Failed to recover from a listener fault; the server is stopped");
        }
        finally
        {
            this.lifecycle.Release();
        }
    }

    void TrackConnection(IConnection connection, CancellationToken cancellationToken)
    {
        var task = Task.Run(() => this.ServeConnectionAsync(connection, cancellationToken), CancellationToken.None);

        lock (this.connectionsLock)
            this.connections.Add(task);

        // Removal is scheduled here rather than inside ServeConnectionAsync so the task is
        // guaranteed to be in the set before anything can try to remove it.
        _ = task.ContinueWith(
            completed =>
            {
                lock (this.connectionsLock)
                    this.connections.Remove(completed);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
    }

    async Task ServeConnectionAsync(IConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            // TLS happens here rather than on the accept loop. A handshake is at best a round trip
            // and at worst never finishes, and the accept loop serves every other client.
            if (connection is IConnectionInitializer initializer)
                await initializer.InitializeAsync(cancellationToken).ConfigureAwait(false);

            if (await this.IsHttp2Async(connection, cancellationToken).ConfigureAwait(false))
            {
                var http2 = new Http2Connection(
                    connection,
                    this.Options,
                    this.pipeline!,
                    this.Services,
                    this.loggerFactory.CreateLogger<Http2Connection>()
                );

                await http2.ProcessAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            var http = new Http1Connection(
                connection,
                this.Options,
                this.pipeline!,
                this.Services,
                this.loggerFactory.CreateLogger<Http1Connection>()
            );

            await http.ProcessAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AuthenticationException or OperationCanceledException)
        {
            // Routine on a TLS endpoint: a client that spoke cleartext to the https port, refused
            // our certificate, or went quiet mid-handshake. Not a server fault.
            this.logger.LogDebug(ex, "Connection {ConnectionId} did not complete its handshake", connection.ConnectionId);
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "Connection {ConnectionId} faulted", connection.ConnectionId);
        }
        finally
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            this.connectionLimit?.Release();
        }
    }

    /// <summary>
    /// Decides which protocol this connection speaks.
    /// <para>
    /// Never a guess. Over TLS it is whatever ALPN agreed — the negotiation already happened and
    /// overriding it would break the client's expectations. Over cleartext the only legitimate
    /// signal is the client opening with the HTTP/2 connection preface, so the first bytes are
    /// peeked (not consumed) and compared against it. Anything else is HTTP/1.1.
    /// </para>
    /// </summary>
    async ValueTask<bool> IsHttp2Async(IConnection connection, CancellationToken cancellationToken)
    {
        if (!this.Options.Http2.Enabled)
            return false;

        if (connection.ApplicationProtocol is { } negotiated)
            return negotiated == "h2";

        // TLS without h2 in ALPN means the client asked for HTTP/1.1; sniffing past that would be
        // second-guessing a negotiation that already concluded.
        if (connection.IsEncrypted || !this.Options.Http2.AllowCleartext)
            return false;

        // The length is captured as an int rather than holding the span itself: a ReadOnlySpan
        // cannot survive an await.
        var prefaceLength = Http2Frame.Preface.Length;

        while (true)
        {
            var result = await connection.Input.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;

            var comparable = (int)Math.Min(buffer.Length, prefaceLength);

            // Decided the moment the bytes diverge, not once a full preface could have arrived.
            // "GET / HTTP/1.1\r\n\r\n" is shorter than the preface, so waiting for 24 bytes would
            // hang every short HTTP/1.1 request until its client gave up.
            if (comparable > 0 && !MatchesPrefacePrefix(buffer, comparable))
            {
                connection.Input.AdvanceTo(buffer.Start, buffer.Start);
                return false;
            }

            if (buffer.Length >= prefaceLength)
            {
                // Examined but not consumed: whichever protocol wins reads these bytes itself.
                connection.Input.AdvanceTo(buffer.Start, buffer.GetPosition(prefaceLength));
                return true;
            }

            connection.Input.AdvanceTo(buffer.Start, buffer.End);

            if (result.IsCompleted || result.IsCanceled)
                return false;
        }

        static bool MatchesPrefacePrefix(in ReadOnlySequence<byte> buffer, int count)
        {
            Span<byte> head = stackalloc byte[Http2Frame.Preface.Length];
            head = head[..count];
            buffer.Slice(0, count).CopyTo(head);

            return head.SequenceEqual(Http2Frame.Preface[..count]);
        }
    }

    /// <summary>
    /// Starts watching for address changes, if anything is going to act on them.
    /// <para>
    /// Skipped entirely when nobody is listening and rebinding is off, so a server on a machine
    /// that never moves does not pay for an event subscription it will never use.
    /// </para>
    /// </summary>
    void StartWatchingTheNetwork()
    {
        if (this.networkWatcher is not null)
            return;

        if (!this.Options.RebindOnNetworkChange && this.NetworkAddressesChanged is null)
            return;

        this.networkWatcher = new NetworkChangeWatcher(
            this.OnNetworkChangedAsync,
            this.Options.NetworkChangeDebounce,
            this.logger
        );
    }

    async Task OnNetworkChangedAsync(CancellationToken cancellationToken)
    {
        if (this.Options.RebindOnNetworkChange && this.acceptLoops is not null)
        {
            try
            {
                // RestartAsync is not reused here only because the reason has to reach the
                // transitions: a consumer watching the server go down on a train needs to see
                // NetworkChanged rather than a bare Stopped. The retry it depends on lives in
                // StartCoreAsync, and this path is the reason it exists — a phone that flips from
                // Wi-Fi to cellular gets one shot at a bind on a network that is still coming up,
                // and if no further address change ever arrives the server never comes back.
                await this.lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await this.RestartCoreAsync(cancellationToken, HttpServerStateReason.NetworkChanged).ConfigureAwait(false);
                }
                finally
                {
                    this.lifecycle.Release();
                }
            }
            catch (OperationCanceledException)
            {
                // The server was stopped while this change was settling. Restarting it now would
                // bring back a server the caller has already shut down.
                return;
            }
            catch (Exception ex)
            {
                // Every retry is spent and StartCoreAsync has already reported the failure against
                // the transition. This line only records which path gave up, and that the server is
                // now down for good unless another address change happens along.
                this.logger.LogError(ex, "Failed to rebind after a network address change; the server is stopped");
            }
        }

        // Same rule as the state events: a handler redrawing a QR code is not allowed to be the
        // reason a rebind looks like a failure, nor to stop the next handler being told.
        this.Raise(this.NetworkAddressesChanged, LocalAddresses.Current(), nameof(this.NetworkAddressesChanged));
    }

    void ThrowIfStarted()
    {
        // Keyed off the composed pipeline rather than the accept loop: a tunnel-only server never
        // starts a listener, but its pipeline is just as frozen once requests are flowing.
        if (this.pipeline is not null)
            throw new InvalidOperationException(
                "Routes and middleware must be registered before the server starts serving requests."
            );
    }
}
