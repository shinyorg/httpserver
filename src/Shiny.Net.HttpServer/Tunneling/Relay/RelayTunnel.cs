using System.Net;

namespace Shiny.Net.HttpServer.Tunneling;

/// <summary>A tunnel registered with a <see cref="RelayServer"/>, as it was when it connected.</summary>
public sealed class RelayTunnel(
    string host,
    string publicUrl,
    EndPoint? remoteEndPoint,
    DateTimeOffset connectedAt,
    object? state
)
{
    /// <summary>The host this tunnel answers for, lowercased and without a port.</summary>
    public string Host { get; } = host;

    /// <summary>The public URL the client was handed.</summary>
    public string PublicUrl { get; } = publicUrl;

    /// <summary>Where the client's control connection came from.</summary>
    public EndPoint? RemoteEndPoint { get; } = remoteEndPoint;

    public DateTimeOffset ConnectedAt { get; } = connectedAt;

    /// <summary>
    /// Whatever <see cref="RelayServerOptions.Authorize"/> put in
    /// <see cref="TunnelRegistrationRequest.State"/> — typically the id of the key that registered it,
    /// so every tunnel a key holds can be found, and closed, when the key is revoked.
    /// </summary>
    public object? State { get; } = state;
}

/// <summary>Why a tunnel stopped being registered.</summary>
public enum RelayTunnelCloseReason
{
    /// <summary>The client's control connection ended: it closed it, or the network dropped it.</summary>
    ConnectionClosed,

    /// <summary>Closed by <see cref="RelayServer.DisconnectAsync(string)"/>.</summary>
    Disconnected,

    /// <summary>The relay stopped.</summary>
    RelayStopped
}

public sealed class RelayTunnelEventArgs(RelayTunnel tunnel) : EventArgs
{
    public RelayTunnel Tunnel { get; } = tunnel;
}

public sealed class RelayTunnelDisconnectedEventArgs(RelayTunnel tunnel, RelayTunnelCloseReason reason) : EventArgs
{
    public RelayTunnel Tunnel { get; } = tunnel;

    public RelayTunnelCloseReason Reason { get; } = reason;
}
