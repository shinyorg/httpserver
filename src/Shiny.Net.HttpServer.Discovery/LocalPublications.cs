using System.Runtime.CompilerServices;
using Shiny.Net.Discovery;

namespace Shiny.Net.HttpServer.Discovery;

/// <summary>
/// The advertisements this app has live on one <see cref="IMdnsManager"/>, so a locator browsing through the same
/// manager can leave them out. An app that both hosts and browses — every peer-to-peer app — otherwise finds itself.
/// <para>
/// Keyed on the manager rather than held globally: "this app" is whoever shares the responder, and two managers in one
/// process (tests, or an app deliberately talking to itself) stay strangers. The match is exact, because mDNS keeps a
/// service type plus instance name unique on the link — the responder renames ours on a conflict, and the publication
/// is read at match time so the settled name is the one compared.
/// </para>
/// </summary>
sealed class LocalPublications
{
    static readonly ConditionalWeakTable<IMdnsManager, LocalPublications> registries = new();

    readonly Lock sync = new();
    readonly List<IMdnsPublication> live = [];


    public static LocalPublications For(IMdnsManager mdns) => registries.GetValue(mdns, _ => new LocalPublications());


    public void Add(IMdnsPublication publication)
    {
        lock (this.sync)
            this.live.Add(publication);
    }


    public void Remove(IMdnsPublication publication)
    {
        lock (this.sync)
            this.live.Remove(publication);
    }


    public bool Contains(MdnsService service)
    {
        var type = Normalize(service.ServiceType);
        lock (this.sync)
        {
            foreach (var publication in this.live)
            {
                if (String.Equals(Normalize(publication.ServiceType), type, StringComparison.OrdinalIgnoreCase) &&
                    String.Equals(publication.InstanceName, service.InstanceName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }


    // "_myapp._tcp", "_myapp._tcp." and "_myapp._tcp.local." are the same type depending on which platform said it
    static string Normalize(string serviceType)
    {
        var type = serviceType.TrimEnd('.');
        return type.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ? type[..^".local".Length] : type;
    }
}
