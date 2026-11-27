using System.Net.Security;
using System.Security.Authentication;

namespace Shiny.Net.HttpServer.Tunneling;

/// <summary>How to reach the relay and what to ask it for.</summary>
public sealed class RelayTunnelOptions
{
    /// <summary>Relay host name.</summary>
    public string Host { get; set; } = "localhost";

    /// <summary>Relay control port — where tunnel clients connect, not where the public arrives.</summary>
    public int Port { get; set; } = 5050;

    /// <summary>
    /// Shared secret presented at registration. The relay decides what to do with it; the reference
    /// relay compares it against a configured value.
    /// </summary>
    public string? Token { get; set; }

    /// <summary>
    /// Subdomain to ask for, e.g. <c>my-phone</c> for <c>my-phone.example.com</c>. Leave null and
    /// the relay assigns one.
    /// </summary>
    public string? Subdomain { get; set; }

    /// <summary>TLS on the control connection. On by default; turn it off only for local testing.</summary>
    public bool UseTls { get; set; } = true;

    public SslProtocols SslProtocols { get; set; } = SslProtocols.Tls12 | SslProtocols.Tls13;

    /// <summary>
    /// Custom server-certificate validation for the control connection. Null uses the platform's
    /// default chain validation, which is what you want unless the relay uses a private CA.
    /// </summary>
    public RemoteCertificateValidationCallback? ServerCertificateValidation { get; set; }

    /// <summary>How long to wait for the relay to acknowledge registration.</summary>
    public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Idle interval between keepalive pings. Mobile networks drop idle connections aggressively,
    /// and a tunnel that has silently died is worse than one that reconnects.
    /// </summary>
    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Delay before redialling after the tunnel drops. Set to null to fail instead of reconnecting.
    /// </summary>
    public TimeSpan? ReconnectDelay { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Holds each request on the tunnel to its own flow-control window, so a handler that never reads its upload - or a
    /// public client that stops reading its download - holds up only its own request. Used when the relay supports it;
    /// against an older relay the tunnel works as before. On by default.
    /// </summary>
    public bool EnableFlowControl { get; set; } = true;

    /// <summary>
    /// How many bytes of one request body this end will hold for a handler that is not reading it, before the relay
    /// has to wait. 512 KiB by default.
    /// </summary>
    public int StreamWindowSize { get; set; } = 512 * 1024;

    /// <summary>
    /// How many flow-controlled connections this end will carry at once; the relay holds itself to it. A connection
    /// counts for as long as the public client keeps it open. 512 by default.
    /// </summary>
    public int MaxStreams { get; set; } = 512;
}
