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

    RequestDelegate? terminalHandler;
    RequestDelegate? pipeline;
    IReadOnlyList<SocketConnectionListener> listeners = [];
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

    /// <summary>Entry point for the DI-first path: configure services, then <c>Build()</c>.</summary>
    public static HttpServerBuilder CreateBuilder() => new();

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

    // On* reads better next to OnRequest — "when a GET for /hello arrives, do this" — and is the
    // spelling the tier-1 examples use. Map* is kept because it is what ASP.NET Core calls these.

    public HttpServer OnGet(string pattern, RequestDelegate handler) => this.Map(HttpMethods.Get, pattern, handler);

    public HttpServer OnPost(string pattern, RequestDelegate handler) => this.Map(HttpMethods.Post, pattern, handler);

    public HttpServer OnPut(string pattern, RequestDelegate handler) => this.Map(HttpMethods.Put, pattern, handler);

    public HttpServer OnDelete(string pattern, RequestDelegate handler) => this.Map(HttpMethods.Delete, pattern, handler);

    public HttpServer OnPatch(string pattern, RequestDelegate handler) => this.Map(HttpMethods.Patch, pattern, handler);

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
    /// Raised on every state transition, on the thread that caused it. Useful for binding a UI to
    /// the server without polling.
    /// </summary>
    public event EventHandler<HttpServerState>? StateChanged;

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
            await this.StartCoreAsync(cancellationToken).ConfigureAwait(false);
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
            await this.StopCoreAsync(cancellationToken).ConfigureAwait(false);
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
    /// </summary>
    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        await this.lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await this.StopCoreAsync(cancellationToken).ConfigureAwait(false);
            await this.StartCoreAsync(cancellationToken).ConfigureAwait(false);
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

        await this.StopAsync(CancellationToken.None).ConfigureAwait(false);

        this.stopping.Dispose();
        this.lifecycle.Dispose();
        this.connectionLimit?.Dispose();
    }

    async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        if (this.acceptLoops is not null)
        {
            this.logger.LogDebug("Start requested while already listening on {Url}", this.ListenUrl);
            return;
        }

        this.SetState(HttpServerState.Starting);

        var bound = new List<SocketConnectionListener>();
        try
        {
            this.EnsurePipeline();
            this.stopping = new CancellationTokenSource();

            var endpoints = this.Options.ResolveEndpoints();
            if (endpoints.Count == 0)
                throw new InvalidOperationException("The server has no endpoints to listen on.");

            for (var i = 0; i < endpoints.Count; i++)
            {
                var connectionListener = new SocketConnectionListener(
                    this.Options,
                    endpoints[i],
                    this.loggerFactory.CreateLogger<SocketConnectionListener>(),
                    i
                );

                // Bound one at a time so a partial failure — the second port already in use —
                // is caught here and unwound, rather than leaving the server half listening.
                await connectionListener.BindAsync(cancellationToken).ConfigureAwait(false);
                bound.Add(connectionListener);
            }

            this.listeners = bound;

            var token = this.stopping.Token;
            this.acceptLoops = Task.WhenAll(
                bound.Select(x => Task.Run(() => this.AcceptLoopAsync(x, token), CancellationToken.None))
            );

            foreach (var connectionListener in bound)
                this.logger.LogInformation("Listening on {Url}", connectionListener.ListenDescription);

            this.SetState(HttpServerState.Running);
        }
        catch
        {
            // A failed bind must not leave the server claiming to be starting forever, nor leave
            // the endpoints that did bind holding their ports.
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
            this.SetState(HttpServerState.Stopped);
            throw;
        }
    }

    async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        if (this.acceptLoops is null)
            return;

        this.SetState(HttpServerState.Stopping);
        this.logger.LogInformation("Shutting down");

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
        this.SetState(HttpServerState.Stopped);
    }

    void SetState(HttpServerState state)
    {
        if (this.State == state)
            return;

        this.State = state;
        this.StateChanged?.Invoke(this, state);
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

    async Task AcceptLoopAsync(IConnectionListener connectionListener, CancellationToken cancellationToken)
    {
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
            try
            {
                connection = await connectionListener.AcceptAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Shutting down.
            }

            if (connection is null)
            {
                this.connectionLimit?.Release();
                return;
            }

            this.TrackConnection(connection, cancellationToken);
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
