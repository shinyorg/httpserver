using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace Shiny.Net.HttpServer;

/// <summary>Server configuration. Defaults are chosen to be safe on a phone, not maximal on a server.</summary>
public sealed class HttpServerOptions
{
    /// <summary>
    /// Interface to bind. Defaults to loopback: a server embedded in a mobile app should not be
    /// reachable from the local network unless its author says so explicitly.
    /// <para>Ignored once <see cref="Endpoints"/> has anything in it.</para>
    /// </summary>
    public IPAddress Address { get; set; } = IPAddress.Loopback;

    /// <summary>
    /// Port to bind. Use 0 to let the OS pick one; read it back from <see cref="HttpServer.ListenUrl"/>.
    /// <para>Ignored once <see cref="Endpoints"/> has anything in it.</para>
    /// </summary>
    public int Port { get; set; } = 5000;

    /// <summary>
    /// TLS configuration. Null (the default) serves plain HTTP.
    /// <para>Ignored once <see cref="Endpoints"/> has anything in it.</para>
    /// </summary>
    public HttpsOptions? Https { get; set; }

    /// <summary>
    /// Everything the server should listen on. Empty by default, in which case
    /// <see cref="Address"/>/<see cref="Port"/>/<see cref="Https"/> describe the one endpoint —
    /// that shorthand is what most embedded servers want and it stays the documented path.
    /// <para>
    /// Add to this list to bind several at once, each with its own TLS settings. The usual reason is
    /// serving cleartext to the device itself and TLS to the network:
    /// <code>
    /// options.Listen(IPAddress.Loopback, 5000);
    /// options.ListenHttps(IPAddress.Any, 5001, certificate);
    /// </code>
    /// Adding even one entry takes over completely — the shorthand properties are not folded in, so
    /// a loopback endpoint you still want has to be listed explicitly.
    /// </para>
    /// </summary>
    public IList<HttpServerEndpoint> Endpoints { get; } = [];

    /// <summary>Adds a cleartext endpoint. Returns it, so further settings can be applied.</summary>
    public HttpServerEndpoint Listen(IPAddress address, int port)
    {
        ArgumentNullException.ThrowIfNull(address);

        var endpoint = new HttpServerEndpoint(address, port);
        this.Endpoints.Add(endpoint);

        return endpoint;
    }

    /// <summary>Adds a TLS endpoint serving <paramref name="certificate"/>. Returns it, so further settings can be applied.</summary>
    public HttpServerEndpoint ListenHttps(IPAddress address, int port, X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        var endpoint = this.Listen(address, port);
        endpoint.Https = new HttpsOptions { Certificate = certificate };

        return endpoint;
    }

    /// <summary>
    /// What the server will actually bind: the explicit list when there is one, otherwise the
    /// single endpoint the shorthand properties describe. Read at every start, so a restart picks
    /// up changes to either.
    /// </summary>
    internal IReadOnlyList<HttpServerEndpoint> ResolveEndpoints() =>
        this.Endpoints.Count > 0
            ? [.. this.Endpoints]
            : [new HttpServerEndpoint(this.Address, this.Port) { Https = this.Https }];

    /// <summary>Pending-connection queue depth handed to <c>listen()</c>.</summary>
    public int Backlog { get; set; } = 128;

    /// <summary>Disables Nagle's algorithm. On by default: request/response latency beats packet efficiency.</summary>
    public bool NoDelay { get; set; } = true;

    /// <summary>
    /// Value sent in the Server response header. Set to null to omit the header entirely.
    /// </summary>
    public string? ServerHeader { get; set; } = "Shiny";

    /// <summary>Whether to emit a Date header on responses, as RFC 9110 asks origin servers to do.</summary>
    public bool IncludeDateHeader { get; set; } = true;

    /// <summary>
    /// When true, Scheme and client IP are taken from X-Forwarded-Proto / X-Forwarded-For.
    /// Only enable when the server genuinely sits behind a proxy or tunnel you control — otherwise
    /// any client can spoof its own address.
    /// </summary>
    public bool UseForwardedHeaders { get; set; }

    /// <summary>
    /// Maximum simultaneous connections. Excess connections wait rather than being rejected.
    /// Null removes the cap.
    /// <para>
    /// The cap is for the whole server, not per endpoint, so busy clients on one endpoint can crowd
    /// out another. Keep it comfortably above the number of endpoints — a cap smaller than that
    /// leaves some of them unable to accept at all until a connection elsewhere finishes.
    /// </para>
    /// </summary>
    public int? MaxConcurrentConnections { get; set; } = 256;

    public HttpServerLimits Limits { get; } = new();

    /// <summary>HTTP/2 configuration. See <see cref="Http2Options.Enabled"/> for how it is selected.</summary>
    public Http2Options Http2 { get; } = new();

    /// <summary>
    /// When true (the default) an unhandled handler exception produces a 500 with no detail.
    /// Turn it off in development to return the exception text instead.
    /// </summary>
    public bool HideExceptionDetails { get; set; } = true;

    /// <summary>
    /// Restarts the listeners when the machine's IP addresses change.
    /// <para>
    /// For a device that moves. A listener bound to the Wi-Fi address it had at startup is dead the
    /// moment the phone joins a different network or switches to a hotspot — the socket survives,
    /// the address does not, and nothing reports it. With this on, the server rebinds; with it off,
    /// <see cref="HttpServer.NetworkAddressesChanged"/> still fires so an app can react itself.
    /// </para>
    /// <para>
    /// Off by default, because a restart drops in-flight requests and a server on a fixed machine
    /// has nothing to gain. Binding to <see cref="IPAddress.Any"/> does not need it either — that
    /// socket keeps working across an address change; what needs it is a bind to a specific address.
    /// </para>
    /// </summary>
    public bool RebindOnNetworkChange { get; set; }

    /// <summary>
    /// How long to wait for the addresses to settle before acting on a change. One transition
    /// raises several events — interface down, up, address acquired — and rebinding on each of
    /// them restarts the server three times for one event.
    /// </summary>
    public TimeSpan NetworkChangeDebounce { get; set; } = TimeSpan.FromSeconds(2);

    // ---- Resilience ----
    //
    // These defaults are deliberately on. A server embedded in an app has nobody watching it: the
    // failures below happen on a device in someone's pocket, hours after the last line of app code
    // ran, and an app that has to opt in to not-silently-dying will not have opted in.

    /// <summary>
    /// How many times a start the <em>server itself</em> initiated is attempted before it is
    /// reported as failed — the second half of a <see cref="HttpServer.RestartAsync"/>, a rebind
    /// after the addresses moved, a recovery from a listener that died.
    /// <para>
    /// A start the app asked for is never retried, whatever this is set to:
    /// <see cref="HttpServer.StartAsync"/> throws and the caller decides, which is both louder and
    /// more useful than a button that appears stuck for fifteen seconds. These are the starts with
    /// nobody to tell — and the reason a phone that moved between networks used to stay unreachable
    /// until another address change happened along.
    /// </para>
    /// <para>Set to 1 to disable the retry and have the first failure be the last.</para>
    /// </summary>
    public int StartRetryAttempts { get; set; } = 5;

    /// <summary>
    /// Delay before the first start retry; each further attempt doubles it, up to
    /// <see cref="StartRetryMaxDelay"/>.
    /// <para>
    /// A second rather than immediately, because the two things that refuse a bind here — a network
    /// that is half up, and the old port still sitting in TIME_WAIT — are both waiting on a clock,
    /// not on us. Retrying instantly just spends the attempts before either could have cleared.
    /// </para>
    /// </summary>
    public TimeSpan StartRetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Ceiling for the start backoff. The defaults reach it on the fifth attempt, about fifteen seconds in.</summary>
    public TimeSpan StartRetryMaxDelay { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How many <em>consecutive</em> failures the accept loop absorbs before it declares the
    /// listener dead. The counter resets on every connection accepted.
    /// <para>
    /// Counting rather than classifying is deliberate. Deciding transient-versus-fatal from a socket
    /// error code is a losing game across the platforms this runs on — descriptor exhaustion, an
    /// interface torn down mid-accept and a client that vanished all report differently on Android,
    /// iOS and desktop, and the list changes with the OS. Time is the honest classifier: anything
    /// that clears within a few attempts was transient, and anything that does not is fatal no
    /// matter what its code claimed.
    /// </para>
    /// </summary>
    public int AcceptRetryAttempts { get; set; } = 5;

    /// <summary>
    /// Delay after the first failed accept; doubles per consecutive failure, up to
    /// <see cref="AcceptRetryMaxDelay"/>. Short, because most of what lands here clears immediately
    /// and the cost of waiting is every other client on that listener.
    /// </summary>
    public TimeSpan AcceptRetryDelay { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Ceiling for the accept backoff.</summary>
    public TimeSpan AcceptRetryMaxDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Rebinds when the accept loop ends while the server still believed it was running.
    /// <para>
    /// On by default. The alternative — the behaviour before this existed — is a server that reports
    /// <see cref="HttpServerState.Running"/> with no listener behind it, refuses every connection,
    /// and cannot be fixed by toggling it off and on because it already thinks it is on.
    /// </para>
    /// <para>
    /// Turn it off to have the fault stop the server instead. It is still reported either way:
    /// the transition carries <see cref="HttpServerStateReason.ListenerFaulted"/> and the cause, and
    /// it is logged at error level. What this setting chooses is whether the server tries to come
    /// back, not whether anyone finds out.
    /// </para>
    /// </summary>
    public bool RecoverFromListenerFaults { get; set; } = true;
}

/// <summary>
/// One address/port the server listens on, with its own TLS settings.
/// <para>
/// TLS is per endpoint rather than per server because the two usually differ: an app that serves
/// itself over loopback and the local network over TLS needs exactly one certificate, on exactly one
/// of those two sockets. Limits, HTTP/2 settings and everything else stay server-wide.
/// </para>
/// </summary>
public sealed class HttpServerEndpoint
{
    public HttpServerEndpoint()
    {
    }

    public HttpServerEndpoint(IPAddress address, int port)
    {
        ArgumentNullException.ThrowIfNull(address);

        this.Address = address;
        this.Port = port;
    }

    /// <summary>Interface to bind. Loopback keeps the server off the network, which is the safe default.</summary>
    public IPAddress Address { get; set; } = IPAddress.Loopback;

    /// <summary>Port to bind. Use 0 to let the OS pick one; read it back from <see cref="HttpServer.ListenUrls"/>.</summary>
    public int Port { get; set; } = 5000;

    /// <summary>TLS for this endpoint alone. Null serves plain HTTP.</summary>
    public HttpsOptions? Https { get; set; }
}

/// <summary>Protocol limits. These exist to keep a misbehaving or hostile client from exhausting memory.</summary>
public sealed class HttpServerLimits
{
    /// <summary>Maximum bytes for the request line (method, target, version).</summary>
    public int MaxRequestLineSize { get; set; } = 8 * 1024;

    /// <summary>Maximum total bytes for all request headers combined.</summary>
    public int MaxRequestHeadersTotalSize { get; set; } = 32 * 1024;

    /// <summary>Maximum number of headers on a single request.</summary>
    public int MaxRequestHeaderCount { get; set; } = 100;

    /// <summary>Maximum request body size. Null removes the limit.</summary>
    public long? MaxRequestBodySize { get; set; } = 30 * 1024 * 1024;

    /// <summary>How long a connection may stay idle between requests before it is closed.</summary>
    public TimeSpan KeepAliveTimeout { get; set; } = TimeSpan.FromSeconds(130);

    /// <summary>How long a client has to finish sending the request line and headers.</summary>
    public TimeSpan RequestHeadersTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Maximum requests served on one connection before it is closed. Null removes the limit.</summary>
    public long? MaxRequestsPerConnection { get; set; } = 1000;

    /// <summary>Read buffer size for the connection's input pipe.</summary>
    public int InputBufferSize { get; set; } = 16 * 1024;
}

/// <summary>TLS settings for the listener.</summary>
public sealed class HttpsOptions
{
    /// <summary>The server certificate. Must include a private key.</summary>
    public X509Certificate2? Certificate { get; set; }

    /// <summary>
    /// Chooses a certificate per connection based on SNI. Takes precedence over
    /// <see cref="Certificate"/>, which is what a multi-tenant relay needs.
    /// </summary>
    public Func<string?, X509Certificate2?>? CertificateSelector { get; set; }

    public SslProtocols SslProtocols { get; set; } = SslProtocols.Tls12 | SslProtocols.Tls13;

    public ClientCertificateMode ClientCertificateMode { get; set; } = ClientCertificateMode.NoCertificate;

    /// <summary>Custom client-certificate validation. Only consulted when client certs are requested.</summary>
    public RemoteCertificateValidationCallback? ClientCertificateValidation { get; set; }

    /// <summary>
    /// How long a client has to complete the TLS handshake. A connection that opens and then says
    /// nothing costs a connection slot until this fires.
    /// </summary>
    public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(10);

    internal SslServerAuthenticationOptions ToSslServerAuthenticationOptions(bool offerHttp2 = false)
    {
        if (this.Certificate is null && this.CertificateSelector is null)
            throw new InvalidOperationException(
                $"{nameof(HttpsOptions)} requires either {nameof(this.Certificate)} or {nameof(this.CertificateSelector)}."
            );

        var options = new SslServerAuthenticationOptions
        {
            // ALPN is the only way HTTP/2 is negotiated over TLS. h2 first: the client picks from
            // the server's list in the server's order of preference.
            ApplicationProtocols = offerHttp2
                ? [SslApplicationProtocol.Http2, SslApplicationProtocol.Http11]
                : [SslApplicationProtocol.Http11],
            EnabledSslProtocols = this.SslProtocols,
            ClientCertificateRequired = this.ClientCertificateMode != ClientCertificateMode.NoCertificate,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck
        };

        if (this.CertificateSelector is { } selector)
            options.ServerCertificateSelectionCallback = (_, hostName) => selector(hostName)
                ?? throw new InvalidOperationException($"No certificate available for host '{hostName}'.");
        else
            options.ServerCertificate = this.Certificate;

        if (this.ClientCertificateMode != ClientCertificateMode.NoCertificate)
        {
            var validation = this.ClientCertificateValidation;
            var required = this.ClientCertificateMode == ClientCertificateMode.RequireCertificate;

            options.RemoteCertificateValidationCallback = (sender, certificate, chain, errors) =>
            {
                // SslStream asks for a client certificate but does not insist on one, so "required"
                // has to be enforced here or AllowCertificate and RequireCertificate behave alike.
                if (certificate is null)
                    return !required;

                // Accept any client cert by default and let the app decide. Requiring a chain we
                // know nothing about would reject every legitimate cert.
                return validation?.Invoke(sender, certificate, chain, errors) ?? true;
            };
        }

        return options;
    }
}

/// <summary>
/// HTTP/2 settings.
/// <para>
/// Which protocol a connection speaks is never guessed: over TLS it is whatever ALPN agreed, and
/// over cleartext it is HTTP/2 only when the client opens with the connection preface. A client
/// that says nothing gets HTTP/1.1, which is the only safe default.
/// </para>
/// </summary>
public sealed class Http2Options
{
    /// <summary>
    /// Whether HTTP/2 is offered at all. On by default; the protocol is still negotiated per
    /// connection, so turning it on cannot break an HTTP/1.1 client.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Accept cleartext HTTP/2 from a client that opens with the connection preface ("prior
    /// knowledge", RFC 9113 3.3). Browsers never do this; API clients and gRPC do.
    /// </summary>
    public bool AllowCleartext { get; set; } = true;

    /// <summary>Maximum streams a client may have open at once.</summary>
    public int MaxConcurrentStreams { get; set; } = 100;

    /// <summary>Flow-control window for each stream.</summary>
    public int InitialStreamWindowSize { get; set; } = 96 * 1024;

    /// <summary>
    /// Flow-control window for the connection as a whole. Larger than one stream's, so several
    /// concurrent uploads do not have to take turns.
    /// </summary>
    public int InitialConnectionWindowSize { get; set; } = 1024 * 1024;

    /// <summary>Largest frame the peer may send. The protocol floor is 16 KiB.</summary>
    public int MaxFrameSize { get; set; } = 16 * 1024;

    /// <summary>Advisory cap on the total size of a request's headers.</summary>
    public int MaxHeaderListSize { get; set; } = 32 * 1024;
}

public enum ClientCertificateMode
{
    NoCertificate,
    AllowCertificate,
    RequireCertificate
}
