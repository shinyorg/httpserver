using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using Shiny.Net.HttpServer.Transports;

namespace Shiny.Net.HttpServer.Tunneling;

/// <summary>
/// One registered tunnel: the client's outbound control connection, plus every public exchange
/// currently multiplexed over it.
/// </summary>
sealed class TunnelSession(
    RelayTunnel tunnel,
    IConnection control,
    TunnelChannel channel
) : IAsyncDisposable
{
    readonly ConcurrentDictionary<uint, PublicStream> streams = new();

    /// <summary>
    /// One public connection multiplexed over the tunnel. Its output is written from two places - this session's
    /// control loop forwarding the app's response, and the relay's request loop answering a 421 or closing - and a
    /// <see cref="System.IO.Pipelines.PipeWriter"/> is not safe to use from two threads: a flush that starts while
    /// another is still finishing re-sends the segment the first one already sent, so a client could read the
    /// previous response a second time as the answer to its next request. Every write goes through the gate.
    /// </summary>
    sealed class PublicStream(IConnection connection)
    {
        public IConnection Connection { get; } = connection;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public bool Live { get; set; } = true;
    }
    uint nextStreamId;
    int disposed;
    int closeReason = -1;
    int announced;
    int ended;

    /// <summary>The host this tunnel answers for, lowercased and without a port.</summary>
    public string Host => this.Tunnel.Host;

    public RelayTunnel Tunnel { get; } = tunnel;

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
    /// Opens a multiplexed stream for one public connection and tells the client about it. The
    /// caller then feeds request bytes with <see cref="SendAsync(uint, ReadOnlyMemory{byte}, CancellationToken)"/>
    /// and closes with <see cref="CloseStreamAsync"/>.
    /// </summary>
    public async ValueTask<uint> OpenStreamAsync(IConnection publicConnection, CancellationToken cancellationToken)
    {
        var streamId = Interlocked.Increment(ref this.nextStreamId);
        this.streams[streamId] = new PublicStream(publicConnection);

        await channel.SendAsync(
            TunnelFrameType.Open,
            streamId,
            publicConnection.RemoteEndPoint?.ToString() ?? string.Empty,
            cancellationToken
        ).ConfigureAwait(false);

        return streamId;
    }

    /// <summary>Sends request bytes down a stream, split across frames when they do not fit in one.</summary>
    public async ValueTask SendAsync(uint streamId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        while (!payload.IsEmpty)
        {
            var take = Math.Min(payload.Length, TunnelProtocol.MaxPayloadLength);
            await channel.SendAsync(TunnelFrameType.Data, streamId, payload[..take], cancellationToken)
                .ConfigureAwait(false);

            payload = payload[take..];
        }
    }

    /// <summary>Sends request bytes straight from a pipe buffer. Must already fit in one frame.</summary>
    public ValueTask SendAsync(uint streamId, ReadOnlySequence<byte> payload, CancellationToken cancellationToken)
        => channel.SendAsync(TunnelFrameType.Data, streamId, payload, cancellationToken);

    /// <summary>
    /// Stops forwarding the app's response to the public connection and waits out any write already under way, so
    /// the caller then owns the connection's output alone - to answer it itself, or to dispose it.
    /// </summary>
    public async ValueTask DetachStreamAsync(uint streamId)
    {
        if (!this.streams.TryRemove(streamId, out var stream))
            return;

        await stream.Gate.WaitAsync().ConfigureAwait(false);
        stream.Live = false;
        stream.Gate.Release();
    }

    public async ValueTask CloseStreamAsync(uint streamId)
    {
        await this.DetachStreamAsync(streamId).ConfigureAwait(false);

        try
        {
            await channel.SendAsync(TunnelFrameType.CloseStream, streamId, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            // The tunnel is already gone; nothing to tell it.
        }
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
                    await this.ForwardAsync(streamId, stream, payload, cancellationToken).ConfigureAwait(false);
                return;

            case TunnelFrameType.CloseStream:
                if (this.streams.TryRemove(streamId, out var closing))
                    await EndGracefullyAsync(closing).ConfigureAwait(false);
                return;

            case TunnelFrameType.Ping:
                await channel.SendAsync(TunnelFrameType.Pong, 0, cancellationToken).ConfigureAwait(false);
                return;

            default:
                return;
        }
    }

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
    /// The app closed its side. Rather than a reset - which discards whatever the socket has yet to send, so a slow
    /// client lost the end of its response - the relay's request loop is told to stop, and it closes the connection
    /// the ordinary way once the response has gone.
    /// </summary>
    static async ValueTask EndGracefullyAsync(PublicStream stream)
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

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) != 0)
            return;

        foreach (var streamId in this.streams.Keys)
        {
            if (this.streams.TryRemove(streamId, out var stream))
                stream.Connection.Abort();
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
