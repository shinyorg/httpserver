using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Shiny.Net.HttpServer.Transports;

/// <summary>Accepts TCP connections on a single endpoint.</summary>
sealed class SocketConnectionListener : IConnectionListener
{
    readonly HttpServerOptions options;
    readonly HttpServerEndpoint endpoint;
    readonly ILogger logger;
    readonly IPEndPoint requestedEndPoint;
    readonly string connectionIdPrefix;
    Socket? listenSocket;
    long connectionCounter;

    // Distinguishes "never bound" (a caller mistake) from "bound and since unbound" (a normal
    // shutdown). Without it, unbinding while the accept loop sits between iterations looks
    // identical to accepting before binding, and shutdown throws.
    bool everBound;

    public SocketConnectionListener(HttpServerOptions options, HttpServerEndpoint endpoint, ILogger logger, int listenerIndex = 0)
    {
        this.options = options;
        this.endpoint = endpoint;
        this.logger = logger;
        this.requestedEndPoint = new IPEndPoint(endpoint.Address, endpoint.Port);

        // Connection ids have to stay unique across a multi-endpoint server, and each listener
        // counts on its own — so the listener's position goes into the id.
        this.connectionIdPrefix = listenerIndex == 0 ? "c" : $"c{listenerIndex}-";
    }

    /// <summary>
    /// The endpoint actually bound. Differs from what was requested when port 0 was asked for,
    /// which is how tests and tunnels get an ephemeral port.
    /// </summary>
    public IPEndPoint? BoundEndPoint { get; private set; }

    public string ListenDescription
    {
        get
        {
            var bound = this.BoundEndPoint ?? this.requestedEndPoint;
            var scheme = this.endpoint.Https is null ? "http" : "https";
            var host = bound.Address.Equals(IPAddress.Any) || bound.Address.Equals(IPAddress.IPv6Any)
                ? "localhost"
                : bound.Address.ToString();

            // A literal IPv6 address is only a valid URL host inside brackets.
            if (bound.AddressFamily == AddressFamily.InterNetworkV6 && host != "localhost")
                host = $"[{host}]";

            return $"{scheme}://{host}:{bound.Port}";
        }
    }

    public ValueTask BindAsync(CancellationToken cancellationToken = default)
    {
        if (this.listenSocket is not null)
            throw new InvalidOperationException("The listener is already bound.");

        var socket = new Socket(this.requestedEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            // Dual-mode so a single IPv6Any listener also serves IPv4 clients. Without this, binding
            // to [::] on some platforms silently ignores IPv4 traffic.
            if (this.requestedEndPoint.AddressFamily == AddressFamily.InterNetworkV6)
                socket.DualMode = true;

            socket.Bind(this.requestedEndPoint);
            socket.Listen(this.options.Backlog);
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            throw new IOException(
                $"Failed to bind {this.requestedEndPoint}. The port may already be in use.",
                ex
            );
        }

        this.listenSocket = socket;
        this.everBound = true;
        this.BoundEndPoint = (IPEndPoint)socket.LocalEndPoint!;
        return default;
    }

    public async ValueTask<IConnection?> AcceptAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            // Re-read each time: an unbind between iterations is a shutdown, not an error.
            var socket = this.listenSocket;
            if (socket is null)
            {
                if (this.everBound)
                    return null;

                throw new InvalidOperationException("BindAsync must be called before accepting connections.");
            }

            try
            {
                var accepted = await socket.AcceptAsync(cancellationToken).ConfigureAwait(false);
                var id = $"{this.connectionIdPrefix}{Interlocked.Increment(ref this.connectionCounter)}";

                // Returned before the TLS handshake runs — that happens on the connection's own
                // task, so a client that stalls mid-handshake cannot hold up the accept loop.
                return SocketConnection.Create(id, accepted, this.options, this.endpoint.Https);
            }
            catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException)
            {
                // Unbound or shutting down: null tells the accept loop to stop.
                return null;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.OperationAborted)
            {
                return null;
            }
            catch (Exception ex)
            {
                // A single connection failing to come up — a client that vanished mid-accept, a
                // transient resource limit — must not take the listener down with it.
                this.logger.LogDebug(ex, "Failed to accept a connection; continuing to listen");
            }
        }
    }

    public ValueTask UnbindAsync(CancellationToken cancellationToken = default)
    {
        var socket = Interlocked.Exchange(ref this.listenSocket, null);
        socket?.Dispose();
        return default;
    }

    public ValueTask DisposeAsync() => this.UnbindAsync();
}
