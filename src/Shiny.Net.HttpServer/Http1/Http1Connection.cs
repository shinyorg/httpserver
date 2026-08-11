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
sealed class Http1Connection
{
    readonly IConnection connection;
    readonly HttpServerOptions options;
    readonly RequestDelegate application;
    readonly IServiceProvider? rootServices;
    readonly ILogger logger;

    readonly HttpContext context = new();
    readonly Http1RequestParser parser = new();
    readonly Http1OutputProducer output;

    public Http1Connection(
        IConnection connection,
        HttpServerOptions options,
        RequestDelegate application,
        IServiceProvider? rootServices,
        ILogger logger
    )
    {
        this.connection = connection;
        this.options = options;
        this.application = application;
        this.rootServices = rootServices;
        this.logger = logger;
        this.output = new Http1OutputProducer(connection.Output, options);
    }

    public async Task ProcessAsync(CancellationToken connectionToken)
    {
        var requestsServed = 0L;

        try
        {
            while (!connectionToken.IsCancellationRequested)
            {
                this.context.Reset();
                this.parser.Reset();
                this.ApplyConnectionInfo();

                var received = await this.TryReadRequestHeadAsync(connectionToken).ConfigureAwait(false);
                if (received == ReadHeadResult.ConnectionClosed)
                    return;

                if (received == ReadHeadResult.BadRequest)
                    // The error response has already been written; the connection is not reusable
                    // because we do not know where the malformed request ended.
                    return;

                requestsServed++;

                var keepAlive = this.DetermineKeepAlive(requestsServed);
                var handled = await this.HandleRequestAsync(keepAlive, connectionToken).ConfigureAwait(false);

                if (!handled)
                    return;
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
    }

    void ApplyConnectionInfo()
    {
        var info = this.context.Connection;
        info.ConnectionId = this.connection.ConnectionId;
        info.IsEncrypted = this.connection.IsEncrypted;
        info.ClientCertificate = this.connection.ClientCertificate;
        info.IsTunneled = this.connection.IsTunneled;

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
        this.context.AbortAction = this.connection.Abort;
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
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(connectionToken, timeoutSource.Token);
        var sawAnyBytes = false;

        while (true)
        {
            ReadResult result;
            try
            {
                result = await input.ReadAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
            {
                if (sawAnyBytes)
                    // Mid-request stall: tell the client why before hanging up.
                    await this.TryWriteErrorResponseAsync(
                        StatusCodes.Status408RequestTimeout,
                        "Request timed out while reading headers.",
                        connectionToken
                    ).ConfigureAwait(false);

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
                return ReadHeadResult.ConnectionClosed;
        }
    }

    bool DetermineKeepAlive(long requestsServed)
    {
        if (this.options.Limits.MaxRequestsPerConnection is { } max && requestsServed >= max)
            return false;

        var connectionHeader = this.context.Request.Headers[HeaderNames.Connection];
        for (var i = 0; i < connectionHeader.Count; i++)
        {
            var value = connectionHeader[i];
            if (value is null)
                continue;

            if (value.Contains("close", StringComparison.OrdinalIgnoreCase))
                return false;

            if (value.Contains("keep-alive", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // 1.1 defaults to persistent, 1.0 does not.
        return this.context.Request.Protocol == HttpProtocols.Http11;
    }

    /// <summary>Returns false when the connection must be closed after this request.</summary>
    async ValueTask<bool> HandleRequestAsync(bool keepAlive, CancellationToken connectionToken)
    {
        var request = this.context.Request;
        var response = this.context.Response;

        using var requestAborted = CancellationTokenSource.CreateLinkedTokenSource(connectionToken);
        this.context.RequestAborted = requestAborted.Token;

        var suppressBody = HttpMethods.IsHead(request.Method);
        this.output.Begin(response, suppressBody, keepAlive);

        try
        {
            this.ValidateHost(request);
            this.ApplyForwardedHeaders();
            var bodyStream = this.CreateRequestBodyStream(request);
            request.Body = bodyStream;
            request.Cookies.SetRaw(request.Headers.GetFirst(HeaderNames.Cookie));

            await this.SendContinueIfExpectedAsync(request, connectionToken).ConfigureAwait(false);
            await this.RunPipelineAsync().ConfigureAwait(false);
            await this.output.CompleteAsync(connectionToken).ConfigureAwait(false);

            // After a 101 the bytes belong to whatever protocol took over, and the handler has
            // already finished speaking it. There is no next request to parse.
            if (this.output.IsUpgrade)
                return false;

            if (!this.output.AllowsKeepAlive)
                return false;

            // Unread body bytes would be misparsed as the head of the next request, so either
            // consume them or give up on reusing the connection.
            if (bodyStream is ReadOnlyRequestStream drainable)
            {
                var drained = await drainable.TryDrainAsync(connectionToken).ConfigureAwait(false);
                if (!drained)
                    return false;
            }

            return true;
        }
        catch (BadHttpRequestException ex)
        {
            await this.TryWriteErrorResponseAsync(ex.StatusCode, ex.Message, connectionToken)
                .ConfigureAwait(false);

            return false;
        }
        catch (Exception ex) when (!IsConnectionResetOrCancellation(ex))
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
            requestAborted.Cancel();
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

            return;
        }

        request.Host = host[0];
    }

    void ApplyForwardedHeaders()
    {
        if (!this.options.UseForwardedHeaders)
            return;

        var request = this.context.Request;

        if (request.Headers.GetFirst(HeaderNames.XForwardedProto) is { Length: > 0 } proto)
            request.Scheme = proto.Contains(',')
                ? proto[..proto.IndexOf(',')].Trim()
                : proto.Trim();

        if (request.Headers.GetFirst(HeaderNames.XForwardedHost) is { Length: > 0 } host)
            request.Host = host.Contains(',') ? host[..host.IndexOf(',')].Trim() : host.Trim();

        if (this.context.GetClientIpAddress(useForwardedHeaders: true) is { } clientIp)
            this.context.Connection.RemoteIpAddress = clientIp;
    }

    Stream CreateRequestBodyStream(HttpRequest request)
    {
        var transferEncoding = request.Headers.GetFirst(HeaderNames.TransferEncoding);
        var hasContentLength = request.Headers.ContainsKey(HeaderNames.ContentLength);
        var isChunked = transferEncoding is not null
            && transferEncoding.Contains("chunked", StringComparison.OrdinalIgnoreCase);

        // Both framings at once is the classic smuggling setup: two intermediaries can disagree on
        // where the body ends. Reject rather than pick a winner.
        if (isChunked && hasContentLength)
            throw new BadHttpRequestException(
                "Request specifies both Transfer-Encoding: chunked and Content-Length."
            );

        if (isChunked)
        {
            request.IsChunked = true;
            return new ChunkedReadStream(this.connection.Input, this.options.Limits.MaxRequestBodySize);
        }

        if (!hasContentLength)
            return EmptyReadStream.Instance;

        var contentLength = request.Headers.ContentLength
            ?? throw new BadHttpRequestException("Malformed Content-Length header.");

        if (this.options.Limits.MaxRequestBodySize is { } max && contentLength > max)
            throw new BadHttpRequestException(
                $"Request body of {contentLength} bytes exceeds the {max} byte limit.",
                StatusCodes.Status413PayloadTooLarge
            );

        return contentLength == 0
            ? EmptyReadStream.Instance
            : new ContentLengthReadStream(this.connection.Input, contentLength);
    }

    async ValueTask SendContinueIfExpectedAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        var expect = request.Headers.GetFirst(HeaderNames.Expect);
        if (expect is null || !expect.Contains("100-continue", StringComparison.OrdinalIgnoreCase))
            return;

        // Sent eagerly rather than on first body read. Slightly less precise than the spec's intent,
        // but it keeps clients that block waiting for it from stalling for a full round of timeouts.
        var writer = this.connection.Output;
        var span = writer.GetSpan(25);
        "HTTP/1.1 100 Continue\r\n\r\n"u8.CopyTo(span);
        writer.Advance(25);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    async ValueTask TryWriteErrorResponseAsync(int statusCode, string message, CancellationToken cancellationToken)
    {
        try
        {
            if (this.output.HasStarted)
                // Headers are already gone; there is no way to change the status now. Abort so the
                // client sees a broken response rather than a plausible-looking truncated one.
                return;

            this.context.Response.Reset();
            this.output.Begin(this.context.Response, suppressBody: false, keepAlive: false);
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
