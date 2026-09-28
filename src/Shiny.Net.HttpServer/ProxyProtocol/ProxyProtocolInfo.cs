using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Shiny.Net.HttpServer;

/// <summary>What the PROXY header's command said the connection is.</summary>
public enum ProxyProtocolCommand
{
    /// <summary>
    /// The balancer opened this connection on its own behalf — a health check, typically. There is
    /// no client to report, so the connection's real endpoints are kept.
    /// </summary>
    Local,

    /// <summary>The connection is relayed for a client, whose addresses (if any) the header carries.</summary>
    Proxy
}

/// <summary>
/// Everything a PROXY protocol header said about a connection, surfaced as
/// <see cref="ConnectionInfo.ProxyProtocol"/>.
/// <para>
/// The client's address has already been applied to <see cref="ConnectionInfo.RemoteIpAddress"/>
/// and <see cref="ConnectionInfo.RemotePort"/> — nothing needs to read this to get it. This is for
/// the rest: which balancer relayed the connection, the address the client dialled, and the v2
/// TLVs (ALPN, authority, TLS details, a unique id) a balancer adds when it terminated TLS itself.
/// </para>
/// </summary>
public sealed class ProxyProtocolInfo
{
    internal ProxyProtocolInfo(int version, ProxyProtocolCommand command)
    {
        this.Version = version;
        this.Command = command;
    }

    /// <summary>1 for the text form, 2 for the binary form.</summary>
    public int Version { get; }

    public ProxyProtocolCommand Command { get; }

    /// <summary>
    /// The family the header declared: <see cref="AddressFamily.InterNetwork"/>,
    /// <see cref="AddressFamily.InterNetworkV6"/>, <see cref="AddressFamily.Unix"/>, or
    /// <see cref="AddressFamily.Unspecified"/> (v1 <c>UNKNOWN</c>, v2 <c>AF_UNSPEC</c>, or a
    /// <see cref="ProxyProtocolCommand.Local"/> connection).
    /// </summary>
    public AddressFamily AddressFamily { get; internal set; } = AddressFamily.Unspecified;

    /// <summary>
    /// The transport the client used to reach the balancer — <see cref="System.Net.Sockets.SocketType.Stream"/>
    /// or <see cref="System.Net.Sockets.SocketType.Dgram"/> — or <see cref="System.Net.Sockets.SocketType.Unknown"/> when unspecified.
    /// </summary>
    public SocketType SocketType { get; internal set; } = SocketType.Unknown;

    /// <summary>The real client, when the header carried an IP address. Already applied to the connection.</summary>
    public IPEndPoint? SourceEndPoint { get; internal set; }

    /// <summary>The address and port the client dialled — the balancer's public side.</summary>
    public IPEndPoint? DestinationEndPoint { get; internal set; }

    /// <summary>The client's socket path, when the balancer relayed a unix-domain-socket connection.</summary>
    public string? SourceUnixPath { get; internal set; }

    /// <summary>The socket path the client connected to, for a relayed unix-domain-socket connection.</summary>
    public string? DestinationUnixPath { get; internal set; }

    /// <summary>
    /// The peer that actually connected to this server and sent the header — the balancer. This is
    /// what <see cref="ConnectionInfo.RemoteIpAddress"/> would have been without the header.
    /// </summary>
    public IPEndPoint? ProxyEndPoint { get; internal set; }

    /// <summary>The protocol ALPN agreed between the client and the balancer (v2 <c>PP2_TYPE_ALPN</c>).</summary>
    public string? Alpn { get; internal set; }

    /// <summary>The host name the client asked for, usually its TLS SNI (v2 <c>PP2_TYPE_AUTHORITY</c>).</summary>
    public string? Authority { get; internal set; }

    /// <summary>
    /// An opaque id the balancer assigned the connection (v2 <c>PP2_TYPE_UNIQUE_ID</c>), up to 128
    /// bytes. Worth logging: it is what ties this server's log line to the balancer's.
    /// </summary>
    public byte[]? UniqueId { get; internal set; }

    /// <summary>What the balancer knew about the client's TLS session, when it terminated one (v2 <c>PP2_TYPE_SSL</c>).</summary>
    public ProxyProtocolSslInfo? Ssl { get; internal set; }

    /// <summary>
    /// Every v2 TLV exactly as received, in order — including the ones decoded into properties
    /// above, and vendor ones this server does not interpret (AWS's VPC endpoint id is type
    /// <c>0xEA</c>, Azure's link id <c>0xEE</c>, GCP's PSC connection id <c>0xE0</c>). Empty for v1.
    /// </summary>
    public IReadOnlyList<ProxyProtocolTlv> Tlvs { get; internal set; } = [];

    /// <summary>The first TLV of <paramref name="type"/>, or null.</summary>
    public ProxyProtocolTlv? GetTlv(byte type)
    {
        foreach (var tlv in this.Tlvs)
        {
            if (tlv.Type == type)
                return tlv;
        }

        return null;
    }
}

/// <summary>One type-length-value entry from a v2 PROXY header.</summary>
public sealed class ProxyProtocolTlv
{
    public ProxyProtocolTlv(byte type, ReadOnlyMemory<byte> value)
    {
        this.Type = type;
        this.Value = value;
    }

    public const byte Alpn = 0x01;
    public const byte Authority = 0x02;
    public const byte Crc32C = 0x03;
    public const byte Noop = 0x04;
    public const byte UniqueId = 0x05;
    public const byte Ssl = 0x20;
    public const byte NetNamespace = 0x30;

    public byte Type { get; }
    public ReadOnlyMemory<byte> Value { get; }

    /// <summary>The value as UTF-8 text — right for the text-valued types, meaningless for the rest.</summary>
    public string GetString() => Encoding.UTF8.GetString(this.Value.Span);

    public override string ToString() => $"0x{this.Type:x2} ({this.Value.Length} bytes)";
}

/// <summary>The v2 <c>PP2_TYPE_SSL</c> TLV: what a TLS-terminating balancer saw of the client's session.</summary>
public sealed class ProxyProtocolSslInfo
{
    /// <summary>The raw <c>client</c> bit field (<c>PP2_CLIENT_SSL</c> = 1, <c>CERT_CONN</c> = 2, <c>CERT_SESS</c> = 4).</summary>
    public byte ClientFlags { get; internal set; }

    /// <summary>
    /// Zero when the client presented a certificate that verified — anything else means it did not
    /// verify, or none was presented. Only meaningful alongside <see cref="HasClientCertificate"/>.
    /// </summary>
    public uint Verify { get; internal set; }

    /// <summary>The client connected to the balancer over TLS.</summary>
    public bool IsSsl => (this.ClientFlags & 0x01) != 0;

    /// <summary>The client presented a certificate on this connection or its resumed session.</summary>
    public bool HasClientCertificate => (this.ClientFlags & 0x06) != 0;

    /// <summary>The client certificate verified (it was presented and <see cref="Verify"/> is zero).</summary>
    public bool IsClientCertificateVerified => this.HasClientCertificate && this.Verify == 0;

    /// <summary>TLS version, e.g. <c>TLSv1.3</c>.</summary>
    public string? Version { get; internal set; }

    /// <summary>Common name of the client certificate's subject.</summary>
    public string? CommonName { get; internal set; }

    /// <summary>Negotiated cipher, e.g. <c>ECDHE-RSA-AES128-GCM-SHA256</c>.</summary>
    public string? Cipher { get; internal set; }

    /// <summary>Algorithm that signed the client certificate, e.g. <c>SHA256</c>.</summary>
    public string? SignatureAlgorithm { get; internal set; }

    /// <summary>Client certificate key algorithm, e.g. <c>RSA2048</c>.</summary>
    public string? KeyAlgorithm { get; internal set; }
}
