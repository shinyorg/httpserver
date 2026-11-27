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
    readonly ConcurrentDictionary<uint, AgentStream> streams = new();
    readonly CancellationTokenSource stopping = new();

    // What this end declares, or null with flow control off; and what the relay declared on the current connection.
    readonly TunnelSettings? local;
    TunnelSettings? relaySettings;
    int flowStreams;

    // Windows grow while they are the bottleneck, judged against the round trip to the relay, within a shared budget.
    // Kept across reconnects: the budget is returned by streams as they end, whichever connection they were on.
    readonly TunnelRoundTrip roundTrip = new();
    readonly StreamWindowBudget budget;

    /// <summary>
    /// One exchange arriving over the tunnel. A flow-controlled one carries its own receiver, which feeds the request
    /// body to the app and grants the relay more as the app reads it, and a send window the response is held to.
    /// </summary>
    sealed class AgentStream(DuplexPipeConnection connection)
    {
        public DuplexPipeConnection Connection { get; } = connection;
        public StreamSendWindow? Send { get; init; }
        public StreamReceiver? Receive { get; set; }

        int released;

        public bool TryRelease() => Interlocked.Exchange(ref this.released, 1) == 0;
    }

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
        this.budget = new StreamWindowBudget(options.TunnelWindowBudget);

        if (options.EnableFlowControl)
        {
            this.local = new TunnelSettings(TunnelSettings.CurrentVersion, options.StreamWindowSize, options.MaxStreams);

            try
            {
                this.local.Validate();
            }
            catch (TunnelProtocolException ex)
            {
                throw new ArgumentOutOfRangeException(nameof(options), ex.Message);
            }

            if (options.MaxStreamWindowSize > TunnelSettings.MaximumWindow)
                throw new ArgumentOutOfRangeException(nameof(options), $"MaxStreamWindowSize must be at most {TunnelSettings.MaximumWindow} bytes.");

            if (options.TunnelWindowBudget < 0)
                throw new ArgumentOutOfRangeException(nameof(options), "TunnelWindowBudget must not be negative.");
        }
    }

    /// <summary>True once the relay and this end have both declared flow control on the current connection.</summary>
    internal bool FlowControlNegotiated => this.local is not null && Volatile.Read(ref this.relaySettings) is not null;

    /// <summary>Flow-controlled streams in flight, for diagnostics and tests.</summary>
    internal int FlowStreams => Volatile.Read(ref this.flowStreams);

    /// <summary>The measured round trip to the relay, for diagnostics and tests.</summary>
    internal TimeSpan? RoundTrip => this.roundTrip.Estimate;

    /// <summary>Room left for windows to grow, for diagnostics and tests.</summary>
    internal long WindowBudgetRemaining => this.budget.Remaining;

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
        Task? loop = null;

        try
        {
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

            // Each connection negotiates afresh: a relay restarted with flow control off must not be held to it.
            Volatile.Write(ref this.relaySettings, null);

            var tunnel = new TunnelChannel(
                PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true)),
                PipeWriter.Create(stream, new StreamPipeWriterOptions(leaveOpen: true))
            );
            this.channel = tunnel;

            var ack = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            this.handshake = ack;

            // The read loop starts before the handshake is sent, because the acknowledgement comes back
            // as an ordinary frame — one loop handles registration and traffic alike.
                loop = Task.Run(() => tunnel.RunAsync(this.HandleFrameAsync, cancellationToken), CancellationToken.None);

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
        catch
        {
            // A dial that fails part way - refused, TLS, a rejected or late handshake - must not leave its socket
            // open: the relay would keep the subdomain registered to it (a HelloAck that arrives after the handshake
            // timeout) and the next bind would be refused as already taken. Closing it is what tells the relay.
            this.channel = null;
            this.CloseTransport();
            connectSocket.Dispose();

            if (loop is not null)
            {
                _ = loop.ContinueWith(
                    static t => _ = t.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default
                );
            }

            throw;
        }
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

                // Timed as well: a keep-alive's Pong is a free round-trip sample.
                this.roundTrip.OnPingSent();
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

            case TunnelFrameType.Settings:
                await this.AcceptSettingsAsync(streamId, payload, cancellationToken).ConfigureAwait(false);
                return;

            case TunnelFrameType.Open:
                this.OpenStream(streamId, Decode(payload), flow: false);
                return;

            case TunnelFrameType.OpenWithFlowControl:
                this.OpenStream(streamId, Decode(payload), flow: true);
                return;

            case TunnelFrameType.Data:
                await this.ReceiveAsync(streamId, payload, cancellationToken).ConfigureAwait(false);
                return;

            case TunnelFrameType.WindowUpdate:
                if (this.streams.TryGetValue(streamId, out var credited)
                    && credited.Send is { } window
                    && TunnelSettings.DecodeWindowUpdate(payload) is { } increment
                    && !window.Add(increment))
                {
                    this.Reset(streamId, credited);
                }
                return;

            case TunnelFrameType.CloseStream:
                this.CloseStream(streamId);
                return;

            case TunnelFrameType.Ping:
                if (this.channel is { } channel)
                    await channel.SendAsync(TunnelFrameType.Pong, 0, cancellationToken).ConfigureAwait(false);
                return;

            case TunnelFrameType.Pong:
                this.roundTrip.OnPong();
                return;

            default:
                return;
        }
    }

    /// <summary>
    /// The relay supports flow control: noted, and answered with this end's own Settings, after which the relay opens
    /// streams with <see cref="TunnelFrameType.OpenWithFlowControl"/>. Once per connection; with flow control off here
    /// the frame is ignored, as a client that predates it would.
    /// </summary>
    async ValueTask AcceptSettingsAsync(uint streamId, ReadOnlySequence<byte> payload, CancellationToken cancellationToken)
    {
        if (streamId != 0 || this.local is not { } settings || Volatile.Read(ref this.relaySettings) is not null)
            return;

        Volatile.Write(ref this.relaySettings, TunnelSettings.Decode(payload));

        if (this.channel is { } channel)
            await channel.SendAsync(TunnelFrameType.Settings, 0, settings.Encode(), cancellationToken).ConfigureAwait(false);

        // A first round-trip sample now, so windows can grow from the first stream on.
        this.SampleRoundTrip();
    }

    /// <summary>Sends a Ping to time, unless one is already out or the estimate is fresh.</summary>
    void SampleRoundTrip()
    {
        if (this.roundTrip.TryBeginSample())
            this.SendQuietly(TunnelFrameType.Ping, 0, ReadOnlyMemory<byte>.Empty);
    }

    void OpenStream(uint streamId, string remoteDescription, bool flow)
    {
        var relay = Volatile.Read(ref this.relaySettings);

        // A flow-controlled stream only follows both Settings frames; one before them, or beyond this end's own stream
        // limit, is a relay not keeping to the protocol, and is refused rather than served with no window to hold it.
        var refused = flow && (this.local is null || relay is null);
        if (!refused && flow && Interlocked.Increment(ref this.flowStreams) > this.local!.MaxStreams)
        {
            Interlocked.Decrement(ref this.flowStreams);
            refused = true;
        }

        if (refused)
        {
            this.SendQuietly(TunnelFrameType.CloseStream, streamId, ReadOnlyMemory<byte>.Empty);
            return;
        }

        var connection = new DuplexPipeConnection(
            $"tunnel-{streamId}",
            ParseEndPoint(remoteDescription),
            isTunneled: true
        );

        var stream = new AgentStream(connection)
        {
            // The relay's window: what it is prepared to buffer of this stream's response for a slow public client.
            Send = flow ? new StreamSendWindow(relay!.InitialWindow) : null
        };

        if (flow)
            stream.Receive = this.CreateReceiver(streamId, connection);

        if (!this.streams.TryAdd(streamId, stream))
        {
            // An id still in use - a stream left over from before a reconnect. Refused, but the relay is told, or its
            // public client waits out the whole keep-alive timeout for a response that is never coming.
            stream.Receive?.Abort();
            this.Retire(stream);
            connection.Abort();
            this.SendQuietly(TunnelFrameType.CloseStream, streamId, ReadOnlyMemory<byte>.Empty);
            return;
        }

        _ = Task.Run(() => this.PumpOutboundAsync(streamId, stream), CancellationToken.None);
        this.accepted.Writer.TryWrite(connection);
    }

    /// <summary>
    /// The request half of a flow-controlled stream: fed to the app by the stream's own pump. The flush into the app's
    /// pipe only completes once the app has read far enough, and only then is the relay granted more - so a handler
    /// that never reads its upload holds up that upload alone.
    /// </summary>
    StreamReceiver CreateReceiver(uint streamId, DuplexPipeConnection connection) => new(
        this.local!.InitialWindow,
        async (chunk, cancellationToken) =>
        {
            try
            {
                connection.TransportWriter.Write(chunk.Span);
                var result = await connection.TransportWriter.FlushAsync(cancellationToken).ConfigureAwait(false);

                // The app has finished with the request; the rest of it is discarded, and still granted back.
                return !result.IsCompleted;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
                return false;
            }
        },
        increment => this.SendQuietlyAsync(TunnelFrameType.WindowUpdate, streamId, TunnelSettings.EncodeWindowUpdate(increment)),
        this.options.MaxStreamWindowSize,
        this.roundTrip,
        this.budget,
        this.SampleRoundTrip
    );

    async ValueTask ReceiveAsync(uint streamId, ReadOnlySequence<byte> payload, CancellationToken cancellationToken)
    {
        if (!this.streams.TryGetValue(streamId, out var stream))
            return;

        if (stream.Receive is { } receiver)
        {
            // Only queued: the stream's own pump hands it to the app, so the loop goes straight back to the others.
            if (!receiver.TryEnqueue(payload))
                this.Reset(streamId, stream);

            return;
        }

        await WriteToStreamAsync(streamId, stream.Connection, payload, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The relay broke the stream's flow control. The stream is reset; the tunnel and its other streams carry on.</summary>
    void Reset(uint streamId, AgentStream stream)
    {
        stream.Receive?.Abort();
        stream.Send?.Close();

        if (this.streams.TryRemove(KeyValuePair.Create(streamId, stream)))
            this.Retire(stream);

        stream.Connection.Abort();
        this.SendQuietly(TunnelFrameType.CloseStream, streamId, ReadOnlyMemory<byte>.Empty);
    }

    void Retire(AgentStream stream)
    {
        if (stream.Receive is not null && stream.TryRelease())
            Interlocked.Decrement(ref this.flowStreams);
    }

    void SendQuietly(TunnelFrameType type, uint streamId, ReadOnlyMemory<byte> payload)
        => _ = this.SendQuietlyAsync(type, streamId, payload).AsTask();

    async ValueTask SendQuietlyAsync(TunnelFrameType type, uint streamId, ReadOnlyMemory<byte> payload)
    {
        if (this.channel is not { } channel)
            return;

        try
        {
            await channel.SendAsync(type, streamId, payload, this.stopping.Token).ConfigureAwait(false);
        }
        catch
        {
            // The tunnel is gone; the supervisor is already handling it.
        }
    }

    /// <summary>A stream without flow control: written into the app's pipe by the frame loop itself, as before.</summary>
    async ValueTask WriteToStreamAsync(uint streamId, DuplexPipeConnection connection, ReadOnlySequence<byte> payload, CancellationToken cancellationToken)
    {
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
        if (!this.streams.TryRemove(streamId, out var stream))
            return;

        // The public client is gone or done: nothing more of the response is wanted. The pump keeps reading the app's
        // output, discarding it, so a handler mid-write is not left waiting on a window that will never open.
        stream.Send?.Close();

        if (stream.Receive is not { } receiver)
        {
            stream.Connection.TransportWriter.Complete();
            return;
        }

        // The relay has given this stream's place back; so does this end, to keep the two counts in step. Whatever of
        // the request is still queued reaches the app first - a client that sent its request and half-closed is still
        // owed an answer to all of it - then the app sees its end.
        this.Retire(stream);
        receiver.Complete();
        _ = receiver.Completion.ContinueWith(
            static (_, state) => ((DuplexPipeConnection)state!).TransportWriter.Complete(),
            stream.Connection,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
    }

    /// <summary>Forwards everything the server writes for one exchange back to the relay as frames.</summary>
    async Task PumpOutboundAsync(uint streamId, AgentStream stream)
    {
        var connection = stream.Connection;
        var reader = connection.TransportReader;
        var tunnel = this.channel;
        var window = stream.Send;
        var discarding = false;

        try
        {
            while (tunnel is not null)
            {
                // Read with the provider's token, not the connection's: disposing the connection cancels that one, and
                // a read cancelled after the app's last bytes arrived but before they were collected dropped the end
                // of the response. A disposed or aborted connection completes the pipe, which ends the loop anyway.
                var result = await reader.ReadAsync(this.stopping.Token).ConfigureAwait(false);

                // Copied out and handed back before any await: the frame waits its turn on a write gate every stream
                // shares, and pipe memory held across that wait can be returned to the pool by an abort on another
                // thread and rented by another stream - whose bytes would then go out in this stream's frame.
                var length = (int)result.Buffer.Length;
                var pending = System.Buffers.ArrayPool<byte>.Shared.Rent(Math.Max(length, 1));
                result.Buffer.CopyTo(pending);
                reader.AdvanceTo(result.Buffer.End);

                try
                {
                    var offset = 0;
                    while (offset < length && !discarding)
                    {
                        var take = Math.Min(length - offset, TunnelProtocol.MaxPayloadLength);

                        // Held to the relay's window: a public client that is not keeping up stops this response, not
                        // the tunnel. A closed window means the relay is done with the stream; the rest is discarded.
                        if (window is not null)
                            take = await window.ReserveAsync(take, this.stopping.Token).ConfigureAwait(false);

                        discarding = take == 0;
                        if (!discarding)
                        {
                            await tunnel.SendAsync(TunnelFrameType.Data, streamId, pending.AsMemory(offset, take), this.stopping.Token)
                                .ConfigureAwait(false);

                            offset += take;
                        }
                    }
                }
                finally
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(pending);
                }

                if (result.IsCompleted || result.IsCanceled)
                    break;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or ObjectDisposedException or IOException)
        {
        }
        finally
        {
            // This stream's entry only: after a reconnect the id can belong to a new stream already.
            this.streams.TryRemove(KeyValuePair.Create(streamId, stream));
            stream.Send?.Close();

            // The app has closed its side, so nothing more of the request will be read. Ending the receiver is what
            // returns the room its window grew into; left waiting, a stream whose app closed first kept it for good.
            stream.Receive?.Abort();
            this.Retire(stream);

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
            if (this.streams.TryRemove(streamId, out var stream))
            {
                stream.Receive?.Abort();
                stream.Send?.Close();
                stream.Connection.Abort();
                this.Retire(stream);
            }
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
