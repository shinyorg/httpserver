using System.Net;

namespace Shiny.Net.HttpServer.Mobile;

/// <summary>
/// The part of the connectivity-change decision that does not need a device.
/// </summary>
/// <remarks>
/// Outside the <c>PLATFORM</c> guard the rest of the lifecycle lives behind, so it compiles - and is
/// tested - on the base target framework. What it decides is whether a rebind could accomplish
/// anything at all, which is worth being sure about: the answer is what stands between a phone on a
/// changing network and a server that restarts for no reason.
/// </remarks>
static class ConnectivityRebindDecision
{
    /// <summary>
    /// Whether any endpoint is bound to an address a network change can invalidate.
    /// </summary>
    /// <remarks>
    /// A listener on <see cref="IPAddress.Any"/> is bound to every interface and already serves an
    /// address the device has only just acquired - rebinding it drops live connections to
    /// accomplish nothing. A loopback listener has no network to lose at all. What needs the rebind
    /// is a bind to one specific routable address, which is the socket left open on an address the
    /// device no longer holds.
    /// </remarks>
    public static bool AnyEndpointCanGoStale(IEnumerable<HttpServerEndpoint> endpoints)
    {
        foreach (var endpoint in endpoints)
        {
            var address = endpoint.Address;

            if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
                continue;

            if (IPAddress.IsLoopback(address))
                continue;

            return true;
        }

        return false;
    }
}
