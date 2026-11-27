using System.Net;

namespace Shiny.Net.HttpServer.Tunneling;

/// <summary>Configuration for the relay: where clients register, where the public arrives.</summary>
public sealed class RelayServerOptions
{
    /// <summary>Interface for both listeners. Defaults to loopback so an unconfigured relay is not public.</summary>
    public IPAddress Address { get; set; } = IPAddress.Loopback;

    /// <summary>
    /// Interface for the control listener, when it differs from <see cref="Address"/>. Null uses
    /// <see cref="Address"/>.
    /// </summary>
    public IPAddress? ControlAddress { get; set; }

    /// <summary>
    /// Interface for the public listener, when it differs from <see cref="Address"/>. Null uses
    /// <see cref="Address"/>. Behind a reverse proxy on the same host, set this to loopback and
    /// <see cref="ControlAddress"/> to a public interface, so only the proxy can reach the public side.
    /// </summary>
    public IPAddress? PublicAddress { get; set; }

    /// <summary>Where tunnel clients register. Not where public traffic goes.</summary>
    public int ControlPort { get; set; } = 5050;

    /// <summary>Where public HTTP traffic arrives to be routed by Host header.</summary>
    public int PublicPort { get; set; } = 8080;

    /// <summary>
    /// Base domain for assigned hosts, e.g. <c>example.com</c> gives <c>abc123.example.com</c>.
    /// </summary>
    public string Domain { get; set; } = "localhost";

    /// <summary>Scheme advertised in the public URL handed back to clients.</summary>
    public string PublicScheme { get; set; } = "http";

    /// <summary>
    /// Included in the public URL when the public port is not the scheme's default. Set false when
    /// something in front of the relay terminates on the standard port.
    /// </summary>
    public bool IncludePortInPublicUrl { get; set; } = true;

    /// <summary>TLS for the control listener. Strongly recommended: registration carries the token.</summary>
    public HttpsOptions? ControlHttps { get; set; }

    /// <summary>TLS for the public listener.</summary>
    public HttpsOptions? PublicHttps { get; set; }

    /// <summary>
    /// Decides whether a registration is allowed and, when it is, which subdomain it gets. Return
    /// null to refuse. Asynchronous so it can consult a database or an identity service; it is
    /// cancelled after <see cref="AuthorizeTimeout"/> or when the relay stops, and an exception
    /// refuses the registration. The default accepts any token matching <see cref="Token"/> and
    /// grants the requested subdomain when it is free.
    /// </summary>
    public Func<TunnelRegistrationRequest, CancellationToken, ValueTask<string?>>? Authorize { get; set; }

    /// <summary>
    /// How long <see cref="Authorize"/> may take before the registration is refused. A control
    /// connection is held open while it runs, so a stalled lookup must not hold it forever.
    /// </summary>
    public TimeSpan AuthorizeTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Shared secret the default <see cref="Authorize"/> compares against. Null accepts anything.</summary>
    public string? Token { get; set; }

    /// <summary>Maximum simultaneously registered tunnels.</summary>
    public int MaxTunnels { get; set; } = 100;

    /// <summary>Largest request head the relay will buffer while looking for the Host header.</summary>
    public int MaxRequestHeadSize { get; set; } = 32 * 1024;

    /// <summary>
    /// Adds <c>X-Forwarded-For</c>, <c>-Proto</c> and <c>-Host</c> to relayed requests. The tunnelled
    /// server has to opt in with <c>HttpServerOptions.UseForwardedHeaders</c> before it trusts them.
    /// </summary>
    public bool AddForwardedHeaders { get; set; } = true;

    /// <summary>How long a public connection may take to send its request head.</summary>
    public TimeSpan RequestHeadTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>How long a public connection may sit idle between requests before it is closed.</summary>
    public TimeSpan KeepAliveTimeout { get; set; } = TimeSpan.FromSeconds(130);

    /// <summary>
    /// Holds each request on a tunnel to its own flow-control window, so one that stalls - a public client that stops
    /// reading its download, an app handler that never reads its upload - waits alone instead of stopping every other
    /// request on the tunnel. Used with clients that support it, which say so when they register; others are served as
    /// before. On by default.
    /// </summary>
    public bool EnableFlowControl { get; set; } = true;

    /// <summary>
    /// How many bytes of one response the relay will hold for a public client that is not keeping up, before the
    /// tunnelled app has to wait: the window every stream starts with, and so what one stalled request costs in memory
    /// here. A stream whose public client keeps up grows its window, up to <see cref="MaxStreamWindowSize"/>, so a long
    /// round trip does not cap a fast download at one starting window per round trip. 256 KiB by default.
    /// </summary>
    public int StreamWindowSize { get; set; } = 256 * 1024;

    /// <summary>
    /// How far one stream's window may grow while its public client keeps up - which is what carries a large transfer
    /// at full speed over a long round trip: a stream moves at most one window per round trip, so 8 MiB is ~160 MB/s at
    /// 50 ms. A stream keeps what it grew into, charged to <see cref="TunnelWindowBudget"/>, until it slows down or
    /// closes. 8 MiB by default.
    /// </summary>
    public int MaxStreamWindowSize { get; set; } = 8 * 1024 * 1024;

    /// <summary>
    /// Room, across all of one tunnel's streams, for windows to grow beyond <see cref="StreamWindowSize"/>. Growth stops
    /// when it is used up, so fast transfers can never add more than this to what a tunnel can make the relay buffer.
    /// 64 MiB by default.
    /// </summary>
    public long TunnelWindowBudget { get; set; } = 64L * 1024 * 1024;

    /// <summary>
    /// Public connections one tunnel may carry at once when flow control is in use; one over the limit gets a 503. A
    /// connection counts for as long as it is open, kept-alive ones between requests included, so this is a safety
    /// bound rather than a working limit: with <see cref="StreamWindowSize"/> it caps what one tunnel can make the
    /// relay buffer (512 x 256 KiB, plus <see cref="TunnelWindowBudget"/>, if every one of them stalled at once). 512 by default.
    /// </summary>
    public int MaxStreamsPerTunnel { get; set; } = 512;

    /// <summary>
    /// How long a write to a public client may wait for it to take the bytes, when flow control is in use. A client
    /// that has stopped reading - or vanished without closing - is disconnected after this, which gives back the window
    /// it was holding and lets the app's response end.
    /// </summary>
    public TimeSpan PublicWriteTimeout { get; set; } = TimeSpan.FromSeconds(60);
}

/// <summary>What a client asked for when it registered.</summary>
public sealed class TunnelRegistrationRequest(string? token, string? requestedSubdomain, EndPoint? remoteEndPoint)
{
    public string? Token { get; } = token;

    public string? RequestedSubdomain { get; } = requestedSubdomain;

    public EndPoint? RemoteEndPoint { get; } = remoteEndPoint;

    /// <summary>
    /// Set by <see cref="RelayServerOptions.Authorize"/> to tag the tunnel it grants — a key id, an
    /// account — and read back from <see cref="RelayTunnel.State"/>.
    /// </summary>
    public object? State { get; set; }

    /// <summary>
    /// Set by <see cref="RelayServerOptions.Authorize"/> to advertise this tunnel under a different
    /// scheme than <see cref="RelayServerOptions.PublicScheme"/> — <c>http</c> for a host no
    /// certificate covers yet, say. Used in the public URL handed to the client and in the
    /// <c>X-Forwarded-Proto</c> the relay adds. <c>http</c> or <c>https</c>; anything else refuses
    /// the registration.
    /// </summary>
    public string? PublicScheme { get; set; }
}
