using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shiny.Net.HttpServer.Transports;

namespace Shiny.Net.HttpServer.Http1;

/// <summary>
/// Drives one connection: parse a request, run the pipeline, write the response, repeat while
/// keep-alive holds.
/// <para>
/// A single <see cref="HttpContext"/>, parser and output producer are allocated per connection and
/// reset between requests. HTTP/1.1 serves one request at a time per connection, so reuse is safe and
/// keeps a busy keep-alive connection close to allocation-free.
/// </para>
/// </summary>
sealed class Http1Connection : IRequestBodySource
{
    // How long the leftovers of a body the handler never read may take to arrive before the
    // connection is given up on instead. Kestrel uses the same five seconds: long enough for the
    // rest of an ordinary upload, short enough that a client trickling one cannot keep the
    // connection — and its slot — for as long as it likes.
    static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    readonly IConnection connection;
    readonly HttpServerOptions options;
    readonly RequestDelegate application;
    readonly IServiceProvider? rootServices;
    readonly ILogger logger;
    readonly CancellationToken draining;

    readonly HttpContext context = new();
    readonly Http1RequestParser parser = new();
    readonly Http1OutputProducer output;
    readonly PipeOperationTimer readTimer;

    // The client asked for 100 Continue and has not had it yet. Sent when the body is first read,
    // so a request refused before then — unauthorized, unrouted, over its limit — never sends it.
    bool continuePending;

    // Per-request state for telling a client that went away from a handler that failed.
    CancellationTokenSource? requestAborted;
    bool handlerRunning;
    bool bodyComplete;

    /// <summary>A forgiven body-read deadline whose cancel is still latched on the input pipe.</summary>
    bool staleCancelPending;
    bool watchForDisconnect;
    bool inputFaulted;
    bool abortRequested;

    // A read of the input started while the handler runs, to notice the client hanging up. Whatever
    // it returns — EOF, or the next pipelined request — the next head read picks up, since a pipe
    // allows only one read at a time and its result must not be lost.
    Task<ReadResult>? pendingRead;

    /// <param name="connection">The transport the requests arrive on.</param>
    /// <param name="options">The server's options, read for limits and timeouts.</param>
    /// <param name="application">The request pipeline each request is handed to.</param>
    /// <param name="rootServices">The container request scopes are created from, if any.</param>
    /// <param name="logger">Where connection-level problems are reported.</param>
    /// <param name="draining">
    /// Signalled when the server begins a graceful stop: an idle connection closes at once, and one
    /// serving a request finishes it, says <c>Connection: close</c>, and closes after.
    /// </param>
    public Http1Connection(
        IConnection connection,
        HttpServerOptions options,
        RequestDelegate application,
        IServiceProvider? rootServices,
        ILogger logger,
        CancellationToken draining = default
    )
    {
        this.connection = connection;
        this.options = options;
        this.application = application;
        this.rootServices = rootServices;
        this.logger = logger;
        this.draining = draining;
        this.output = new Http1OutputProducer(connection.Output, options, draining);
        this.readTimer = new PipeOperationTimer(connection.Input.CancelPendingRead);
    }

    /// <summary>
    /// True when the connection closed on the server's initiative while the client may still be sending: the host
    /// lingers before closing so the response is not lost to a reset.
    /// </summary>
    internal bool LingerOnClose { get; private set; }

    public async Task ProcessAsync(CancellationToken connectionToken)
    {
        var requestsServed = 0L;

        try
        {
            while (!connectionToken.IsCancellationRequested && !this.draining.IsCancellationRequested)
            {
                this.context.Reset();
                this.parser.Reset();
                this.ApplyConnectionInfo();

                var received = await this.TryReadRequestHeadAsync(connectionToken).ConfigureAwait(false);
                if (received == ReadHeadResult.ConnectionClosed)
                    return;

                if (received == ReadHeadResult.BadRequest)
                {
                    // The error response has already been written; the connection is not reusable
                    // because we do not know where the malformed request ended.
                    this.LingerOnClose = true;
                    return;
                }

                requestsServed++;

                var keepAlive = this.DetermineKeepAlive(requestsServed);
                var handled = await this.HandleRequestAsync(keepAlive, connectionToken).ConfigureAwait(false);

                if (!handled)
                {
                    // Closing with request bytes still to come - an unread body, or one held back for a
                    // 100 Continue - would reset the connection over the response that was just written.
                    this.LingerOnClose = !this.output.IsUpgrade
                        && !this.abortRequested
                        && !this.output.WriteTimedOut
                        && (!this.bodyComplete || this.continuePending);
                    return;
                }
            }
        }
        catch (Exception ex) when (IsConnectionResetOrCancellation(ex))
        {
            this.logger.LogDebug(
                "Connection {ConnectionId} closed by the peer: {Reason}",
                this.connection.ConnectionId,
                ex.GetType().Name
            );
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "Unhandled error on connection {ConnectionId}", this.connection.ConnectionId);
            this.connection.Abort();
        }
        finally
        {
            // A client that stopped reading would make the dispose that follows wait forever on
            // the very flush that just timed out.
            if (this.output.WriteTimedOut)
                this.connection.Abort();

            await this.EndPendingReadAsync().ConfigureAwait(false);

            this.readTimer.Dispose();
            this.output.Dispose();
        }
    }

    /// <summary>
    /// Ends a disconnect watch still pending when the connection closes. A socket disposed with a
    /// receive outstanding is closed abortively, so the client would see a reset where it should see
    /// the end of a perfectly good response. Cancelling the read first lets the close be a FIN.
    /// </summary>
    async ValueTask EndPendingReadAsync()
    {
        if (this.pendingRead is not { } pending)
            return;

        this.pendingRead = null;
        if (!pending.IsCompleted)
            this.connection.Input.CancelPendingRead();

        try
        {
            await pending.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Usually the client's reset, already acted on; at worst a read that would not let go.
            this.logger.LogDebug("Disconnect watch on {ConnectionId} ended with {Reason}", this.connection.ConnectionId, ex.GetType().Name);
            _ = pending.ContinueWith(
                static t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default
            );
        }
    }

    void ApplyConnectionInfo()
    {
        var info = this.context.Connection;
        info.ConnectionId = this.connection.ConnectionId;
        info.IsEncrypted = this.connection.IsEncrypted;
        info.ClientCertificate = this.connection.ClientCertificate;
        info.IsTunneled = this.connection.IsTunneled;
        info.ProxyProtocol = (this.connection as IProxyProtocolConnection)?.ProxyProtocol;

        if (this.connection.RemoteEndPoint is IPEndPoint remote)
        {
            info.RemoteIpAddress = remote.Address;
            info.RemotePort = remote.Port;
        }
        if (this.connection.LocalEndPoint is IPEndPoint local)
        {
            info.LocalIpAddress = local.Address;
            info.LocalPort = local.Port;
        }

        this.context.Request.Scheme = this.connection.IsEncrypted ? "https" : "http";
        this.context.AbortAction = this.AbortRequest;
        this.context.Transport = this.connection;
    }

    enum ReadHeadResult
    {
        Success,
        ConnectionClosed,
        BadRequest
    }

    async ValueTask<ReadHeadResult> TryReadRequestHeadAsync(CancellationToken connectionToken)
    {
        var input = this.connection.Input;
        var request = this.context.Request;

        // Two different clocks: a generous idle window while waiting for the first byte of a request
        // (that is just an unused keep-alive connection), and a tight one once the client has started
        // sending (a request that stalls mid-headers is either broken or hostile).
        using var timeoutSource = new CancellationTokenSource(this.options.Limits.KeepAliveTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(connectionToken, timeoutSource.Token, this.draining);
        var sawAnyBytes = false;

        while (true)
        {
            ReadResult result;
            try
            {
                // The read the disconnect watch already started, if there is one: it may be holding
                // the start of this very request.
                var pending = this.pendingRead;
                result = pending is null
                    ? await input.ReadAsync(linked.Token).ConfigureAwait(false)
                    : await pending.WaitAsync(linked.Token).ConfigureAwait(false);

                this.pendingRead = null;
            }
            catch (OperationCanceledException) when (this.draining.IsCancellationRequested && !timeoutSource.IsCancellationRequested)
            {
                // The server is stopping. An idle connection has nothing to finish, and one part way
                // through a head has not started anything the server owes it an answer for.
                return ReadHeadResult.ConnectionClosed;
            }
            catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
            {
                if (sawAnyBytes)
                {
                    // Mid-request stall: tell the client why before hanging up - and linger, since a client
                    // trickling its headers is still sending, and closing over that resets away the 408.
                    await this.TryWriteErrorResponseAsync(
                        StatusCodes.Status408RequestTimeout,
                        "Request timed out while reading headers.",
                        connectionToken
                    ).ConfigureAwait(false);

                    this.LingerOnClose = true;
                }

                return ReadHeadResult.ConnectionClosed;
            }

            var buffer = result.Buffer;

            if (!buffer.IsEmpty && !sawAnyBytes)
            {
                sawAnyBytes = true;
                timeoutSource.CancelAfter(this.options.Limits.RequestHeadersTimeout);
            }

            try
            {
                var reader = new SequenceReader<byte>(buffer);
                var complete = this.parser.TryParseRequestHead(ref reader, request, this.options.Limits);

                // Consume whatever full lines were parsed and mark the rest examined, so the next
                // read waits for genuinely new bytes rather than spinning on the same buffer.
                var consumed = buffer.GetPosition(reader.Consumed);
                input.AdvanceTo(consumed, complete ? consumed : buffer.End);

                if (complete)
                    return ReadHeadResult.Success;
            }
            catch (BadHttpRequestException ex)
            {
                input.AdvanceTo(buffer.Start, buffer.End);
                this.logger.LogDebug(
                    "Rejected request on {ConnectionId}: {Message}",
                    this.connection.ConnectionId,
                    ex.Message
                );
                await this.TryWriteErrorResponseAsync(ex.StatusCode, ex.Message, connectionToken)
                    .ConfigureAwait(false);

                return ReadHeadResult.BadRequest;
            }

            if (result.IsCompleted)
                // A clean close between requests is normal. A close mid-request is not, but there is
                // nobody left to tell.
                return ReadHeadResult.ConnectionClosed;

            if (result.IsCanceled)
            {
                // A cancel left latched by a body-read deadline that was forgiven (the body had arrived) is no reason
                // to drop the connection: it is consumed here and the read goes on.
                if (!this.staleCancelPending || !result.Buffer.IsEmpty)
                    return ReadHeadResult.ConnectionClosed;

                this.staleCancelPending = false;
            }
        }
    }

    bool DetermineKeepAlive(long requestsServed)
    {
        if (this.options.Limits.MaxRequestsPerConnection is { } max && requestsServed >= max)
            return false;

        // Every token of every Connection header is looked at before deciding: "keep-alive, close"
        // asks for a close, and stopping at the first keep-alive missed it.
        var tokens = Tokenize(this.context.Request.Headers[HeaderNames.Connection]);
        if (tokens.Any(x => x.Equals("close", StringComparison.OrdinalIgnoreCase)))
            return false;

        if (tokens.Any(x => x.Equals("keep-alive", StringComparison.OrdinalIgnoreCase)))
            return true;

        // 1.1 defaults to persistent, 1.0 does not.
        return this.context.Request.Protocol == HttpProtocols.Http11;
    }

    /// <summary>Splits a comma-separated header list into its trimmed, non-empty elements (RFC 9110 §5.6.1).</summary>
    static List<string> Tokenize(Microsoft.Extensions.Primitives.StringValues values)
    {
        var tokens = new List<string>();
        foreach (var value in values)
        {
            if (value is not null)
                tokens.AddRange(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        return tokens;
    }

    /// <summary>Returns false when the connection must be closed after this request.</summary>
    async ValueTask<bool> HandleRequestAsync(bool keepAlive, CancellationToken connectionToken)
    {
        var request = this.context.Request;
        var response = this.context.Response;

        using var requestAborted = CancellationTokenSource.CreateLinkedTokenSource(connectionToken);
        this.requestAborted = requestAborted;
        this.context.RequestAborted = requestAborted.Token;
        this.handlerRunning = false;
        this.bodyComplete = false;
        this.watchForDisconnect = false;

        var suppressBody = HttpMethods.IsHead(request.Method);
        this.output.Begin(response, suppressBody, keepAlive);

        try
        {
            this.ValidateHost(request);
            this.ApplyForwardedHeaders();
            request.InitializeMaxBodySize(this.options.Limits.MaxRequestBodySize);
            var bodyStream = this.CreateRequestBodyStream(request);
            request.Body = bodyStream;
            request.Cookies.SetRaw(request.Headers.GetFirst(HeaderNames.Cookie));

            this.continuePending = bodyStream is not EmptyReadStream && ExpectsContinue(request);
            this.bodyComplete = bodyStream is EmptyReadStream;

            // An upgrade hands the input to the protocol taking over, so nothing else may read it.
            this.watchForDisconnect = !request.Headers.ContainsKey(HeaderNames.Upgrade)
                && !HttpMethods.IsConnect(request.Method);

            this.handlerRunning = true;
            try
            {
                if (this.bodyComplete)
                    this.StartWatchingForDisconnect();

                await this.RunPipelineAsync().ConfigureAwait(false);
            }
            finally
            {
                this.handlerRunning = false;
            }

            await this.output.CompleteAsync(connectionToken).ConfigureAwait(false);

            // After a 101 the bytes belong to whatever protocol took over, and the handler has
            // already finished speaking it. There is no next request to parse.
            if (this.output.IsUpgrade)
                return false;

            if (!this.output.AllowsKeepAlive || this.abortRequested || this.draining.IsCancellationRequested)
                return false;

            // A client still waiting for 100 Continue may or may not send its body now that it has a
            // final response (RFC 9110 §10.1.1). Not knowing which, the connection cannot be reused.
            if (this.continuePending)
                return false;

            // Unread body bytes would be misparsed as the head of the next request, so either
            // consume them or give up on reusing the connection.
            if (!this.bodyComplete && bodyStream is ReadOnlyRequestStream drainable)
                return await this.TryDrainAsync(drainable, connectionToken).ConfigureAwait(false);

            return true;
        }
        catch (BadHttpRequestException ex)
        {
            await this.TryWriteErrorResponseAsync(ex.StatusCode, ex.Message, connectionToken)
                .ConfigureAwait(false);

            return false;
        }
        catch (Exception ex) when (this.IsDisconnect(ex, requestAborted.Token))
        {
            // The client went away (or the server is aborting it). There is nobody to answer, and
            // an error log for every closed browser tab would bury the real ones.
            this.logger.LogDebug(
                "Connection {ConnectionId} went away during {Method} {Path}: {Reason}",
                this.connection.ConnectionId,
                request.Method,
                request.Path,
                ex.GetType().Name
            );

            return false;
        }
        catch (Exception ex)
        {
            this.logger.LogError(
                ex,
                "Unhandled exception handling {Method} {Path}",
                request.Method,
                request.Path
            );

            // The app's handlers get first refusal. Only when none of them claims the exception
            // does the connection fall back to its own 500 — a server that cannot answer at all is
            // the last resort, not the first response.
            if (await this.TryRunExceptionHandlersAsync(ex, connectionToken).ConfigureAwait(false))
            {
                await this.output.CompleteAsync(connectionToken).ConfigureAwait(false);
                return false;
            }

            var detail = this.options.HideExceptionDetails
                ? "An unexpected error occurred."
                : ex.ToString();

            await this.TryWriteErrorResponseAsync(
                StatusCodes.Status500InternalServerError,
                detail,
                connectionToken
            ).ConfigureAwait(false);

            return false;
        }
        finally
        {
            this.requestAborted = null;

            // Quietly: an app callback on RequestAborted that throws here would otherwise escape as a connection
            // error and reset a response that has already been delivered.
            TryCancel(requestAborted);
        }
    }

    /// <summary>
    /// Tells a client hanging up apart from a handler failing. The exception types alone cannot:
    /// a missing file is an <see cref="IOException"/> and an outbound HttpClient timeout is an
    /// <see cref="OperationCanceledException"/>, and both deserve a 500 and a log line. What can is
    /// where it came from — this connection's own reads and writes failing, the request being
    /// aborted, or the request's token cancelled underneath an exception that looks like it.
    /// </summary>
    bool IsDisconnect(Exception ex, CancellationToken requestToken)
    {
        if (this.inputFaulted || this.output.TransportFaulted || this.abortRequested)
            return true;

        // After a 101 the handler is speaking another protocol over the raw transport, where a
        // reset reaches it as exactly these exceptions and there is no HTTP response left to send.
        if (this.output.IsUpgrade)
            return IsConnectionResetOrCancellation(ex);

        return requestToken.IsCancellationRequested && IsConnectionResetOrCancellation(ex);
    }

    async ValueTask<bool> TryDrainAsync(ReadOnlyRequestStream body, CancellationToken connectionToken)
    {
        using var timeout = new CancellationTokenSource(DrainTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(connectionToken, timeout.Token);

        try
        {
            return await body.TryDrainAsync(linked.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or BadHttpRequestException)
        {
            // Too slow, or malformed. Either way the response has gone and the connection has not
            // earned another request.
            this.logger.LogDebug(
                "Gave up draining an unread request body on {ConnectionId}: {Reason}",
                this.connection.ConnectionId,
                ex.Message
            );
            return false;
        }
    }

    /// <summary>
    /// <see cref="HttpContext.Abort"/>: cancels the request first, so anything the handler started
    /// with <see cref="HttpContext.RequestAborted"/> stops too, then tears the connection down.
    /// </summary>
    void AbortRequest()
    {
        this.abortRequested = true;
        try
        {
            TryCancel(this.requestAborted);
        }
        finally
        {
            // A callback registered on RequestAborted that throws must not leave the connection open.
            this.connection.Abort();
        }
    }

    static void TryCancel(CancellationTokenSource? source) => HttpContext.CancelQuietly(source);

    /// <summary>
    /// Watches the input while the handler runs, once nothing of the request is left to read, so a
    /// client that hangs up cancels <see cref="HttpContext.RequestAborted"/> instead of leaving the
    /// handler working for nobody. The read is never cancelled: its result belongs to whoever reads
    /// next, and a half-finished read on a TLS stream is not one that can be safely abandoned.
    /// </summary>
    void StartWatchingForDisconnect()
    {
        if (!this.handlerRunning || !this.watchForDisconnect || this.pendingRead is not null || this.requestAborted is not { } aborted)
            return;

        this.pendingRead = this.WatchForDisconnectAsync(aborted);
    }

    async Task<ReadResult> WatchForDisconnectAsync(CancellationTokenSource aborted)
    {
        try
        {
            var result = await this.connection.Input.ReadAsync(CancellationToken.None).ConfigureAwait(false);

            // EOF with nothing buffered is the client closing. Data is the next pipelined request,
            // which says nothing about this one — the watch ends there and leaves the bytes alone.
            if (result.IsCompleted && result.Buffer.IsEmpty)
                TryCancel(aborted);

            return result;
        }
        catch
        {
            // A reset. The next head read rethrows it; the handler just needs to stop.
            TryCancel(aborted);
            throw;
        }
    }

    /// <summary>
    /// Offers the exception to the app's <see cref="IExceptionHandler"/>s.
    /// <para>
    /// Resolved from the root provider rather than the request scope, because the request scope is
    /// already gone by the time an exception reaches here — it is disposed when the pipeline
    /// returns, which is exactly what the exception interrupted.
    /// </para>
    /// </summary>
    async ValueTask<bool> TryRunExceptionHandlersAsync(Exception exception, CancellationToken cancellationToken)
    {
        if (this.rootServices?.GetService(typeof(ExceptionHandlerPipeline)) is not ExceptionHandlerPipeline pipeline
            || pipeline.IsEmpty
            || this.output.HasStarted)
            return false;

        return await pipeline.TryHandleAsync(this.context, exception, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the middleware pipeline inside a per-request DI scope, exactly as ASP.NET Core does: one
    /// scope per request, disposed once the pipeline returns. Scoped registrations therefore resolve
    /// to a single instance shared by every middleware and endpoint handling this request.
    /// <para>
    /// The scope is disposed here rather than after the response is framed, because by the time the
    /// pipeline returns every handler write has already happened — only chunk terminators remain.
    /// Deliberately not completing the response in this method: an exception must reach the caller's
    /// handler with the response still unstarted, so it can turn into a real 500.
    /// </para>
    /// </summary>
    async ValueTask RunPipelineAsync()
    {
        if (this.rootServices is null)
        {
            await this.application(this.context).ConfigureAwait(false);
            return;
        }

        // AsyncServiceScope so services implementing IAsyncDisposable are awaited rather than
        // dropped, which matters for anything holding a connection or file handle.
        var scope = this.rootServices.CreateAsyncScope();
        try
        {
            this.context.RequestServices = scope.ServiceProvider;
            await this.application(this.context).ConfigureAwait(false);
        }
        finally
        {
            this.context.RequestServices = EmptyServiceProvider.Instance;
            await scope.DisposeAsync().ConfigureAwait(false);
        }
    }

    void ValidateHost(HttpRequest request)
    {
        var host = request.Headers[HeaderNames.Host];

        // HTTP/1.1 requires exactly one Host header. Multiple values are a routing ambiguity and a
        // known request-smuggling vector.
        if (host.Count > 1)
            throw new BadHttpRequestException("Request contains multiple Host headers.");

        if (host.Count == 0)
        {
            if (request.Protocol == HttpProtocols.Http11)
                throw new BadHttpRequestException("HTTP/1.1 requests must include a Host header.");
        }
        else if (!HostValidator.IsValid(host[0] ?? string.Empty))
        {
            throw new BadHttpRequestException("Invalid Host header.");
        }

        // An absolute-form target names the host itself, and RFC 9112 §3.2.2 says it wins over
        // whatever the Host header claims.
        if (this.parser.AbsoluteFormAuthority is { } authority)
            request.Host = authority;
        else if (host.Count == 1)
            request.Host = host[0];
    }

    void ApplyForwardedHeaders()
    {
        if (!this.options.UseForwardedHeaders)
            return;

        // Read from the right, one hop per trusted proxy: the left-hand entries are whatever the
        // client sent and are never believed.
        ForwardedHeaders.Apply(this.context, this.options.ForwardedHeaders);
    }

    Stream CreateRequestBodyStream(HttpRequest request)
    {
        var hasContentLength = request.Headers.ContainsKey(HeaderNames.ContentLength);

        // Transfer-Encoding frames the body whatever it says, so any presence of it has to end in a
        // body this server can find the end of — RFC 9112 §6.1 and §6.3. Treating "gzip" as no body
        // at all used to leave the body on the wire to be parsed as the next request.
        if (request.Headers.ContainsKey(HeaderNames.TransferEncoding))
        {
            // HTTP/1.0 has no Transfer-Encoding. A 1.0 recipient further down the chain would frame
            // the body some other way, which is exactly the disagreement smuggling needs.
            if (request.Protocol == HttpProtocols.Http10)
                throw new BadHttpRequestException("Transfer-Encoding is not allowed on HTTP/1.0 requests.");

            // Both framings at once is the classic smuggling setup: two intermediaries can disagree
            // on where the body ends. Reject rather than pick a winner.
            if (hasContentLength)
                throw new BadHttpRequestException(
                    "Request specifies both Transfer-Encoding and Content-Length."
                );

            var codings = Tokenize(request.Headers[HeaderNames.TransferEncoding]);

            // Exactly "chunked", compared as a whole token: "xchunked" is not chunked, and a final
            // coding other than chunked leaves the body's end to the connection closing, which a
            // request cannot use.
            if (codings.Count == 0 || !codings[^1].Equals("chunked", StringComparison.OrdinalIgnoreCase))
                throw new BadHttpRequestException("The final transfer coding of a request must be chunked.");

            var earlier = codings.Take(codings.Count - 1).ToList();
            if (earlier.Any(x => x.Equals("chunked", StringComparison.OrdinalIgnoreCase)))
                throw new BadHttpRequestException("Request applies the chunked transfer coding more than once.");

            // Anything layered under chunked (gzip, chunked) would reach the handler still encoded,
            // looking like an ordinary body. Nothing here decodes it, so say so (RFC 9112 §6.1).
            if (earlier.Count > 0)
                throw new BadHttpRequestException(
                    $"Transfer coding '{earlier[0]}' is not supported.",
                    StatusCodes.Status501NotImplemented
                );

            // The size limit is not checked here: routing has not run yet, and the endpoint may allow
            // more (or less) than the server default. The stream checks it when the body is first read.
            request.IsChunked = true;
            return new ChunkedReadStream(
                this.connection.Input,
                maxBodySize: null,
                start: this,
                maxTrailersSize: this.options.Limits.MaxRequestHeadersTotalSize
            );
        }

        if (!hasContentLength)
            return EmptyReadStream.Instance;

        var contentLength = request.Headers.ContentLength
            ?? throw new BadHttpRequestException("Malformed Content-Length header.");

        return contentLength == 0
            ? EmptyReadStream.Instance
            : new ContentLengthReadStream(this.connection.Input, contentLength, this);
    }

    static bool ExpectsContinue(HttpRequest request)
        => request.Headers.GetFirst(HeaderNames.Expect) is { } expect
            && expect.Contains("100-continue", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Called by the body stream just before its first read: fixes the size limit, and sends the
    /// 100 Continue the client may be waiting on — unless the declared length is already over the
    /// limit, in which case the read is about to fail with a 413 and the body is better never sent.
    /// </summary>
    async ValueTask<long?> IRequestBodySource.StartAsync(CancellationToken cancellationToken)
    {
        var request = this.context.Request;
        var max = request.LockMaxBodySize();

        if (this.continuePending)
        {
            this.continuePending = false;

            var refused = max is { } limit && request.ContentLength > limit;

            // Once the response has started a 100 would land in the middle of it, and a client that
            // already has a final response has stopped waiting for one anyway.
            if (!refused && !this.output.HasStarted)
                await this.SendContinueAsync(cancellationToken).ConfigureAwait(false);
        }

        return max;
    }

    async ValueTask SendContinueAsync(CancellationToken cancellationToken)
    {
        var writer = this.connection.Output;
        var span = writer.GetSpan(25);
        "HTTP/1.1 100 Continue\r\n\r\n"u8.CopyTo(span);
        writer.Advance(25);
        await this.output.FlushTransportAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Every body read comes through here, under <see cref="HttpServerLimits.RequestBodyTimeout"/>:
    /// a client that sends headers and then trickles — or stops sending — its body would otherwise
    /// hold the handler, and the connection's slot, for as long as it liked.
    /// </summary>
    async ValueTask<ReadResult> IRequestBodySource.ReadAsync(CancellationToken cancellationToken)
    {
        var input = this.connection.Input;
        if (this.readTimer.Expired)
            throw this.BodyTimeout();

        ReadResult result;
        this.readTimer.Start(this.options.Limits.RequestBodyTimeout);
        try
        {
            result = await input.ReadAsync(cancellationToken).ConfigureAwait(false);

            // A cancel left over from a deadline that was forgiven last time - see below - not a real one.
            if (this.staleCancelPending && result.IsCanceled && result.Buffer.IsEmpty)
            {
                this.staleCancelPending = false;
                input.AdvanceTo(result.Buffer.Start, result.Buffer.End);
                result = await input.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or ObjectDisposedException)
        {
            this.inputFaulted = true;
            throw;
        }
        finally
        {
            this.readTimer.Stop();
        }

        if (this.readTimer.Expired && !result.Buffer.IsEmpty)
        {
            // The deadline's timer is a callback, and on a busy machine it can run after the bytes it was waiting for
            // arrived but before this read picked them up. The client met the deadline: the data is served. If the
            // cancel did not land on this read, it is still latched on the pipe and would cut the next one short.
            this.readTimer.Forgive();
            this.staleCancelPending = !result.IsCanceled;
            return result;
        }

        if (this.readTimer.Expired)
        {
            // Advanced before throwing, or the drain or the next read would find the pipe still
            // waiting on this one.
            input.AdvanceTo(result.Buffer.Start);
            throw this.BodyTimeout();
        }

        return result;
    }

    void IRequestBodySource.OnBodyComplete()
    {
        this.bodyComplete = true;
        this.StartWatchingForDisconnect();
    }

    BadHttpRequestException BodyTimeout() => new(
        $"The request body was not received within {this.options.Limits.RequestBodyTimeout.TotalSeconds:0.#}s.",
        StatusCodes.Status408RequestTimeout
    );

    async ValueTask TryWriteErrorResponseAsync(int statusCode, string message, CancellationToken cancellationToken)
    {
        try
        {
            if (this.output.HasStarted)
                // Headers are already gone; there is no way to change the status now. Abort so the
                // client sees a broken response rather than a plausible-looking truncated one.
                return;

            this.context.Response.Reset();

            // A HEAD gets the status and headers of the error, never its text: a body on a HEAD
            // response is read by the client as the start of the next one.
            this.output.Begin(
                this.context.Response,
                suppressBody: HttpMethods.IsHead(this.context.Request.Method),
                keepAlive: false
            );
            this.context.Response.StatusCode = statusCode;
            await this.context.Response
                .WriteTextAsync(message, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            await this.output.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.logger.LogDebug(ex, "Failed to write error response on {ConnectionId}", this.connection.ConnectionId);
        }
    }

    internal static bool IsConnectionResetOrCancellation(Exception ex) => ex is OperationCanceledException
        or System.Net.Sockets.SocketException
        or ObjectDisposedException
        or IOException;
}
