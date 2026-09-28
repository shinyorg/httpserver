using Shiny.Net.HttpServer.Tunneling;

namespace Shiny.Net.HttpServer.Security;

/// <summary>
/// Which names the server answers to.
/// <code>
/// builder.AddHostFiltering(o => o.AllowedHosts.Add("*.local"));
///
/// var app = builder.Build();
/// app.UseHostFiltering();      // first, ahead of everything else
/// </code>
/// <para>
/// The attack this stops is DNS rebinding. A page on <c>evil.example</c> resolves its own name to
/// the attacker's server, loads, then re-points the name at <c>192.168.1.20</c> — the phone on the
/// other side of the room. The browser still thinks it is talking to <c>evil.example</c>, so the
/// same-origin policy lets the page read every response, and the server on <c>0.0.0.0</c> answers
/// because nothing told it not to. The one thing the attacker cannot change is the
/// <c>Host</c> header: it says <c>evil.example</c>, and a server that knows its own names turns it away.
/// </para>
/// <para>
/// Shaped after ASP.NET Core's <c>HostFilteringOptions</c>, with one deliberate difference: an empty
/// <see cref="AllowedHosts"/> is not "allow everything". The defaults below already admit every name
/// a rebinding attack cannot produce, so <c>AddHostFiltering()</c> with no configuration at all is
/// the protection a LAN server wants. Add <c>"*"</c> to switch the check off.
/// </para>
/// </summary>
public sealed class HostFilteringOptions
{
    readonly List<Func<string?>> publicUrls = [];

    /// <summary>
    /// Names the server answers to, beyond the ones the switches below already admit. Each entry is
    /// one of:
    /// <list type="bullet">
    /// <item><c>device.example.com</c> — that name, in any case.</item>
    /// <item><c>*.example.com</c> — any subdomain of it, at any depth, but not <c>example.com</c> itself.</item>
    /// <item><c>192.168.1.20</c>, <c>[fe80::1]</c> or <c>fe80::1</c> — that address.</item>
    /// <item><c>*</c> — anything; the filter lets every request through.</item>
    /// </list>
    /// Ports are ignored on both sides, so <c>example.com:8443</c> as an entry means
    /// <c>example.com</c>. A malformed entry fails when the middleware is built, not at the first request.
    /// </summary>
    public IList<string> AllowedHosts { get; set; } = new List<string>();

    /// <summary>
    /// Whether a request with no host at all passes — an HTTP/1.0 request, which predates the
    /// <c>Host</c> header, or an HTTP/2 request without <c>:authority</c>. True by default, as in
    /// ASP.NET Core: no browser sends one, so it cannot be a rebinding attack. (An HTTP/1.1 request
    /// without a <c>Host</c> header never gets this far; the parser has already refused it.)
    /// </summary>
    public bool AllowEmptyHosts { get; set; } = true;

    /// <summary>
    /// <c>localhost</c>, any <c>*.localhost</c> name, <c>127.0.0.0/8</c> and <c>::1</c>. True by
    /// default. Browsers resolve these names to loopback themselves, never asking DNS, so no
    /// attacker can rebind them — and they are what a tunnel agent such as <c>cloudflared</c> uses
    /// on the hop from itself to the server.
    /// </summary>
    public bool AllowLoopbackHosts { get; set; } = true;

    /// <summary>
    /// Any IP-address literal — <c>http://192.168.1.20:8080</c>, <c>http://[fe80::1]/</c>. True by
    /// default.
    /// <para>
    /// Rebinding needs a name to rebind: a browser sends an address literal as the <c>Host</c> only
    /// when the page itself was loaded from that address, and then there was no DNS to lie with.
    /// Admitting them keeps the server reachable by the addresses a phone actually has — which
    /// change with every network it joins — and by what <c>Shiny.Net.HttpServer.Discovery</c>
    /// resolves to. Turn it off, and list the addresses in <see cref="AllowedHosts"/>, only when the
    /// <c>Host</c> header is also trusted for something else, such as building absolute URLs.
    /// </para>
    /// </summary>
    public bool AllowIpAddressHosts { get; set; } = true;

    /// <summary>
    /// Lets every request that arrived through an <see cref="ITunnelProvider"/> (the relay, SSH,
    /// Azure Relay) through without looking at its host. True by default.
    /// <para>
    /// A tunnel's front door is already on the public internet, so a browser tricked into visiting
    /// it gains the attacker nothing they could not do by visiting it themselves; what rebinding
    /// reaches is the private side, and a tunnelled connection did not come from there. Its host is
    /// whatever the tunnel's edge was addressed as, which the app often does not know in advance.
    /// Set it to false to hold tunnelled requests to the same list, with <see cref="AllowTunnel"/>
    /// or <see cref="AllowPublicUrl"/> admitting the tunnel's own name.
    /// </para>
    /// </summary>
    public bool AllowTunneledConnections { get; set; } = true;

    /// <summary>
    /// Whether a rejection says which host was refused, in the problem details' <c>detail</c>. True
    /// by default, which is what makes a misconfigured list easy to diagnose; the value is only ever
    /// echoed back to the client that sent it.
    /// </summary>
    public bool IncludeFailureMessage { get; set; } = true;

    /// <summary>
    /// Admits the host of a public URL that is only known — or only stable — at run time: a tunnel
    /// agent's address, which changes each time a quick tunnel restarts.
    /// <code>
    /// o.AllowPublicUrl(() => cloudflared.PublicUrl);
    /// </code>
    /// The delegate is asked on every request whose host nothing else admitted, so it should be
    /// cheap; a null or unparseable URL admits nothing.
    /// </summary>
    public HostFilteringOptions AllowPublicUrl(Func<string?> publicUrl)
    {
        ArgumentNullException.ThrowIfNull(publicUrl);

        this.publicUrls.Add(publicUrl);
        return this;
    }

    /// <summary>
    /// Admits whatever host the tunnel's <see cref="ITunnelProvider.PublicUrl"/> names, as it is
    /// now — so an SSH tunnel that reconnects to a new address keeps working. Only needed with
    /// <see cref="AllowTunneledConnections"/> off, or when the same name also reaches the server
    /// directly.
    /// </summary>
    public HostFilteringOptions AllowTunnel(ITunnelProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return this.AllowPublicUrl(() => provider.PublicUrl);
    }

    internal IReadOnlyList<Func<string?>> PublicUrls => this.publicUrls;
}
