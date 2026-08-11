using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Shiny.Net.HttpServer.Security;

/// <summary>
/// A single address or a CIDR block — <c>10.0.0.7</c>, <c>192.168.0.0/16</c>, <c>2001:db8::/32</c>.
/// <para>
/// Written rather than taken from <c>System.Net.IPNetwork</c> because that type rejects a prefix
/// whose host bits are set: <c>10.0.0.5/8</c> throws, and that is exactly what people type into a
/// whitelist. Here the host bits are masked off and the range means what the author meant.
/// </para>
/// <para>
/// IPv4-mapped IPv6 (<c>::ffff:127.0.0.1</c>) is unmapped on both sides before comparing, so a rule
/// written as <c>127.0.0.0/8</c> still matches a client that arrived over a dual-stack socket.
/// </para>
/// </summary>
public sealed class IpAddressRange
{
    // Masked at construction: everything below the prefix is zeroed, so Contains is a straight
    // bit comparison with nothing to normalise per request.
    readonly byte[] network;

    public IpAddressRange(IPAddress address, int prefixLength)
    {
        ArgumentNullException.ThrowIfNull(address);

        var normalized = Normalize(address);
        var bits = normalized.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;

        if (prefixLength < 0 || prefixLength > bits)
            throw new ArgumentOutOfRangeException(
                nameof(prefixLength),
                prefixLength,
                $"A prefix length for {normalized.AddressFamily} must be between 0 and {bits}."
            );

        this.PrefixLength = prefixLength;
        this.network = Mask(normalized.GetAddressBytes(), prefixLength);

        // Reported as the masked network rather than what was typed, so ToString names the range
        // that is actually being matched.
        this.BaseAddress = new IPAddress(this.network);
    }

    /// <summary>A range covering exactly one address.</summary>
    public IpAddressRange(IPAddress address)
        : this(address, Normalize(address).AddressFamily == AddressFamily.InterNetwork ? 32 : 128)
    {
    }

    /// <summary>The network address, with host bits already masked off.</summary>
    public IPAddress BaseAddress { get; }

    public int PrefixLength { get; }

    public AddressFamily AddressFamily => this.BaseAddress.AddressFamily;

    /// <summary>Parses <c>address</c> or <c>address/prefix</c>. Throws on anything else.</summary>
    public static IpAddressRange Parse(string value)
        => TryParse(value, out var range)
            ? range
            : throw new FormatException(
                $"'{value}' is not an IP address or CIDR range. Expected something like " +
                "'10.0.0.1', '192.168.0.0/16' or '2001:db8::/32'."
            );

    public static bool TryParse(string? value, [NotNullWhen(true)] out IpAddressRange? range)
    {
        range = null;

        if (string.IsNullOrWhiteSpace(value))
            return false;

        var text = value.AsSpan().Trim();
        var slash = text.IndexOf('/');

        var addressText = slash < 0 ? text : text[..slash].Trim();
        if (!IPAddress.TryParse(addressText, out var address))
            return false;

        var normalized = Normalize(address);
        var bits = normalized.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        var prefixLength = bits;

        if (slash >= 0)
        {
            var prefixText = text[(slash + 1)..].Trim();
            if (!int.TryParse(prefixText, NumberStyles.None, CultureInfo.InvariantCulture, out prefixLength))
                return false;

            if (prefixLength < 0 || prefixLength > bits)
                return false;
        }

        range = new IpAddressRange(normalized, prefixLength);
        return true;
    }

    /// <summary>True when <paramref name="address"/> falls inside this range.</summary>
    public bool Contains(IPAddress? address)
    {
        if (address is null)
            return false;

        var normalized = Normalize(address);
        if (normalized.AddressFamily != this.AddressFamily)
            return false;

        Span<byte> bytes = stackalloc byte[16];
        if (!normalized.TryWriteBytes(bytes, out var written) || written != this.network.Length)
            return false;

        var wholeBytes = this.PrefixLength / 8;
        for (var i = 0; i < wholeBytes; i++)
        {
            if (bytes[i] != this.network[i])
                return false;
        }

        var remainingBits = this.PrefixLength % 8;
        if (remainingBits == 0)
            return true;

        var mask = (byte)(0xFF << (8 - remainingBits));
        return (bytes[wholeBytes] & mask) == this.network[wholeBytes];
    }

    public override string ToString() => $"{this.BaseAddress}/{this.PrefixLength}";

    /// <summary>
    /// Collapses an IPv4-mapped IPv6 address back to IPv4. A client on a dual-stack listener shows
    /// up as <c>::ffff:10.0.0.1</c>, and a rule written as <c>10.0.0.0/8</c> has to match it.
    /// </summary>
    internal static IPAddress Normalize(IPAddress address)
        => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    static byte[] Mask(byte[] bytes, int prefixLength)
    {
        var wholeBytes = prefixLength / 8;
        var remainingBits = prefixLength % 8;

        // The partially covered byte is masked before the tail is zeroed. The other order zeroes it
        // first and then masks a zero, which leaves every prefix that does not land on a byte
        // boundary — /12, /20 — matching nothing at all, while /8 and /24 look fine.
        if (remainingBits != 0 && wholeBytes < bytes.Length)
        {
            bytes[wholeBytes] &= (byte)(0xFF << (8 - remainingBits));
            wholeBytes++;
        }

        for (var i = wholeBytes; i < bytes.Length; i++)
            bytes[i] = 0;

        return bytes;
    }

    // ---- The ranges every filter ends up naming ----

    /// <summary>127.0.0.0/8 — IPv4 loopback.</summary>
    public static IpAddressRange Loopback { get; } = Parse("127.0.0.0/8");

    /// <summary>::1/128 — IPv6 loopback.</summary>
    public static IpAddressRange LoopbackV6 { get; } = Parse("::1/128");

    /// <summary>10.0.0.0/8 (RFC 1918).</summary>
    public static IpAddressRange PrivateClassA { get; } = Parse("10.0.0.0/8");

    /// <summary>172.16.0.0/12 (RFC 1918).</summary>
    public static IpAddressRange PrivateClassB { get; } = Parse("172.16.0.0/12");

    /// <summary>192.168.0.0/16 (RFC 1918).</summary>
    public static IpAddressRange PrivateClassC { get; } = Parse("192.168.0.0/16");

    /// <summary>169.254.0.0/16 — IPv4 link-local.</summary>
    public static IpAddressRange LinkLocal { get; } = Parse("169.254.0.0/16");

    /// <summary>fc00::/7 — IPv6 unique local addresses.</summary>
    public static IpAddressRange UniqueLocalV6 { get; } = Parse("fc00::/7");

    /// <summary>fe80::/10 — IPv6 link-local.</summary>
    public static IpAddressRange LinkLocalV6 { get; } = Parse("fe80::/10");
}
