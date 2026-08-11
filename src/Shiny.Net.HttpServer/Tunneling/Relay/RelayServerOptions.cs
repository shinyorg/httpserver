using System.Net;

namespace Shiny.Net.HttpServer.Tunneling;

/// <summary>Configuration for the relay: where clients register, where the public arrives.</summary>
public sealed class RelayServerOptions
{
    /// <summary>Interface for both listeners. Defaults to loopback so an unconfigured relay is not public.</summary>
    public IPAddress Address { get; set; } = IPAddress.Loopback;

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
    /// null to refuse. The default accepts any token matching <see cref="Token"/> and grants the
    /// requested subdomain when it is free.
    /// </summary>
    public Func<TunnelRegistrationRequest, string?>? Authorize { get; set; }

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
}

/// <summary>What a client asked for when it registered.</summary>
public sealed class TunnelRegistrationRequest(string? token, string? requestedSubdomain, EndPoint? remoteEndPoint)
{
    public string? Token { get; } = token;

    public string? RequestedSubdomain { get; } = requestedSubdomain;

    public EndPoint? RemoteEndPoint { get; } = remoteEndPoint;
}
