using System.Net;
using System.Runtime.CompilerServices;
using Shiny.Net.Discovery;

namespace Shiny.Net.HttpServer.Discovery;

/// <summary>A server found on the local link, resolved to something a client can actually call.</summary>
/// <param name="InstanceName">The advertised name — normally the device's, as its owner named it.</param>
/// <param name="BaseAddress">A URL ready to hand to an <c>HttpClient</c>.</param>
/// <param name="Addresses">Every address the instance resolved to, in case the first is not reachable.</param>
/// <param name="Port">The advertised port.</param>
/// <param name="TxtRecords">The advertised TXT records, for whatever the app filters on.</param>
public sealed record DiscoveredHttpServer(
    string InstanceName,
    Uri BaseAddress,
    IReadOnlyList<IPAddress> Addresses,
    int Port,
    IReadOnlyDictionary<string, string> TxtRecords
)
{
    /// <summary>The same server through a different one of its addresses.</summary>
    public Uri BaseAddressFor(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        var host = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"[{address}]"
            : address.ToString();

        var scheme = this.TxtRecords.TryGetValue("scheme", out var value) ? value : "http";
        var path = this.TxtRecords.TryGetValue("path", out var advertised) ? advertised : "/";

        return new Uri($"{scheme}://{host}:{this.Port}{(path.StartsWith('/') ? path : "/" + path)}");
    }
}

/// <summary>Finds servers other devices are advertising.</summary>
public interface IHttpServerLocator
{
    /// <summary>
    /// Watches the link for servers appearing and disappearing until the token is cancelled.
    /// <para>
    /// The natural shape for a UI: bind it to a list and let devices come and go as they are turned
    /// on, walk out of range, or change networks.
    /// </para>
    /// </summary>
    IAsyncEnumerable<HttpServerDiscoveryEvent> WatchAsync(string serviceType = "_http._tcp", CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the first server found, or null if none answers within <paramref name="timeout"/>.
    /// The one-liner for "connect to my other device".
    /// </summary>
    Task<DiscoveredHttpServer?> FindFirstAsync(
        string serviceType = "_http._tcp",
        TimeSpan? timeout = null,
        Func<DiscoveredHttpServer, bool>? filter = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>Collects everything that answers within <paramref name="timeout"/>.</summary>
    Task<IReadOnlyList<DiscoveredHttpServer>> FindAllAsync(
        string serviceType = "_http._tcp",
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default
    );
}

/// <summary>Whether a server appeared or went away.</summary>
/// <param name="Found">True when it appeared, false when it went.</param>
/// <param name="Server">The server the event is about.</param>
public sealed record HttpServerDiscoveryEvent(bool Found, DiscoveredHttpServer Server);

/// <summary>Browses DNS-SD and turns what it finds into base addresses.</summary>
public sealed class HttpServerLocator(IMdnsManager mdns) : IHttpServerLocator
{
    readonly IMdnsManager mdns = mdns ?? throw new ArgumentNullException(nameof(mdns));

    public async IAsyncEnumerable<HttpServerDiscoveryEvent> WatchAsync(
        string serviceType = "_http._tcp",
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceType);

        var config = new MdnsBrowseConfig(serviceType);

        await foreach (var result in this.mdns.Browse(config, cancellationToken).ConfigureAwait(false))
        {
            // An unresolved result has no address and no port, so there is no URL to hand anyone.
            // Reported only on the way out, where the name is all a caller needs to drop it.
            if (result.Status == MdnsBrowseStatus.Lost)
            {
                yield return new HttpServerDiscoveryEvent(false, Describe(result.Service));
                continue;
            }

            if (!result.Service.IsResolved)
                continue;

            yield return new HttpServerDiscoveryEvent(true, Describe(result.Service));
        }
    }

    public async Task<DiscoveredHttpServer?> FindFirstAsync(
        string serviceType = "_http._tcp",
        TimeSpan? timeout = null,
        Func<DiscoveredHttpServer, bool>? filter = null,
        CancellationToken cancellationToken = default
    )
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(5));

        try
        {
            await foreach (var found in this.WatchAsync(serviceType, deadline.Token).ConfigureAwait(false))
            {
                if (found.Found && (filter is null || filter(found.Server)))
                    return found.Server;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The timeout, which is an answer of "nothing there" rather than a failure.
        }

        return null;
    }

    public async Task<IReadOnlyList<DiscoveredHttpServer>> FindAllAsync(
        string serviceType = "_http._tcp",
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default
    )
    {
        var found = new Dictionary<string, DiscoveredHttpServer>(StringComparer.Ordinal);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(5));

        try
        {
            await foreach (var change in this.WatchAsync(serviceType, deadline.Token).ConfigureAwait(false))
            {
                // Keyed on the name so a service that re-announces during the window — which they
                // do — is one entry rather than three.
                if (change.Found)
                    found[change.Server.InstanceName] = change.Server;
                else
                    found.Remove(change.Server.InstanceName);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The browse window closed. Whatever answered is the answer.
        }

        return [.. found.Values];
    }

    static DiscoveredHttpServer Describe(MdnsService service)
    {
        var server = new DiscoveredHttpServer(
            service.InstanceName,
            new Uri("http://0.0.0.0/"),
            service.Addresses,
            service.Port,
            service.TxtRecords
        );

        var address = service.Addresses.FirstOrDefault(x => x.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            ?? service.Addresses.FirstOrDefault();

        return address is null ? server : server with { BaseAddress = server.BaseAddressFor(address) };
    }
}
