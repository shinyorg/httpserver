using System.Net;

namespace Shiny.Net.HttpServer.Security;

/// <summary>
/// Who may reach the server, by address.
/// <para>
/// Two rules, in this order: a denied range always loses, and once an allow list exists nothing
/// outside it gets in. A policy with only <c>Deny</c> entries is a blacklist; add a single
/// <c>Allow</c> and it becomes a whitelist, because an allow list that let unlisted addresses
/// through would not be one.
/// </para>
/// <para>
/// The address checked is <c>ctx.Connection.RemoteIpAddress</c> — the peer that actually opened the
/// socket, unless the server was configured with
/// <see cref="HttpServerOptions.UseForwardedHeaders"/>. That opt-in stays where it is on purpose:
/// an IP filter that read <c>X-Forwarded-For</c> by default could be walked straight past with one
/// header.
/// </para>
/// </summary>
public sealed class IpFilterPolicy
{
    readonly IpAddressRange[] allowed;
    readonly IpAddressRange[] denied;

    internal IpFilterPolicy(IpAddressRange[] allowed, IpAddressRange[] denied, bool allowUnknownAddress)
    {
        this.allowed = allowed;
        this.denied = denied;
        this.AllowUnknownAddress = allowUnknownAddress;
    }

    public IReadOnlyList<IpAddressRange> Allowed => this.allowed;

    public IReadOnlyList<IpAddressRange> Denied => this.denied;

    /// <summary>
    /// What to do when there is no remote address at all — an in-memory transport, or a tunnel that
    /// does not report one. False (the default) fails closed, because a whitelist that cannot see
    /// the caller has not established anything about them.
    /// </summary>
    public bool AllowUnknownAddress { get; }

    /// <summary>True when this policy is a whitelist rather than a blacklist.</summary>
    public bool IsWhitelist => this.allowed.Length > 0;

    public bool IsAllowed(IPAddress? address)
    {
        if (address is null)
            return this.AllowUnknownAddress;

        foreach (var range in this.denied)
        {
            if (range.Contains(address))
                return false;
        }

        if (this.allowed.Length == 0)
            return true;

        foreach (var range in this.allowed)
        {
            if (range.Contains(address))
                return true;
        }

        return false;
    }

    /// <summary>Builds a policy inline, without going through <see cref="IpFilterOptions"/>.</summary>
    public static IpFilterPolicy Create(Action<IpFilterPolicyBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new IpFilterPolicyBuilder();
        configure(builder);

        return builder.Build();
    }
}

/// <summary>
/// Assembles an <see cref="IpFilterPolicy"/>.
/// <code>
/// var lanOnly = IpFilterPolicy.Create(p => p
///     .AllowLoopback()
///     .AllowPrivateNetworks()
///     .Deny("192.168.4.0/24"));      // guest VLAN
/// </code>
/// </summary>
public sealed class IpFilterPolicyBuilder
{
    readonly List<IpAddressRange> allowed = [];
    readonly List<IpAddressRange> denied = [];
    bool allowUnknownAddress;

    /// <summary>
    /// Adds allowed ranges. Each is an address or CIDR block: <c>10.0.0.4</c>, <c>10.0.0.0/8</c>,
    /// <c>2001:db8::/32</c>. Adding any turns the policy into a whitelist.
    /// </summary>
    public IpFilterPolicyBuilder Allow(params string[] ranges)
    {
        ArgumentNullException.ThrowIfNull(ranges);

        foreach (var range in ranges)
            this.allowed.Add(IpAddressRange.Parse(range));

        return this;
    }

    public IpFilterPolicyBuilder Allow(params IpAddressRange[] ranges)
    {
        ArgumentNullException.ThrowIfNull(ranges);

        this.allowed.AddRange(ranges);
        return this;
    }

    /// <summary>Adds denied ranges. A denial beats every allow, including a wider one.</summary>
    public IpFilterPolicyBuilder Deny(params string[] ranges)
    {
        ArgumentNullException.ThrowIfNull(ranges);

        foreach (var range in ranges)
            this.denied.Add(IpAddressRange.Parse(range));

        return this;
    }

    public IpFilterPolicyBuilder Deny(params IpAddressRange[] ranges)
    {
        ArgumentNullException.ThrowIfNull(ranges);

        this.denied.AddRange(ranges);
        return this;
    }

    /// <summary>Allows 127.0.0.0/8 and ::1 — the machine the server is running on.</summary>
    public IpFilterPolicyBuilder AllowLoopback()
        => this.Allow(IpAddressRange.Loopback, IpAddressRange.LoopbackV6);

    /// <summary>
    /// Allows the RFC 1918 blocks plus IPv6 unique-local and both link-local ranges — "anything on
    /// my network", which is what an embedded server shared over Wi-Fi usually means.
    /// </summary>
    public IpFilterPolicyBuilder AllowPrivateNetworks()
        => this.Allow(
            IpAddressRange.PrivateClassA,
            IpAddressRange.PrivateClassB,
            IpAddressRange.PrivateClassC,
            IpAddressRange.LinkLocal,
            IpAddressRange.UniqueLocalV6,
            IpAddressRange.LinkLocalV6
        );

    /// <summary>
    /// Lets a request through when the transport reports no remote address. Off by default; turn it
    /// on only when the server is deliberately served over something without one, such as an
    /// in-process transport.
    /// </summary>
    public IpFilterPolicyBuilder AllowUnknownAddress(bool allow = true)
    {
        this.allowUnknownAddress = allow;
        return this;
    }

    public IpFilterPolicy Build() => new([.. this.allowed], [.. this.denied], this.allowUnknownAddress);
}
