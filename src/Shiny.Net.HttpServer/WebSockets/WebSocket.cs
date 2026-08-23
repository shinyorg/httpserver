using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Text;
using Shiny.Net.HttpServer.Transports;

namespace Shiny.Net.HttpServer.WebSockets;

/// <summary>A received message.</summary>
public sealed class WebSocketMessage
{
    internal WebSocketMessage(WebSocketOpcode type, byte[] payload)
    {
        this.Type = type;
        this.Payload = payload;
    }

    /// <summary><see cref="WebSocketOpcode.Text"/> or <see cref="WebSocketOpcode.Binary"/>.</summary>
    public WebSocketOpcode Type { get; }

    public byte[] Payload { get; }

    public bool IsText => this.Type == WebSocketOpcode.Text;

    /// <summary>The payload decoded as UTF-8. Only meaningful for a text message.</summary>
    public string Text => Encoding.UTF8.GetString(this.Payload);
}

/// <summary>Why a socket ended.</summary>
public sealed class WebSocketCloseResult(WebSocketCloseStatus status, string? description)
{
    public WebSocketCloseStatus Status { get; } = status;

    public string? Description { get; } = description;
}

/// <summary>
/// A live WebSocket.
/// <para>
/// Reading and writing are independent — a socket is full duplex and treating it as
/// request/response is the usual way to deadlock one. Control frames are handled here: a ping is
/// answered with a pong automatically, and a close is acknowledged, so a handler only ever sees the
/// messages it actually cares about.
/// </para>
/// </summary>
public sealed class WebSocket : IAsyncDisposable
{
    readonly IConnection connection;
    readonly PipeReader input;
    readonly PipeWriter output;
    readonly SemaphoreSlim writeGate = new(1, 1);
    readonly long maxMessageLength;
    readonly CancellationTokenSource keepAliveStopped = new();

    bool closeSent;
    bool closeReceived;
    int disposed;
    int pendingPings;

    internal WebSocket(IConnection connection, long maxMessageLength)
    {
        this.connection = connection;
        this.input = connection.Input;
        this.output = connection.Output;
        this.maxMessageLength = maxMessageLength;
    }

    /// <summary>The sub-protocol agreed during the handshake, if any.</summary>
    public string? SubProtocol { get; internal init; }

    /// <summary>True when permessage-deflate was negotiated, so messages are compressed both ways.</summary>
    public bool PerMessageDeflate { get; internal init; }

    /// <summary>The minimum payload worth compressing. Below it, deflate makes a message larger.</summary>
    internal int CompressionThreshold { get; init; } = 256;

    /// <summary>Anything the app wants to hang off this socket — a user id, a subscription set.</summary>
    public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>True until a close frame has been both sent and received.</summary>
    public bool IsOpen => !this.closeSent || !this.closeReceived;

    /// <summary>How the peer closed, once it has.</summary>
    public WebSocketCloseResult? CloseResult { get; private set; }

    /// <summary>
    /// Reads the next message, returning null when the socket closes. Pings and pongs are handled
    /// internally and never surface here.
    /// </summary>
    public async ValueTask<WebSocketMessage?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        var payload = new ArrayBufferWriter<byte>();
        var messageType = WebSocketOpcode.Continuation;
        var fragmented = false;
        var compressed = false;

        while (true)
        {
            var header = await this.ReadHeaderAsync(cancellationToken).ConfigureAwait(false);
            if (header is not { } frame)
                return null;

            // RFC 6455 §5.1: every frame from a client must be masked. An unmasked one is either a
            // broken client or a proxy rewriting traffic, and neither should be trusted.
            if (!frame.Masked)
                throw new WebSocketProtocolException(WebSocketCloseStatus.ProtocolError, "Client frames must be masked.");

            if (frame.IsControl)
            {
                // Control frames interleave with a fragmented message, so this cannot wait until
                // the message is complete.
                if (await this.HandleControlAsync(frame, cancellationToken).ConfigureAwait(false))
                    return null;

                continue;
            }

            switch (frame.Opcode)
            {
                case WebSocketOpcode.Text or WebSocketOpcode.Binary when fragmented:
                    throw new WebSocketProtocolException(
                        WebSocketCloseStatus.ProtocolError,
                        "A new message started before the previous one finished."
                    );

                case WebSocketOpcode.Text or WebSocketOpcode.Binary:
                    messageType = frame.Opcode;

                    // RFC 7692 §6.1: RSV1 is set on the first frame of a message and describes the
                    // whole message, continuation frames included.
                    compressed = frame.Rsv1;
                    break;

                case WebSocketOpcode.Continuation when !fragmented:
                    throw new WebSocketProtocolException(
                        WebSocketCloseStatus.ProtocolError,
                        "A continuation frame arrived with no message to continue."
                    );
            }

            if (payload.WrittenCount + frame.PayloadLength > this.maxMessageLength)
                throw new WebSocketProtocolException(WebSocketCloseStatus.MessageTooBig, "The message is too large.");

            await this.ReadPayloadAsync(frame, payload, cancellationToken).ConfigureAwait(false);

            if (frame.Fin)
            {
                var body = compressed
                    ? Shiny.Net.HttpServer.WebSockets.PerMessageDeflate.Decompress(payload.WrittenSpan, this.maxMessageLength)
                    : payload.WrittenSpan.ToArray();

                return new WebSocketMessage(messageType, body);
            }

            fragmented = true;
        }
    }

    /// <summary>Sends a text message.</summary>
    public ValueTask SendAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        return this.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketOpcode.Text, cancellationToken);
    }

    /// <summary>Sends a binary message.</summary>
    public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        => this.SendAsync(payload, WebSocketOpcode.Binary, cancellationToken);

    async ValueTask SendAsync(ReadOnlyMemory<byte> payload, WebSocketOpcode opcode, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(this.disposed != 0, this);

        if (this.closeSent)
            throw new InvalidOperationException("The socket has already been closed.");

        if (this.PerMessageDeflate && payload.Length >= this.CompressionThreshold)
        {
            var compressed = Shiny.Net.HttpServer.WebSockets.PerMessageDeflate.Compress(payload.Span);

            // Only when it actually helped. Compression that makes a message bigger is a cost with
            // no benefit, and the peer cannot tell the difference — RSV1 says which one it got.
            if (compressed.Length < payload.Length)
            {
                await this.WriteFrameAsync(opcode, compressed, cancellationToken, rsv1: true).ConfigureAwait(false);
                return;
            }
        }

        await this.WriteFrameAsync(opcode, payload, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends a ping. The peer is expected to pong; nothing here waits for it.</summary>
    public ValueTask PingAsync(ReadOnlyMemory<byte> payload = default, CancellationToken cancellationToken = default)
        => this.WriteFrameAsync(WebSocketOpcode.Ping, payload, cancellationToken);

    /// <summary>
    /// Sends a close frame. The socket is not fully closed until the peer's close comes back, which
    /// <see cref="ReceiveAsync"/> reports by returning null.
    /// </summary>
    public async ValueTask CloseAsync(
        WebSocketCloseStatus status = WebSocketCloseStatus.NormalClosure,
        string? description = null,
        CancellationToken cancellationToken = default
    )
    {
        if (this.closeSent || this.disposed != 0)
            return;

        this.closeSent = true;

        var reason = description is { Length: > 0 } ? Encoding.UTF8.GetBytes(description) : [];
        var payload = new byte[2 + reason.Length];

        BinaryPrimitives.WriteUInt16BigEndian(payload, (ushort)status);
        reason.CopyTo(payload, 2);

        try
        {
            await this.WriteFrameAsync(WebSocketOpcode.Close, payload, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsDisconnect(ex))
        {
            // The peer went away before the courtesy close reached it. Nothing to salvage.
        }
    }

    async ValueTask<WebSocketFrameHeader?> ReadHeaderAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var result = await this.input.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;

            try
            {
                var remaining = buffer;
                if (WebSocketFrameCodec.TryReadHeader(ref remaining, out var header, this.PerMessageDeflate))
                {
                    this.input.AdvanceTo(remaining.Start);
                    return header;
                }
            }
            catch
            {
                this.input.AdvanceTo(buffer.Start, buffer.End);
                throw;
            }

            this.input.AdvanceTo(buffer.Start, buffer.End);

            if (result.IsCompleted || result.IsCanceled)
                return null;
        }
    }

    async ValueTask ReadPayloadAsync(
        WebSocketFrameHeader header,
        IBufferWriter<byte> destination,
        CancellationToken cancellationToken
    )
    {
        var remaining = header.PayloadLength;
        var maskOffset = 0;

        while (remaining > 0)
        {
            var result = await this.input.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;

            if (buffer.IsEmpty && (result.IsCompleted || result.IsCanceled))
                throw new WebSocketProtocolException(WebSocketCloseStatus.ProtocolError, "The frame payload was truncated.");

            var take = (int)Math.Min(buffer.Length, remaining);
            var chunk = buffer.Slice(0, take);

            var span = destination.GetSpan(take)[..take];
            chunk.CopyTo(span);

            // The mask cycles every four bytes across the whole payload, so a chunk that starts
            // mid-cycle has to say where it starts.
            WebSocketFrameCodec.Unmask(span, header.MaskingKey, maskOffset);
            destination.Advance(take);

            this.input.AdvanceTo(chunk.End);
            remaining -= take;
            maskOffset += take;
        }
    }

    /// <summary>Handles a control frame. Returns true when the socket should stop reading.</summary>
    async ValueTask<bool> HandleControlAsync(WebSocketFrameHeader header, CancellationToken cancellationToken)
    {
        var payload = new ArrayBufferWriter<byte>((int)Math.Max(header.PayloadLength, 1));
        await this.ReadPayloadAsync(header, payload, cancellationToken).ConfigureAwait(false);

        switch (header.Opcode)
        {
            case WebSocketOpcode.Ping:
                // Answered with the same payload, which is what the spec requires and what
                // keepalive implementations check for.
                await this.WriteFrameAsync(WebSocketOpcode.Pong, payload.WrittenMemory, cancellationToken)
                    .ConfigureAwait(false);
                return false;

            case WebSocketOpcode.Pong:
                // Any pong proves the peer is alive, whoever asked. That is all the keepalive
                // loop wants to know.
                Interlocked.Exchange(ref this.pendingPings, 0);
                return false;

            case WebSocketOpcode.Close:
                this.closeReceived = true;
                this.CloseResult = ParseClose(payload.WrittenSpan);

                // Echo the close back so the peer can shut down cleanly, then stop.
                await this.CloseAsync(
                    this.CloseResult.Status == WebSocketCloseStatus.NoStatusReceived
                        ? WebSocketCloseStatus.NormalClosure
                        : this.CloseResult.Status,
                    cancellationToken: cancellationToken
                ).ConfigureAwait(false);

                return true;

            default:
                return false;
        }
    }

    static WebSocketCloseResult ParseClose(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2)
            return new WebSocketCloseResult(WebSocketCloseStatus.NoStatusReceived, null);

        var status = (WebSocketCloseStatus)BinaryPrimitives.ReadUInt16BigEndian(payload);
        var description = payload.Length > 2 ? Encoding.UTF8.GetString(payload[2..]) : null;

        return new WebSocketCloseResult(status, description);
    }

    async ValueTask WriteFrameAsync(
        WebSocketOpcode opcode,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken,
        bool rsv1 = false
    )
    {
        // Serialized: two concurrent sends would interleave their frames and corrupt both.
        await this.writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            WebSocketFrameCodec.WriteHeader(this.output, opcode, fin: true, payload.Length, rsv1);

            if (!payload.IsEmpty)
                this.output.Write(payload.Span);

            await this.output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            this.writeGate.Release();
        }
    }

    internal static bool IsDisconnect(Exception ex) => ex
        is OperationCanceledException
        or System.Net.Sockets.SocketException
        or ObjectDisposedException
        or InvalidOperationException
        or IOException;

    /// <summary>
    /// Pings the peer on an interval and tears the socket down when the pongs stop.
    /// <para>
    /// Not a nicety on a mobile network. A phone that loses signal, a laptop that sleeps, a NAT
    /// that drops an idle mapping — none of these close the socket, and a server that never asks
    /// will hold a connection object and its buffers open for a peer that has been gone for hours.
    /// This is also what keeps a tunnelled socket alive through an intermediary that reaps idle
    /// connections.
    /// </para>
    /// </summary>
    internal void StartKeepAlive(TimeSpan interval, int missedPingsBeforeClosing)
        => _ = Task.Run(async () =>
        {
            var token = this.keepAliveStopped.Token;

            try
            {
                using var timer = new PeriodicTimer(interval);

                while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                {
                    if (this.disposed != 0 || this.closeSent)
                        return;

                    if (Interlocked.Increment(ref this.pendingPings) > missedPingsBeforeClosing)
                    {
                        // The peer has stopped answering. Aborting rather than closing politely,
                        // because a close handshake needs a peer that is still there.
                        this.connection.Abort();
                        return;
                    }

                    await this.PingAsync(default, token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (IsDisconnect(ex))
            {
                // The socket went away underneath the keepalive. That is the outcome it exists to
                // detect, so there is nothing to report.
            }
        }, CancellationToken.None);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) != 0)
            return;

        await this.keepAliveStopped.CancelAsync().ConfigureAwait(false);
        this.keepAliveStopped.Dispose();

        this.writeGate.Dispose();
        await this.connection.DisposeAsync().ConfigureAwait(false);
    }
}
