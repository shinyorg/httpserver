using System.Net;
using Shiny.Net.HttpServer.Security;

namespace Shiny.Net.HttpServer;

/// <summary>Whether a listener expects the HAProxy PROXY protocol header ahead of each connection.</summary>
public enum ProxyProtocolMode
{
    /// <summary>
    /// No header is read. A connection that opens with one is handed to HTTP as-is, where it is a
    /// malformed request — which is the right outcome for a listener nobody said was behind a balancer.
    /// </summary>
    Off,

    /// <summary>
    /// A header is read when a trusted peer sends one, and the connection is served either way.
    /// For migrations — the balancer is being switched over and both kinds of connection arrive for
    /// a while. Once the switch is done, prefer <see cref="Required"/>.
    /// </summary>
    Optional,

    /// <summary>
    /// Every connection must open with a header, from a trusted peer. Anything else is closed before
    /// a byte of HTTP (or TLS) is read. The mode to run in once the only way in is the balancer.
    /// </summary>
    Required
}

/// <summary>
/// PROXY protocol (v1 text and v2 binary) settings for one endpoint.
/// <para>
/// A TCP load balancer — HAProxy, an AWS Network Load Balancer, nginx's <c>stream</c> module,
/// Traefik, fly.io — opens its own connection to this server, so without help every request appears
/// to come from the balancer. The PROXY protocol fixes that below HTTP: the balancer writes one small
/// header naming the real client before anything else, and the server swaps that client into
/// <see cref="ConnectionInfo.RemoteIpAddress"/> and <see cref="ConnectionInfo.RemotePort"/>. IP
/// filtering, rate limiting, W3C logs and telemetry then see the real client without knowing a
/// balancer was involved.
/// </para>
/// <para>
/// Because it sits below TLS, it works where <c>X-Forwarded-For</c> cannot: a balancer passing TLS
/// straight through to this server never sees the HTTP headers it would have to add to.
/// </para>
/// </summary>
public sealed class ProxyProtocolOptions
{
    /// <summary>Largest header the v1 (text) form may be, CRLF included — fixed by the specification.</summary>
    public const int V1MaxHeaderSize = 107;

    /// <summary>Off, optional, or required. Off by default: nothing is read that nobody asked for.</summary>
    public ProxyProtocolMode Mode { get; set; } = ProxyProtocolMode.Off;

    /// <summary>
    /// Peers allowed to speak the PROXY protocol — the balancer's own addresses, not the clients'.
    /// <para>
    /// Must not be empty when <see cref="Mode"/> is on; the server refuses to start rather than
    /// guess. The header is an unauthenticated claim about who the client is, so taking it from
    /// anyone would let any client choose its own address and walk straight past an IP filter. To
    /// trust every peer deliberately — the port is firewalled so only the balancer can reach it —
    /// add <c>0.0.0.0/0</c> and <c>::/0</c>.
    /// </para>
    /// <para>
    /// A header from anyone else is never parsed: with <see cref="ProxyProtocolMode.Required"/> the
    /// connection is closed, and with <see cref="ProxyProtocolMode.Optional"/> it is served as plain
    /// HTTP — where a leading <c>PROXY</c> line is simply a bad request.
    /// </para>
    /// </summary>
    public IList<IpAddressRange> TrustedProxies { get; } = [];

    /// <summary>
    /// How long a peer has to deliver the complete header. Balancers send it the instant they
    /// connect, so this only ever fires for a peer that is not the balancer it claims to be — or a
    /// client that connected and said nothing, which otherwise holds a connection slot.
    /// </summary>
    public TimeSpan HeaderTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Largest v2 (binary) header accepted, the fixed 16 bytes included. The v2 length field allows
    /// up to 64 KiB; real headers are a few hundred bytes even with TLVs, so anything past this is
    /// refused rather than buffered. The v1 form is always capped at <see cref="V1MaxHeaderSize"/>.
    /// </summary>
    public int MaxHeaderSize { get; set; } = 4096;

    /// <summary>Adds a trusted peer or CIDR range, e.g. <c>10.0.0.0/8</c>. Returns this, for chaining.</summary>
    public ProxyProtocolOptions Trust(string addressOrRange)
    {
        this.TrustedProxies.Add(IpAddressRange.Parse(addressOrRange));
        return this;
    }

    /// <summary>Adds a trusted peer or range. Returns this, for chaining.</summary>
    public ProxyProtocolOptions Trust(IpAddressRange range)
    {
        ArgumentNullException.ThrowIfNull(range);
        this.TrustedProxies.Add(range);
        return this;
    }

    internal bool IsTrusted(IPAddress? peer)
    {
        if (peer is null)
            return false;

        foreach (var range in this.TrustedProxies)
        {
            if (range.Contains(peer))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Refuses a configuration that would either do nothing or trust everybody by accident. Run when
    /// the listener is created, so a mistake stops the server starting rather than surfacing as the
    /// balancer's address in every log line.
    /// </summary>
    internal void Validate()
    {
        if (this.Mode == ProxyProtocolMode.Off)
            return;

        if (this.TrustedProxies.Count == 0)
            throw new InvalidOperationException(
                $"The PROXY protocol is {this.Mode} but {nameof(this.TrustedProxies)} is empty. Add the load balancer's " +
                "addresses (e.g. options.Trust(\"10.0.0.0/8\")), or \"0.0.0.0/0\" and \"::/0\" if the port is firewalled " +
                "so that only the balancer can reach it."
            );

        if (this.HeaderTimeout <= TimeSpan.Zero)
            throw new InvalidOperationException($"{nameof(this.HeaderTimeout)} must be positive.");

        // 16 bytes of fixed header plus the largest address block (two AF_UNIX paths) is the least a
        // compliant balancer may send; a cap below it would refuse valid headers from a unix-socket peer.
        if (this.MaxHeaderSize < 16 + 216)
            throw new InvalidOperationException($"{nameof(this.MaxHeaderSize)} must be at least 232 bytes.");
    }
}
