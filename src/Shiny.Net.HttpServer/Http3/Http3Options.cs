using System.Net;
using System.Security.Cryptography.X509Certificates;

namespace Shiny.Net.HttpServer.Http3;

/// <summary>
/// The HTTP/3 endpoint.
/// <para>
/// A separate listener rather than a mode of the TCP one, because HTTP/3 does not run on TCP at
/// all: it is QUIC over UDP, on its own socket. A server usually runs both and advertises the
/// HTTP/3 endpoint from the TCP one with <c>Alt-Svc</c>, since a client has no way to know QUIC is
/// available until something tells it.
/// </para>
/// </summary>
public sealed class Http3Options
{
    /// <summary>Interface to bind. Loopback by default, matching the TCP listener.</summary>
    public IPAddress Address { get; set; } = IPAddress.Loopback;

    /// <summary>UDP port. Conventionally the same number as the HTTPS port, which lets Alt-Svc omit it.</summary>
    public int Port { get; set; } = 5001;

    /// <summary>
    /// The server certificate. Required: QUIC has no plaintext mode, so there is no such thing as
    /// an HTTP/3 endpoint without TLS.
    /// </summary>
    public X509Certificate2? Certificate { get; set; }

    /// <summary>Chooses a certificate per connection from SNI.</summary>
    public Func<string?, X509Certificate2?>? CertificateSelector { get; set; }

    /// <summary>Concurrent request streams a client may open. Each is one in-flight request.</summary>
    public int MaxBidirectionalStreams { get; set; } = 100;

    /// <summary>
    /// Unidirectional streams a client may open. Three is the protocol's own need — control, QPACK
    /// encoder, QPACK decoder — and a little headroom absorbs a peer that opens them twice.
    /// </summary>
    public int MaxUnidirectionalStreams { get; set; } = 10;

    /// <summary>Largest field section this server will decode, in QPACK's accounting.</summary>
    public int MaxFieldSectionSize { get; set; } = 32 * 1024;

    /// <summary>How long a connection may sit idle before QUIC closes it.</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(130);

    /// <summary>
    /// Value for the <c>Alt-Svc</c> header the TCP listener should advertise, or null to build one
    /// from <see cref="Port"/>.
    /// </summary>
    public string? AltSvc { get; set; }

    internal string BuildAltSvc() => this.AltSvc ?? $"h3=\":{this.Port}\"; ma=86400";
}
