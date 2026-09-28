using System.Net;
using System.Security.Cryptography.X509Certificates;

namespace Shiny.Net.HttpServer;

/// <summary>
/// Transport-level facts about the connection carrying the request: who is on the other end,
/// which local endpoint accepted it, and whether TLS is in play.
/// </summary>
public sealed class ConnectionInfo
{
    /// <summary>Stable id for the underlying connection. Useful for correlating logs.</summary>
    public string ConnectionId { get; internal set; } = string.Empty;

    public IPAddress? RemoteIpAddress { get; internal set; }
    public int RemotePort { get; internal set; }
    public IPAddress? LocalIpAddress { get; internal set; }
    public int LocalPort { get; internal set; }

    /// <summary>True when the transport negotiated TLS directly with this server.</summary>
    public bool IsEncrypted { get; internal set; }

    /// <summary>Client certificate, when TLS client auth was requested and provided.</summary>
    public X509Certificate2? ClientCertificate { get; internal set; }

    /// <summary>
    /// True when the connection arrived through a tunnel rather than a local socket. Handy for
    /// deciding whether to trust forwarded headers.
    /// </summary>
    public bool IsTunneled { get; internal set; }

    /// <summary>
    /// The PROXY protocol header this connection opened with, when its endpoint accepts one and a
    /// trusted balancer sent it. The client address it named is already in
    /// <see cref="RemoteIpAddress"/>/<see cref="RemotePort"/>; this carries the rest — the balancer's
    /// own address, the address the client dialled, and any v2 TLVs.
    /// </summary>
    public ProxyProtocolInfo? ProxyProtocol { get; internal set; }

    internal void Reset()
    {
        this.ConnectionId = string.Empty;
        this.RemoteIpAddress = null;
        this.RemotePort = 0;
        this.LocalIpAddress = null;
        this.LocalPort = 0;
        this.IsEncrypted = false;
        this.ClientCertificate = null;
        this.IsTunneled = false;
        this.ProxyProtocol = null;
    }
}
