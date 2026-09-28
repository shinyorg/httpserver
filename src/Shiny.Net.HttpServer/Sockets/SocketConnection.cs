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
sealed class SocketConnection : IConnection, IConnectionInitializer, IProxyProtocolConnection
{
    readonly Socket socket;
    readonly HttpServerOptions options;
    readonly HttpsOptions? https;
    readonly ProxyProtocolOptions? proxyProtocol;
    Stream? stream;
    PipeReader? input;
    PipeWriter? output;
    int aborted;

    SocketConnection(string connectionId, Socket socket, HttpServerOptions options, HttpsOptions? https, ProxyProtocolOptions? proxyProtocol)
    {
        this.ConnectionId = connectionId;
        this.socket = socket;
        this.options = options;
        this.https = https;
        this.proxyProtocol = proxyProtocol is { Mode: not ProxyProtocolMode.Off } ? proxyProtocol : null;

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

    /// <summary>
    /// The peer — or, once a trusted PROXY header has been read, the client it named. Replaced
    /// here rather than patched later so every consumer (both HTTP versions, logging, the IP filter)
    /// sees one answer without any of them knowing a balancer exists.
    /// </summary>
    public EndPoint? RemoteEndPoint { get; private set; }
    public EndPoint? LocalEndPoint { get; }
    public ProxyProtocolInfo? ProxyProtocol { get; private set; }
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
        HttpsOptions? https,
        ProxyProtocolOptions? proxyProtocol = null
    )
    {
        socket.NoDelay = options.NoDelay;
        return new SocketConnection(connectionId, socket, options, https, proxyProtocol);
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

        // First, before TLS: a balancer passing TLS through writes the header in cleartext ahead of
        // the client's ClientHello, and before protocol detection, which must see the HTTP/2
        // preface rather than the header.
        if (this.proxyProtocol is { } proxy)
            transport = await this.ReadProxyHeaderAsync(transport, proxy, cancellationToken).ConfigureAwait(false);

        if (this.https is { } tls)
        {
            // A handshake that never finishes otherwise holds a connection slot indefinitely. It
            // also bounds the ClientHello read below, which is part of the same handshake.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(tls.HandshakeTimeout);

            if (tls.ChallengeResponder is { } responder)
            {
                var (hello, consumed) = await TlsClientHelloReader.ReadAsync(transport, timeout.Token).ConfigureAwait(false);
                transport = new PrefixedStream(transport, consumed);

                if (hello is not null && responder(hello) is { } challenge)
                {
                    await AnswerChallengeAsync(transport, tls, challenge, timeout.Token).ConfigureAwait(false);
                    throw new TlsChallengeAnsweredException();
                }
            }

            var ssl = new SslStream(transport, leaveInnerStreamOpen: false);
            var http2 = this.options.Http2.Enabled;

            try
            {
                if (tls.CertificateContextSelector is null)
                {
                    await ssl
                        .AuthenticateAsServerAsync(tls.ToSslServerAuthenticationOptions(http2), timeout.Token)
                        .ConfigureAwait(false);
                }
                else
                {
                    // The options are built from the ClientHello, so the context selector sees the
                    // SNI name — and is asked afresh on every connection, which is what lets a
                    // renewed certificate replace the old one without a restart.
                    await ssl
                        .AuthenticateAsServerAsync(SelectServerOptions, (tls, http2), timeout.Token)
                        .ConfigureAwait(false);
                }
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

    async ValueTask<Stream> ReadProxyHeaderAsync(Stream transport, ProxyProtocolOptions proxy, CancellationToken cancellationToken)
    {
        var peer = this.RemoteEndPoint as IPEndPoint;
        if (!proxy.IsTrusted(peer?.Address))
        {
            // Never parsed. Optional hands the bytes to HTTP untouched, where a PROXY line is a 400;
            // Required closes the connection, since the only legitimate caller is the balancer.
            if (proxy.Mode == ProxyProtocolMode.Required)
                throw new ProxyProtocolException($"{peer?.Address} is not a trusted proxy.");

            return transport;
        }

        var (info, rest) = await ProxyProtocolReader.ReadAsync(transport, proxy, cancellationToken).ConfigureAwait(false);
        if (info is null)
            return rest;

        info.ProxyEndPoint = peer;
        this.ProxyProtocol = info;

        // LOCAL (a health check), UNKNOWN / AF_UNSPEC, and unix-socket clients carry no IP to report,
        // so the connection keeps its real endpoint — the spec's instruction, and the honest answer.
        if (info.Command == ProxyProtocolCommand.Proxy && info.SourceEndPoint is { } source)
            this.RemoteEndPoint = source;

        return rest;
    }

    static ValueTask<SslServerAuthenticationOptions> SelectServerOptions(
        SslStream stream,
        SslClientHelloInfo hello,
        object? state,
        CancellationToken cancellationToken
    )
    {
        var (tls, offerHttp2) = ((HttpsOptions, bool))state!;
        return ValueTask.FromResult(tls.ToSslServerAuthenticationOptions(offerHttp2, hello.ServerName));
    }

    /// <summary>
    /// Completes a challenge handshake — one certificate, one ALPN protocol — and closes. Nothing is
    /// read or written over it: for TLS-ALPN-01 the handshake itself is the whole answer.
    /// </summary>
    static async Task AnswerChallengeAsync(
        Stream transport,
        HttpsOptions tls,
        TlsChallengeResponse challenge,
        CancellationToken cancellationToken
    )
    {
        await using var ssl = new SslStream(transport, leaveInnerStreamOpen: true);

        await ssl
            .AuthenticateAsServerAsync(
                new SslServerAuthenticationOptions
                {
                    ServerCertificate = challenge.Certificate,
                    ApplicationProtocols = [challenge.ApplicationProtocol],
                    EnabledSslProtocols = tls.SslProtocols,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                },
                cancellationToken
            )
            .ConfigureAwait(false);
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

/// <summary>
/// A connection that may have opened with a PROXY protocol header. Kept off <see cref="IConnection"/>
/// for the same reason as <see cref="IConnectionInitializer"/>: it is a socket-listener concern, and
/// a public interface every tunnel transport implements is not the place for it.
/// </summary>
interface IProxyProtocolConnection
{
    ProxyProtocolInfo? ProxyProtocol { get; }
}
