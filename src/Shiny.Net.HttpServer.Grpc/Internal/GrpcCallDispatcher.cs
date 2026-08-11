using System.Buffers;
using System.Globalization;
using System.IO.Pipelines;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Shiny.Net.HttpServer.Grpc.Internal;

/// <summary>
/// Runs one gRPC call end to end: works out which framing the caller is using, sets the deadline,
/// invokes the method, and reports a status whatever happens — including when the handler throws
/// halfway through a stream, which is exactly the case trailers exist for.
/// </summary>
sealed class GrpcCallDispatcher(
    string serviceName,
    string methodName,
    GrpcMethodHandler method,
    GrpcOptions options,
    ILogger logger
)
{
    public async ValueTask InvokeAsync(HttpContext http)
    {
        if (!GrpcProtocol.TryParseContentType(http.Request.ContentType, out var kind))
        {
            await RejectAsync(
                http,
                StatusCodes.Status415UnsupportedMediaType,
                $"'{http.Request.ContentType}' is not a gRPC content type."
            ).ConfigureAwait(false);

            return;
        }

        if (kind != GrpcProtocolKind.Grpc && !options.EnableGrpcWeb)
        {
            await RejectAsync(http, StatusCodes.Status415UnsupportedMediaType, "gRPC-Web is disabled on this server.")
                .ConfigureAwait(false);

            return;
        }

        // Native gRPC is defined over HTTP/2 and nothing else: its status lives in trailers, and
        // HTTP/1.1 keep-alive framing cannot carry them on a Content-Length response. A client stuck
        // on 1.1 wants gRPC-Web, so say so rather than fail on the last frame.
        if (kind == GrpcProtocolKind.Grpc && http.Request.Protocol is HttpProtocols.Http10 or HttpProtocols.Http11)
        {
            await RejectAsync(
                http,
                StatusCodes.Status505HttpVersionNotSupported,
                "gRPC requires HTTP/2. Use gRPC-Web (application/grpc-web) over HTTP/1.1."
            ).ConfigureAwait(false);

            return;
        }

        var requestEncoding = http.Request.Headers.GetFirst(GrpcProtocol.HeaderEncoding) ?? GrpcProtocol.EncodingIdentity;
        var responseEncoding = ResolveResponseEncoding(http);

        var response = http.Response;
        response.StatusCode = StatusCodes.Status200OK;

        // Echoed, not rebuilt: the suffix after the '+' names the message encoding the two ends
        // agreed on, and a client that asked for +json is entitled to be answered in kind.
        response.ContentType = Normalize(http.Request.ContentType!, kind);
        response.Headers.Set(GrpcProtocol.HeaderEncoding, responseEncoding);
        response.Headers.Set(
            GrpcProtocol.HeaderAcceptEncoding,
            $"{GrpcProtocol.EncodingIdentity},{GrpcProtocol.EncodingGzip},{GrpcProtocol.EncodingDeflate}"
        );

        if (kind == GrpcProtocolKind.Grpc)
        {
            // HTTP/2 needs no announcement, but an HTTP/1.1 proxy in the middle is entitled to drop
            // trailers it was not told to expect — and dropping these ones loses the call's outcome.
            response.DeclareTrailer(GrpcProtocol.HeaderStatus);
            response.DeclareTrailer(GrpcProtocol.HeaderMessage);
        }

        var deadline = GrpcProtocol.ParseTimeout(http.Request.Headers.GetFirst(GrpcProtocol.HeaderTimeout));

        using var deadlineSource = new CancellationTokenSource();
        using var callSource = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, deadlineSource.Token);

        if (deadline is { } timeout)
            deadlineSource.CancelAfter(timeout);

        var context = new GrpcCallContext(
            http,
            serviceName,
            methodName,
            deadline is { } expiry ? DateTimeOffset.UtcNow.Add(expiry) : null,
            callSource.Token
        );

        var writer = new GrpcFrameWriter(response.BodyWriter, kind, responseEncoding, options.MaxSendMessageSize);

        var status = GrpcStatusCode.Ok;
        string? statusMessage = null;

        try
        {
            if (kind != GrpcProtocolKind.Grpc && !method.SupportsWeb)
                throw new GrpcStatusException(
                    GrpcStatusCode.Unimplemented,
                    "gRPC-Web cannot express client-streaming or bidirectional calls."
                );

            if (!GrpcCompression.IsSupported(requestEncoding))
                throw new GrpcStatusException(
                    GrpcStatusCode.Unimplemented,
                    $"The message encoding '{requestEncoding}' is not supported by this server."
                );

            var reader = await this.CreateReaderAsync(http, kind, callSource.Token).ConfigureAwait(false);
            var environment = new GrpcCallEnvironment(reader, writer, requestEncoding, options.MaxReceiveMessageSize);

            // Headers go out before the handler runs. Everything rejected above this line still has
            // an unstarted response, which is what lets it answer with a single HEADERS frame
            // carrying the status — the Trailers-Only shape clients expect for a call that never
            // began. Once the handler is running, the outcome belongs in the trailers.
            await http.Response.StartAsync(callSource.Token).ConfigureAwait(false);

            await method.InvokeAsync(context, environment, callSource.Token).ConfigureAwait(false);
        }
        catch (GrpcStatusException ex)
        {
            status = ex.StatusCode;
            statusMessage = ex.StatusMessage ?? ex.Message;
        }
        catch (OperationCanceledException) when (deadlineSource.IsCancellationRequested)
        {
            status = GrpcStatusCode.DeadlineExceeded;
            statusMessage = "The call did not complete before its deadline.";

            logger.LogDebug("Deadline exceeded on {Method}", context.FullMethod);
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
            // The caller is gone. Nothing can be reported to them, and the response never reaches a
            // wire that is still there, so this only decides what the logs say.
            logger.LogDebug("Caller cancelled {Method}", context.FullMethod);
            return;
        }
        catch (Exception ex)
        {
            status = GrpcStatusCode.Unknown;
            statusMessage = options.EnableDetailedErrors
                ? ex.Message
                : "An exception was thrown by the handler.";

            logger.LogError(ex, "Unhandled exception in {Method}", context.FullMethod);
        }

        await this.CompleteAsync(http, kind, writer, context, status, statusMessage).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the call's outcome: trailers on native gRPC, a trailer frame in the body on gRPC-Web.
    /// </summary>
    async ValueTask CompleteAsync(
        HttpContext http,
        GrpcProtocolKind kind,
        GrpcFrameWriter writer,
        GrpcCallContext context,
        GrpcStatusCode status,
        string? statusMessage
    )
    {
        var trailers = kind == GrpcProtocolKind.Grpc ? http.Response.Trailers : context.ResponseTrailers;

        if (kind == GrpcProtocolKind.Grpc)
        {
            foreach (var (name, values) in context.ResponseTrailers)
                trailers[name] = values;
        }

        trailers.Set(GrpcProtocol.HeaderStatus, ((int)status).ToString(CultureInfo.InvariantCulture));

        if (!string.IsNullOrEmpty(statusMessage))
            trailers.Set(GrpcProtocol.HeaderMessage, GrpcProtocol.EscapeMessage(statusMessage));
        else
            trailers.Remove(GrpcProtocol.HeaderMessage);

        // The status is the response on gRPC-Web too, so its frame has to go out even when the call
        // produced no messages at all.
        if (kind != GrpcProtocolKind.Grpc)
        {
            try
            {
                await writer.WriteTrailerFrameAsync(trailers, http.RequestAborted).ConfigureAwait(false);
                await writer.FinishAsync(http.RequestAborted).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <summary>
    /// The request body, framed. Text mode is decoded up front rather than streamed: it only exists
    /// for browsers, browsers cannot client-stream over gRPC-Web, and a base64 group split across a
    /// chunk boundary is a decoder's problem that a single message does not have.
    /// </summary>
    async ValueTask<PipeReader> CreateReaderAsync(HttpContext http, GrpcProtocolKind kind, CancellationToken cancellationToken)
    {
        if (kind != GrpcProtocolKind.GrpcWebText)
            return http.Request.BodyReader;

        var buffer = new ArrayBufferWriter<byte>(1024);
        var limit = options.MaxReceiveMessageSize is { } max ? max + 1024 : int.MaxValue;

        while (true)
        {
            var result = await http.Request.BodyReader.ReadAsync(cancellationToken).ConfigureAwait(false);

            foreach (var segment in result.Buffer)
                buffer.Write(segment.Span);

            http.Request.BodyReader.AdvanceTo(result.Buffer.End);

            if (buffer.WrittenCount > limit)
                throw new GrpcStatusException(
                    GrpcStatusCode.ResourceExhausted,
                    "The base64 request body exceeded the message size limit."
                );

            if (result.IsCompleted)
                break;
        }

        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(Encoding.ASCII.GetString(buffer.WrittenSpan).Trim());
        }
        catch (FormatException ex)
        {
            throw new GrpcStatusException(GrpcStatusCode.Internal, "The request body is not valid base64.", ex);
        }

        return PipeReader.Create(new ReadOnlySequence<byte>(decoded));
    }

    string ResolveResponseEncoding(HttpContext http)
    {
        if (options.ResponseCompression is not { } configured)
            return GrpcProtocol.EncodingIdentity;

        var accepted = http.Request.Headers.GetFirst(GrpcProtocol.HeaderAcceptEncoding);

        return GrpcProtocol.Accepts(accepted, configured) ? configured : GrpcProtocol.EncodingIdentity;
    }

    static string Normalize(string requestContentType, GrpcProtocolKind kind)
    {
        var semicolon = requestContentType.IndexOf(';');
        var bare = (semicolon >= 0 ? requestContentType[..semicolon] : requestContentType).Trim();

        return bare.Length > 0
            ? bare
            : kind switch
            {
                GrpcProtocolKind.GrpcWeb => GrpcProtocol.ContentTypeGrpcWeb,
                GrpcProtocolKind.GrpcWebText => GrpcProtocol.ContentTypeGrpcWebText,
                _ => GrpcProtocol.ContentTypeGrpc
            };
    }

    static async ValueTask RejectAsync(HttpContext http, int statusCode, string message)
    {
        http.Response.StatusCode = statusCode;
        await http.Response.WriteTextAsync(message).ConfigureAwait(false);
    }
}
