using System.Buffers;
using System.Net.Quic;
using System.Net.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shiny.Net.HttpServer.Http2.Hpack;
using Shiny.Net.HttpServer.Http3.Qpack;

#pragma warning disable CA1416 // QUIC support is checked at runtime; the listener refuses to bind without it.

namespace Shiny.Net.HttpServer.Http3;

/// <summary>
/// Serves one QUIC connection as HTTP/3.
/// <para>
/// Much of what HTTP/2 had to build is gone: QUIC provides the streams, their ordering and their
/// flow control, and a lost packet on one stream no longer stalls the others. What remains is the
/// control stream handshake, framing on each request stream, and QPACK.
/// </para>
/// </summary>
sealed class Http3Connection(
    QuicConnection connection,
    Http3Options options,
    HttpServerOptions serverOptions,
    RequestDelegate pipeline,
    IServiceProvider? services,
    ILoggerFactory loggerFactory
)
{
    readonly ILogger logger = loggerFactory.CreateLogger<Http3Connection>();
    readonly QpackDecoder decoder = new(options.MaxFieldSectionSize);
    readonly QpackEncoder encoder = new();

    long nextConnectionId;

    public async Task ProcessAsync(CancellationToken cancellationToken)
    {
        try
        {
            // The control stream carries SETTINGS and must be opened before anything is sent that
            // depends on them — which, for a server, means before the first response.
            await this.SendControlStreamAsync(cancellationToken).ConfigureAwait(false);

            while (!cancellationToken.IsCancellationRequested)
            {
                var stream = await connection.AcceptInboundStreamAsync(cancellationToken).ConfigureAwait(false);

                _ = stream.Type == QuicStreamType.Bidirectional
                    ? Task.Run(() => this.ServeRequestAsync(stream, cancellationToken), CancellationToken.None)
                    : Task.Run(() => this.ReadUnidirectionalAsync(stream, cancellationToken), CancellationToken.None);
            }
        }
        catch (Exception ex) when (IsExpectedDisconnect(ex))
        {
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "The HTTP/3 connection faulted");
        }
        finally
        {
            try
            {
                await connection.CloseAsync(Http3ErrorCode.NoError, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsExpectedDisconnect(ex))
            {
            }

            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    async Task SendControlStreamAsync(CancellationToken cancellationToken)
    {
        var stream = await connection
            .OpenOutboundStreamAsync(QuicStreamType.Unidirectional, cancellationToken)
            .ConfigureAwait(false);

        var buffer = new ArrayBufferWriter<byte>(32);

        VariableLengthInteger.Write(buffer, Http3StreamType.Control);

        // Capacity zero is what keeps QPACK simple here: the peer may not reference a dynamic
        // table, so no field section can ever block on one arriving.
        var settings = Http3Frame.BuildSettings(
        [
            (Http3SettingId.QpackMaxTableCapacity, 0),
            (Http3SettingId.QpackBlockedStreams, 0),
            (Http3SettingId.MaxFieldSectionSize, options.MaxFieldSectionSize)
        ]);

        Http3Frame.Write(buffer, Http3FrameType.Settings, settings);

        await stream.WriteAsync(buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        // Deliberately left open. A control stream that closes is a connection error (RFC 9114
        // §6.2.1), so it lives as long as the connection does.
    }

    /// <summary>
    /// Reads a peer-opened unidirectional stream.
    /// <para>
    /// Its first varint says what it is. The control stream's settings are read and applied; the
    /// QPACK encoder and decoder streams are drained and ignored, which is correct while the
    /// dynamic table is disabled — a peer that respects our capacity of zero sends nothing on them.
    /// </para>
    /// </summary>
    async Task ReadUnidirectionalAsync(QuicStream stream, CancellationToken cancellationToken)
    {
        try
        {
            var type = await VariableLengthInteger.ReadAsync(stream, cancellationToken).ConfigureAwait(false);

            if (type is null)
                return;

            if (type == Http3StreamType.Control)
            {
                await this.ReadControlStreamAsync(stream, cancellationToken).ConfigureAwait(false);
                return;
            }

            // Unknown stream types are ignored by design — that is the extension mechanism — and
            // the QPACK streams have nothing to say to a server that disabled the dynamic table.
            var scratch = new byte[1024];

            while (await stream.ReadAsync(scratch, cancellationToken).ConfigureAwait(false) > 0)
            {
            }
        }
        catch (Exception ex) when (IsExpectedDisconnect(ex))
        {
        }
        catch (Exception ex)
        {
            this.logger.LogDebug(ex, "A unidirectional stream ended badly");
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    async Task ReadControlStreamAsync(QuicStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var pending = new ArrayBufferWriter<byte>(4096);
        var sawSettings = false;

        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;

            pending.Write(buffer.AsSpan(0, read));

            while (true)
            {
                var sequence = new ReadOnlySequence<byte>(pending.WrittenMemory);

                if (!Http3Frame.TryReadHeader(sequence, out var header, out var consumed))
                    break;

                var headerLength = (int)sequence.Slice(sequence.Start, consumed).Length;
                var total = headerLength + (int)header.Length;

                if (pending.WrittenCount < total)
                    break;

                var payload = pending.WrittenSpan.Slice(headerLength, (int)header.Length);

                if (header.KnownType == Http3FrameType.Settings)
                {
                    if (sawSettings)
                        throw new InvalidOperationException("SETTINGS arrived twice on the control stream.");

                    sawSettings = true;

                    if (!Http3Frame.TryParseSettings(payload, out var settings))
                        throw new InvalidOperationException("The SETTINGS payload is malformed.");

                    this.logger.LogDebug("HTTP/3 peer settings: {Count}", settings.Count);
                }
                else if (!sawSettings)
                {
                    // SETTINGS must come first (RFC 9114 §6.2.1); anything else before it means the
                    // rest cannot be interpreted safely.
                    throw new InvalidOperationException("The control stream began with a frame other than SETTINGS.");
                }

                // Rebuild the buffer without the frame just handled. Control frames are rare and
                // tiny, so the copy is not worth avoiding with a ring buffer.
                var rest = pending.WrittenSpan[total..].ToArray();
                pending.Clear();
                pending.Write(rest);
            }
        }
    }

    /// <summary>Serves one request on a bidirectional stream.</summary>
    async Task ServeRequestAsync(QuicStream stream, CancellationToken cancellationToken)
    {
        var id = $"h3-{connection.RemoteEndPoint}-{Interlocked.Increment(ref this.nextConnectionId)}";

        try
        {
            var request = await this.ReadRequestAsync(stream, cancellationToken).ConfigureAwait(false);
            if (request is null)
                return;

            var (fields, body) = request.Value;

            var context = new HttpContext();

            using var aborted = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            context.RequestAborted = aborted.Token;

            this.ApplyConnectionInfo(context, id, stream);

            if (!Http3RequestMapper.TryApply(context, fields, body, out var error))
            {
                await AbortAsync(stream, Http3ErrorCode.MessageError, cancellationToken).ConfigureAwait(false);
                this.logger.LogDebug("Rejected an HTTP/3 request: {Error}", error);

                return;
            }

            var output = new Http3ResponseBodyControl(stream, context.Response, this.encoder);
            context.Response.Bind(output);

            await this.RunPipelineAsync(context).ConfigureAwait(false);
            await output.CompleteAsync(context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsExpectedDisconnect(ex))
        {
        }
        catch (QpackException ex)
        {
            this.logger.LogDebug(ex, "QPACK decoding failed");
            await AbortAsync(stream, Http3ErrorCode.QpackDecompressionFailed, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "An HTTP/3 request faulted");
            await AbortAsync(stream, Http3ErrorCode.InternalError, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads HEADERS and any DATA frames from a request stream.
    /// <para>
    /// The body is buffered rather than streamed. Bounded by the configured maximum, and the reason
    /// is honesty about scope: streaming it would mean plumbing a pipe through the frame reader,
    /// and an embedded server's uploads are small. The limit is enforced as it reads, so a large
    /// body is refused rather than accumulated.
    /// </para>
    /// </summary>
    async Task<(List<HeaderField> Fields, byte[] Body)?> ReadRequestAsync(QuicStream stream, CancellationToken cancellationToken)
    {
        var pending = new ArrayBufferWriter<byte>(4096);
        var buffer = new byte[8192];

        List<HeaderField>? fields = null;
        var body = new ArrayBufferWriter<byte>();
        var maxBody = serverOptions.Limits.MaxRequestBodySize ?? long.MaxValue;

        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

            if (read > 0)
                pending.Write(buffer.AsSpan(0, read));

            while (true)
            {
                var sequence = new ReadOnlySequence<byte>(pending.WrittenMemory);

                if (!Http3Frame.TryReadHeader(sequence, out var header, out var consumed))
                    break;

                var headerLength = (int)sequence.Slice(sequence.Start, consumed).Length;

                if (header.Length < 0 || pending.WrittenCount < headerLength + header.Length)
                    break;

                var payload = pending.WrittenSpan.Slice(headerLength, (int)header.Length);

                switch (header.KnownType)
                {
                    case Http3FrameType.Headers when fields is null:
                        fields = this.decoder.Decode(payload);
                        break;

                    case Http3FrameType.Headers:
                        // A second HEADERS is trailers, which nothing here consumes.
                        break;

                    case Http3FrameType.Data:
                        if (body.WrittenCount + payload.Length > maxBody)
                            throw new BadHttpRequestException(
                                "The request body is larger than the configured limit.",
                                StatusCodes.Status413PayloadTooLarge
                            );

                        body.Write(payload);
                        break;

                    default:
                        // Unknown and connection-level frames are ignored on a request stream.
                        break;
                }

                var total = headerLength + (int)header.Length;
                var rest = pending.WrittenSpan[total..].ToArray();

                pending.Clear();
                pending.Write(rest);
            }

            if (read == 0)
                break;
        }

        return fields is null ? null : (fields, body.WrittenSpan.ToArray());
    }

    void ApplyConnectionInfo(HttpContext context, string id, QuicStream stream)
    {
        var info = context.Connection;

        info.ConnectionId = id;

        // QUIC has no unencrypted mode, so this is not a question the way it is on TCP.
        info.IsEncrypted = true;

        if (connection.RemoteEndPoint is { } remote)
        {
            info.RemoteIpAddress = remote.Address;
            info.RemotePort = remote.Port;
        }

        if (connection.LocalEndPoint is System.Net.IPEndPoint local)
        {
            info.LocalIpAddress = local.Address;
            info.LocalPort = local.Port;
        }

        context.Request.Protocol = HttpProtocols.Http3;
        context.Request.Scheme = "https";
        context.AbortAction = () => stream.Abort(QuicAbortDirection.Both, Http3ErrorCode.RequestCancelled);
    }

    /// <summary>
    /// Runs the pipeline in a request scope, so <c>Scoped</c> registrations behave the same on
    /// HTTP/3 as on every other transport.
    /// </summary>
    async Task RunPipelineAsync(HttpContext context)
    {
        if (services is null)
        {
            await pipeline(context).ConfigureAwait(false);
            return;
        }

        var scope = services.CreateAsyncScope();

        try
        {
            context.RequestServices = scope.ServiceProvider;
            await pipeline(context).ConfigureAwait(false);
        }
        finally
        {
            context.RequestServices = EmptyServiceProvider.Instance;
            await scope.DisposeAsync().ConfigureAwait(false);
        }
    }

    static async ValueTask AbortAsync(QuicStream stream, long errorCode, CancellationToken cancellationToken)
    {
        try
        {
            stream.Abort(QuicAbortDirection.Both, errorCode);
        }
        catch (Exception ex) when (IsExpectedDisconnect(ex))
        {
        }

        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    internal static bool IsExpectedDisconnect(Exception ex) => ex
        is OperationCanceledException
        or ObjectDisposedException
        or QuicException
        or IOException;
}
