using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Shiny.Net.HttpServer;

/// <summary>The addresses this machine can currently be reached on.</summary>
public static class LocalAddresses
{
    /// <summary>
    /// Every non-loopback unicast address on an interface that is up.
    /// <para>
    /// What a phone needs to answer "what URL do I tell the other device to open". Ordered so IPv4
    /// comes first, because that is still what a person types and what a QR code is scanned into.
    /// </para>
    /// </summary>
    public static IReadOnlyList<IPAddress> Current(bool includeIPv6 = false)
    {
        var addresses = new List<IPAddress>();

        NetworkInterface[] interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            // Sandboxed platforms can refuse the enumeration outright. An empty list is a better
            // answer than an exception from a property that is only ever informational.
            return [];
        }

        foreach (var adapter in interfaces)
        {
            if (adapter.OperationalStatus != OperationalStatus.Up
                || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
            {
                var address = unicast.Address;

                if (IPAddress.IsLoopback(address))
                    continue;

                if (address.AddressFamily == AddressFamily.InterNetwork)
                {
                    addresses.Add(address);
                }
                else if (includeIPv6 && address.AddressFamily == AddressFamily.InterNetworkV6 && !address.IsIPv6LinkLocal)
                {
                    addresses.Add(address);
                }
            }
        }

        return addresses;
    }

    /// <summary>A stable signature of the current addresses, for spotting that they changed.</summary>
    internal static string Signature()
    {
        var addresses = Current(includeIPv6: true).Select(x => x.ToString()).ToList();
        addresses.Sort(StringComparer.Ordinal);

        return string.Join(',', addresses);
    }
}

/// <summary>
/// Watches for the machine's addresses changing and tells the server about it.
/// <para>
/// The case this exists for is a phone. A listener bound to <c>192.168.1.40</c> because that was
/// the Wi-Fi address stops working the moment the device joins another network, switches to a
/// hotspot, or drops to cellular — the socket stays open on an address that no longer exists, and
/// nothing fails loudly. On a server this never happens, which is why no server framework does
/// this and why an embedded one has to.
/// </para>
/// <para>
/// Changes arrive in bursts — an interface goes down, comes up, acquires an address, acquires a
/// second one — so they are debounced, and the addresses are compared before anything is restarted.
/// </para>
/// </summary>
sealed class NetworkChangeWatcher : IDisposable
{
    readonly Func<CancellationToken, Task> onChanged;
    readonly TimeSpan debounce;
    readonly ILogger logger;
    readonly CancellationTokenSource stopped = new();

    string signature = LocalAddresses.Signature();
    Task? pending;
    int disposed;

    public NetworkChangeWatcher(Func<CancellationToken, Task> onChanged, TimeSpan debounce, ILogger logger)
    {
        this.onChanged = onChanged;
        this.debounce = debounce;
        this.logger = logger;

        NetworkChange.NetworkAddressChanged += this.OnNetworkAddressChanged;
    }

    void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        if (this.disposed != 0 || this.pending is { IsCompleted: false })
            return;

        this.pending = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(this.debounce, this.stopped.Token).ConfigureAwait(false);

                var current = LocalAddresses.Signature();
                if (current == this.signature)
                    return;

                this.logger.LogInformation("Network addresses changed: {Addresses}", current.Length == 0 ? "(none)" : current);
                this.signature = current;

                await this.onChanged(this.stopped.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The server stopped while the change was settling.
            }
            catch (Exception ex)
            {
                // Never allowed to escape: this runs on a thread nobody is awaiting, and an
                // unobserved exception here would take the process down.
                this.logger.LogError(ex, "Failed to react to a network address change");
            }
        }, CancellationToken.None);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) != 0)
            return;

        NetworkChange.NetworkAddressChanged -= this.OnNetworkAddressChanged;

        this.stopped.Cancel();
        this.stopped.Dispose();
    }
}
