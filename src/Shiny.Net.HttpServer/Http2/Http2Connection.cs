using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shiny.Net.HttpServer.Http2.Hpack;
using Shiny.Net.HttpServer.Transports;

namespace Shiny.Net.HttpServer.Http2;

/// <summary>
/// Drives one HTTP/2 connection: read frames, dispatch streams, write responses.
/// <para>
/// The shape is different from HTTP/1.1 and that difference is the whole protocol. One read loop
/// owns the socket and fans frames out to streams, because frames for many streams are interleaved
/// on one connection; each stream then runs the pipeline concurrently. Nothing writes to the socket
/// except through the frame writer, which serializes — two handlers flushing at once would splice
/// their frames together.
/// </para>
/// </summary>
sealed class Http2Connection
{
    readonly IConnection connection;
    readonly HttpServerOptions options;
    readonly RequestDelegate application;
    readonly IServiceProvider? rootServices;
    readonly ILogger logger;

    readonly Http2FrameWriter writer;
    readonly HpackDecoder decoder;
    readonly HpackEncoder encoder = new();
    readonly ConcurrentDictionary<uint, Http2Stream> streams = new();

    /// <summary>What we told the peer it may do. Constrains what it sends us.</summary>
    readonly Http2Settings localSettings;

    /// <summary>What the peer told us it may accept. Constrains what we send it.</summary>
    readonly Http2Settings remoteSettings = new();

    readonly SemaphoreSlim connectionWindowAvailable = new(0);
    readonly CancellationTokenSource shutdown = new();

    // A header block can span HEADERS + CONTINUATION, and nothing may interleave between them, so
    // this state outlives any one frame.
    readonly ArrayBufferWriter<byte> headerBlock = new();
    uint continuationStream;
    Http2FrameFlags headerFlags;

    long connectionSendWindow = Http2Settings.DefaultInitialWindowSize;
    long connectionReceiveWindow = Http2Settings.DefaultInitialWindowSize;
    uint lastStreamId;
    bool goAwaySent;

    public Http2Connection(
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
        this.writer = new Http2FrameWriter(connection.Output);

        this.localSettings = new Http2Settings
        {
            MaxConcurrentStreams = options.Http2.MaxConcurrentStreams,
            InitialWindowSize = options.Http2.InitialStreamWindowSize,
            MaxFrameSize = options.Http2.MaxFrameSize,
            MaxHeaderListSize = options.Http2.MaxHeaderListSize,
            EnablePush = false
        };

        this.decoder = new HpackDecoder(this.localSettings.HeaderTableSize)
        {
            MaxAllowedTableSize = this.localSettings.HeaderTableSize
        };
    }

    public async Task ProcessAsync(CancellationToken connectionToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(connectionToken, this.shutdown.Token);
        var token = linked.Token;

        try
        {
            if (!await this.ReadPrefaceAsync(token).ConfigureAwait(false))
                return;

            await this.SendSettingsAsync(token).ConfigureAwait(false);
            await this.ReadFramesAsync(token).ConfigureAwait(false);
        }
        catch (Http2ConnectionException ex)
        {
            this.logger.LogDebug("HTTP/2 connection error on {ConnectionId}: {Message}", this.connection.ConnectionId, ex.Message);
            await this.TryGoAwayAsync(ex.Code, ex.Message).ConfigureAwait(false);
        }
        catch (HpackException ex)
        {
            // A broken header block leaves the compression state unrecoverable, so the connection
            // cannot continue even though only one stream misbehaved.
            await this.TryGoAwayAsync(Http2ErrorCode.CompressionError, ex.Message).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsDisconnect(ex))
        {
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "Unhandled HTTP/2 error on {ConnectionId}", this.connection.ConnectionId);
            await this.TryGoAwayAsync(Http2ErrorCode.InternalError, "Internal error").ConfigureAwait(false);
        }
        finally
        {
            await this.ShutdownAsync().ConfigureAwait(false);
        }
    }

    async ValueTask<bool> ReadPrefaceAsync(CancellationToken cancellationToken)
    {
        var input = this.connection.Input;

        while (true)
        {
            var result = await input.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;

            if (buffer.Length >= Http2Frame.Preface.Length)
            {
                Span<byte> preface = stackalloc byte[Http2Frame.Preface.Length];
                buffer.Slice(0, preface.Length).CopyTo(preface);

                if (!preface.SequenceEqual(Http2Frame.Preface))
                    throw new Http2ConnectionException(Http2ErrorCode.ProtocolError, "Bad connection preface.");

                input.AdvanceTo(buffer.GetPosition(preface.Length));
                return true;
            }

            input.AdvanceTo(buffer.Start, buffer.End);

            if (result.IsCompleted || result.IsCanceled)
                return false;
        }
    }

    async ValueTask SendSettingsAsync(CancellationToken cancellationToken)
    {
        await this.writer.WriteAsync(
            w =>
            {
                var payload = new ArrayBufferWriter<byte>(48);
                this.localSettings.Write(payload);

                Http2Frame.Write(w, Http2FrameType.Settings, Http2FrameFlags.None, 0, payload.WrittenSpan);

                // The connection-level window starts at 65535 regardless of SETTINGS, so a server
                // that wants a larger one has to ask for it explicitly.
                var increment = this.options.Http2.InitialConnectionWindowSize - Http2Settings.DefaultInitialWindowSize;
                if (increment > 0)
                {
                    Span<byte> update = stackalloc byte[4];
                    BinaryPrimitives.WriteUInt32BigEndian(update, (uint)increment);
                    Http2Frame.Write(w, Http2FrameType.WindowUpdate, Http2FrameFlags.None, 0, update);

                    Interlocked.Add(ref this.connectionReceiveWindow, increment);
                }
            },
            cancellationToken
        ).ConfigureAwait(false);
    }

    async Task ReadFramesAsync(CancellationToken cancellationToken)
    {
        var input = this.connection.Input;

        while (!cancellationToken.IsCancellationRequested)
        {
            var result = await input.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;

            while (true)
            {
                var frameBuffer = buffer;
                if (!Http2Frame.TryReadHeader(ref frameBuffer, out var header))
                    break;

                if (header.Length > this.localSettings.MaxFrameSize)
                    throw new Http2ConnectionException(Http2ErrorCode.FrameSizeError, "A frame exceeds SETTINGS_MAX_FRAME_SIZE.");

                if (frameBuffer.Length < header.Length)
                    break;

                var payload = frameBuffer.Slice(0, header.Length);
                buffer = frameBuffer.Slice(header.Length);

                if (this.continuationStream != 0 && header.Type != Http2FrameType.Continuation)
                    throw new Http2ConnectionException(
                        Http2ErrorCode.ProtocolError,
                        "A frame interrupted a header block."
                    );

                await this.HandleFrameAsync(header, payload, cancellationToken).ConfigureAwait(false);
            }

            input.AdvanceTo(buffer.Start, buffer.End);

            if (result.IsCompleted || result.IsCanceled)
                return;
        }
    }

    async ValueTask HandleFrameAsync(
        Http2FrameHeader header,
        ReadOnlySequence<byte> payload,
        CancellationToken cancellationToken
    )
    {
        switch (header.Type)
        {
            case Http2FrameType.Settings:
                await this.HandleSettingsAsync(header, payload, cancellationToken).ConfigureAwait(false);
                return;

            case Http2FrameType.Ping:
                await this.HandlePingAsync(header, payload, cancellationToken).ConfigureAwait(false);
                return;

            case Http2FrameType.WindowUpdate:
                this.HandleWindowUpdate(header, payload);
                return;

            case Http2FrameType.RstStream:
                this.HandleRstStream(header, payload);
                return;

            case Http2FrameType.GoAway:
                this.shutdown.Cancel();
                return;

            case Http2FrameType.Priority:
                // Priority signalling is advisory and deprecated; accepting and ignoring it is both
                // legal and what every modern server does.
                if (header.Length != 5)
                    throw new Http2ConnectionException(Http2ErrorCode.FrameSizeError, "A PRIORITY frame must be 5 bytes.");
                return;

            case Http2FrameType.PushPromise:
                throw new Http2ConnectionException(Http2ErrorCode.ProtocolError, "A client cannot push.");

            case Http2FrameType.Data:
                await this.HandleDataAsync(header, payload, cancellationToken).ConfigureAwait(false);
                return;

            case Http2FrameType.Headers:
            {
                if (header.StreamId == 0)
                    throw new Http2ConnectionException(Http2ErrorCode.ProtocolError, "HEADERS on stream 0.");

                var body = Http2Frame.RemovePadding(header, payload);

                // The priority fields, if present, sit in front of the header block.
                if (header.Has(Http2FrameFlags.Priority))
                {
                    if (body.Length < 5)
                        throw new Http2ConnectionException(Http2ErrorCode.FrameSizeError, "HEADERS is too short for its priority fields.");

                    body = body.Slice(5);
                }

                this.headerBlock.Write(body.ToArray());
                this.headerFlags = header.Flags;

                if (header.Has(Http2FrameFlags.EndHeaders))
                {
                    await this.StartStreamAsync(header.StreamId, this.headerBlock, this.headerFlags, cancellationToken)
                        .ConfigureAwait(false);

                    this.headerBlock.Clear();
                    this.continuationStream = 0;
                }
                else
                {
                    this.continuationStream = header.StreamId;
                }

                return;
            }

            case Http2FrameType.Continuation:
            {
                if (this.continuationStream != header.StreamId)
                    throw new Http2ConnectionException(Http2ErrorCode.ProtocolError, "Unexpected CONTINUATION.");

                this.headerBlock.Write(payload.ToArray());

                if (header.Has(Http2FrameFlags.EndHeaders))
                {
                    await this.StartStreamAsync(header.StreamId, this.headerBlock, this.headerFlags, cancellationToken)
                        .ConfigureAwait(false);

                    this.headerBlock.Clear();
                    this.continuationStream = 0;
                }

                return;
            }

            default:
                // Unknown frame types must be ignored — that is how the protocol stays extensible.
                return;
        }
    }

    async ValueTask HandleSettingsAsync(Http2FrameHeader header, ReadOnlySequence<byte> payload, CancellationToken cancellationToken)
    {
        if (header.StreamId != 0)
            throw new Http2ConnectionException(Http2ErrorCode.ProtocolError, "SETTINGS on a non-zero stream.");

        if (header.Has(Http2FrameFlags.Ack))
        {
            if (header.Length != 0)
                throw new Http2ConnectionException(Http2ErrorCode.FrameSizeError, "A SETTINGS ack must be empty.");

            return;
        }

        var delta = this.remoteSettings.Apply(payload);

        // A changed INITIAL_WINDOW_SIZE retroactively adjusts every open stream by the same amount.
        if (delta != 0)
        {
            foreach (var stream in this.streams.Values)
            {
                stream.SendWindow += delta;

                if (delta > 0)
                    Release(stream.WindowAvailable);
            }
        }

        await this.writer.WriteFrameAsync(Http2FrameType.Settings, Http2FrameFlags.Ack, 0, default, cancellationToken)
            .ConfigureAwait(false);
    }

    async ValueTask HandlePingAsync(Http2FrameHeader header, ReadOnlySequence<byte> payload, CancellationToken cancellationToken)
    {
        if (header.Length != 8)
            throw new Http2ConnectionException(Http2ErrorCode.FrameSizeError, "A PING frame must be 8 bytes.");

        if (header.Has(Http2FrameFlags.Ack))
            return;

        // Echoed verbatim: the payload is the peer's, and it is how they match the reply.
        await this.writer.WriteFrameAsync(Http2FrameType.Ping, Http2FrameFlags.Ack, 0, payload.ToArray(), cancellationToken)
            .ConfigureAwait(false);
    }

    void HandleWindowUpdate(Http2FrameHeader header, ReadOnlySequence<byte> payload)
    {
        if (header.Length != 4)
            throw new Http2ConnectionException(Http2ErrorCode.FrameSizeError, "A WINDOW_UPDATE frame must be 4 bytes.");

        Span<byte> raw = stackalloc byte[4];
        payload.CopyTo(raw);

        var increment = BinaryPrimitives.ReadUInt32BigEndian(raw) & 0x7FFFFFFF;
        if (increment == 0)
            throw new Http2ConnectionException(Http2ErrorCode.ProtocolError, "A WINDOW_UPDATE of zero is not allowed.");

        if (header.StreamId == 0)
        {
            var updated = Interlocked.Add(ref this.connectionSendWindow, increment);

            if (updated > int.MaxValue)
                throw new Http2ConnectionException(Http2ErrorCode.FlowControlError, "The connection window overflowed.");

            Release(this.connectionWindowAvailable);
            return;
        }

        if (this.streams.TryGetValue(header.StreamId, out var stream))
        {
            stream.SendWindow += increment;
            Release(stream.WindowAvailable);
        }
    }

    void HandleRstStream(Http2FrameHeader header, ReadOnlySequence<byte> payload)
    {
        if (header.Length != 4)
            throw new Http2ConnectionException(Http2ErrorCode.FrameSizeError, "A RST_STREAM frame must be 4 bytes.");

        if (this.streams.TryRemove(header.StreamId, out var stream))
            stream.Abort();
    }

    async ValueTask HandleDataAsync(Http2FrameHeader header, ReadOnlySequence<byte> payload, CancellationToken cancellationToken)
    {
        if (header.StreamId == 0)
            throw new Http2ConnectionException(Http2ErrorCode.ProtocolError, "DATA on stream 0.");

        // The whole frame counts against flow control, padding included — otherwise a peer could
        // send unbounded padding for free.
        var received = header.Length;

        if (!this.streams.TryGetValue(header.StreamId, out var stream))
        {
            // The stream is gone but the bytes still arrived, so the window still has to be
            // returned or the connection slowly starves.
            await this.ReplenishConnectionWindowAsync(received, cancellationToken).ConfigureAwait(false);
            return;
        }

        var body = Http2Frame.RemovePadding(header, payload);

        if (!body.IsEmpty)
        {
            foreach (var segment in body)
                stream.RequestBodyWriter.Write(segment.Span);

            await stream.RequestBodyWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        if (header.Has(Http2FrameFlags.EndStream))
        {
            stream.State = Http2StreamState.HalfClosedRemote;
            stream.CompleteRequestBody();
        }

        await this.ReplenishWindowsAsync(stream, received, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gives flow-control credit back once bytes have been handed to the handler.
    /// <para>
    /// Topped up in chunks rather than per frame: a WINDOW_UPDATE for every DATA frame doubles the
    /// packet count on an upload for no benefit.
    /// </para>
    /// </summary>
    async ValueTask ReplenishWindowsAsync(Http2Stream stream, int consumed, CancellationToken cancellationToken)
    {
        stream.ReceiveWindow -= consumed;

        var threshold = this.localSettings.InitialWindowSize / 2;
        if (stream.ReceiveWindow <= threshold && stream.State != Http2StreamState.Closed)
        {
            var increment = this.localSettings.InitialWindowSize - (int)stream.ReceiveWindow;
            stream.ReceiveWindow += increment;

            await this.SendWindowUpdateAsync(stream.Id, increment, cancellationToken).ConfigureAwait(false);
        }

        await this.ReplenishConnectionWindowAsync(consumed, cancellationToken).ConfigureAwait(false);
    }

    async ValueTask ReplenishConnectionWindowAsync(int consumed, CancellationToken cancellationToken)
    {
        var remaining = Interlocked.Add(ref this.connectionReceiveWindow, -consumed);
        var target = this.options.Http2.InitialConnectionWindowSize;

        if (remaining > target / 2)
            return;

        var increment = (int)(target - remaining);
        Interlocked.Add(ref this.connectionReceiveWindow, increment);

        await this.SendWindowUpdateAsync(0, increment, cancellationToken).ConfigureAwait(false);
    }

    ValueTask SendWindowUpdateAsync(uint streamId, int increment, CancellationToken cancellationToken)
    {
        if (increment <= 0)
            return ValueTask.CompletedTask;

        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(payload, (uint)increment);

        return this.writer.WriteFrameAsync(Http2FrameType.WindowUpdate, Http2FrameFlags.None, streamId, payload, cancellationToken);
    }

    async ValueTask StartStreamAsync(
        uint streamId,
        ArrayBufferWriter<byte> headerBlock,
        Http2FrameFlags flags,
        CancellationToken cancellationToken
    )
    {
        // Client streams are odd and strictly increasing; anything else is a protocol error, and
        // enforcing it is what stops a peer reusing an id whose state we already discarded.
        if (streamId % 2 == 0 || streamId <= this.lastStreamId)
            throw new Http2ConnectionException(Http2ErrorCode.ProtocolError, $"Invalid new stream id {streamId}.");

        this.lastStreamId = streamId;

        var fields = new List<HeaderField>(16);
        this.decoder.Decode(headerBlock.WrittenSpan, fields);

        if (this.localSettings.MaxConcurrentStreams is { } max && this.streams.Count >= max)
        {
            await this.SendRstStreamAsync(streamId, Http2ErrorCode.RefusedStream, cancellationToken).ConfigureAwait(false);
            return;
        }

        var stream = new Http2Stream(
            streamId,
            this.remoteSettings.InitialWindowSize,
            this.localSettings.InitialWindowSize
        )
        {
            State = Http2StreamState.Open,
            Dispatched = true
        };

        this.streams[streamId] = stream;

        if ((flags & Http2FrameFlags.EndStream) != 0)
        {
            stream.State = Http2StreamState.HalfClosedRemote;
            stream.CompleteRequestBody();
        }

        stream.Handler = Task.Run(() => this.RunStreamAsync(stream, fields), CancellationToken.None);
    }

    async Task RunStreamAsync(Http2Stream stream, List<HeaderField> fields)
    {
        var context = new HttpContext();

        try
        {
            using var aborted = CancellationTokenSource.CreateLinkedTokenSource(stream.Aborted.Token, this.shutdown.Token);
            context.RequestAborted = aborted.Token;

            this.ApplyConnectionInfo(context);

            if (!Http2RequestMapper.TryApply(context, fields, stream, out var error))
            {
                await this.SendRstStreamAsync(stream.Id, Http2ErrorCode.ProtocolError, CancellationToken.None).ConfigureAwait(false);
                this.logger.LogDebug("Rejected HTTP/2 stream {StreamId}: {Error}", stream.Id, error);

                return;
            }

            var output = new Http2ResponseBodyControl(this, stream, context.Response);
            context.Response.Bind(output);

            await this.RunPipelineAsync(context).ConfigureAwait(false);
            await output.CompleteAsync(context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsDisconnect(ex))
        {
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "Unhandled exception on HTTP/2 stream {StreamId}", stream.Id);

            try
            {
                await this.SendRstStreamAsync(stream.Id, Http2ErrorCode.InternalError, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
            }
        }
        finally
        {
            this.streams.TryRemove(stream.Id, out _);
            stream.Dispose();
        }
    }

    async Task RunPipelineAsync(HttpContext context)
    {
        if (this.rootServices is null)
        {
            await this.application(context).ConfigureAwait(false);
            return;
        }

        var scope = this.rootServices.CreateAsyncScope();
        try
        {
            context.RequestServices = scope.ServiceProvider;
            await this.application(context).ConfigureAwait(false);
        }
        finally
        {
            context.RequestServices = EmptyServiceProvider.Instance;
            await scope.DisposeAsync().ConfigureAwait(false);
        }
    }

    void ApplyConnectionInfo(HttpContext context)
    {
        var info = context.Connection;
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

        context.Request.Protocol = HttpProtocols.Http2;
        context.Request.Scheme = this.connection.IsEncrypted ? "https" : "http";
        context.AbortAction = this.connection.Abort;
    }

    /// <summary>Writes the response headers for a stream.</summary>
    internal ValueTask WriteHeadersAsync(Http2Stream stream, HttpResponse response, bool endStream, CancellationToken cancellationToken)
    {
        var fields = new List<HeaderField>(response.Headers.Count + 1)
        {
            // :status must come first, and pseudo-headers must all precede the regular ones.
            new(":status", response.StatusCode.ToString(System.Globalization.CultureInfo.InvariantCulture))
        };

        foreach (var header in response.Headers)
        {
            // Connection-specific headers are forbidden in HTTP/2 and a peer may treat them as a
            // protocol error. The framing they describe does not exist here.
            if (IsConnectionSpecific(header.Key))
                continue;

            foreach (var value in header.Value)
            {
                if (value is not null)
                    fields.Add(new HeaderField(header.Key.ToLowerInvariant(), value));
            }
        }

        response.Headers.IsReadOnly = true;

        return this.writer.WriteAsync(
            w =>
            {
                var block = new ArrayBufferWriter<byte>(256);
                this.encoder.Encode(fields, block);

                var flags = Http2FrameFlags.EndHeaders | (endStream ? Http2FrameFlags.EndStream : Http2FrameFlags.None);
                var maxFrame = this.remoteSettings.MaxFrameSize;
                var remaining = block.WrittenSpan;

                if (remaining.Length <= maxFrame)
                {
                    Http2Frame.Write(w, Http2FrameType.Headers, flags, stream.Id, remaining);
                    return;
                }

                // Too big for one frame: the first is HEADERS and the rest CONTINUATION, and only
                // the last carries END_HEADERS.
                var first = remaining[..maxFrame];
                Http2Frame.Write(
                    w,
                    Http2FrameType.Headers,
                    endStream ? Http2FrameFlags.EndStream : Http2FrameFlags.None,
                    stream.Id,
                    first
                );

                remaining = remaining[maxFrame..];

                while (remaining.Length > maxFrame)
                {
                    Http2Frame.Write(w, Http2FrameType.Continuation, Http2FrameFlags.None, stream.Id, remaining[..maxFrame]);
                    remaining = remaining[maxFrame..];
                }

                Http2Frame.Write(w, Http2FrameType.Continuation, Http2FrameFlags.EndHeaders, stream.Id, remaining);
            },
            cancellationToken
        );
    }

    /// <summary>
    /// Writes body bytes, respecting both flow-control windows.
    /// <para>
    /// A send waits for whichever window is short. That wait is the correct behaviour, not a
    /// deadlock: the peer grants credit as it consumes, and blocking here is what stops a fast
    /// server from overrunning a slow client's buffers.
    /// </para>
    /// </summary>
    internal async ValueTask WriteDataAsync(
        Http2Stream stream,
        ReadOnlyMemory<byte> data,
        bool endStream,
        CancellationToken cancellationToken
    )
    {
        while (!data.IsEmpty)
        {
            var available = await this.ReserveWindowAsync(stream, data.Length, cancellationToken).ConfigureAwait(false);
            if (available <= 0)
                return;

            var chunk = data[..available];
            data = data[available..];

            var last = endStream && data.IsEmpty;

            await this.writer.WriteFrameAsync(
                Http2FrameType.Data,
                last ? Http2FrameFlags.EndStream : Http2FrameFlags.None,
                stream.Id,
                chunk,
                cancellationToken
            ).ConfigureAwait(false);
        }

        if (endStream && data.IsEmpty)
            await this.writer.WriteFrameAsync(Http2FrameType.Data, Http2FrameFlags.EndStream, stream.Id, default, cancellationToken)
                .ConfigureAwait(false);
    }

    /// <summary>Takes as much window as is available, waiting when there is none.</summary>
    async ValueTask<int> ReserveWindowAsync(Http2Stream stream, int wanted, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (stream.State == Http2StreamState.Closed || cancellationToken.IsCancellationRequested)
                return 0;

            var available = (int)Math.Min(
                Math.Min(stream.SendWindow, Interlocked.Read(ref this.connectionSendWindow)),
                Math.Min(wanted, this.remoteSettings.MaxFrameSize)
            );

            if (available > 0)
            {
                stream.SendWindow -= available;
                Interlocked.Add(ref this.connectionSendWindow, -available);

                return available;
            }

            // Wait on whichever window is empty. Both are signalled on WINDOW_UPDATE.
            var waitOn = stream.SendWindow <= 0 ? stream.WindowAvailable : this.connectionWindowAvailable;
            await waitOn.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal ValueTask SendRstStreamAsync(uint streamId, Http2ErrorCode code, CancellationToken cancellationToken)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(payload, (uint)code);

        return this.writer.WriteFrameAsync(Http2FrameType.RstStream, Http2FrameFlags.None, streamId, payload, cancellationToken);
    }

    async ValueTask TryGoAwayAsync(Http2ErrorCode code, string message)
    {
        if (this.goAwaySent)
            return;

        this.goAwaySent = true;

        try
        {
            var reason = System.Text.Encoding.UTF8.GetBytes(message);
            var payload = new byte[8 + reason.Length];

            BinaryPrimitives.WriteUInt32BigEndian(payload, this.lastStreamId);
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4), (uint)code);
            reason.CopyTo(payload, 8);

            await this.writer.WriteFrameAsync(Http2FrameType.GoAway, Http2FrameFlags.None, 0, payload, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            // The peer is already gone. Nothing to tell it.
        }
    }

    async ValueTask ShutdownAsync()
    {
        try
        {
            this.shutdown.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        var handlers = new List<Task>();

        foreach (var stream in this.streams.Values)
        {
            stream.Abort();

            if (stream.Handler is { } handler)
                handlers.Add(handler);
        }

        if (handlers.Count > 0)
        {
            try
            {
                await Task.WhenAll(handlers).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
            }
        }

        await this.writer.DisposeAsync().ConfigureAwait(false);
        this.connectionWindowAvailable.Dispose();
        this.shutdown.Dispose();
    }

    static void Release(SemaphoreSlim semaphore)
    {
        try
        {
            semaphore.Release();
        }
        catch (SemaphoreFullException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    static bool IsConnectionSpecific(string name) => name.Equals(HeaderNames.Connection, StringComparison.OrdinalIgnoreCase)
        || name.Equals(HeaderNames.TransferEncoding, StringComparison.OrdinalIgnoreCase)
        || name.Equals(HeaderNames.KeepAlive, StringComparison.OrdinalIgnoreCase)
        || name.Equals(HeaderNames.Upgrade, StringComparison.OrdinalIgnoreCase)
        || name.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase);

    internal static bool IsDisconnect(Exception ex) => ex
        is OperationCanceledException
        or System.Net.Sockets.SocketException
        or ObjectDisposedException
        or IOException;
}
