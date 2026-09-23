using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Shiny.Net.HttpServer.CommandLine;


/// <summary>The addresses a running server can be reached at, worked out from its settings.</summary>
public static class ServerUrls
{
    /// <summary>Every address worth listing: localhost and each LAN address when bound to all of them.</summary>
    public static IEnumerable<string> All(ServeSettings settings)
    {
        if (!IsWildcard(settings.Address))
        {
            yield return Url(settings, Host(settings.Address));
            yield break;
        }

        yield return Url(settings, "localhost");

        foreach (var address in LocalAddresses())
            yield return Url(settings, Host(address));
    }


    /// <summary>
    /// The address another device can reach - which is the point of a QR code - and null when there
    /// is no such address.
    /// </summary>
    public static string? Shareable(ServeSettings settings)
    {
        if (!IsWildcard(settings.Address))
            return IPAddress.IsLoopback(settings.Address) ? null : Url(settings, Host(settings.Address));

        var address = LocalAddresses().FirstOrDefault();
        return address == null ? null : Url(settings, Host(address));
    }


    /// <summary>The tunnel terminates at the site root, so the mount point has to be put back on.</summary>
    public static string Tunnel(string url, string prefix)
        => prefix == "/" ? url.TrimEnd('/') + "/" : url.TrimEnd('/') + prefix;


    static bool IsWildcard(IPAddress address)
        => address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any);


    static string Url(ServeSettings settings, string host)
        => $"{settings.Scheme}://{host}:{settings.Port}{settings.UrlPrefix}";


    static IEnumerable<IPAddress> LocalAddresses()
        => NetworkInterface
            .GetAllNetworkInterfaces()
            .Where(x => x.OperationalStatus == OperationalStatus.Up && x.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(x => x.GetIPProperties().UnicastAddresses)
            .Select(x => x.Address)
            .Where(x => x.AddressFamily == AddressFamily.InterNetwork);


    static string Host(IPAddress address)
        => address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
}
