using System.Net;
using System.Net.Sockets;

namespace Shiny.Net.HttpServer.Security;

/// <summary>
/// <see cref="HostFilteringOptions"/> compiled into something that answers per request without
/// re-reading the list. Built once, when the middleware is.
/// </summary>
sealed class HostMatcher
{
    readonly bool allowAll;
    readonly bool allowEmpty;
    readonly bool allowLoopback;
    readonly bool allowIpAddresses;
    readonly HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
    readonly List<string> suffixes = [];
    readonly HashSet<IPAddress> addresses = [];
    readonly Func<string?>[] publicUrls;

    public HostMatcher(HostFilteringOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        this.allowEmpty = options.AllowEmptyHosts;
        this.allowLoopback = options.AllowLoopbackHosts;
        this.allowIpAddresses = options.AllowIpAddressHosts;
        this.publicUrls = [.. options.PublicUrls];

        foreach (var raw in options.AllowedHosts)
        {
            var entry = raw?.Trim();
            if (entry is not { Length: > 0 })
                throw new ArgumentException("AllowedHosts contains an empty entry.", nameof(options));

            if (entry == "*")
            {
                this.allowAll = true;
                continue;
            }

            if (entry.StartsWith("*.", StringComparison.Ordinal))
            {
                if (!TryParse(entry[2..], out var suffix, out var suffixAddress) || suffixAddress is not null)
                    throw Malformed(entry);

                this.suffixes.Add("." + suffix);
                continue;
            }

            // An unbracketed IPv6 address is not a valid Host, but it is what people type into a list.
            if (IPAddress.TryParse(entry, out var bare) && bare.AddressFamily == AddressFamily.InterNetworkV6 && !entry.StartsWith('['))
            {
                this.addresses.Add(bare);
                continue;
            }

            if (!TryParse(entry, out var name, out var address))
                throw Malformed(entry);

            if (address is not null)
                this.addresses.Add(address);
            else
                this.names.Add(name);
        }
    }

    public bool AllowsEverything => this.allowAll;

    /// <summary>Whether the server answers to <paramref name="host"/>, a <c>Host</c> header value.</summary>
    public bool IsAllowed(string? host)
    {
        if (this.allowAll)
            return true;

        if (host is null || host.AsSpan().Trim().IsEmpty)
            return this.allowEmpty;

        if (!TryParse(host, out var name, out var address))
            return false;

        if (address is not null)
        {
            if (this.allowIpAddresses || this.addresses.Contains(address))
                return true;

            if (this.allowLoopback && IPAddress.IsLoopback(address))
                return true;
        }
        else
        {
            if (this.names.Contains(name))
                return true;

            foreach (var suffix in this.suffixes)
            {
                if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            if (this.allowLoopback && IsLoopbackName(name))
                return true;
        }

        return this.MatchesPublicUrl(name, address);
    }

    bool MatchesPublicUrl(string name, IPAddress? address)
    {
        foreach (var source in this.publicUrls)
        {
            if (source() is not { Length: > 0 } url || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
                continue;

            if (!TryParse(uri.Authority, out var publicName, out var publicAddress))
                continue;

            if (address is not null ? address.Equals(publicAddress) : string.Equals(name, publicName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    static bool IsLoopbackName(string name)
        => name.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);

    static ArgumentException Malformed(string entry)
        => new($"AllowedHosts entry '{entry}' is not a host name, a '*.domain' wildcard, an IP address or '*'.");

    /// <summary>
    /// Splits a <c>Host</c> value (RFC 9110 §7.2: <c>uri-host [ ":" port ]</c>) into a name or an
    /// address, dropping the port. Anything that is not a well-formed host fails, so a
    /// header an attacker bent out of shape is refused rather than matched loosely.
    /// </summary>
    internal static bool TryParse(string value, out string name, out IPAddress? address)
    {
        name = string.Empty;
        address = null;

        var span = value.AsSpan().Trim();
        if (span.IsEmpty)
            return false;

        ReadOnlySpan<char> host;
        ReadOnlySpan<char> port;

        if (span[0] == '[')
        {
            var close = span.IndexOf(']');
            if (close < 0)
                return false;

            host = span[1..close];
            var rest = span[(close + 1)..];

            if (!rest.IsEmpty && rest[0] != ':')
                return false;

            port = rest.IsEmpty ? default : rest[1..];

            if (!IPAddress.TryParse(host, out var v6) || v6.AddressFamily != AddressFamily.InterNetworkV6)
                return false;

            if (!IsPort(port))
                return false;

            address = v6;
            name = v6.ToString();
            return true;
        }

        var colon = span.IndexOf(':');
        if (colon >= 0)
        {
            // A second colon is an unbracketed IPv6 address, which RFC 3986 does not allow in a Host.
            if (span[(colon + 1)..].Contains(':'))
                return false;

            host = span[..colon];
            port = span[(colon + 1)..];

            if (!IsPort(port))
                return false;
        }
        else
        {
            host = span;
        }

        // "example.com." is the same name, fully qualified.
        if (host.Length > 1 && host[^1] == '.')
            host = host[..^1];

        if (host.IsEmpty || !IsRegName(host))
            return false;

        // Only the canonical dotted quad counts as an address. IPAddress.TryParse also accepts "1",
        // "0x7f.1" and friends, which a browser would have canonicalised before sending — and which,
        // arriving raw, are a name that merely looks numeric.
        if (IPAddress.TryParse(host, out var v4) && v4.AddressFamily == AddressFamily.InterNetwork)
        {
            if (!host.SequenceEqual(v4.ToString()))
                return false;

            address = v4;
            name = v4.ToString();
            return true;
        }

        name = host.ToString();
        return true;
    }

    static bool IsPort(ReadOnlySpan<char> port)
    {
        // An empty port after the colon is legal (RFC 3986 §3.2.3) and means the default.
        if (port.Length > 5)
            return false;

        var value = 0;
        foreach (var c in port)
        {
            if (c is < '0' or > '9')
                return false;

            value = (value * 10) + (c - '0');
        }

        return value <= ushort.MaxValue;
    }

    static bool IsRegName(ReadOnlySpan<char> host)
    {
        // Letters, digits, '-', '.' and '_' (which real networks use, despite RFC 952). Nothing that
        // needs percent-encoding: no browser sends it, so only something hand-crafting a header would.
        foreach (var c in host)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_'))
                return false;
        }

        return true;
    }
}
