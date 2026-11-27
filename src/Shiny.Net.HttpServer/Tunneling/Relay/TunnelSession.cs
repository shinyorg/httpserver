using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using Shiny.Net.HttpServer.Transports;

namespace Shiny.Net.HttpServer.Tunneling;

/// <summary>
/// One registered tunnel: the client's outbound control connection, plus every public exchange
/// currently multiplexed over it.
/// <para>
/// A stream is flow-controlled when both ends support it - the client says so with a Settings frame after
/// registering. Its response then reaches the public client through a <see cref="StreamReceiver"/> of its own, and its
/// request body goes out against the client's window, so one stream that stalls waits alone. A stream to a client that
/// sent no Settings is forwarded as before: the frame loop writes it to the public client itself.
/// </para>
/// </summary>
sealed class TunnelSession(
    RelayTunnel tunnel,
    IConnection control,
    TunnelChannel channel,
    RelayServerOptions options
) : IAsyncDisposable
{
    readonly ConcurrentDictionary<uint, PublicStream> streams = new();

    // What this relay declared, or null with flow control turned off; and what the client declared, once it has.
    readonly TunnelSettings? local = options.EnableFlowControl
        ? new TunnelSettings(TunnelSettings.CurrentVersion, options.StreamWindowSize, options.MaxStreamsPerTunnel)
        : null;
    TunnelSettings? peer;
    int flowStreams;

    // Windows grow while they are the bottleneck, judged against the tunnel's round trip, within a shared budget.
    readonly TunnelRoundTrip roundTrip = new();
    readonly StreamWindowBudget budget = new(options.TunnelWindowBudget);

    /// <summary>
    /// One public connection multiplexed over the tunnel. Its output is written from two places - the app's response,
    /// and the relay's request loop answering a 421 or closing - and a <see cref="System.IO.Pipelines.PipeWriter"/> is
    /// not safe to use from two threads: a flush that starts while another is still finishing re-sends the segment the
    /// first one already sent, so a client could read the previous response a second time as the answer to its next
    /// request. Every write goes through the gate.
    /// </summary>
    sealed class PublicStream(IConnection connection)
    {
        public IConnection Connection { get; } = connection;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public bool Live { get; set; } = true;

        /// <summary>Credit for request body bytes to the client; null on a stream without flow control.</summary>
        public StreamSendWindow? Send { get; init; }

        /// <summary>Delivers the app's response to the public client; null on a stream without flow control.</summary>
        public StreamReceiver? Receive { get; set; }

        int released;

        /// <summary>True exactly once: whoever gets it gives back the stream's place under the stream limit.</summary>
        public bool TryRelease() => Interlocked.Exchange(ref this.released, 1) == 0;
    }

    uint nextStreamId;
    int disposed;
    int closeReason = -1;
    int announced;
    int ended;

    /// <summary>The host this tunnel answers for, lowercased and without a port.</summary>
    public string Host => this.Tunnel.Host;

    public RelayTunnel Tunnel { get; } = tunnel;

    /// <summary>What this relay declares in its Settings frame, or null when flow control is off.</summary>
    public TunnelSettings? LocalSettings => this.local;

    /// <summary>True once both ends have declared flow control: streams opened from now on use it.</summary>
    public bool FlowControlNegotiated => this.local is not null && Volatile.Read(ref this.peer) is not null;

    /// <summary>Flow-controlled streams in flight, for diagnostics and tests.</summary>
    public int FlowStreams => Volatile.Read(ref this.flowStreams);

    /// <summary>The tunnel's measured round trip, for diagnostics and tests.</summary>
    public TimeSpan? RoundTrip => this.roundTrip.Estimate;

    /// <summary>Room left for windows to grow, for diagnostics and tests.</summary>
    public long WindowBudgetRemaining => this.budget.Remaining;

    /// <summary>
    /// Why the tunnel is ending. The first caller wins, so a disconnect is not relabelled by the
    /// connection teardown it causes.
    /// </summary>
    public RelayTunnelCloseReason CloseReason => this.closeReason < 0
        ? RelayTunnelCloseReason.ConnectionClosed
        : (RelayTunnelCloseReason)this.closeReason;

    public void SetCloseReason(RelayTunnelCloseReason reason)
        => Interlocked.CompareExchange(ref this.closeReason, (int)reason, -1);

    /// <summary>Marks the tunnel as reported connected. Only an announced tunnel is reported closed.</summary>
    /// <remarks>
    /// A full fence, paired with the one in <see cref="TryBeginClose"/>: an announcer that then checks
    /// <see cref="IsClosing"/> and a closer that then checks the announcement cannot both miss.
    /// </remarks>
    public void MarkAnnounced() => Interlocked.Exchange(ref this.announced, 1);

    /// <summary>True exactly once, for an announced tunnel: whoever gets it reports the close.</summary>
    public bool TryMarkEnded()
        => Volatile.Read(ref this.announced) == 1 && Interlocked.Exchange(ref this.ended, 1) == 0;

    readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    int closing;

    /// <summary>True exactly once: whoever gets it tears the session down and then calls <see cref="MarkClosed"/>.</summary>
    public bool TryBeginClose() => Interlocked.Exchange(ref this.closing, 1) == 0;

    /// <summary>True once a close has begun.</summary>
    public bool IsClosing => Volatile.Read(ref this.closing) == 1;

    /// <summary>Completes once the session is torn down and its close has been reported.</summary>
    public Task Closed => this.closed.Task;

    public void MarkClosed() => this.closed.TrySetResult();

    public EndPoint? RemoteEndPoint => control.RemoteEndPoint;

    /// <summary>Pumps the control connection until the client disconnects.</summary>
    public Task RunAsync(CancellationToken cancellationToken)
        => channel.RunAsync(this.HandleFrameAsync, cancellationToken);

    /// <summary>
    /// Opens a multiplexed stream for one public connection and tells the client about it. The caller then feeds
    /// request bytes with <see cref="SendAsync(uint, ReadOnlyMemory{byte}, CancellationToken)"/> and closes with
    /// <see cref="CloseStreamAsync"/>. Returns 0 - nothing opened - when the tunnel is already carrying as many
    /// flow-controlled streams as either end allows.
    /// </summary>
    public async ValueTask<uint> OpenStreamAsync(IConnection publicConnection, CancellationToken cancellationToken)
    {
        var peerSettings = Volatile.Read(ref this.peer);
        var flow = this.local is not null && peerSettings is not null;

        if (flow && !this.TryTakeSlot(Math.Min(this.local!.MaxStreams, peerSettings!.MaxStreams)))
            return 0;

        var streamId = Interlocked.Increment(ref this.nextStreamId);
        var stream = new PublicStream(publicConnection)
        {
            // The client's window: what it is prepared to buffer of this stream's request body.
            Send = flow ? new StreamSendWindow(peerSettings!.InitialWindow) : null
        };

        if (flow)
            stream.Receive = this.CreateReceiver(streamId, stream);

        this.streams[streamId] = stream;

        try
        {
            await channel.SendAsync(
                flow ? TunnelFrameType.OpenWithFlowControl : TunnelFrameType.Open,
                streamId,
                publicConnection.RemoteEndPoint?.ToString() ?? string.Empty,
                cancellationToken
            ).ConfigureAwait(false);
        }
        catch
        {
            // Never announced, so nothing else will close it.
            if (this.streams.TryRemove(KeyValuePair.Create(streamId, stream)))
                this.Retire(stream);

            throw;
        }

        return streamId;
    }

    /// <summary>
    /// Takes a place under the stream limit. A safety bound rather than a working limit: a stream lasts as long as its
    /// public connection, kept-alive ones included, and the relay cannot tell an idle connection from a response that
    /// is merely quiet (a long poll, an event stream), so it never closes one to make room - the limit is set high.
    /// </summary>
    bool TryTakeSlot(int limit)
    {
        if (Interlocked.Increment(ref this.flowStreams) <= limit)
            return true;

        Interlocked.Decrement(ref this.flowStreams);
        return false;
    }

    /// <summary>
    /// The response half of a flow-controlled stream: delivered to the public client by the stream's own pump, with
    /// credit granted back to the client as the public client takes it.
    /// </summary>
    StreamReceiver CreateReceiver(uint streamId, PublicStream stream) => new(
        this.local!.InitialWindow,
        async (chunk, cancellationToken) =>
        {
            await stream.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Re-checked under the gate: the relay may have detached the stream to answer it itself.
                if (!stream.Live)
                    return false;

                stream.Connection.Output.Write(chunk.Span);

                // Not cancelled by a token - a flush torn part way through a TLS record breaks the connection for the
                // records after it - but bounded all the same: a public client that has stopped reading, or vanished,
                // would otherwise hold this stream's window, and its place under the stream limit, for ever.
                using var timeout = WriteTimeout(stream.Connection);
                var result = await stream.Connection.Output.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                return !result.IsCompleted;
            }
            catch (Exception ex) when (IsExpectedDisconnect(ex))
            {
                this.Fail(streamId, stream);
                return false;
            }
            finally
            {
                stream.Gate.Release();
            }
        },
        increment => this.SendQuietlyAsync(TunnelFrameType.WindowUpdate, streamId, TunnelSettings.EncodeWindowUpdate(increment)),
        options.MaxStreamWindowSize,
        this.roundTrip,
        this.budget,
        this.SampleRoundTrip
    );

    /// <summary>Sends a Ping to time, unless one is already out or the estimate is fresh.</summary>
    void SampleRoundTrip()
    {
        if (this.roundTrip.TryBeginSample())
            _ = this.SendQuietlyAsync(TunnelFrameType.Ping, 0, ReadOnlyMemory<byte>.Empty).AsTask();
    }

    /// <summary>Aborts <paramref name="connection"/> if a write to it does not finish within the configured timeout.</summary>
    IDisposable? WriteTimeout(IConnection connection)
    {
        var limit = options.PublicWriteTimeout;
        if (limit <= TimeSpan.Zero || limit == Timeout.InfiniteTimeSpan)
            return null;

        var timer = new CancellationTokenSource(limit);
        timer.Token.Register(static state => ((IConnection)state!).Abort(), connection);
        return timer;
    }

    /// <summary>Sends request bytes down a stream, split across frames when they do not fit in one.</summary>
    public async ValueTask SendAsync(uint streamId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        var window = this.streams.TryGetValue(streamId, out var stream) ? stream.Send : null;

        while (!payload.IsEmpty)
        {
            var take = Math.Min(payload.Length, TunnelProtocol.MaxPayloadLength);

            if (window is not null)
            {
                // Held to the client's window: a handler that is not reading this body stops this stream, not the tunnel.
                take = await window.ReserveAsync(take, cancellationToken).ConfigureAwait(false);
                if (take == 0)
                    return;
            }

            await channel.SendAsync(TunnelFrameType.Data, streamId, payload[..take], cancellationToken)
                .ConfigureAwait(false);

            payload = payload[take..];
        }
    }

    /// <summary>Sends request bytes straight from a pipe buffer. Must already fit in one frame.</summary>
    public async ValueTask SendAsync(uint streamId, ReadOnlySequence<byte> payload, CancellationToken cancellationToken)
    {
        if (!this.streams.TryGetValue(streamId, out var stream) || stream.Send is not { } window)
        {
            await channel.SendAsync(TunnelFrameType.Data, streamId, payload, cancellationToken).ConfigureAwait(false);
            return;
        }

        while (payload.Length > 0)
        {
            var take = await window.ReserveAsync((int)payload.Length, cancellationToken).ConfigureAwait(false);
            if (take == 0)
                return;

            await channel.SendAsync(TunnelFrameType.Data, streamId, payload.Slice(0, take), cancellationToken)
                .ConfigureAwait(false);

            payload = payload.Slice(take);
        }
    }

    /// <summary>
    /// Stops forwarding the app's response to the public connection and waits out any write already under way, so
    /// the caller then owns the connection's output alone - to answer it itself, or to dispose it.
    /// </summary>
    public async ValueTask DetachStreamAsync(uint streamId)
    {
        if (!this.streams.TryRemove(streamId, out var stream))
            return;

        stream.Receive?.Abort();
        stream.Send?.Close();

        await stream.Gate.WaitAsync().ConfigureAwait(false);
        stream.Live = false;
        stream.Gate.Release();

        this.Retire(stream);
    }

    public async ValueTask CloseStreamAsync(uint streamId)
    {
        await this.DetachStreamAsync(streamId).ConfigureAwait(false);
        await this.SendQuietlyAsync(TunnelFrameType.CloseStream, streamId, ReadOnlyMemory<byte>.Empty).ConfigureAwait(false);
    }

    async ValueTask HandleFrameAsync(
        TunnelFrameType type,
        uint streamId,
        ReadOnlySequence<byte> payload,
        CancellationToken cancellationToken
    )
    {
        switch (type)
        {
            case TunnelFrameType.Data:
                if (this.streams.TryGetValue(streamId, out var stream))
                    await this.ReceiveAsync(streamId, stream, payload, cancellationToken).ConfigureAwait(false);
                return;

            case TunnelFrameType.WindowUpdate:
                if (this.streams.TryGetValue(streamId, out var credited)
                    && credited.Send is { } window
                    && TunnelSettings.DecodeWindowUpdate(payload) is { } increment
                    && !window.Add(increment))
                {
                    await this.ResetAsync(streamId, credited).ConfigureAwait(false);
                }
                return;

            case TunnelFrameType.Settings:
                // Once, on the connection: a second one, or one on a stream, is ignored rather than allowed to change
                // the windows of streams already open.
                if (streamId == 0 && this.local is not null && Volatile.Read(ref this.peer) is null)
                {
                    Volatile.Write(ref this.peer, TunnelSettings.Decode(payload));

                    // A first round-trip sample now, so windows can grow from the first stream on.
                    this.SampleRoundTrip();
                }
                return;

            case TunnelFrameType.Pong:
                this.roundTrip.OnPong();
                return;

            case TunnelFrameType.CloseStream:
                if (this.streams.TryRemove(streamId, out var closing))
                    await this.EndFromClientAsync(closing).ConfigureAwait(false);
                return;

            case TunnelFrameType.Ping:
                await channel.SendAsync(TunnelFrameType.Pong, 0, cancellationToken).ConfigureAwait(false);
                return;

            default:
                return;
        }
    }

    async ValueTask ReceiveAsync(uint streamId, PublicStream stream, ReadOnlySequence<byte> payload, CancellationToken cancellationToken)
    {
        if (stream.Receive is not { } receiver)
        {
            await ForwardAsync(streamId, stream, payload, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Only queued: the stream's own pump delivers it, so the loop goes straight back to the other streams.
        if (!receiver.TryEnqueue(payload))
            await this.ResetAsync(streamId, stream).ConfigureAwait(false);
    }

    /// <summary>A stream without flow control: written to the public client by the frame loop itself, as before.</summary>
    async ValueTask ForwardAsync(uint streamId, PublicStream stream, ReadOnlySequence<byte> payload, CancellationToken cancellationToken)
    {
        await stream.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-checked under the gate: the relay may have detached the stream to answer it itself.
            if (!stream.Live)
                return;

            foreach (var segment in payload)
                stream.Connection.Output.Write(segment.Span);

            await stream.Connection.Output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException or System.Net.Sockets.SocketException)
        {
            // One public client resetting mid-download is that client's problem. Letting it escape would end the
            // control loop and with it every other stream on the tunnel.
            stream.Live = false;
            this.streams.TryRemove(KeyValuePair.Create(streamId, stream));
            stream.Connection.Abort();
        }
        finally
        {
            stream.Gate.Release();
        }
    }

    /// <summary>
    /// The client broke the stream's flow control - sent past its window, or granted more than a window can hold. The
    /// stream is reset; the tunnel and its other streams carry on.
    /// </summary>
    async ValueTask ResetAsync(uint streamId, PublicStream stream)
    {
        this.Fail(streamId, stream);
        await this.SendQuietlyAsync(TunnelFrameType.CloseStream, streamId, ReadOnlyMemory<byte>.Empty).ConfigureAwait(false);
    }

    /// <summary>The public side of a stream is gone or broken: it is torn down, and the request loop sees it end.</summary>
    void Fail(uint streamId, PublicStream stream)
    {
        stream.Live = false;
        stream.Receive?.Abort();
        stream.Send?.Close();

        if (this.streams.TryRemove(KeyValuePair.Create(streamId, stream)))
            this.Retire(stream);

        stream.Connection.Abort();
    }

    /// <summary>
    /// The app closed its side. Rather than a reset - which discards whatever the socket has yet to send, so a slow
    /// client lost the end of its response - the relay's request loop is told to stop, and it closes the connection the
    /// ordinary way once the response has gone. A flow-controlled stream may still have some of it queued: that goes
    /// first, on the stream's own pump, not on the frame loop.
    /// </summary>
    async ValueTask EndFromClientAsync(PublicStream stream)
    {
        stream.Send?.Close();

        if (stream.Receive is not { } receiver)
        {
            await EndGracefullyAsync(stream).ConfigureAwait(false);
            return;
        }

        receiver.Complete();
        _ = receiver.Completion.ContinueWith(
            async (_, state) =>
            {
                var (session, ending) = ((TunnelSession, PublicStream))state!;
                await EndGracefullyAsync(ending).ConfigureAwait(false);
                session.Retire(ending);
            },
            (this, stream),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        ).Unwrap();
    }

    static async Task EndGracefullyAsync(PublicStream stream)
    {
        await stream.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            stream.Live = false;
            stream.Connection.Input.CancelPendingRead();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
        }
        finally
        {
            stream.Gate.Release();
        }
    }

    /// <summary>Gives back a flow-controlled stream's place under the stream limit, once.</summary>
    void Retire(PublicStream stream)
    {
        if (stream.Receive is not null && stream.TryRelease())
            Interlocked.Decrement(ref this.flowStreams);
    }

    async ValueTask SendQuietlyAsync(TunnelFrameType type, uint streamId, ReadOnlyMemory<byte> payload)
    {
        try
        {
            await channel.SendAsync(type, streamId, payload, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // The tunnel is already gone; nothing to tell it.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) != 0)
            return;

        foreach (var streamId in this.streams.Keys)
        {
            if (this.streams.TryRemove(streamId, out var stream))
            {
                stream.Receive?.Abort();
                stream.Send?.Close();
                stream.Connection.Abort();
                this.Retire(stream);
            }
        }

        await channel.DisposeAsync().ConfigureAwait(false);
        await control.DisposeAsync().ConfigureAwait(false);
    }

    internal static bool IsExpectedDisconnect(Exception ex) => ex
        is OperationCanceledException
        or System.Net.Sockets.SocketException
        or ObjectDisposedException
        or IOException
        or InvalidOperationException;
}
