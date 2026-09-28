using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Shiny.Net.HttpServer.Transports;

enum ProxyProtocolParseStatus
{
    /// <summary>What has arrived so far is consistent with a header; read at least <c>needed</c> bytes and retry.</summary>
    Incomplete,

    /// <summary>The bytes diverge from both signatures: this connection did not open with a header.</summary>
    NotProxyProtocol,

    /// <summary>A whole header was parsed; <c>consumed</c> bytes belong to it and the rest to whatever follows.</summary>
    Complete
}

/// <summary>A PROXY header was present but malformed, oversized, late, or from a peer not allowed to send one.</summary>
sealed class ProxyProtocolException(string message) : IOException(message);

/// <summary>
/// Parses the HAProxy PROXY protocol, versions 1 and 2
/// (https://www.haproxy.org/download/3.0/doc/proxy-protocol.txt).
/// <para>
/// Works on whatever bytes have arrived so far and says how many more it needs, rather than owning
/// the read. That keeps it synchronous and span-based — trivially testable against byte arrays —
/// and leaves partial reads, the timeout and the buffering of whatever follows the header to
/// <see cref="ProxyProtocolReader"/>.
/// </para>
/// <para>
/// Strict on purpose. The header decides who the server believes the client is, so anything even
/// slightly off — a double space in v1, an unknown command or family in v2, a TLV running past the
/// end, a failed checksum — is refused rather than interpreted charitably.
/// </para>
/// </summary>
static class ProxyProtocolParser
{
    static ReadOnlySpan<byte> V1Signature => "PROXY "u8;

    static ReadOnlySpan<byte> V2Signature => [0x0D, 0x0A, 0x0D, 0x0A, 0x00, 0x0D, 0x0A, 0x51, 0x55, 0x49, 0x54, 0x0A];

    const int V2FixedLength = 16;
    const int UnixPathLength = 108;

    public static ProxyProtocolParseStatus TryParse(
        ReadOnlySpan<byte> buffer,
        int maxV2HeaderSize,
        out ProxyProtocolInfo? info,
        out int consumed,
        out int needed
    )
    {
        info = null;
        consumed = 0;
        needed = 0;

        if (buffer.IsEmpty)
        {
            needed = 1;
            return ProxyProtocolParseStatus.Incomplete;
        }

        // Decided the moment the bytes diverge, exactly as the HTTP/2 preface is sniffed: a TLS
        // ClientHello (0x16) or "GET " is rejected on its first byte, so Optional mode never waits
        // on a client for bytes it was never going to send.
        if (IsPrefixOf(buffer, V2Signature))
        {
            return buffer.Length < V2Signature.Length
                ? Incomplete(V2Signature.Length, out needed)
                : ParseV2(buffer, maxV2HeaderSize, out info, out consumed, out needed);
        }

        if (IsPrefixOf(buffer, V1Signature))
        {
            return buffer.Length < V1Signature.Length
                ? Incomplete(V1Signature.Length, out needed)
                : ParseV1(buffer, out info, out consumed, out needed);
        }

        return ProxyProtocolParseStatus.NotProxyProtocol;
    }

    static ProxyProtocolParseStatus Incomplete(int total, out int needed)
    {
        needed = total;
        return ProxyProtocolParseStatus.Incomplete;
    }

    static bool IsPrefixOf(ReadOnlySpan<byte> buffer, ReadOnlySpan<byte> signature)
    {
        var length = Math.Min(buffer.Length, signature.Length);
        return buffer[..length].SequenceEqual(signature[..length]);
    }

    // ---- v1 ----

    static ProxyProtocolParseStatus ParseV1(ReadOnlySpan<byte> buffer, out ProxyProtocolInfo? info, out int consumed, out int needed)
    {
        info = null;
        consumed = 0;
        needed = 0;

        var window = buffer[..Math.Min(buffer.Length, ProxyProtocolOptions.V1MaxHeaderSize)];
        var lf = window.IndexOf((byte)'\n');
        if (lf < 0)
        {
            // The spec caps the line so a receiver can read it in one go and stop there; a peer still
            // talking at 107 bytes is not sending a PROXY header, whatever its first six bytes said.
            if (window.Length >= ProxyProtocolOptions.V1MaxHeaderSize)
                throw new ProxyProtocolException($"PROXY v1 header exceeds {ProxyProtocolOptions.V1MaxHeaderSize} bytes.");

            return Incomplete(buffer.Length + 1, out needed);
        }

        if (lf == 0 || window[lf - 1] != (byte)'\r')
            throw new ProxyProtocolException("PROXY v1 header is not terminated by CRLF.");

        var line = window[..(lf - 1)];
        foreach (var b in line)
        {
            if (b < 0x20 || b > 0x7E)
                throw new ProxyProtocolException("PROXY v1 header contains a non-printable byte.");
        }

        var text = Encoding.ASCII.GetString(line);
        var parts = text.Split(' ');

        // parts[0] is "PROXY", already matched by the signature.
        if (parts.Length < 2)
            throw new ProxyProtocolException("PROXY v1 header has no protocol.");

        consumed = lf + 1;

        if (parts[1] == "UNKNOWN")
        {
            // Whatever follows UNKNOWN is to be ignored: the balancer could not describe the client,
            // so the connection's own endpoints stand.
            info = new ProxyProtocolInfo(1, ProxyProtocolCommand.Proxy)
            {
                AddressFamily = AddressFamily.Unspecified,
                SocketType = SocketType.Unknown
            };
            return ProxyProtocolParseStatus.Complete;
        }

        var family = parts[1] switch
        {
            "TCP4" => AddressFamily.InterNetwork,
            "TCP6" => AddressFamily.InterNetworkV6,
            _ => throw new ProxyProtocolException($"PROXY v1 protocol '{parts[1]}' is not TCP4, TCP6 or UNKNOWN.")
        };

        // Exactly five fields after PROXY, separated by exactly one space — Split turns a doubled
        // space into an empty field, which the parsers below reject.
        if (parts.Length != 6)
            throw new ProxyProtocolException("PROXY v1 header must have exactly six space-separated fields.");

        var source = ParseV1Address(parts[2], family);
        var destination = ParseV1Address(parts[3], family);
        var sourcePort = ParseV1Port(parts[4]);
        var destinationPort = ParseV1Port(parts[5]);

        info = new ProxyProtocolInfo(1, ProxyProtocolCommand.Proxy)
        {
            AddressFamily = family,
            SocketType = SocketType.Stream,
            SourceEndPoint = new IPEndPoint(source, sourcePort),
            DestinationEndPoint = new IPEndPoint(destination, destinationPort)
        };
        return ProxyProtocolParseStatus.Complete;
    }

    static IPAddress ParseV1Address(string text, AddressFamily family)
    {
        // IPAddress.TryParse is generous — "1", "0x7f.1" and "127.1" are all IPv4 to it — and a
        // header deciding who the client is has no business being read generously.
        var valid = family == AddressFamily.InterNetwork
            ? text.Length is >= 7 and <= 15 && text.Count(c => c == '.') == 3 && text.All(c => c == '.' || char.IsAsciiDigit(c))
            : text.Length is >= 2 and <= 45 && text.Contains(':') && text.All(c => c == ':' || c == '.' || char.IsAsciiHexDigit(c));

        if (!valid || !IPAddress.TryParse(text, out var address) || address.AddressFamily != family)
            throw new ProxyProtocolException($"PROXY v1 address '{text}' is not a valid {(family == AddressFamily.InterNetwork ? "IPv4" : "IPv6")} address.");

        return address;
    }

    static int ParseV1Port(string text)
    {
        if (text.Length is 0 or > 5
            || (text.Length > 1 && text[0] == '0')
            || !text.All(char.IsAsciiDigit)
            || !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            || port > 65535)
            throw new ProxyProtocolException($"PROXY v1 port '{text}' is not a valid port.");

        return port;
    }

    // ---- v2 ----

    static ProxyProtocolParseStatus ParseV2(
        ReadOnlySpan<byte> buffer,
        int maxHeaderSize,
        out ProxyProtocolInfo? info,
        out int consumed,
        out int needed
    )
    {
        info = null;
        consumed = 0;
        needed = 0;

        if (buffer.Length < V2FixedLength)
            return Incomplete(V2FixedLength, out needed);

        var versionCommand = buffer[12];
        if (versionCommand >> 4 != 2)
            throw new ProxyProtocolException($"PROXY v2 header declares version {versionCommand >> 4}.");

        var command = (versionCommand & 0x0F) switch
        {
            0 => ProxyProtocolCommand.Local,
            1 => ProxyProtocolCommand.Proxy,
            var other => throw new ProxyProtocolException($"PROXY v2 command {other} is not LOCAL or PROXY.")
        };

        var familyProtocol = buffer[13];
        var (family, addressLength) = (familyProtocol >> 4) switch
        {
            0 => (AddressFamily.Unspecified, 0),
            1 => (AddressFamily.InterNetwork, 12),
            2 => (AddressFamily.InterNetworkV6, 36),
            3 => (AddressFamily.Unix, UnixPathLength * 2),
            var other => throw new ProxyProtocolException($"PROXY v2 address family {other} is not defined.")
        };
        var socketType = (familyProtocol & 0x0F) switch
        {
            0 => SocketType.Unknown,
            1 => SocketType.Stream,
            2 => SocketType.Dgram,
            var other => throw new ProxyProtocolException($"PROXY v2 transport protocol {other} is not defined.")
        };

        var length = BinaryPrimitives.ReadUInt16BigEndian(buffer[14..]);
        var total = V2FixedLength + length;

        // Checked before waiting for the rest: the length is the peer's claim, and buffering up to
        // 64 KiB on the strength of it is exactly what a cap exists to prevent.
        if (total > maxHeaderSize)
            throw new ProxyProtocolException($"PROXY v2 header is {total} bytes; the limit is {maxHeaderSize}.");

        if (length < addressLength)
            throw new ProxyProtocolException($"PROXY v2 header is too short for its address family ({length} < {addressLength}).");

        if (buffer.Length < total)
            return Incomplete(total, out needed);

        var header = buffer[..total];
        info = new ProxyProtocolInfo(2, command);

        // LOCAL means the balancer is talking for itself — the spec says to keep the real endpoints
        // and discard the address block, whatever family it claims. TLVs still describe the
        // connection, so they are kept.
        if (command == ProxyProtocolCommand.Proxy)
        {
            info.AddressFamily = family;
            info.SocketType = socketType;

            var addresses = header.Slice(V2FixedLength, addressLength);
            switch (family)
            {
                case AddressFamily.InterNetwork:
                    info.SourceEndPoint = new IPEndPoint(new IPAddress(addresses[..4]), BinaryPrimitives.ReadUInt16BigEndian(addresses[8..]));
                    info.DestinationEndPoint = new IPEndPoint(new IPAddress(addresses[4..8]), BinaryPrimitives.ReadUInt16BigEndian(addresses[10..]));
                    break;

                case AddressFamily.InterNetworkV6:
                    info.SourceEndPoint = new IPEndPoint(new IPAddress(addresses[..16]), BinaryPrimitives.ReadUInt16BigEndian(addresses[32..]));
                    info.DestinationEndPoint = new IPEndPoint(new IPAddress(addresses[16..32]), BinaryPrimitives.ReadUInt16BigEndian(addresses[34..]));
                    break;

                case AddressFamily.Unix:
                    info.SourceUnixPath = ReadUnixPath(addresses[..UnixPathLength]);
                    info.DestinationUnixPath = ReadUnixPath(addresses[UnixPathLength..]);
                    break;
            }
        }

        info.Tlvs = ParseTlvs(header, V2FixedLength + addressLength, info);
        consumed = total;
        return ProxyProtocolParseStatus.Complete;
    }

    static string? ReadUnixPath(ReadOnlySpan<byte> field)
    {
        // NUL-padded. A leading NUL is Linux's abstract namespace; the name follows it, so only
        // trailing padding is trimmed.
        var end = field.LastIndexOfAnyExcept((byte)0);
        return end < 0 ? null : Encoding.UTF8.GetString(field[..(end + 1)]);
    }

    static List<ProxyProtocolTlv> ParseTlvs(ReadOnlySpan<byte> header, int start, ProxyProtocolInfo info)
    {
        var tlvs = new List<ProxyProtocolTlv>();
        var position = start;

        while (position < header.Length)
        {
            if (header.Length - position < 3)
                throw new ProxyProtocolException("PROXY v2 TLV is truncated.");

            var type = header[position];
            var valueLength = BinaryPrimitives.ReadUInt16BigEndian(header[(position + 1)..]);
            var valueStart = position + 3;

            if (valueStart + valueLength > header.Length)
                throw new ProxyProtocolException($"PROXY v2 TLV 0x{type:x2} runs past the end of the header.");

            var value = header.Slice(valueStart, valueLength);
            tlvs.Add(new ProxyProtocolTlv(type, value.ToArray()));

            switch (type)
            {
                case ProxyProtocolTlv.Alpn:
                    info.Alpn = Encoding.ASCII.GetString(value);
                    break;

                case ProxyProtocolTlv.Authority:
                    info.Authority = Encoding.UTF8.GetString(value);
                    break;

                case ProxyProtocolTlv.UniqueId:
                    if (valueLength > 128)
                        throw new ProxyProtocolException("PROXY v2 unique id exceeds 128 bytes.");
                    info.UniqueId = value.ToArray();
                    break;

                case ProxyProtocolTlv.Crc32C:
                    if (valueLength != 4)
                        throw new ProxyProtocolException("PROXY v2 CRC32C TLV must be 4 bytes.");
                    VerifyChecksum(header, valueStart);
                    break;

                case ProxyProtocolTlv.Ssl:
                    info.Ssl = ParseSsl(value);
                    break;
            }

            position = valueStart + valueLength;
        }

        return tlvs;
    }

    static ProxyProtocolSslInfo ParseSsl(ReadOnlySpan<byte> value)
    {
        if (value.Length < 5)
            throw new ProxyProtocolException("PROXY v2 SSL TLV is truncated.");

        var ssl = new ProxyProtocolSslInfo
        {
            ClientFlags = value[0],
            Verify = BinaryPrimitives.ReadUInt32BigEndian(value[1..])
        };

        var position = 5;
        while (position < value.Length)
        {
            if (value.Length - position < 3)
                throw new ProxyProtocolException("PROXY v2 SSL sub-TLV is truncated.");

            var type = value[position];
            var length = BinaryPrimitives.ReadUInt16BigEndian(value[(position + 1)..]);
            var start = position + 3;

            if (start + length > value.Length)
                throw new ProxyProtocolException($"PROXY v2 SSL sub-TLV 0x{type:x2} runs past the end of its parent.");

            var text = value.Slice(start, length);
            switch (type)
            {
                case 0x21: ssl.Version = Encoding.ASCII.GetString(text); break;
                case 0x22: ssl.CommonName = Encoding.UTF8.GetString(text); break;
                case 0x23: ssl.Cipher = Encoding.ASCII.GetString(text); break;
                case 0x24: ssl.SignatureAlgorithm = Encoding.ASCII.GetString(text); break;
                case 0x25: ssl.KeyAlgorithm = Encoding.ASCII.GetString(text); break;
            }

            position = start + length;
        }

        return ssl;
    }

    /// <summary>
    /// The checksum covers the whole header with its own four bytes zeroed. A mismatch means the
    /// header was damaged or forged in transit, and the connection is refused, as the spec asks.
    /// </summary>
    static void VerifyChecksum(ReadOnlySpan<byte> header, int checksumOffset)
    {
        var expected = BinaryPrimitives.ReadUInt32BigEndian(header[checksumOffset..]);

        var crc = Crc32C.Update(Crc32C.Initial, header[..checksumOffset]);
        crc = Crc32C.Update(crc, [0, 0, 0, 0]);
        crc = Crc32C.Update(crc, header[(checksumOffset + 4)..]);

        if (~crc != expected)
            throw new ProxyProtocolException("PROXY v2 CRC32C checksum does not match.");
    }
}

/// <summary>
/// CRC-32C (Castagnoli), table-driven. Written out rather than taken from System.IO.Hashing because
/// the core takes no dependencies beyond Microsoft.Extensions abstractions, and this is only ever
/// run over a header of a few hundred bytes.
/// </summary>
static class Crc32C
{
    public const uint Initial = 0xFFFFFFFF;

    static readonly uint[] Table = BuildTable();

    static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var crc = i;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0x82F63B78 : crc >> 1;
            table[i] = crc;
        }
        return table;
    }

    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    /// <summary>The finished checksum of <paramref name="data"/>.</summary>
    public static uint Compute(ReadOnlySpan<byte> data) => ~Update(Initial, data);
}
