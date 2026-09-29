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
    readonly ConcurrentDictionary<uint, IConnection> streams = new();
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
    public void MarkAnnounced() => Volatile.Write(ref this.announced, 1);

    /// <summary>True exactly once, for an announced tunnel: whoever gets it reports the close.</summary>
    public bool TryMarkEnded()
        => Volatile.Read(ref this.announced) == 1 && Interlocked.Exchange(ref this.ended, 1) == 0;

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
        this.streams[streamId] = publicConnection;

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

    public async ValueTask CloseStreamAsync(uint streamId)
    {
        this.streams.TryRemove(streamId, out _);

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
                if (this.streams.TryGetValue(streamId, out var connection))
                {
                    foreach (var segment in payload)
                        connection.Output.Write(segment.Span);

                    await connection.Output.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                return;

            case TunnelFrameType.CloseStream:
                if (this.streams.TryRemove(streamId, out var closing))
                    closing.Abort();
                return;

            case TunnelFrameType.Ping:
                await channel.SendAsync(TunnelFrameType.Pong, 0, cancellationToken).ConfigureAwait(false);
                return;

            default:
                return;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) != 0)
            return;

        foreach (var streamId in this.streams.Keys)
        {
            if (this.streams.TryRemove(streamId, out var connection))
                connection.Abort();
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
