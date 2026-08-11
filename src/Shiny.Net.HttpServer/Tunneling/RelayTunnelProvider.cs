using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shiny.Net.HttpServer.Transports;

namespace Shiny.Net.HttpServer.Tunneling;

/// <summary>
/// Dials out to a relay and turns everything arriving on that one outbound connection back into
/// ordinary <see cref="IConnection"/>s for the server.
/// <para>
/// Outbound-only is the entire trick. A server embedded in a phone app cannot accept an inbound
/// connection — carrier NAT sees to that — but it can always open one. Requests arriving at the
/// relay's public address are multiplexed down that connection as frames, unpacked here into
/// in-memory pipes, and handed to the same HTTP core that serves local sockets.
/// </para>
/// </summary>
public sealed class RelayTunnelProvider : ITunnelProvider
{
    readonly RelayTunnelOptions options;
    readonly ILogger logger;
    readonly Channel<IConnection> accepted = Channel.CreateUnbounded<IConnection>(
        new UnboundedChannelOptions { SingleReader = true }
    );
    readonly ConcurrentDictionary<uint, DuplexPipeConnection> streams = new();
    readonly CancellationTokenSource stopping = new();

    TaskCompletionSource<string>? handshake;
    TunnelChannel? channel;
    Stream? transport;
    Socket? socket;
    Task? supervisor;
    int disposed;

    public RelayTunnelProvider(RelayTunnelOptions options, ILogger<RelayTunnelProvider>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        this.options = options;
        this.logger = logger ?? NullLogger<RelayTunnelProvider>.Instance;
    }

    public string Name => "shiny-relay";

    public string? PublicUrl { get; private set; }

    public string ListenDescription => this.PublicUrl ?? $"{this.options.Host}:{this.options.Port} (not yet registered)";

    public async ValueTask BindAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed != 0, this);

        if (this.supervisor is not null)
            throw new InvalidOperationException("The tunnel is already open.");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(this.stopping.Token, cancellationToken);
        var loop = await this.DialAsync(linked.Token).ConfigureAwait(false);

        this.supervisor = Task.Run(() => this.SuperviseAsync(loop), CancellationToken.None);
    }

    public async ValueTask<IConnection?> AcceptAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await this.accepted.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            // The tunnel closed for good; this is how the accept loop learns to stop.
            return null;
        }
    }

    public async ValueTask UnbindAsync(CancellationToken cancellationToken = default)
    {
        if (this.supervisor is null)
            return;

        await this.stopping.CancelAsync().ConfigureAwait(false);
        this.CloseTransport();

        try
        {
            await this.supervisor.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
        }

        this.supervisor = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) != 0)
            return;

        await this.UnbindAsync(CancellationToken.None).ConfigureAwait(false);
        this.stopping.Dispose();
    }

    // ---- Dial and supervise ----

    async Task<Task> DialAsync(CancellationToken cancellationToken)
    {
        var connectSocket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        await connectSocket.ConnectAsync(this.options.Host, this.options.Port, cancellationToken).ConfigureAwait(false);

        Stream stream = new NetworkStream(connectSocket, ownsSocket: false);

        if (this.options.UseTls)
        {
            var ssl = new SslStream(
                stream,
                leaveInnerStreamOpen: false,
                this.options.ServerCertificateValidation
            );

            await ssl.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = this.options.Host,
                    EnabledSslProtocols = this.options.SslProtocols
                },
                cancellationToken
            ).ConfigureAwait(false);

            stream = ssl;
        }

        this.socket = connectSocket;
        this.transport = stream;

        var tunnel = new TunnelChannel(
            PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true)),
            PipeWriter.Create(stream, new StreamPipeWriterOptions(leaveOpen: true))
        );
        this.channel = tunnel;

        var ack = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        this.handshake = ack;

        // The read loop starts before the handshake is sent, because the acknowledgement comes back
        // as an ordinary frame — one loop handles registration and traffic alike.
        var loop = Task.Run(() => tunnel.RunAsync(this.HandleFrameAsync, cancellationToken), CancellationToken.None);

        await tunnel
            .SendAsync(TunnelFrameType.Hello, 0, $"{this.options.Token}\n{this.options.Subdomain}", cancellationToken)
            .ConfigureAwait(false);

        this.PublicUrl = await ack.Task
            .WaitAsync(this.options.HandshakeTimeout, cancellationToken)
            .ConfigureAwait(false);

        this.logger.LogInformation("Tunnel registered at {Url}", this.PublicUrl);

        _ = Task.Run(() => this.KeepAliveAsync(tunnel, cancellationToken), CancellationToken.None);

        return loop;
    }

    async Task SuperviseAsync(Task loop)
    {
        var token = this.stopping.Token;

        while (true)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Every way a tunnel ends looks like a fault from inside the read loop: the socket
                // is torn down under it. Whether that was a shutdown or a genuine drop is decided
                // here, and neither is something to propagate to the caller.
                if (!token.IsCancellationRequested)
                    this.logger.LogWarning(ex, "Tunnel connection dropped");
            }

            this.AbortAllStreams();
            this.CloseTransport();

            if (token.IsCancellationRequested || this.options.ReconnectDelay is not { } delay)
                break;

            this.logger.LogInformation("Reconnecting to the relay in {Delay}", delay);

            try
            {
                await Task.Delay(delay, token).ConfigureAwait(false);
                loop = await this.DialAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(ex, "Reconnect attempt failed");
                loop = Task.CompletedTask;
            }
        }

        this.accepted.Writer.TryComplete();
    }

    async Task KeepAliveAsync(TunnelChannel tunnel, CancellationToken cancellationToken)
    {
        if (this.options.KeepAliveInterval <= TimeSpan.Zero)
            return;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(this.options.KeepAliveInterval, cancellationToken).ConfigureAwait(false);
                await tunnel.SendAsync(TunnelFrameType.Ping, 0, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException)
        {
            // The tunnel went away; the supervisor is already handling it.
        }
    }

    // ---- Frame handling ----

    async ValueTask HandleFrameAsync(
        TunnelFrameType type,
        uint streamId,
        ReadOnlySequence<byte> payload,
        CancellationToken cancellationToken
    )
    {
        switch (type)
        {
            case TunnelFrameType.HelloAck:
                this.handshake?.TrySetResult(Decode(payload));
                return;

            case TunnelFrameType.HelloReject:
                this.handshake?.TrySetException(
                    new InvalidOperationException($"The relay refused the tunnel: {Decode(payload)}")
                );
                return;

            case TunnelFrameType.Open:
                this.OpenStream(streamId, Decode(payload));
                return;

            case TunnelFrameType.Data:
                await this.WriteToStreamAsync(streamId, payload, cancellationToken).ConfigureAwait(false);
                return;

            case TunnelFrameType.CloseStream:
                this.CloseStream(streamId);
                return;

            case TunnelFrameType.Ping:
                if (this.channel is { } channel)
                    await channel.SendAsync(TunnelFrameType.Pong, 0, cancellationToken).ConfigureAwait(false);
                return;

            default:
                return;
        }
    }

    void OpenStream(uint streamId, string remoteDescription)
    {
        var connection = new DuplexPipeConnection(
            $"tunnel-{streamId}",
            ParseEndPoint(remoteDescription),
            isTunneled: true
        );

        if (!this.streams.TryAdd(streamId, connection))
        {
            connection.Abort();
            return;
        }

        _ = Task.Run(() => this.PumpOutboundAsync(streamId, connection), CancellationToken.None);
        this.accepted.Writer.TryWrite(connection);
    }

    async ValueTask WriteToStreamAsync(uint streamId, ReadOnlySequence<byte> payload, CancellationToken cancellationToken)
    {
        if (!this.streams.TryGetValue(streamId, out var connection))
            return;

        try
        {
            foreach (var segment in payload)
                connection.TransportWriter.Write(segment.Span);

            await connection.TransportWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // The application side finished with this exchange; nothing left to deliver into.
            this.CloseStream(streamId);
        }
    }

    void CloseStream(uint streamId)
    {
        if (this.streams.TryRemove(streamId, out var connection))
            connection.TransportWriter.Complete();
    }

    /// <summary>Forwards everything the server writes for one exchange back to the relay as frames.</summary>
    async Task PumpOutboundAsync(uint streamId, DuplexPipeConnection connection)
    {
        var reader = connection.TransportReader;
        var tunnel = this.channel;

        try
        {
            while (tunnel is not null)
            {
                var result = await reader.ReadAsync(connection.Aborted).ConfigureAwait(false);
                var buffer = result.Buffer;

                while (!buffer.IsEmpty)
                {
                    var chunk = buffer.Slice(0, Math.Min(buffer.Length, TunnelProtocol.MaxPayloadLength));
                    await tunnel.SendAsync(TunnelFrameType.Data, streamId, chunk, this.stopping.Token)
                        .ConfigureAwait(false);

                    buffer = buffer.Slice(chunk.End);
                }

                reader.AdvanceTo(result.Buffer.End);

                if (result.IsCompleted || result.IsCanceled)
                    break;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or ObjectDisposedException or IOException)
        {
        }
        finally
        {
            this.streams.TryRemove(streamId, out _);

            if (tunnel is not null)
            {
                try
                {
                    await tunnel.SendAsync(TunnelFrameType.CloseStream, streamId, this.stopping.Token)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // The tunnel is gone; the relay will time the stream out on its side.
                }
            }
        }
    }

    void AbortAllStreams()
    {
        foreach (var streamId in this.streams.Keys)
        {
            if (this.streams.TryRemove(streamId, out var connection))
                connection.Abort();
        }
    }

    void CloseTransport()
    {
        try
        {
            this.transport?.Dispose();
        }
        catch
        {
        }

        try
        {
            this.socket?.Dispose();
        }
        catch
        {
        }

        this.transport = null;
        this.socket = null;
    }

    static string Decode(ReadOnlySequence<byte> payload)
        => payload.IsEmpty ? string.Empty : System.Text.Encoding.UTF8.GetString(payload.ToArray());

    /// <summary>
    /// The relay describes the original client as <c>ip:port</c>. Unparseable values are simply
    /// dropped — a missing remote address is a nuisance, a wrong one is a security problem.
    /// </summary>
    static EndPoint? ParseEndPoint(string description)
        => IPEndPoint.TryParse(description, out var endPoint) ? endPoint : null;
}
