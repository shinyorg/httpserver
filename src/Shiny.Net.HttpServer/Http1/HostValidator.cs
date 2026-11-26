namespace Shiny.Net.HttpServer.Http1;

/// <summary>
/// Checks a Host header or an absolute-form authority against RFC 3986's <c>host [ ":" port ]</c>.
/// <para>
/// Not a full URI parser — just enough that what reaches <see cref="HttpRequest.Host"/> is a host
/// and not a path, a second header, or something with spaces in it that a virtual-host check,
/// a redirect or a generated link would carry somewhere it should not go.
/// </para>
/// </summary>
static class HostValidator
{
    public static bool IsValid(string host)
    {
        // An empty Host is legal: it is what a request for a URI with no authority sends.
        if (host.Length == 0)
            return true;

        Span<byte> bytes = host.Length <= 256 ? stackalloc byte[host.Length] : new byte[host.Length];
        for (var i = 0; i < host.Length; i++)
        {
            // Anything outside ASCII is not a valid host character at all; IDNs travel as punycode.
            if (host[i] > 0x7F)
                return false;

            bytes[i] = (byte)host[i];
        }

        return IsValid(bytes);
    }

    public static bool IsValid(ReadOnlySpan<byte> host)
    {
        if (host.IsEmpty)
            return true;

        ReadOnlySpan<byte> port;

        if (host[0] == (byte)'[')
        {
            // IP-literal: "[" IPv6 / IPvFuture "]", optionally followed by a port.
            var close = host.IndexOf((byte)']');
            if (close < 2)
                return false;

            foreach (var b in host[1..close])
            {
                if (!IsHex(b) && b is not ((byte)':' or (byte)'.') && !IsRegNameChar(b))
                    return false;
            }

            var after = host[(close + 1)..];
            if (after.IsEmpty)
                return true;

            if (after[0] != (byte)':')
                return false;

            port = after[1..];
        }
        else
        {
            var colon = host.IndexOf((byte)':');
            var name = colon < 0 ? host : host[..colon];
            port = colon < 0 ? default : host[(colon + 1)..];

            if (name.IsEmpty)
                return false;

            foreach (var b in name)
            {
                if (!IsRegNameChar(b))
                    return false;
            }
        }

        // An empty port after the colon is allowed by the grammar; anything else must be digits.
        foreach (var b in port)
        {
            if (b is < (byte)'0' or > (byte)'9')
                return false;
        }
        return true;
    }

    // reg-name = *( unreserved / pct-encoded / sub-delims )
    static bool IsRegNameChar(byte b)
        => b is (>= (byte)'a' and <= (byte)'z') or (>= (byte)'A' and <= (byte)'Z') or (>= (byte)'0' and <= (byte)'9')
            or (byte)'-' or (byte)'.' or (byte)'_' or (byte)'~' or (byte)'%'
            or (byte)'!' or (byte)'$' or (byte)'&' or (byte)'\'' or (byte)'(' or (byte)')'
            or (byte)'*' or (byte)'+' or (byte)',' or (byte)';' or (byte)'=';

    static bool IsHex(byte b)
        => b is (>= (byte)'0' and <= (byte)'9') or (>= (byte)'a' and <= (byte)'f') or (>= (byte)'A' and <= (byte)'F');
}
