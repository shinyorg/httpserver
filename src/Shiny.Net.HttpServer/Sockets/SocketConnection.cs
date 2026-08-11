using System.IO.Pipelines;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace Shiny.Net.HttpServer.Transports;

/// <summary>
/// A TCP connection exposed as a duplex pipe.
/// <para>
/// The pipes are built over the socket's <see cref="NetworkStream"/> (or <see cref="SslStream"/>)
/// rather than driving the socket directly. That costs a little throughput versus a hand-rolled
/// socket scheduler, and buys TLS for free plus a great deal less code to get wrong. The parser
/// still gets a real <see cref="PipeReader"/>, so request parsing remains zero-copy.
/// </para>
/// </summary>
sealed class SocketConnection : IConnection, IConnectionInitializer
{
    readonly Socket socket;
    readonly HttpServerOptions options;
    readonly HttpsOptions? https;
    Stream? stream;
    PipeReader? input;
    PipeWriter? output;
    int aborted;

    SocketConnection(string connectionId, Socket socket, HttpServerOptions options, HttpsOptions? https)
    {
        this.ConnectionId = connectionId;
        this.socket = socket;
        this.options = options;
        this.https = https;

        // Cache the endpoints now: reading them off a disposed socket throws, and we still want
        // them for logging after a connection drops.
        try
        {
            this.RemoteEndPoint = socket.RemoteEndPoint;
            this.LocalEndPoint = socket.LocalEndPoint;
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public string ConnectionId { get; }

    public PipeReader Input => this.input ?? throw new InvalidOperationException(
        $"{nameof(InitializeAsync)} must complete before the connection can be read."
    );

    public PipeWriter Output => this.output ?? throw new InvalidOperationException(
        $"{nameof(InitializeAsync)} must complete before the connection can be written."
    );

    public EndPoint? RemoteEndPoint { get; }
    public EndPoint? LocalEndPoint { get; }
    public bool IsEncrypted { get; private set; }
    public X509Certificate2? ClientCertificate { get; private set; }
    public bool IsTunneled => false;

    public string? ApplicationProtocol { get; private set; }

    /// <summary>
    /// Wraps a freshly accepted socket. Nothing is read or written yet — see
    /// <see cref="InitializeAsync"/>, which is where the TLS handshake happens.
    /// </summary>
    public static SocketConnection Create(
        string connectionId,
        Socket socket,
        HttpServerOptions options,
        HttpsOptions? https
    )
    {
        socket.NoDelay = options.NoDelay;
        return new SocketConnection(connectionId, socket, options, https);
    }

    /// <summary>
    /// Completes the TLS handshake, when the endpoint this connection arrived on has one configured,
    /// and opens the pipes over whatever stream that left behind.
    /// <para>
    /// Deliberately separate from accepting. A handshake takes a round trip at minimum and can be
    /// made to take forever by a client that connects and then says nothing; running it on the
    /// accept loop would let one such client stall every other connection to the server.
    /// </para>
    /// </summary>
    public async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        Stream transport = new NetworkStream(this.socket, ownsSocket: false);

        if (this.https is { } tls)
        {
            var ssl = new SslStream(transport, leaveInnerStreamOpen: false);

            // A handshake that never finishes otherwise holds a connection slot indefinitely.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(tls.HandshakeTimeout);

            try
            {
                await ssl
                    .AuthenticateAsServerAsync(
                        tls.ToSslServerAuthenticationOptions(this.options.Http2.Enabled),
                        timeout.Token
                    )
                    .ConfigureAwait(false);
            }
            catch
            {
                await ssl.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            transport = ssl;
            this.IsEncrypted = true;
            this.ClientCertificate = ssl.RemoteCertificate as X509Certificate2;

            var negotiated = ssl.NegotiatedApplicationProtocol;
            this.ApplicationProtocol = negotiated.Protocol.IsEmpty ? null : negotiated.ToString();
        }

        this.stream = transport;
        this.input = PipeReader.Create(
            transport,
            new StreamPipeReaderOptions(bufferSize: this.options.Limits.InputBufferSize, leaveOpen: true)
        );
        this.output = PipeWriter.Create(
            transport,
            new StreamPipeWriterOptions(leaveOpen: true)
        );
    }

    public void Abort()
    {
        if (Interlocked.Exchange(ref this.aborted, 1) != 0)
            return;

        // Reset rather than a graceful FIN: an aborted connection is one we no longer trust to
        // behave, and we do not want to wait on it.
        try
        {
            this.socket.LingerState = new LingerOption(true, 0);
            this.socket.Shutdown(SocketShutdown.Both);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
        }
        try
        {
            this.socket.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (this.input is not null)
                await this.input.CompleteAsync().ConfigureAwait(false);

            if (this.output is not null)
                await this.output.CompleteAsync().ConfigureAwait(false);
        }
        catch
        {
            // Completing pipes over a already-dead socket is expected to fail; nothing to salvage.
        }

        try
        {
            if (this.stream is not null)
                await this.stream.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
        }

        this.ClientCertificate?.Dispose();

        try
        {
            this.socket.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}

/// <summary>
/// A connection that needs work done before it can carry HTTP — today, a TLS handshake. Kept off
/// <see cref="IConnection"/> because transports that arrive ready to use (the tunnel's in-memory
/// pipes) should not have to implement a no-op.
/// </summary>
interface IConnectionInitializer
{
    ValueTask InitializeAsync(CancellationToken cancellationToken);
}
