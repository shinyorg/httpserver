using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shiny.Net.HttpServer.Switchboard.Client.Internal;

namespace Shiny.Net.HttpServer.Switchboard.Client;

/// <summary>
/// One line to a switchboard: an event stream down, plain POSTs up.
/// <code>
/// line.On&lt;string, string&gt;("MessageReceived", (user, text) => Console.WriteLine($"{user}: {text}"));
/// line.Closed += (_, e) => Console.WriteLine($"closed: {e.Reason}");
///
/// await line.StartAsync();
/// var count = await line.InvokeAsync&lt;int&gt;("OnlineCount");
/// await foreach (var tick in line.StreamAsync&lt;Tick&gt;("Ticks", 10)) { … }
/// await line.StopAsync();
/// </code>
/// <para>
/// Messages from the server are handled one at a time, in the order they were sent. A handler that
/// returns a value — an answer the server is waiting for — runs off that queue instead, so a handler
/// that waits on a person does not hold up everything behind it.
/// </para>
/// </summary>
public sealed class SwitchboardLine : IAsyncDisposable
{
    const string LineHeader = "X-Switchboard-Line";
    static readonly HttpRequestOptionsKey<bool> BrowserStreaming = new("WebAssemblyEnableStreamingResponse");

    readonly Uri baseUrl;
    readonly SwitchboardLineOptions options;
    readonly IRetryPolicy? retryPolicy;
    readonly ILogger logger;
    readonly HttpClient http;
    readonly bool ownsHttp;
    readonly ConcurrentDictionary<string, ImmutableArray<Func<LineArguments, Task>>> handlers = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, Func<LineArguments, Task<string>>> resultHandlers = new(StringComparer.Ordinal);
    readonly RecentIds answered = new(1024);
    readonly object gate = new();

    LineState state = LineState.Disconnected;
    string? lineId;
    string? token;
    long lastEventId;
    long sequence;
    TimeSpan heartbeat;
    CancellationTokenSource? lifetime;
    Channel<Dispatch>? dispatch;
    Task? runner;
    Task? dispatcher;
    bool stopping;

    internal SwitchboardLine(
        Uri baseUrl,
        SwitchboardLineOptions options,
        IReadOnlyList<IJsonTypeInfoResolver> resolvers,
        IRetryPolicy? retryPolicy,
        ILoggerFactory? loggerFactory
    )
    {
        this.baseUrl = baseUrl;
        this.options = options;
        this.retryPolicy = retryPolicy;
        this.logger = loggerFactory?.CreateLogger<SwitchboardLine>() ?? (ILogger)NullLogger.Instance;
        this.Json = new LineJson(resolvers);

        if (options.HttpClient is { } supplied)
        {
            this.http = supplied;
        }
        else
        {
            HttpMessageHandler handler = new HttpClientHandler();
            if (options.HttpMessageHandlerFactory is { } factory)
                handler = factory(handler);

            // No overall timeout: the event stream is meant to stay open. Calls time out through
            // the cancellation token the caller passes.
            this.http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            this.ownsHttp = true;
        }
    }

    internal LineJson Json { get; }

    public LineState State
    {
        get
        {
            lock (this.gate)
                return this.state;
        }
    }

    /// <summary>The line's public id, while connected. What the server's <c>Clients.Line(id)</c> addresses.</summary>
    public string? LineId
    {
        get
        {
            lock (this.gate)
                return this.state == LineState.Disconnected ? null : this.lineId;
        }
    }

    public event EventHandler<LineStateChangedEventArgs>? StateChanged;

    /// <summary>Raised before each reconnect attempt.</summary>
    public event EventHandler<LineReconnectingEventArgs>? Reconnecting;

    public event EventHandler<LineReconnectedEventArgs>? Reconnected;

    /// <summary>Raised once when the line is closed for good — whoever closed it, and why.</summary>
    public event EventHandler<LineClosedEventArgs>? Closed;

    // ---- lifecycle ----

    /// <summary>Opens the line. Returns once the server has announced it.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource life;

        lock (this.gate)
        {
            if (this.state != LineState.Disconnected)
                throw new InvalidOperationException($"The line is already {this.state}.");

            this.stopping = false;
            this.token = null;
            this.lifetime = life = new CancellationTokenSource();
            this.dispatch = Channel.CreateUnbounded<Dispatch>(new UnboundedChannelOptions { SingleReader = true });
        }

        this.SetState(LineState.Connecting);
        this.dispatcher = Task.Run(() => this.DispatchLoopAsync(this.dispatch));

        try
        {
            // The stream lives on the line's own token, not the caller's: the caller's only governs
            // how long it is willing to wait for the line to open.
            Attached? attached;
            using (cancellationToken.Register(static state => ((CancellationTokenSource)state!).Cancel(), life))
                attached = await this.ConnectAsync(resume: false, life.Token).ConfigureAwait(false);

            this.SetState(LineState.Connected);
            this.runner = Task.Run(() => this.RunAsync(attached!, life.Token), CancellationToken.None);
        }
        catch
        {
            lock (this.gate)
                this.state = LineState.Disconnected;

            this.dispatch.Writer.TryComplete();
            this.StateChanged?.Invoke(this, new LineStateChangedEventArgs(LineState.Connecting, LineState.Disconnected));
            throw;
        }
    }

    /// <summary>
    /// Closes the line on purpose. The server is told (<c>ClientClosed</c>) and nothing reconnects.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? life;
        Task? run;
        string? currentToken;

        lock (this.gate)
        {
            if (this.state == LineState.Disconnected)
                return;

            this.stopping = true;
            life = this.lifetime;
            run = this.runner;
            currentToken = this.token;
        }

        if (currentToken is not null)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));

                using var request = await this.NewRequestAsync(HttpMethod.Delete, "connect").ConfigureAwait(false);
                request.Headers.Add(LineHeader, currentToken);
                using var _ = await this.http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                // The server hears about it either way: its stream ends and the line is swept.
                this.logger.LogDebug(ex, "Could not tell the switchboard the line was closing");
            }
        }

        if (life is not null)
            await life.CancelAsync().ConfigureAwait(false);

        if (run is not null)
        {
            try
            {
                await run.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.logger.LogDebug(ex, "The line's receive loop ended with an error while stopping");
            }
        }

        this.Finish(LineCloseReason.ClientClosed, null, null);
    }

    public async ValueTask DisposeAsync()
    {
        await this.StopAsync().ConfigureAwait(false);

        if (this.dispatcher is { } running)
            await running.ConfigureAwait(false);

        if (this.ownsHttp)
            this.http.Dispose();
    }

    // ---- handlers ----

    /// <summary>
    /// Handles calls to <paramref name="method"/>, reading the arguments yourself. Several handlers
    /// for one method all run, in registration order. Dispose the result to remove it.
    /// </summary>
    public IDisposable Handle(string method, Func<LineArguments, Task> handler)
    {
        ArgumentException.ThrowIfNullOrEmpty(method);
        ArgumentNullException.ThrowIfNull(handler);

        this.handlers.AddOrUpdate(method, _ => [handler], (_, existing) => existing.Add(handler));
        return new Registration(() => this.handlers.AddOrUpdate(method, _ => [], (_, existing) => existing.Remove(handler)));
    }

    /// <summary>
    /// Answers calls to <paramref name="method"/> that the server waits on. One answer per call, so one
    /// handler per method: registering another replaces it.
    /// </summary>
    public IDisposable Handle<TResult>(string method, Func<LineArguments, Task<TResult>> handler)
    {
        ArgumentException.ThrowIfNullOrEmpty(method);
        ArgumentNullException.ThrowIfNull(handler);

        Func<LineArguments, Task<string>> wrapped = async args =>
        {
            var result = await handler(args).ConfigureAwait(false);
            return result is null ? "null" : JsonSerializer.Serialize(result, this.Json.Get<TResult>());
        };

        this.resultHandlers[method] = wrapped;
        return new Registration(() => this.resultHandlers.TryRemove(new KeyValuePair<string, Func<LineArguments, Task<string>>>(method, wrapped)));
    }

    public IDisposable On(string method, Action handler)
        => this.Handle(method, _ => { handler(); return Task.CompletedTask; });

    public IDisposable On(string method, Func<Task> handler)
        => this.Handle(method, _ => handler());

    public IDisposable On<T1>(string method, Action<T1> handler)
        => this.Handle(method, a => { handler(a.Get<T1>(0)); return Task.CompletedTask; });

    public IDisposable On<T1>(string method, Func<T1, Task> handler)
        => this.Handle(method, a => handler(a.Get<T1>(0)));

    public IDisposable On<T1, T2>(string method, Action<T1, T2> handler)
        => this.Handle(method, a => { handler(a.Get<T1>(0), a.Get<T2>(1)); return Task.CompletedTask; });

    public IDisposable On<T1, T2>(string method, Func<T1, T2, Task> handler)
        => this.Handle(method, a => handler(a.Get<T1>(0), a.Get<T2>(1)));

    public IDisposable On<T1, T2, T3>(string method, Action<T1, T2, T3> handler)
        => this.Handle(method, a => { handler(a.Get<T1>(0), a.Get<T2>(1), a.Get<T3>(2)); return Task.CompletedTask; });

    public IDisposable On<T1, T2, T3>(string method, Func<T1, T2, T3, Task> handler)
        => this.Handle(method, a => handler(a.Get<T1>(0), a.Get<T2>(1), a.Get<T3>(2)));

    /// <summary>Answers a server call that waits for a value.</summary>
    public IDisposable On<TResult>(string method, Func<Task<TResult>> handler)
        => this.Handle<TResult>(method, _ => handler());

    public IDisposable On<T1, TResult>(string method, Func<T1, Task<TResult>> handler)
        => this.Handle<TResult>(method, a => handler(a.Get<T1>(0)));

    public IDisposable On<T1, T2, TResult>(string method, Func<T1, T2, Task<TResult>> handler)
        => this.Handle<TResult>(method, a => handler(a.Get<T1>(0), a.Get<T2>(1)));

    // ---- calling the server ----

    /// <summary>Calls a method that returns nothing. Completes once the server has run it.</summary>
    public Task SendAsync(string method, params object?[] args) => this.InvokeCoreAsync(method, args, default);

    /// <summary>Calls a method that returns nothing.</summary>
    public Task InvokeAsync(string method, params object?[] args) => this.InvokeCoreAsync(method, args, default);

    /// <summary>Calls a method and returns its result.</summary>
    public Task<T> InvokeAsync<T>(string method, params object?[] args) => this.InvokeCoreAsync<T>(method, args, default);

    /// <summary>Calls a method that returns an <c>IAsyncEnumerable&lt;T&gt;</c>, receiving each item as the server yields it.</summary>
    public IAsyncEnumerable<T> StreamAsync<T>(string method, params object?[] args) => this.StreamCoreAsync<T>(method, args, default);

    public async Task InvokeCoreAsync(string method, object?[] args, CancellationToken cancellationToken)
    {
        using var response = await this.PostInvokeAsync(method, args, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
    }

    public async Task<T> InvokeCoreAsync<T>(string method, object?[] args, CancellationToken cancellationToken)
    {
        // Resolved before sending, so a type with no metadata fails without running the call.
        var typeInfo = this.Json.Get<T>();

        using var response = await this.PostInvokeAsync(method, args, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NoContent)
            return default!;

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(body, default, cancellationToken).ConfigureAwait(false);

        return document.RootElement.TryGetProperty("result", out var result) && result.ValueKind != JsonValueKind.Null
            ? result.Deserialize(typeInfo)!
            : default!;
    }

    public async IAsyncEnumerable<T> StreamCoreAsync<T>(
        string method,
        object?[] args,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        var typeInfo = this.Json.Get<T>();

        using var response = await this.PostInvokeAsync(method, args, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        using var reader = new EventStreamReader(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false) is { } item)
        {
            switch (item.Event)
            {
                case "item":
                    yield return item.Data == "null" ? default! : JsonSerializer.Deserialize(item.Data, typeInfo)!;
                    break;

                case "complete":
                    yield break;

                case "error":
                    throw new SwitchboardException(ErrorMessage(item.Data) ?? "The stream failed");
            }
        }

        throw new SwitchboardException("The stream ended before the server completed it");
    }

    async Task<HttpResponseMessage> PostInvokeAsync(string method, object?[]? args, HttpCompletionOption completion, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(method);

        string currentToken;
        lock (this.gate)
        {
            if (this.state != LineState.Connected || this.token is null)
                throw new InvalidOperationException($"The line is {this.state}; calls can only be made while it is connected.");

            currentToken = this.token;
        }

        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("target", method);
            this.Json.WriteArguments(writer, args);
            writer.WriteNumber("seq", Interlocked.Increment(ref this.sequence));
            writer.WriteEndObject();
        }

        using var request = await this.NewRequestAsync(HttpMethod.Post, "invoke").ConfigureAwait(false);
        request.Headers.Add(LineHeader, currentToken);
        request.Content = new ReadOnlyMemoryContent(buffer.WrittenMemory);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        var response = await this.http.SendAsync(request, completion, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
            return response;

        using (response)
            throw await ErrorAsync(response, cancellationToken).ConfigureAwait(false);
    }

    // ---- connecting ----

    sealed class Attached(HttpResponseMessage response, EventStreamReader reader, CancellationTokenSource watchdog, CancellationTokenSource linked, bool newLine, bool missed) : IDisposable
    {
        public EventStreamReader Reader { get; } = reader;

        public CancellationTokenSource Watchdog { get; } = watchdog;

        public CancellationToken Token => linked.Token;

        public bool NewLine { get; } = newLine;

        public bool Missed { get; } = missed;

        public void Dispose()
        {
            this.Reader.Dispose();
            response.Dispose();
            linked.Dispose();
            this.Watchdog.Dispose();
        }
    }

    /// <summary>
    /// Opens the event stream and reads its first event. With <paramref name="resume"/>, returns null
    /// when the server no longer has the line (410) so the caller can open a new one.
    /// </summary>
    async Task<Attached?> ConnectAsync(bool resume, CancellationToken cancellationToken)
    {
        using var request = await this.NewRequestAsync(HttpMethod.Get, "connect").ConfigureAwait(false);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        if (resume)
        {
            request.Headers.Add(LineHeader, this.token);
            request.Headers.Add("Last-Event-ID", Interlocked.Read(ref this.lastEventId).ToString(CultureInfo.InvariantCulture));
        }

        var response = await this.http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        if (resume && response.StatusCode == HttpStatusCode.Gone)
        {
            response.Dispose();
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            using (response)
                throw await ErrorAsync(response, cancellationToken).ConfigureAwait(false);
        }

        var watchdog = new CancellationTokenSource();
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, watchdog.Token);

        try
        {
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var reader = new EventStreamReader(stream, () => this.Feed(watchdog));
            this.Feed(watchdog);

            var first = await reader.ReadAsync(linked.Token).ConfigureAwait(false)
                ?? throw new SwitchboardException("The server ended the stream before announcing the line");

            using var payload = JsonDocument.Parse(first.Data);
            var root = payload.RootElement;

            switch (first.Event)
            {
                case "connected":
                    lock (this.gate)
                    {
                        this.lineId = root.GetProperty("lineId").GetString();
                        this.token = root.GetProperty("lineToken").GetString();
                        this.heartbeat = TimeSpan.FromMilliseconds(root.TryGetProperty("heartbeatMs", out var hb) ? hb.GetInt64() : 0);
                    }

                    Interlocked.Exchange(ref this.lastEventId, 0);
                    Interlocked.Exchange(ref this.sequence, 0);
                    this.Feed(watchdog);

                    return new Attached(response, reader, watchdog, linked, newLine: true, missed: false);

                case "resumed":
                    return new Attached(response, reader, watchdog, linked, newLine: false, missed: root.TryGetProperty("missed", out var missed) && missed.GetBoolean());

                default:
                    throw new SwitchboardException($"Expected the server to announce the line, but it sent '{first.Event}'");
            }
        }
        catch
        {
            response.Dispose();
            linked.Dispose();
            watchdog.Dispose();
            throw;
        }
    }

    /// <summary>Pushes the dead-link deadline out: something just arrived on the stream.</summary>
    void Feed(CancellationTokenSource watchdog)
    {
        var timeout = this.options.ServerTimeout
            ?? (this.heartbeat > TimeSpan.Zero ? this.heartbeat * 2 : TimeSpan.FromSeconds(30));

        try
        {
            watchdog.CancelAfter(timeout);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>Receives on one stream after another until the line closes for good.</summary>
    async Task RunAsync(Attached attached, CancellationToken cancellationToken)
    {
        var current = attached;

        while (true)
        {
            var (close, error) = await this.ReceiveAsync(current, cancellationToken).ConfigureAwait(false);
            current.Dispose();

            lock (this.gate)
            {
                if (this.stopping)
                    return;
            }

            if (close is { AllowReconnect: false })
            {
                this.Finish(LineCloseReason.HungUp, close.Reason, null);
                return;
            }

            if (close is not null)
            {
                // The server ended the line itself; its token is dead, so any reconnect is a new line.
                lock (this.gate)
                    this.token = null;
            }

            if (this.retryPolicy is null)
            {
                this.Finish(close is null ? LineCloseReason.Dropped : LineCloseReason.ServerClosed, close?.Reason, error);
                return;
            }

            var next = await this.ReconnectAsync(error, close, cancellationToken).ConfigureAwait(false);
            if (next is null)
                return;

            current = next;
        }
    }

    sealed record CloseInfo(string? Reason, bool AllowReconnect);

    async Task<(CloseInfo? Close, Exception? Error)> ReceiveAsync(Attached attached, CancellationToken cancellationToken)
    {
        CloseInfo? close = null;

        try
        {
            while (await attached.Reader.ReadAsync(attached.Token).ConfigureAwait(false) is { } item)
            {
                if (item.Id is { } id && long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                    Interlocked.Exchange(ref this.lastEventId, number);

                switch (item.Event)
                {
                    case "invoke":
                        this.Route(item.Data);
                        break;

                    case "close":
                        using (var payload = JsonDocument.Parse(item.Data))
                        {
                            var root = payload.RootElement;
                            close = new CloseInfo(
                                root.TryGetProperty("reason", out var reason) ? reason.GetString() : null,
                                root.TryGetProperty("allowReconnect", out var allow) && allow.GetBoolean()
                            );
                        }

                        break;
                }
            }

            return (close, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return (close, null);
        }
        catch (OperationCanceledException) when (attached.Watchdog.IsCancellationRequested)
        {
            return (close, new TimeoutException("The server went quiet for longer than its heartbeat allows; treating the line as dropped."));
        }
        catch (Exception ex)
        {
            return (close, ex);
        }
    }

    async Task<Attached?> ReconnectAsync(Exception? error, CloseInfo? close, CancellationToken cancellationToken)
    {
        this.SetState(LineState.Reconnecting);

        var started = Stopwatch.GetTimestamp();
        var attempt = 0;
        var lastError = error;

        while (true)
        {
            var delay = this.retryPolicy!.NextRetryDelay(new RetryContext(attempt, Stopwatch.GetElapsedTime(started), lastError));
            if (delay is null)
            {
                this.Finish(close is null ? LineCloseReason.RetriesExhausted : LineCloseReason.ServerClosed, close?.Reason, lastError);
                return null;
            }

            this.Raise(this.Reconnecting, new LineReconnectingEventArgs(lastError, attempt + 1));

            try
            {
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay.Value, cancellationToken).ConfigureAwait(false);

                Attached? next = null;

                bool canResume;
                lock (this.gate)
                    canResume = this.token is not null;

                if (canResume)
                    next = await this.ConnectAsync(resume: true, cancellationToken).ConfigureAwait(false);

                if (next is null)
                {
                    lock (this.gate)
                        this.token = null;

                    next = await this.ConnectAsync(resume: false, cancellationToken).ConfigureAwait(false);
                }

                this.SetState(LineState.Connected);

                string id;
                lock (this.gate)
                    id = this.lineId!;

                this.Raise(this.Reconnected, new LineReconnectedEventArgs(id, next!.NewLine, next.Missed));
                return next;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return null;
            }
            catch (SwitchboardException ex) when (ex.StatusCode is 401 or 403)
            {
                // Retrying with the same credentials will not change the answer.
                this.Finish(LineCloseReason.Faulted, ex.Message, ex);
                return null;
            }
            catch (Exception ex)
            {
                this.logger.LogDebug(ex, "Reconnect attempt {Attempt} failed", attempt + 1);
                lastError = ex;
                attempt++;
            }
        }
    }

    void Finish(LineCloseReason reason, string? message, Exception? exception)
    {
        LineState previous;

        lock (this.gate)
        {
            if (this.state == LineState.Disconnected)
                return;

            previous = this.state;
            this.state = LineState.Disconnected;
            this.token = null;
            this.dispatch?.Writer.TryComplete();
        }

        this.Raise(this.StateChanged, new LineStateChangedEventArgs(previous, LineState.Disconnected));
        this.Raise(this.Closed, new LineClosedEventArgs(reason, message, exception));
    }

    void SetState(LineState next)
    {
        LineState previous;

        lock (this.gate)
        {
            previous = this.state;
            if (previous == next)
                return;

            this.state = next;
        }

        this.Raise(this.StateChanged, new LineStateChangedEventArgs(previous, next));
    }

    void Raise<T>(EventHandler<T>? handler, T args)
    {
        try
        {
            handler?.Invoke(this, args);
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "A line event handler threw");
        }
    }

    // ---- receiving calls ----

    sealed record Dispatch(JsonDocument Document, string Target, JsonElement Arguments);

    void Route(string data)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(data);
        }
        catch (JsonException ex)
        {
            this.logger.LogWarning(ex, "Ignoring a malformed call from the switchboard");
            return;
        }

        var root = document.RootElement;
        var target = root.TryGetProperty("target", out var t) ? t.GetString() : null;
        if (target is null)
        {
            document.Dispose();
            return;
        }

        var args = root.TryGetProperty("args", out var a) ? a : default;

        if (root.TryGetProperty("invocationId", out var idElement) && idElement.GetString() is { } invocationId)
        {
            // A question replayed after a resume has already been answered — or is being.
            if (!this.answered.Add(invocationId))
            {
                document.Dispose();
                return;
            }

            _ = Task.Run(() => this.AnswerAsync(document, target, args, invocationId));
            return;
        }

        if (this.dispatch is not { } channel || !channel.Writer.TryWrite(new Dispatch(document, target, args)))
            document.Dispose();
    }

    async Task DispatchLoopAsync(Channel<Dispatch> channel)
    {
        await foreach (var item in channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            using (item.Document)
            {
                if (!this.handlers.TryGetValue(item.Target, out var registered) || registered.IsDefaultOrEmpty)
                {
                    this.logger.LogDebug("No handler for '{Method}'", item.Target);
                    continue;
                }

                var args = new LineArguments(this, item.Target, item.Arguments);
                foreach (var handler in registered)
                {
                    try
                    {
                        await handler(args).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        this.logger.LogError(ex, "The handler for '{Method}' threw", item.Target);
                    }
                }
            }
        }
    }

    async Task AnswerAsync(JsonDocument document, string target, JsonElement args, string invocationId)
    {
        string? result = null;
        string? error = null;

        using (document)
        {
            if (!this.resultHandlers.TryGetValue(target, out var handler))
            {
                error = this.handlers.ContainsKey(target)
                    ? $"The client handles '{target}' but does not return a result from it."
                    : $"The client has no handler for '{target}'.";
            }
            else
            {
                try
                {
                    result = await handler(new LineArguments(this, target, args)).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }
            }
        }

        string? currentToken;
        lock (this.gate)
            currentToken = this.token;

        if (currentToken is null)
            return;

        var buffer = new ArrayBufferWriter<byte>(128);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("invocationId", invocationId);

            if (error is not null)
            {
                writer.WriteString("error", error);
            }
            else
            {
                writer.WritePropertyName("result");
                writer.WriteRawValue(result!, skipInputValidation: true);
            }

            writer.WriteEndObject();
        }

        try
        {
            using var request = await this.NewRequestAsync(HttpMethod.Post, "completion").ConfigureAwait(false);
            request.Headers.Add(LineHeader, currentToken);
            request.Content = new ReadOnlyMemoryContent(buffer.WrittenMemory);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            using var response = await this.http.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                this.logger.LogDebug("The switchboard did not accept the answer to '{Method}': {Status}", target, (int)response.StatusCode);
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Could not send the answer to '{Method}'", target);
        }
    }

    // ---- http ----

    async Task<HttpRequestMessage> NewRequestAsync(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(this.baseUrl.AbsoluteUri.TrimEnd('/') + "/" + path))
        {
            Version = this.options.HttpVersion,
            VersionPolicy = this.options.HttpVersionPolicy
        };

        // Browsers buffer a fetch response unless asked not to — which, for an event stream, is the
        // difference between working and not.
        request.Options.Set(BrowserStreaming, true);

        foreach (var header in this.options.Headers)
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);

        if (this.options.AccessTokenProvider is { } provider && await provider().ConfigureAwait(false) is { Length: > 0 } accessToken)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        return request;
    }

    static async Task<SwitchboardException> ErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string? message = null;

        try
        {
            message = ErrorMessage(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
        }

        var status = (int)response.StatusCode;
        message ??= response.StatusCode == HttpStatusCode.Gone
            ? "The line is closed"
            : $"The switchboard answered {status} {response.ReasonPhrase}";

        return new SwitchboardException(message, status);
    }

    static string? ErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error)
                ? error.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    sealed class Registration(Action remove) : IDisposable
    {
        int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref this.disposed, 1) == 0)
                remove();
        }
    }
}
