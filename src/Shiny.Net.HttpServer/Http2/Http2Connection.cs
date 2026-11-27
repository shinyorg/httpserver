using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipelines;
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
/// <para>
/// The read loop must never wait on a handler. It is the only thing that sees WINDOW_UPDATE, PING,
/// RST_STREAM and every other stream's frames, so a loop parked on one slow handler stops the whole
/// connection - and if that handler is itself waiting on a WINDOW_UPDATE, forever. Request bodies are
/// buffered instead, and the receive windows are what bound the buffer.
/// </para>
/// </summary>
sealed class Http2Connection
{
    /// <summary>The window over which stream resets and refusals are counted for abuse.</summary>
    const long AbuseWindowMilliseconds = 10_000;

    /// <summary>
    /// Client resets of streams still being handled, per <see cref="AbuseWindowMilliseconds"/>,
    /// before the connection is closed with ENHANCE_YOUR_CALM. Cancelling is normal; cancelling a
    /// hundred requests a second, every second, is the rapid-reset attack (CVE-2023-44487): each
    /// one costs a handler, and the reset frees the client's concurrency slot immediately.
    /// </summary>
    const int MaxResetsPerWindow = 1_000;

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

    readonly Http2Signal connectionWindowAvailable = new();

    /// <summary>Ends the read loop. Everything on the connection stops with it.</summary>
    readonly CancellationTokenSource shutdown = new();

    /// <summary>Streams still in the table whose response has ended; see the concurrency check in StartStreamAsync.</summary>
    int endedStreams;

    /// <summary>Orders "is a new stream accepted" against "the drain's GOAWAY names the last accepted stream".</summary>
    readonly object streamIdGate = new();
    readonly CancellationToken closing;

    /// <summary>Part of every stream's RequestAborted: cancelled when the server stops or the connection ends.</summary>
    readonly CancellationTokenSource streamsAborted = new();

    /// <summary>Fires the idle and request-header timeouts; re-armed as the connection changes state.</summary>
    CancellationTokenSource timeout = new();
    readonly object timeoutGate = new();

    // A header block can span HEADERS + CONTINUATION, and nothing may interleave between them, so
    // this state outlives any one frame.
    readonly ArrayBufferWriter<byte> headerBlock = new();
    readonly int maxHeaderBlockSize;
    volatile uint continuationStream;
    Http2FrameFlags headerFlags;
    long headerStarted;
    long idleSince = Environment.TickCount64;

    long connectionSendWindow = Http2Settings.DefaultInitialWindowSize;
    long connectionReceiveWindow = Http2Settings.DefaultInitialWindowSize;
    long connectionPendingCredit;
    uint lastStreamId;

    int goAwayState;
    volatile bool draining;
    volatile bool remoteGoAway;
    volatile bool closed;

    long abuseWindowStart = Environment.TickCount64;
    int refusedStreams;
    int resetStreams;

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
        this.writer = new Http2FrameWriter(
            connection.Output,
            this.shutdown.Token,
            options.Limits.ResponseWriteTimeout,
            () =>
            {
                this.logger.LogDebug(
                    "Closing HTTP/2 connection {ConnectionId}: the client stopped accepting data",
                    connection.ConnectionId
                );
                this.AbortStreams();
            }
        );
        this.closing = this.shutdown.Token;

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
            MaxAllowedTableSize = this.localSettings.HeaderTableSize,
            MaxHeaderListSize = options.Http2.MaxHeaderListSize
        };

        // Encoded bytes are buffered until the block is complete, before any of it can be decoded
        // and measured, so they need a ceiling of their own. Twice the list limit: an encoder only
        // ever makes a header smaller, so a block this big decodes to more than the limit anyway,
        // and the slack lets a modestly oversized request get a 431 instead of losing the connection.
        this.maxHeaderBlockSize = (int)Math.Min(int.MaxValue, 2L * options.Http2.MaxHeaderListSize);
    }

    /// <summary>
    /// True once the connection ended on an error GOAWAY: the client may still have frames in flight, and closing
    /// over them would reset the connection and could cost the client the GOAWAY that explains why.
    /// </summary>
    internal bool LingerOnClose { get; private set; }

    /// <summary>Serves the connection until it closes. A stop with no drain phase: both moments at once.</summary>
    public Task ProcessAsync(CancellationToken connectionToken) => this.ProcessAsync(connectionToken, connectionToken);

    /// <summary>
    /// Serves the connection until it closes, following the server's two stop moments.
    /// <paramref name="drainToken"/> is the polite one: GOAWAY goes out, no new streams are taken, and
    /// the open ones are left to finish - the same courtesy an HTTP/1.1 request in flight gets.
    /// <paramref name="abortToken"/> is the server running out of patience: open streams are cancelled
    /// and the connection is torn down.
    /// </summary>
    public async Task ProcessAsync(CancellationToken drainToken, CancellationToken abortToken)
    {
        try
        {
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(drainToken, abortToken, this.shutdown.Token))
            {
                if (!await this.ReadPrefaceAsync(linked.Token).ConfigureAwait(false))
                    return;

                await this.SendSettingsAsync(linked.Token).ConfigureAwait(false);
            }

            // From here a stopping server drains the connection rather than dropping it. Registered
            // only after SETTINGS has gone, because GOAWAY may not be the first frame we send.
            using var stopping = drainToken.Register(static state => ((Http2Connection)state!).BeginDrain(), this);
            using var aborting = abortToken.Register(static state => ((Http2Connection)state!).AbortStreams(), this);

            await this.ReadFramesAsync(this.shutdown.Token).ConfigureAwait(false);

            // The loop ended cleanly: the client hung up, went idle, or a drain finished.
            await this.TryGoAwayAsync(Http2ErrorCode.NoError, "").ConfigureAwait(false);
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
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this.timeout.Token);
        try
        {
            await this.ReadFramesAsync(input, cancellationToken, linked).ConfigureAwait(false);
        }
        finally
        {
            // Whichever linked source is current: a stale expiry swaps it.
            this.currentLinked?.Dispose();
            linked.Dispose();
        }
    }

    CancellationTokenSource? currentLinked;

    async Task ReadFramesAsync(PipeReader input, CancellationToken cancellationToken, CancellationTokenSource linked)
    {
        var reading = true;

        while (reading)
        {
            this.ArmTimeout();

            ReadResult? read = null;
            while (read is null)
            {
                try
                {
                    read = await input.ReadAsync((this.currentLinked ?? linked).Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (this.timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    // A block left incomplete is a client that stalled or is stalling on purpose; a
                    // connection with nothing open is one nobody is using. Either way it goes.
                    if (this.continuationStream != 0)
                        throw new Http2ConnectionException(Http2ErrorCode.ProtocolError, "Request headers timed out.");

                    if (this.streams.IsEmpty)
                        return;

                    // Stale: the idle timer came due just as a stream was accepted, and a fired source cannot be
                    // re-armed. Closing now would drop a request already being served, after the GOAWAY told the
                    // client it was processed - so the timeout starts over and the read carries on.
                    this.RenewTimeout(cancellationToken);
                }
            }

            var result = read.Value;

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

            // A cancelled read is how a finished drain wakes this loop; the frames already buffered
            // were still handled above. Anything else that cancels it ends the connection, as before.
            reading = !result.IsCompleted && !result.IsCanceled && !this.IsDrained;
        }
    }

    /// <summary>
    /// GOAWAY has gone one way or the other and every stream it let finish has finished, so the
    /// connection has nothing left to do.
    /// </summary>
    bool IsDrained => (this.draining || this.remoteGoAway) && this.streams.IsEmpty && this.continuationStream == 0;

    /// <summary>
    /// Points the timeout at whichever limit applies now: the request-header deadline while a
    /// header block is incomplete, the keep-alive deadline while no stream is open, and none while
    /// a stream is - a stream is the handler's business, with its own timeouts.
    /// <para>
    /// Deadlines rather than durations, so a client cannot hold a header block open by dripping
    /// CONTINUATION frames, or an idle connection by pinging it.
    /// </para>
    /// </summary>
    void ArmTimeout()
    {
        lock (this.timeoutGate)
        {
            if (this.timeout.IsCancellationRequested || this.closed)
                return;

            var now = Environment.TickCount64;

            if (this.continuationStream != 0)
                this.timeout.CancelAfter(Remaining(this.options.Limits.RequestHeadersTimeout, now - this.headerStarted));
            else if (this.streams.IsEmpty)
                this.timeout.CancelAfter(Remaining(this.options.Limits.KeepAliveTimeout, now - this.idleSince));
            else
                this.timeout.CancelAfter(Timeout.InfiniteTimeSpan);
        }
    }

    void RenewTimeout(CancellationToken cancellationToken)
    {
        CancellationTokenSource old;
        lock (this.timeoutGate)
        {
            old = this.timeout;
            this.timeout = new CancellationTokenSource();
        }

        old.Dispose();
        this.currentLinked?.Dispose();
        this.currentLinked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this.timeout.Token);
    }

    static TimeSpan Remaining(TimeSpan limit, long elapsedMilliseconds)
    {
        // CancelAfter takes at most int.MaxValue milliseconds; anything that long is "never".
        if (limit == Timeout.InfiniteTimeSpan || limit.TotalMilliseconds >= int.MaxValue)
            return Timeout.InfiniteTimeSpan;

        return TimeSpan.FromMilliseconds(Math.Max(1, limit.TotalMilliseconds - elapsedMilliseconds));
    }

    /// <summary>
    /// The server is stopping. Tell the client which streams will be answered, stop taking new ones,
    /// and let the open ones finish - told to stop, through RequestAborted, exactly as an HTTP/1.1
    /// request is - before the connection closes. Runs on whichever thread is stopping the server.
    /// </summary>
    void BeginDrain()
    {
        if (this.closed)
            return;

        lock (this.streamIdGate)
            this.draining = true;

        _ = this.TryGoAwayAsync(Http2ErrorCode.NoError, "Server is shutting down.").AsTask();

        // Open streams are left to finish; how long they get is the server's ShutdownTimeout, which
        // arrives as the abort token. Once the last one ends the read loop is woken and the
        // connection closes on its own.
        this.WakeIfDrained();
    }

    /// <summary>The stop ran out of time: cancel what is still running and close.</summary>
    void AbortStreams()
    {
        if (this.closed)
            return;

        // Quietly: these run the app's RequestAborted callbacks inline, on the stopping thread.
        HttpContext.CancelQuietly(this.streamsAborted);
        HttpContext.CancelQuietly(this.shutdown);
    }

    /// <summary>Unparks the read loop once a drain has nothing left to wait for.</summary>
    void WakeIfDrained()
    {
        if (!this.IsDrained)
            return;

        try
        {
            this.connection.Input.CancelPendingRead();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
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
                await this.HandleWindowUpdateAsync(header, payload, cancellationToken).ConfigureAwait(false);
                return;

            case Http2FrameType.RstStream:
                this.HandleRstStream(header);
                return;

            case Http2FrameType.GoAway:
                // The client will open no more streams, but the ones it has open still want their
                // answers (RFC 9113 §6.8). The connection closes once they have them.
                if (header.StreamId != 0)
                    throw new Http2ConnectionException(Http2ErrorCode.ProtocolError, "GOAWAY on a non-zero stream.");

                if (header.Length < 8)
                    throw new Http2ConnectionException(Http2ErrorCode.FrameSizeError, "A GOAWAY frame must be at least 8 bytes.");

                this.remoteGoAway = true;
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

                this.BufferHeaderBlock(body);
                this.headerFlags = header.Flags;

                if (header.Has(Http2FrameFlags.EndHeaders))
                {
                    await this.EndHeaderBlockAsync(header.StreamId, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    // The client has this long to finish the block, however it spaces the frames.
                    this.headerStarted = Environment.TickCount64;
                    this.continuationStream = header.StreamId;
                }

                return;
            }

            case Http2FrameType.Continuation:
            {
                if (this.continuationStream != header.StreamId)
                    throw new Http2ConnectionException(Http2ErrorCode.ProtocolError, "Unexpected CONTINUATION.");

                this.BufferHeaderBlock(payload);

                if (header.Has(Http2FrameFlags.EndHeaders))
                    await this.EndHeaderBlockAsync(header.StreamId, cancellationToken).ConfigureAwait(false);

                return;
            }

            default:
                // Unknown frame types must be ignored — that is how the protocol stays extensible.
                return;
        }
    }

    /// <summary>
    /// Holds a fragment of a header block until the block is complete. Bounded, because nothing
    /// can be decoded - or measured against MAX_HEADER_LIST_SIZE - until it is, and a peer could
    /// otherwise stream CONTINUATION frames into memory for as long as it liked.
    /// </summary>
    void BufferHeaderBlock(ReadOnlySequence<byte> fragment)
    {
        if (this.headerBlock.WrittenCount + fragment.Length > this.maxHeaderBlockSize)
            throw new Http2ConnectionException(Http2ErrorCode.EnhanceYourCalm, "A header block exceeds the size limit.");

        foreach (var segment in fragment)
            this.headerBlock.Write(segment.Span);
    }

    async ValueTask EndHeaderBlockAsync(uint streamId, CancellationToken cancellationToken)
    {
        try
        {
            await this.StartStreamAsync(streamId, this.headerBlock, this.headerFlags, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            this.headerBlock.Clear();
            this.continuationStream = 0;
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

        // A changed INITIAL_WINDOW_SIZE retroactively adjusts every open stream by the same amount,
        // and a stream it would push past 2^31-1 is a connection error (RFC 9113 §6.9.2).
        if (delta != 0)
        {
            var overflows = this.streams.Values.Any(x => x.SendWindow + delta > int.MaxValue);
            if (overflows)
                throw new Http2ConnectionException(Http2ErrorCode.FlowControlError, "INITIAL_WINDOW_SIZE overflowed a stream's window.");

            foreach (var stream in this.streams.Values)
            {
                stream.AdjustSendWindow(delta);

                if (delta > 0)
                    stream.WindowAvailable.Set();
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

    async ValueTask HandleWindowUpdateAsync(Http2FrameHeader header, ReadOnlySequence<byte> payload, CancellationToken cancellationToken)
    {
        if (header.Length != 4)
            throw new Http2ConnectionException(Http2ErrorCode.FrameSizeError, "A WINDOW_UPDATE frame must be 4 bytes.");

        Span<byte> raw = stackalloc byte[4];
        payload.CopyTo(raw);

        var increment = BinaryPrimitives.ReadUInt32BigEndian(raw) & 0x7FFFFFFF;

        if (header.StreamId == 0)
        {
            if (increment == 0)
                throw new Http2ConnectionException(Http2ErrorCode.ProtocolError, "A WINDOW_UPDATE of zero is not allowed.");

            var updated = Interlocked.Add(ref this.connectionSendWindow, increment);

            if (updated > int.MaxValue)
                throw new Http2ConnectionException(Http2ErrorCode.FlowControlError, "The connection window overflowed.");

            // Every waiting writer, not one: the credit may well cover several of them.
            this.connectionWindowAvailable.Set();
            return;
        }

        if (!this.streams.TryGetValue(header.StreamId, out var stream) || stream.State == Http2StreamState.Closed)
            return;

        // On a stream these are stream errors, not connection errors (RFC 9113 §6.9, §6.9.1): the
        // stream is reset and everything else carries on.
        if (increment == 0)
        {
            await this.ResetStreamAsync(stream, Http2ErrorCode.ProtocolError, "A WINDOW_UPDATE of zero is not allowed.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (stream.AdjustSendWindow(increment) > int.MaxValue)
        {
            await this.ResetStreamAsync(stream, Http2ErrorCode.FlowControlError, "The stream window overflowed.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        stream.WindowAvailable.Set();
    }

    void HandleRstStream(Http2FrameHeader header)
    {
        if (header.Length != 4)
            throw new Http2ConnectionException(Http2ErrorCode.FrameSizeError, "A RST_STREAM frame must be 4 bytes.");

        if (header.StreamId == 0)
            throw new Http2ConnectionException(Http2ErrorCode.ProtocolError, "RST_STREAM on stream 0.");

        // The stream stays in the table until its handler finishes. Removing it here would free the
        // concurrency slot while the handler is still running, and a client that opens and resets
        // streams in a loop would then run as many handlers as it could send frames.
        if (!this.streams.TryGetValue(header.StreamId, out var stream) || stream.State == Http2StreamState.Closed)
            return;

        this.CountAbuse(ref this.resetStreams, MaxResetsPerWindow, "Too many stream resets.");
        stream.Abort(new IOException("The client reset the stream."));
    }

    /// <summary>
    /// Counts one reset or refused stream, closing the connection with ENHANCE_YOUR_CALM once a
    /// window holds more than <paramref name="limit"/>. Read loop only, so no locking.
    /// </summary>
    void CountAbuse(ref int counter, int limit, string message)
    {
        var now = Environment.TickCount64;
        if (now - this.abuseWindowStart > AbuseWindowMilliseconds)
        {
            this.abuseWindowStart = now;
            this.refusedStreams = 0;
            this.resetStreams = 0;
        }

        counter++;
        if (counter > limit)
            throw new Http2ConnectionException(Http2ErrorCode.EnhanceYourCalm, message);
    }

    async ValueTask HandleDataAsync(Http2FrameHeader header, ReadOnlySequence<byte> payload, CancellationToken cancellationToken)
    {
        if (header.StreamId == 0)
            throw new Http2ConnectionException(Http2ErrorCode.ProtocolError, "DATA on stream 0.");

        // The whole frame counts against flow control, padding included — otherwise a peer could
        // send unbounded padding for free. Spent on arrival; given back as the handler reads.
        var received = header.Length;

        if (Interlocked.Add(ref this.connectionReceiveWindow, -received) < 0)
            throw new Http2ConnectionException(Http2ErrorCode.FlowControlError, "DATA exceeded the connection's flow-control window.");

        if (!this.streams.TryGetValue(header.StreamId, out var stream) || stream.State == Http2StreamState.Closed)
        {
            // The stream is gone, or one side reset it, but the bytes still arrived — so the window
            // still has to be returned or the connection slowly starves.
            await this.CreditConnectionAsync(received).ConfigureAwait(false);
            return;
        }

        if (stream.State == Http2StreamState.HalfClosedRemote)
        {
            // The client already ended this stream with END_STREAM (RFC 9113 §5.1).
            await this.CreditConnectionAsync(received).ConfigureAwait(false);
            await this.ResetStreamAsync(stream, Http2ErrorCode.StreamClosed, "DATA after the end of the stream.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (stream.AdjustReceiveWindow(-received) < 0)
        {
            await this.CreditConnectionAsync(received).ConfigureAwait(false);
            await this.ResetStreamAsync(stream, Http2ErrorCode.FlowControlError, "DATA exceeded the stream's flow-control window.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var body = Http2Frame.RemovePadding(header, payload);
        var padding = received - (int)body.Length;
        stream.ReceivedBodyBytes += body.Length;

        if (stream.DeclaredContentLength is { } declared && stream.ReceivedBodyBytes > declared)
        {
            await this.CreditConnectionAsync(received).ConfigureAwait(false);
            await this.ResetStreamAsync(stream, Http2ErrorCode.ProtocolError, "The request body is longer than its Content-Length.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        // Credit for the body goes back as the handler reads it. Padding is never read, so its
        // credit goes back now - and so does the body's, if the handler has already finished.
        var creditNow = padding;
        if (!body.IsEmpty && !stream.TryWriteBody(body))
            creditNow += (int)body.Length;

        if (padding > 0)
            await this.CreditStreamAsync(stream, padding, cancellationToken).ConfigureAwait(false);

        if (creditNow > 0)
            await this.CreditConnectionAsync(creditNow).ConfigureAwait(false);

        if (header.Has(Http2FrameFlags.EndStream))
            await this.EndRequestBodyAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The client has finished sending. A body that stops short of its Content-Length is a
    /// truncated upload, and has to reach the handler as an error rather than a clean end of
    /// stream (RFC 9113 §8.1.1).
    /// </summary>
    async ValueTask EndRequestBodyAsync(Http2Stream stream, CancellationToken cancellationToken)
    {
        if (stream.DeclaredContentLength is { } declared && stream.ReceivedBodyBytes != declared)
        {
            await this.ResetStreamAsync(stream, Http2ErrorCode.ProtocolError, "The request body is shorter than its Content-Length.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        stream.State = Http2StreamState.HalfClosedRemote;
        stream.CompleteRequestBody();
    }

    /// <summary>
    /// Resets one stream: the handler sees RequestAborted and an exception from its body, and the
    /// client sees RST_STREAM. Aborted first, so the stream is closed before the reset is queued and
    /// nothing of the response can follow it out.
    /// </summary>
    /// <summary>
    /// <see cref="HttpContext.Abort"/> on HTTP/2: this stream is reset with CANCEL, as HTTP/3 does. Aborting the
    /// connection instead took every other request multiplexed on it down too - and a late Abort on a finished stream
    /// did the same to a connection it no longer had anything to do with.
    /// </summary>
    void AbortStream(Http2Stream stream)
    {
        if (stream.State == Http2StreamState.Closed)
            return;

        // The stream is aborted synchronously, before the reset's first await, so RequestAborted is already
        // cancelled when this returns. Only the RST_STREAM write itself is left running.
        _ = this.ResetStreamAsync(stream, Http2ErrorCode.Cancel, "Aborted by the application.", CancellationToken.None)
            .AsTask()
            .ContinueWith(
                static t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default
            );
    }

    async ValueTask ResetStreamAsync(Http2Stream stream, Http2ErrorCode code, string reason, CancellationToken cancellationToken)
    {
        this.logger.LogDebug("Resetting HTTP/2 stream {StreamId} ({Code}): {Reason}", stream.Id, code, reason);

        stream.Abort(new IOException(reason));
        await this.SendRstStreamAsync(stream.Id, code, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The handler has read <paramref name="count"/> body bytes, so the client may send that many
    /// more. Runs on the handler's thread.
    /// </summary>
    async ValueTask OnBodyConsumedAsync(Http2Stream stream, int count)
    {
        await this.CreditConnectionAsync(count).ConfigureAwait(false);
        await this.CreditStreamAsync(stream, count, this.closing).ConfigureAwait(false);
    }

    /// <summary>
    /// Gives stream credit back. Topped up in chunks rather than per read: a WINDOW_UPDATE for every
    /// read doubles the packet count on an upload for no benefit. A stream the client has finished
    /// sending on needs no more.
    /// </summary>
    async ValueTask CreditStreamAsync(Http2Stream stream, int count, CancellationToken cancellationToken)
    {
        if (stream.State != Http2StreamState.Open)
            return;

        if (stream.AddPendingCredit(count) < this.localSettings.InitialWindowSize / 2)
            return;

        var increment = stream.TakePendingCredit();
        if (increment <= 0)
            return;

        stream.AdjustReceiveWindow(increment);

        // The state is checked again under the writer's lock, as for DATA: the stream can be reset between the check
        // above and this write, and nothing may follow our RST_STREAM out.
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(payload, (uint)increment);
        await this.writer.WriteAsync(
            w =>
            {
                if (stream.State == Http2StreamState.Open)
                    Http2Frame.Write(w, Http2FrameType.WindowUpdate, Http2FrameFlags.None, stream.Id, payload);
            },
            cancellationToken
        ).ConfigureAwait(false);
    }

    /// <summary>Gives connection credit back, in chunks, for bytes that have left the buffer.</summary>
    async ValueTask CreditConnectionAsync(long count)
    {
        var target = this.options.Http2.InitialConnectionWindowSize;

        if (Interlocked.Add(ref this.connectionPendingCredit, count) < target / 2)
            return;

        var increment = Interlocked.Exchange(ref this.connectionPendingCredit, 0);
        if (increment <= 0)
            return;

        Interlocked.Add(ref this.connectionReceiveWindow, increment);
        await this.SendWindowUpdateAsync(0, (int)increment, this.closing).ConfigureAwait(false);
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
        // Decoded before anything else is decided about the frame. HPACK is stateful: the dynamic
        // table is shared by every header block on the connection, so a block we skip decoding
        // leaves the decoder reading the next one against the wrong table.
        var fields = new List<HeaderField>(16);
        var withinLimit = this.decoder.Decode(headerBlock.WrittenSpan, fields);
        var endStream = (flags & Http2FrameFlags.EndStream) != 0;

        // A second HEADERS block on a live stream is request trailers, not a new request. Nothing
        // here consumes them, but they do end the request body — and tearing the connection down
        // over a legal frame would be worse than ignoring their content.
        if (this.streams.TryGetValue(streamId, out var open))
        {
            await this.HandleTrailersAsync(open, endStream, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Client streams are odd and strictly increasing; an even id is a protocol error. An id at
        // or below the high-water mark belongs to a stream that has already been torn down, so its
        // late frames are ignored the same way late DATA is.
        if (streamId % 2 == 0)
            throw new Http2ConnectionException(Http2ErrorCode.ProtocolError, $"Invalid new stream id {streamId}.");

        if (streamId <= this.lastStreamId)
            return;

        // After GOAWAY, in either direction, nothing new starts. Streams above the id we put in our
        // GOAWAY are ignored (RFC 9113 §6.8); the client knows to retry them elsewhere.
        // Checked and recorded under the same lock BeginDrain sets draining under: otherwise a stream could pass the
        // check, a drain's GOAWAY go out naming the stream before it as the last one processed, and this one then run
        // anyway - a request the client was told to retry elsewhere, executed twice.
        lock (this.streamIdGate)
        {
            if (this.draining || this.remoteGoAway)
                return;

            this.lastStreamId = streamId;
        }

        if (!withinLimit)
        {
            await this.RefuseOversizedHeadersAsync(streamId, endStream, cancellationToken).ConfigureAwait(false);
            return;
        }

        // The table holds every stream whose handler is still running, reset or not, so this is
        // the number of handlers the client has us running - the thing the limit is there to cap.
        // Streams whose response has already ended are left out: the client saw END_STREAM, counts
        // that stream as closed (RFC 9113 §5.1.2) and may open the next one at once, while the
        // handler is still unwinding here. Refusing those - and counting it as abuse - turned a
        // compliant client running at the limit into REFUSED_STREAM and then ENHANCE_YOUR_CALM. A
        // stream the client reset is not "ended" and still counts, so resets cannot buy it more
        // running handlers; the hard ceiling bounds handlers that unwind slowly after ending.
        if (this.localSettings.MaxConcurrentStreams is { } max
            && (this.streams.Count >= 2L * max
                || this.streams.Count - Volatile.Read(ref this.endedStreams) >= max))
        {
            await this.SendRstStreamAsync(streamId, Http2ErrorCode.RefusedStream, cancellationToken).ConfigureAwait(false);

            // A client that respects the limit is never refused, except briefly when it resets
            // streams faster than their handlers wind down. One that is refused over and over is
            // either broken or attacking.
            this.CountAbuse(ref this.refusedStreams, Math.Max(100, max), "Too many refused streams.");
            return;
        }

        var declaredLength = GetContentLength(fields);

        if (endStream && declaredLength > 0)
        {
            // A request that ends with its headers has no body, whatever Content-Length promised.
            await this.SendRstStreamAsync(streamId, Http2ErrorCode.ProtocolError, cancellationToken).ConfigureAwait(false);
            return;
        }

        var stream = new Http2Stream(
            streamId,
            this.remoteSettings.InitialWindowSize,
            this.localSettings.InitialWindowSize
        )
        {
            State = Http2StreamState.Open,
            Dispatched = true,
            DeclaredContentLength = declaredLength
        };

        stream.BodyConsumed = count => this.OnBodyConsumedAsync(stream, count);
        stream.BodyReadTimeout = this.options.Limits.RequestBodyTimeout;
        this.streams[streamId] = stream;

        if (endStream)
        {
            stream.State = Http2StreamState.HalfClosedRemote;
            stream.CompleteRequestBody();
        }
        else
        {
            stream.HasRequestBody = true;
        }

        stream.Handler = Task.Run(() => this.RunStreamAsync(stream, fields), CancellationToken.None);
    }

    /// <summary>
    /// Trailers have to end the stream, and can only come while it is open. Their content is not
    /// used; their END_STREAM is.
    /// </summary>
    async ValueTask HandleTrailersAsync(Http2Stream stream, bool endStream, CancellationToken cancellationToken)
    {
        if (stream.State == Http2StreamState.Closed)
            return;

        if (stream.State == Http2StreamState.HalfClosedRemote)
        {
            await this.ResetStreamAsync(stream, Http2ErrorCode.StreamClosed, "HEADERS after the end of the stream.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (!endStream)
        {
            await this.ResetStreamAsync(stream, Http2ErrorCode.ProtocolError, "Trailers must end the stream.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await this.EndRequestBodyAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The request's headers decoded to more than MAX_HEADER_LIST_SIZE. The block was decoded in
    /// full, so the connection is fine; only this request is refused, with the 431 it deserves.
    /// </summary>
    async ValueTask RefuseOversizedHeadersAsync(uint streamId, bool endStream, CancellationToken cancellationToken)
    {
        this.logger.LogDebug("Refused HTTP/2 stream {StreamId}: request headers exceed MaxHeaderListSize", streamId);

        List<HeaderField> fields = [new(":status", "431")];
        await this.WriteHeaderBlockAsync(streamId, null, fields, endStream: true, cancellationToken).ConfigureAwait(false);

        // A complete response before the body arrived: tell the client to stop sending it, without
        // failing the response it now has (RFC 9113 §8.1).
        if (!endStream)
            await this.SendRstStreamAsync(streamId, Http2ErrorCode.NoError, cancellationToken).ConfigureAwait(false);
    }

    static long? GetContentLength(List<HeaderField> fields)
    {
        var field = fields.FirstOrDefault(x => x.Name == "content-length");

        return field.Name is not null && long.TryParse(field.Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var length)
            ? length
            : null;
    }

    async Task RunStreamAsync(Http2Stream stream, List<HeaderField> fields)
    {
        var context = new HttpContext();
        Http2ResponseBodyControl? output = null;
        var ended = false;

        try
        {
            using var aborted = CancellationTokenSource.CreateLinkedTokenSource(stream.Aborted.Token, this.streamsAborted.Token);
            context.RequestAborted = aborted.Token;

            this.ApplyConnectionInfo(context);
            context.AbortAction = HttpContext.CancelThenAbort(aborted, () => this.AbortStream(stream));

            if (!Http2RequestMapper.TryApply(context, fields, stream, out var error))
            {
                await this.SendRstStreamAsync(stream.Id, Http2ErrorCode.ProtocolError, CancellationToken.None).ConfigureAwait(false);
                this.logger.LogDebug("Rejected HTTP/2 stream {StreamId}: {Error}", stream.Id, error);

                return;
            }

            context.Request.InitializeMaxBodySize(this.options.Limits.MaxRequestBodySize);

            output = new Http2ResponseBodyControl(this, stream, context.Response);
            context.Response.Bind(output);

            await this.RunPipelineAsync(context).ConfigureAwait(false);
            await output.CompleteAsync(context.RequestAborted).ConfigureAwait(false);

            // END_STREAM is out: the client may treat the slot as free from here.
            if (stream.State != Http2StreamState.Closed)
            {
                ended = true;
                Interlocked.Increment(ref this.endedStreams);
            }

            await this.StopUnreadBodyAsync(stream).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsDisconnect(ex))
        {
        }
        catch (BadHttpRequestException ex) when (output is { HasStarted: false })
        {
            // The client's fault, not the server's — a body over its limit, today. It gets a real
            // status rather than the INTERNAL_ERROR reset below.
            this.logger.LogDebug("Rejected HTTP/2 stream {StreamId}: {Message}", stream.Id, ex.Message);

            try
            {
                await this.WriteErrorResponseAsync(stream, context, ex).ConfigureAwait(false);
            }
            catch
            {
            }
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "Unhandled exception on HTTP/2 stream {StreamId}", stream.Id);

            try
            {
                if (stream.State != Http2StreamState.Closed)
                    await this.ResetStreamAsync(stream, Http2ErrorCode.InternalError, ex.Message, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
            }
        }
        finally
        {
            this.streams.TryRemove(stream.Id, out _);

            if (ended)
                Interlocked.Decrement(ref this.endedStreams);

            // Nothing will read the rest of the body now. Whatever was buffered and never read still
            // holds connection credit, and the client cannot send anything else until it is back.
            var unread = stream.CompleteReader();
            try
            {
                if (unread > 0)
                    await this.CreditConnectionAsync(unread).ConfigureAwait(false);
            }
            catch
            {
                // The connection is already gone.
            }

            stream.Dispose();

            if (this.streams.IsEmpty)
                Interlocked.Exchange(ref this.idleSince, Environment.TickCount64);

            this.ArmTimeout();
            this.WakeIfDrained();
        }
    }

    async ValueTask WriteErrorResponseAsync(Http2Stream stream, HttpContext context, BadHttpRequestException exception)
    {
        var response = context.Response;
        response.Reset();

        var output = new Http2ResponseBodyControl(this, stream, response);
        response.Bind(output);
        response.StatusCode = exception.StatusCode;

        await response.WriteTextAsync(exception.Message, cancellationToken: CancellationToken.None).ConfigureAwait(false);
        await output.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
        await this.StopUnreadBodyAsync(stream).ConfigureAwait(false);
    }

    /// <summary>
    /// The response is complete but the client is still sending a body nobody will read. Without
    /// this it waits for flow-control credit that never comes, since a finished stream's DATA no
    /// longer earns any. NO_ERROR after a complete response tells it to stop without failing the
    /// request (RFC 9113 §8.1).
    /// </summary>
    async ValueTask StopUnreadBodyAsync(Http2Stream stream)
    {
        if (stream.State != Http2StreamState.Open)
            return;

        stream.State = Http2StreamState.Closed;
        await this.SendRstStreamAsync(stream.Id, Http2ErrorCode.NoError, CancellationToken.None).ConfigureAwait(false);
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

        context.Request.Protocol = HttpProtocols.Http2;
        context.Request.Scheme = this.connection.IsEncrypted ? "https" : "http";
    }

    /// <summary>Writes the response headers for a stream.</summary>
    internal ValueTask WriteHeadersAsync(Http2Stream stream, HttpResponse response, bool endStream, CancellationToken cancellationToken)
    {
        var fields = new List<HeaderField>(response.Headers.Count + 1)
        {
            // :status must come first, and pseudo-headers must all precede the regular ones.
            new(":status", response.StatusCode.ToString(System.Globalization.CultureInfo.InvariantCulture))
        };

        AppendFields(fields, response.Headers);

        // A response that ends here has no body, so there is no second HEADERS frame for trailers to
        // ride on: they belong in this block. That shape has a name in gRPC — Trailers-Only — and it
        // is how a call that failed before producing anything reports its status.
        if (endStream && response.HasTrailers)
            AppendFields(fields, response.Trailers);

        response.Headers.IsReadOnly = true;

        return this.WriteHeaderBlockAsync(stream, fields, endStream, cancellationToken);
    }

    /// <summary>
    /// Writes an interim (1xx) HEADERS frame. Never END_STREAM: a 1xx is by definition not the end of
    /// anything, and a peer that sees one ending a stream must treat it as malformed.
    /// </summary>
    internal ValueTask WriteInformationalAsync(Http2Stream stream, int statusCode, HeaderDictionary headers, CancellationToken cancellationToken)
    {
        var fields = new List<HeaderField>(headers.Count + 1)
        {
            new(":status", statusCode.ToString(System.Globalization.CultureInfo.InvariantCulture))
        };

        AppendFields(fields, headers);

        return this.WriteHeaderBlockAsync(stream, fields, endStream: false, cancellationToken);
    }

    /// <summary>
    /// Writes the trailing HEADERS frame that closes a stream after its body.
    /// <para>
    /// No pseudo-headers: the status was settled by the first HEADERS frame, and a peer that finds
    /// one here is required to treat the stream as malformed.
    /// </para>
    /// </summary>
    internal ValueTask WriteTrailersAsync(Http2Stream stream, HeaderDictionary trailers, CancellationToken cancellationToken)
    {
        var fields = new List<HeaderField>(trailers.Count);
        AppendFields(fields, trailers);

        return this.WriteHeaderBlockAsync(stream, fields, endStream: true, cancellationToken);
    }

    static void AppendFields(List<HeaderField> fields, HeaderDictionary headers)
    {
        foreach (var header in headers)
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
    }

    ValueTask WriteHeaderBlockAsync(
        Http2Stream stream,
        List<HeaderField> fields,
        bool endStream,
        CancellationToken cancellationToken
    ) => this.WriteHeaderBlockAsync(stream.Id, stream, fields, endStream, cancellationToken);

    ValueTask WriteHeaderBlockAsync(
        uint streamId,
        Http2Stream? stream,
        List<HeaderField> fields,
        bool endStream,
        CancellationToken cancellationToken
    )
    {
        return this.writer.WriteAsync(
            w =>
            {
                // Checked under the writer's lock, which is also where a reset is queued, so
                // nothing of a response can follow its stream's RST_STREAM out. The encoder keeps no
                // dynamic table, so a block skipped here cannot put it out of step with the peer.
                if (stream is { State: Http2StreamState.Closed })
                    return;

                var block = new ArrayBufferWriter<byte>(256);
                this.encoder.Encode(fields, block);

                var flags = Http2FrameFlags.EndHeaders | (endStream ? Http2FrameFlags.EndStream : Http2FrameFlags.None);
                var maxFrame = this.remoteSettings.MaxFrameSize;
                var remaining = block.WrittenSpan;

                if (remaining.Length <= maxFrame)
                {
                    Http2Frame.Write(w, Http2FrameType.Headers, flags, streamId, remaining);
                    return;
                }

                // Too big for one frame: the first is HEADERS and the rest CONTINUATION, and only
                // the last carries END_HEADERS.
                var first = remaining[..maxFrame];
                Http2Frame.Write(
                    w,
                    Http2FrameType.Headers,
                    endStream ? Http2FrameFlags.EndStream : Http2FrameFlags.None,
                    streamId,
                    first
                );

                remaining = remaining[maxFrame..];

                while (remaining.Length > maxFrame)
                {
                    Http2Frame.Write(w, Http2FrameType.Continuation, Http2FrameFlags.None, streamId, remaining[..maxFrame]);
                    remaining = remaining[maxFrame..];
                }

                Http2Frame.Write(w, Http2FrameType.Continuation, Http2FrameFlags.EndHeaders, streamId, remaining);
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
        // An empty body still needs a frame to carry END_STREAM; a non-empty one carries it on its
        // last chunk, and a second frame claiming the same end is a protocol error.
        var empty = data.IsEmpty;

        while (!data.IsEmpty)
        {
            var available = await this.ReserveWindowAsync(stream, data.Length, cancellationToken).ConfigureAwait(false);
            if (available <= 0)
                return;

            var chunk = data[..available];
            data = data[available..];

            var last = endStream && data.IsEmpty;

            await this.WriteDataFrameAsync(stream, chunk, last, cancellationToken).ConfigureAwait(false);
        }

        if (endStream && empty)
            await this.WriteDataFrameAsync(stream, default, true, cancellationToken).ConfigureAwait(false);
    }

    ValueTask WriteDataFrameAsync(Http2Stream stream, ReadOnlyMemory<byte> chunk, bool endStream, CancellationToken cancellationToken)
        => this.writer.WriteAsync(
            w =>
            {
                // Under the writer's lock, like headers: nothing may follow a reset out.
                if (stream.State != Http2StreamState.Closed)
                    Http2Frame.Write(w, Http2FrameType.Data, endStream ? Http2FrameFlags.EndStream : Http2FrameFlags.None, stream.Id, chunk.Span);
            },
            cancellationToken
        );

    /// <summary>
    /// Takes as much window as is available, waiting when there is none.
    /// <para>
    /// The connection window is taken with a compare-and-swap, because every stream's writer spends
    /// it at once: two that each read the same balance and then subtract would together send more
    /// than the peer allowed, and a peer is entitled to close the connection over that.
    /// </para>
    /// </summary>
    async ValueTask<int> ReserveWindowAsync(Http2Stream stream, int wanted, CancellationToken cancellationToken)
    {
        // The wait for credit runs under ResponseWriteTimeout, as a write does over HTTP/1.1. A
        // client that vanished - a laptop closing, a network handing over - sends no reset and no
        // more WINDOW_UPDATEs, and with no bytes in flight nothing at the TCP level notices either;
        // without this the stream, its handler and whatever the handler holds wait for ever.
        var limit = this.options.Limits.ResponseWriteTimeout;
        var deadline = limit == Timeout.InfiniteTimeSpan || limit <= TimeSpan.Zero
            ? long.MaxValue
            : Environment.TickCount64 + (long)limit.TotalMilliseconds;

        while (true)
        {
            if (stream.State == Http2StreamState.Closed || this.closed || cancellationToken.IsCancellationRequested)
                return 0;

            // Taken before the windows are read, so credit that lands in between still wakes us.
            var streamSignal = stream.WindowAvailable.Task;
            var connectionSignal = this.connectionWindowAvailable.Task;

            var connectionWindow = Interlocked.Read(ref this.connectionSendWindow);
            var available = (int)Math.Min(
                Math.Min(stream.SendWindow, connectionWindow),
                Math.Min(wanted, this.remoteSettings.MaxFrameSize)
            );

            if (available <= 0)
            {
                // Either window being refilled, the stream ending or the connection closing wakes
                // this; the loop then works out which.
                var left = deadline - Environment.TickCount64;
                if (left <= 0)
                    throw this.WriteStalled(stream, limit);

                var woken = Task.WhenAny(streamSignal, connectionSignal);
                var wait = deadline == long.MaxValue
                    ? woken.WaitAsync(cancellationToken)
                    : woken.WaitAsync(TimeSpan.FromMilliseconds(Math.Min(left, int.MaxValue)), cancellationToken);

                try
                {
                    await wait.ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    throw this.WriteStalled(stream, limit);
                }
            }
            else if (Interlocked.CompareExchange(ref this.connectionSendWindow, connectionWindow - available, connectionWindow) == connectionWindow)
            {
                // Only this stream's writer spends its window, so it needs no swap of its own.
                stream.AdjustSendWindow(-available);
                return available;
            }
        }
    }

    /// <summary>
    /// The client has given a stream no credit for <see cref="HttpServerLimits.ResponseWriteTimeout"/>.
    /// The stream is reset, so the handler sees RequestAborted, and the write that was waiting fails
    /// as a client disconnect - the same outcome as a stalled write over HTTP/1.1. The reset is not
    /// waited on: the peer is likely gone, and the handler should not wait on it again.
    /// </summary>
    IOException WriteStalled(Http2Stream stream, TimeSpan limit)
    {
        var message = $"The client did not accept response data within {limit.TotalSeconds:0.#}s.";
        this.logger.LogDebug("Resetting HTTP/2 stream {StreamId}: {Reason}", stream.Id, message);

        stream.Abort(new IOException(message));
        _ = this.SendRstStreamAsync(stream.Id, Http2ErrorCode.Cancel, CancellationToken.None)
            .AsTask()
            .ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);

        return new IOException(message);
    }

    internal ValueTask SendRstStreamAsync(uint streamId, Http2ErrorCode code, CancellationToken cancellationToken)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(payload, (uint)code);

        return this.writer.WriteFrameAsync(Http2FrameType.RstStream, Http2FrameFlags.None, streamId, payload, cancellationToken);
    }

    /// <summary>
    /// Sends GOAWAY. A graceful one (NO_ERROR) goes at most once; an error may still follow it,
    /// which the protocol allows and which tells the client the drain went wrong.
    /// </summary>
    async ValueTask TryGoAwayAsync(Http2ErrorCode code, string message)
    {
        // 0 = none sent, 1 = graceful sent, 2 = error sent.
        var alreadySent = code == Http2ErrorCode.NoError
            ? Interlocked.CompareExchange(ref this.goAwayState, 1, 0) != 0
            : Interlocked.Exchange(ref this.goAwayState, 2) == 2;

        if (alreadySent)
            return;

        // An error ends the connection while the client may still be sending; see LingerOnClose.
        if (code != Http2ErrorCode.NoError)
            this.LingerOnClose = true;

        try
        {
            var reason = System.Text.Encoding.UTF8.GetBytes(message);
            var payload = new byte[8 + reason.Length];

            uint last;
            lock (this.streamIdGate)
                last = this.lastStreamId;

            BinaryPrimitives.WriteUInt32BigEndian(payload, last);
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
        this.closed = true;

        HttpContext.CancelQuietly(this.shutdown);
        HttpContext.CancelQuietly(this.streamsAborted);

        var handlers = new List<Task>();

        foreach (var stream in this.streams.Values)
        {
            stream.Abort(new IOException("The connection closed."));

            if (stream.Handler is { } handler)
                handlers.Add(handler);
        }

        // A writer parked on the connection window - an error response written with no token, say -
        // would otherwise wait for credit from a peer that is gone.
        this.connectionWindowAvailable.Set();

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

        lock (this.timeoutGate)
            this.timeout.Dispose();

        this.shutdown.Dispose();
        this.streamsAborted.Dispose();
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
