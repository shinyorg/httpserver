using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Shiny.Net.HttpServer.Security;
using Shiny.Net.HttpServer.Transports;

namespace Shiny.Net.HttpServer.Tests;

/// <summary>Builds PROXY headers the way a balancer would, for feeding to the parser and the server.</summary>
static class ProxyHeader
{
    public static readonly byte[] V2Signature = [0x0D, 0x0A, 0x0D, 0x0A, 0x00, 0x0D, 0x0A, 0x51, 0x55, 0x49, 0x54, 0x0A];

    public static byte[] V1(string line) => Encoding.ASCII.GetBytes(line + "\r\n");

    public static byte[] V1Tcp4(string source, int sourcePort, string destination = "10.0.0.1", int destinationPort = 443)
        => V1($"PROXY TCP4 {source} {destination} {sourcePort} {destinationPort}");

    /// <summary>A v2 header. <paramref name="tlvs"/> are appended after the address block, verbatim.</summary>
    public static byte[] V2(
        IPEndPoint? source,
        IPEndPoint? destination,
        byte command = 0x1,
        byte? familyProtocol = null,
        byte[]? tlvs = null,
        byte version = 0x2,
        byte[]? addressBlock = null
    )
    {
        var address = addressBlock ?? [];
        byte family = 0x00;

        if (addressBlock is null && source is not null && destination is not null)
        {
            if (source.AddressFamily == AddressFamily.InterNetwork)
            {
                family = 0x11;
                address = new byte[12];
                source.Address.GetAddressBytes().CopyTo(address, 0);
                destination.Address.GetAddressBytes().CopyTo(address, 4);
                BinaryPrimitives.WriteUInt16BigEndian(address.AsSpan(8), (ushort)source.Port);
                BinaryPrimitives.WriteUInt16BigEndian(address.AsSpan(10), (ushort)destination.Port);
            }
            else
            {
                family = 0x21;
                address = new byte[36];
                source.Address.GetAddressBytes().CopyTo(address, 0);
                destination.Address.GetAddressBytes().CopyTo(address, 16);
                BinaryPrimitives.WriteUInt16BigEndian(address.AsSpan(32), (ushort)source.Port);
                BinaryPrimitives.WriteUInt16BigEndian(address.AsSpan(34), (ushort)destination.Port);
            }
        }

        tlvs ??= [];
        var header = new byte[16 + address.Length + tlvs.Length];
        V2Signature.CopyTo(header, 0);
        header[12] = (byte)((version << 4) | command);
        header[13] = familyProtocol ?? family;
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(14), (ushort)(address.Length + tlvs.Length));
        address.CopyTo(header, 16);
        tlvs.CopyTo(header, 16 + address.Length);
        return header;
    }

    public static byte[] Tlv(byte type, byte[] value)
    {
        var tlv = new byte[3 + value.Length];
        tlv[0] = type;
        BinaryPrimitives.WriteUInt16BigEndian(tlv.AsSpan(1), (ushort)value.Length);
        value.CopyTo(tlv, 3);
        return tlv;
    }

    public static byte[] Tlv(byte type, string value) => Tlv(type, Encoding.UTF8.GetBytes(value));

    /// <summary>Fills in a CRC32C TLV that <paramref name="header"/> already carries with zeroed value at <paramref name="offset"/>.</summary>
    public static void SignCrc(byte[] header, int offset)
        => BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(offset), Crc32C.Compute(header));

    public static IPEndPoint Ep(string address, int port) => new(IPAddress.Parse(address), port);
}

public class ProxyProtocolParserTests
{
    static ProxyProtocolInfo ParseComplete(byte[] bytes, out int consumed)
    {
        var status = ProxyProtocolParser.TryParse(bytes, 4096, out var info, out consumed, out _);
        Assert.Equal(ProxyProtocolParseStatus.Complete, status);
        return info!;
    }

    static void AssertRejected(byte[] bytes, int max = 4096)
        => Assert.Throws<ProxyProtocolException>(() => ProxyProtocolParser.TryParse(bytes, max, out _, out _, out _));

    [Fact]
    public void Parses_a_v1_tcp4_header_and_stops_at_its_end()
    {
        var bytes = ProxyHeader.V1Tcp4("192.0.2.10", 51234).Concat("GET /"u8.ToArray()).ToArray();

        var info = ParseComplete(bytes, out var consumed);

        Assert.Equal(bytes.Length - 5, consumed);
        Assert.Equal(1, info.Version);
        Assert.Equal(ProxyProtocolCommand.Proxy, info.Command);
        Assert.Equal(AddressFamily.InterNetwork, info.AddressFamily);
        Assert.Equal(ProxyHeader.Ep("192.0.2.10", 51234), info.SourceEndPoint);
        Assert.Equal(ProxyHeader.Ep("10.0.0.1", 443), info.DestinationEndPoint);
    }

    [Fact]
    public void Parses_a_v1_tcp6_header()
    {
        var info = ParseComplete(ProxyHeader.V1("PROXY TCP6 2001:db8::7 2001:db8::1 40000 8443"), out _);

        Assert.Equal(ProxyHeader.Ep("2001:db8::7", 40000), info.SourceEndPoint);
        Assert.Equal(AddressFamily.InterNetworkV6, info.AddressFamily);
    }

    [Fact]
    public void Treats_v1_unknown_as_no_address_and_ignores_the_rest_of_the_line()
    {
        var info = ParseComplete(ProxyHeader.V1("PROXY UNKNOWN whatever follows is ignored"), out _);

        Assert.Equal(AddressFamily.Unspecified, info.AddressFamily);
        Assert.Null(info.SourceEndPoint);
    }

    [Theory]
    [InlineData("PROXY TCP4 127.1 10.0.0.1 1 2")] // IPAddress.TryParse would take this
    [InlineData("PROXY TCP4 192.0.2.1  10.0.0.1 1 2")] // doubled space
    [InlineData("PROXY TCP4 192.0.2.1 10.0.0.1 65536 2")]
    [InlineData("PROXY TCP4 192.0.2.1 10.0.0.1 01 2")]
    [InlineData("PROXY TCP4 2001:db8::1 10.0.0.1 1 2")] // family mismatch
    [InlineData("PROXY TCP6 192.0.2.1 10.0.0.1 1 2")]
    [InlineData("PROXY UDP4 192.0.2.1 10.0.0.1 1 2")]
    [InlineData("PROXY TCP4 192.0.2.1 10.0.0.1 1")]
    [InlineData("PROXY ")]
    public void Rejects_a_malformed_v1_header(string line) => AssertRejected(ProxyHeader.V1(line));

    [Fact]
    public void Rejects_a_v1_header_without_a_carriage_return()
        => AssertRejected("PROXY TCP4 192.0.2.1 10.0.0.1 1 2\n"u8.ToArray());

    [Fact]
    public void Rejects_a_v1_header_longer_than_107_bytes()
        => AssertRejected(Encoding.ASCII.GetBytes("PROXY UNKNOWN " + new string('x', 120)));

    [Fact]
    public void Asks_for_more_while_a_header_is_arriving_a_byte_at_a_time()
    {
        foreach (var bytes in new[]
        {
            ProxyHeader.V1Tcp4("192.0.2.10", 51234),
            ProxyHeader.V2(ProxyHeader.Ep("192.0.2.10", 51234), ProxyHeader.Ep("10.0.0.1", 443), tlvs: ProxyHeader.Tlv(0x01, "h2"))
        })
        {
            for (var length = 0; length < bytes.Length; length++)
            {
                var status = ProxyProtocolParser.TryParse(bytes.AsSpan(0, length), 4096, out _, out _, out var needed);

                Assert.Equal(ProxyProtocolParseStatus.Incomplete, status);
                Assert.True(needed > length);
            }

            Assert.Equal(ProxyHeader.Ep("192.0.2.10", 51234), ParseComplete(bytes, out _).SourceEndPoint);
        }
    }

    [Theory]
    [InlineData("GET / HTTP/1.1\r\n")]
    [InlineData("POST / HTTP/1.1\r\n")]
    [InlineData("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n")]
    [InlineData("\u0016\u0003\u0001")] // a TLS ClientHello
    public void Recognises_a_connection_that_did_not_open_with_a_header(string opening)
        => Assert.Equal(
            ProxyProtocolParseStatus.NotProxyProtocol,
            ProxyProtocolParser.TryParse(Encoding.Latin1.GetBytes(opening), 4096, out _, out _, out _)
        );

    [Fact]
    public void Parses_a_v2_ipv4_header()
    {
        var bytes = ProxyHeader.V2(ProxyHeader.Ep("198.51.100.4", 1000), ProxyHeader.Ep("10.0.0.1", 443));
        var info = ParseComplete(bytes, out var consumed);

        Assert.Equal(bytes.Length, consumed);
        Assert.Equal(2, info.Version);
        Assert.Equal(SocketType.Stream, info.SocketType);
        Assert.Equal(ProxyHeader.Ep("198.51.100.4", 1000), info.SourceEndPoint);
        Assert.Equal(ProxyHeader.Ep("10.0.0.1", 443), info.DestinationEndPoint);
    }

    [Fact]
    public void Parses_a_v2_ipv6_header()
    {
        var info = ParseComplete(ProxyHeader.V2(ProxyHeader.Ep("2001:db8::9", 5), ProxyHeader.Ep("2001:db8::1", 443)), out _);
        Assert.Equal(ProxyHeader.Ep("2001:db8::9", 5), info.SourceEndPoint);
    }

    [Fact]
    public void Discards_the_address_block_of_a_v2_local_command()
    {
        var info = ParseComplete(
            ProxyHeader.V2(ProxyHeader.Ep("198.51.100.4", 1000), ProxyHeader.Ep("10.0.0.1", 443), command: 0x0),
            out _
        );

        Assert.Equal(ProxyProtocolCommand.Local, info.Command);
        Assert.Null(info.SourceEndPoint);
    }

    [Fact]
    public void Parses_a_v2_unspec_header_as_no_address()
    {
        var info = ParseComplete(ProxyHeader.V2(null, null), out _);

        Assert.Equal(AddressFamily.Unspecified, info.AddressFamily);
        Assert.Null(info.SourceEndPoint);
    }

    [Fact]
    public void Parses_v2_unix_socket_paths()
    {
        var block = new byte[216];
        Encoding.UTF8.GetBytes("/run/client.sock").CopyTo(block, 0);
        Encoding.UTF8.GetBytes("/run/haproxy.sock").CopyTo(block, 108);

        var info = ParseComplete(ProxyHeader.V2(null, null, familyProtocol: 0x31, addressBlock: block), out _);

        Assert.Equal(AddressFamily.Unix, info.AddressFamily);
        Assert.Equal("/run/client.sock", info.SourceUnixPath);
        Assert.Equal("/run/haproxy.sock", info.DestinationUnixPath);
        Assert.Null(info.SourceEndPoint);
    }

    [Fact]
    public void Decodes_the_well_known_v2_tlvs_and_keeps_vendor_ones()
    {
        var sslValue = new byte[] { 0x05, 0, 0, 0, 0 }
            .Concat(ProxyHeader.Tlv(0x21, "TLSv1.3"))
            .Concat(ProxyHeader.Tlv(0x22, "client.example"))
            .Concat(ProxyHeader.Tlv(0x23, "TLS_AES_128_GCM_SHA256"))
            .Concat(ProxyHeader.Tlv(0x24, "SHA256"))
            .Concat(ProxyHeader.Tlv(0x25, "RSA2048"))
            .ToArray();

        var tlvs = ProxyHeader.Tlv(0x01, "h2")
            .Concat(ProxyHeader.Tlv(0x02, "api.example.com"))
            .Concat(ProxyHeader.Tlv(0x05, [1, 2, 3, 4]))
            .Concat(ProxyHeader.Tlv(0x20, sslValue))
            .Concat(ProxyHeader.Tlv(0x04, new byte[7]))
            .Concat(ProxyHeader.Tlv(0xEA, "\u0001vpce-0123"))
            .ToArray();

        var info = ParseComplete(ProxyHeader.V2(ProxyHeader.Ep("198.51.100.4", 1), ProxyHeader.Ep("10.0.0.1", 443), tlvs: tlvs), out _);

        Assert.Equal("h2", info.Alpn);
        Assert.Equal("api.example.com", info.Authority);
        Assert.Equal([1, 2, 3, 4], info.UniqueId);
        Assert.NotNull(info.Ssl);
        Assert.True(info.Ssl.IsSsl);
        Assert.True(info.Ssl.IsClientCertificateVerified);
        Assert.Equal("TLSv1.3", info.Ssl.Version);
        Assert.Equal("client.example", info.Ssl.CommonName);
        Assert.Equal("TLS_AES_128_GCM_SHA256", info.Ssl.Cipher);
        Assert.Equal("SHA256", info.Ssl.SignatureAlgorithm);
        Assert.Equal("RSA2048", info.Ssl.KeyAlgorithm);
        Assert.Equal(6, info.Tlvs.Count);
        Assert.Equal("\u0001vpce-0123", info.GetTlv(0xEA)!.GetString());
    }

    [Fact]
    public void Accepts_a_v2_header_whose_crc32c_matches()
    {
        var header = ProxyHeader.V2(ProxyHeader.Ep("198.51.100.4", 1), ProxyHeader.Ep("10.0.0.1", 443), tlvs: ProxyHeader.Tlv(0x03, new byte[4]));
        ProxyHeader.SignCrc(header, header.Length - 4);

        Assert.Equal(ProxyHeader.Ep("198.51.100.4", 1), ParseComplete(header, out _).SourceEndPoint);
    }

    [Fact]
    public void Rejects_a_v2_header_whose_crc32c_does_not_match()
    {
        var header = ProxyHeader.V2(ProxyHeader.Ep("198.51.100.4", 1), ProxyHeader.Ep("10.0.0.1", 443), tlvs: ProxyHeader.Tlv(0x03, new byte[4]));
        ProxyHeader.SignCrc(header, header.Length - 4);
        header[16] ^= 0xFF; // the source address, altered after signing

        AssertRejected(header);
    }

    [Fact]
    public void Computes_the_standard_crc32c_check_value()
        => Assert.Equal(0xE3069283u, Crc32C.Compute("123456789"u8));

    [Fact]
    public void Rejects_a_v2_header_larger_than_the_limit_before_it_arrives()
    {
        // Only the 16 fixed bytes: the claimed length alone is enough to refuse it, without
        // buffering 60 KiB on the peer's say-so.
        var header = ProxyHeader.V2(null, null, tlvs: ProxyHeader.Tlv(0x04, new byte[60_000]));

        AssertRejected(header[..16], max: 4096);
    }

    [Fact]
    public void Rejects_a_v2_tlv_that_runs_past_the_header()
    {
        var header = ProxyHeader.V2(ProxyHeader.Ep("198.51.100.4", 1), ProxyHeader.Ep("10.0.0.1", 443), tlvs: [0x01, 0x00, 0x09, (byte)'h']);
        AssertRejected(header);
    }

    [Theory]
    [InlineData((byte)0x1, (byte)0x2, (byte)0x11)] // version 1 in a v2 signature
    [InlineData((byte)0x2, (byte)0x2, (byte)0x11)] // command 2
    [InlineData((byte)0x2, (byte)0x1, (byte)0x41)] // family 4
    [InlineData((byte)0x2, (byte)0x1, (byte)0x13)] // protocol 3
    public void Rejects_undefined_v2_version_command_family_or_protocol(byte version, byte command, byte familyProtocol)
    {
        var header = ProxyHeader.V2(ProxyHeader.Ep("198.51.100.4", 1), ProxyHeader.Ep("10.0.0.1", 443), command: command, familyProtocol: familyProtocol, version: version);
        AssertRejected(header);
    }

    [Fact]
    public void Rejects_a_v2_length_too_short_for_the_declared_family()
        => AssertRejected(ProxyHeader.V2(null, null, familyProtocol: 0x21, addressBlock: new byte[12]));
}

public class ProxyProtocolServerTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    const string Report = "/who";

    static void MapWho(HttpServer app) => app.MapGet(Report, ctx => ctx.Response.WriteAsync(
        $"{ctx.Connection.RemoteIpAddress}:{ctx.Connection.RemotePort}|v{ctx.Connection.ProxyProtocol?.Version}|{ctx.Request.Protocol}|{ctx.Connection.ProxyProtocol?.Alpn}|{ctx.Connection.ProxyProtocol?.ProxyEndPoint?.Address}"
    ));

    static Task<TestServer> StartAsync(ProxyProtocolMode mode = ProxyProtocolMode.Required, string trust = "127.0.0.1", Action<HttpServer>? configure = null, TimeSpan? timeout = null)
        => TestServer.StartAsync(
            app =>
            {
                configure?.Invoke(app);
                MapWho(app);
            },
            builder => builder.Options.ProxyProtocol = new ProxyProtocolOptions
            {
                Mode = mode,
                HeaderTimeout = timeout ?? TimeSpan.FromSeconds(5)
            }.Trust(trust)
        );

    /// <summary>
    /// An <see cref="HttpClient"/> whose every connection opens with <paramref name="header"/> — which
    /// is exactly what a balancer does, and lets the real HTTP/1.1, HTTP/2 and TLS stacks run above it.
    /// </summary>
    static HttpClient CreateClient(string baseAddress, byte[] header, Version version, SocketsHttpHandler? handler = null)
    {
        handler ??= new SocketsHttpHandler();
        handler.ConnectCallback = async (context, cancellationToken) =>
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            await socket.ConnectAsync(context.DnsEndPoint, cancellationToken);
            await socket.SendAsync(header, SocketFlags.None, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        };

        return new HttpClient(handler)
        {
            BaseAddress = new Uri(baseAddress),
            DefaultRequestVersion = version,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    /// <summary>Sends raw bytes and returns everything the server says before closing (or going quiet).</summary>
    static async Task<string> SendRawAsync(int port, params byte[][] chunks)
    {
        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        await socket.ConnectAsync(IPAddress.Loopback, port);

        try
        {
            foreach (var chunk in chunks)
            {
                await socket.SendAsync(chunk, SocketFlags.None);
                await Task.Delay(20);
            }
        }
        catch (SocketException)
        {
            // The server may have hung up mid-send, which is what several of these tests expect.
        }

        var response = new StringBuilder();
        var buffer = new byte[8192];
        while (true)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            int read;
            try
            {
                read = await socket.ReceiveAsync(buffer, SocketFlags.None, timeout.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException)
            {
                break;
            }

            if (read == 0)
                break;

            response.Append(Encoding.ASCII.GetString(buffer, 0, read));
            if (response.ToString().Contains("\r\n\r\n") && response.ToString().Contains("Connection: close", StringComparison.OrdinalIgnoreCase))
                continue;
        }

        return response.ToString();
    }

    static byte[] Request(string path = Report) => Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n");

    static byte[] V2(string source, int port) => ProxyHeader.V2(ProxyHeader.Ep(source, port), ProxyHeader.Ep("10.0.0.1", 443));

    [Fact]
    public async Task Http1_sees_the_v1_client_when_header_and_request_arrive_in_one_segment()
    {
        await using var server = await StartAsync();

        var response = await SendRawAsync(server.Port, [.. ProxyHeader.V1Tcp4("192.0.2.10", 51234), .. Request()]);

        Assert.Contains("200 OK", response);
        Assert.Contains("192.0.2.10:51234|v1|HTTP/1.1||127.0.0.1", response);
    }

    [Fact]
    public async Task Http1_sees_the_v2_client_when_the_header_dribbles_in()
    {
        await using var server = await StartAsync();
        var header = V2("198.51.100.4", 4000);

        // Split across several segments, the last mixed with the start of the request — the reader
        // has to cope with a partial header and hand the over-read bytes on.
        var request = Request();
        var response = await SendRawAsync(
            server.Port,
            header[..5], header[5..14], header[14..20], [.. header[20..], .. request[..10]], request[10..]
        );

        Assert.Contains("198.51.100.4:4000|v2|HTTP/1.1", response);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Http1_client_through_a_balancer(int version)
    {
        await using var server = await StartAsync();
        var header = version == 1 ? ProxyHeader.V1Tcp4("192.0.2.10", 51234) : V2("192.0.2.10", 51234);

        using var client = CreateClient($"http://127.0.0.1:{server.Port}", header, HttpVersion.Version11);

        Assert.Equal($"192.0.2.10:51234|v{version}|HTTP/1.1||127.0.0.1", await client.GetStringAsync(Report, Token));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Cleartext_http2_prior_knowledge_through_a_balancer(int version)
    {
        await using var server = await StartAsync();
        var header = version == 1
            ? ProxyHeader.V1("PROXY TCP6 2001:db8::7 2001:db8::1 40000 443")
            : ProxyHeader.V2(ProxyHeader.Ep("2001:db8::7", 40000), ProxyHeader.Ep("2001:db8::1", 443));

        using var client = CreateClient($"http://127.0.0.1:{server.Port}", header, HttpVersion.Version20);

        // Twice, to show every stream on the connection carries the address — not just the first.
        Assert.Equal($"2001:db8::7:40000|v{version}|HTTP/2||127.0.0.1", await client.GetStringAsync(Report, Token));
        Assert.Equal($"2001:db8::7:40000|v{version}|HTTP/2||127.0.0.1", await client.GetStringAsync(Report, Token));
    }

    [Theory]
    [InlineData(1, "1.1")]
    [InlineData(2, "1.1")]
    [InlineData(1, "2.0")]
    [InlineData(2, "2.0")]
    public async Task Tls_passthrough_reads_the_header_before_the_handshake(int version, string httpVersion)
    {
        using var certificate = ServerCertificate.Create();
        var builder = HttpServer.CreateBuilder();
        builder.Options.Address = IPAddress.Loopback;
        builder.Options.Port = 0;
        builder.Options.Https = new HttpsOptions { Certificate = certificate };
        builder.Options.ProxyProtocol = new ProxyProtocolOptions { Mode = ProxyProtocolMode.Required }.Trust("127.0.0.0/8");

        await using var server = builder.Build();
        MapWho(server);
        await server.StartAsync(Token);
        var port = new Uri(server.ListenUrl!).Port;

        var tlvs = ProxyHeader.Tlv(ProxyProtocolTlv.Alpn, httpVersion == "2.0" ? "h2" : "http/1.1");
        var header = version == 1
            ? ProxyHeader.V1Tcp4("192.0.2.99", 7000)
            : ProxyHeader.V2(ProxyHeader.Ep("192.0.2.99", 7000), ProxyHeader.Ep("10.0.0.1", 443), tlvs: tlvs);

        using var client = CreateClient(
            $"https://127.0.0.1:{port}",
            header,
            Version.Parse(httpVersion),
            (SocketsHttpHandler)CertificatePinning.CreateHandler(certificate)
        );

        var expectedProtocol = httpVersion == "2.0" ? "HTTP/2" : "HTTP/1.1";
        var expectedAlpn = version == 2 ? (httpVersion == "2.0" ? "h2" : "http/1.1") : "";

        Assert.Equal($"192.0.2.99:7000|v{version}|{expectedProtocol}|{expectedAlpn}|127.0.0.1", await client.GetStringAsync(Report, Token));
    }

    [Fact]
    public async Task Required_mode_closes_a_connection_without_a_header()
    {
        await using var server = await StartAsync();

        Assert.Equal(string.Empty, await SendRawAsync(server.Port, Request()));
    }

    [Fact]
    public async Task Optional_mode_serves_connections_with_and_without_a_header()
    {
        await using var server = await StartAsync(ProxyProtocolMode.Optional);

        Assert.StartsWith("127.0.0.1:", await server.Client.GetStringAsync(Report, Token));
        Assert.Contains("192.0.2.10:51234|v1", await SendRawAsync(server.Port, [.. ProxyHeader.V1Tcp4("192.0.2.10", 51234), .. Request()]));
    }

    [Fact]
    public async Task Required_mode_closes_an_untrusted_peer_without_reading_its_header()
    {
        await using var server = await StartAsync(trust: "10.0.0.0/8");

        Assert.Equal(string.Empty, await SendRawAsync(server.Port, [.. ProxyHeader.V1Tcp4("192.0.2.10", 1), .. Request()]));
    }

    [Fact]
    public async Task Optional_mode_does_not_parse_a_header_from_an_untrusted_peer()
    {
        // The header reaches HTTP untouched, where "PROXY TCP4 ..." is not a valid request line (the
        // parser happens to answer 505, reading the ports as an HTTP version) — so a client cannot
        // pick its own address by sending one.
        await using var server = await StartAsync(ProxyProtocolMode.Optional, trust: "10.0.0.0/8");

        var response = await SendRawAsync(server.Port, [.. ProxyHeader.V1Tcp4("192.0.2.10", 1), .. Request()]);

        Assert.StartsWith("HTTP/1.1 5", response);
        Assert.DoesNotContain("|v1|", response);
    }

    [Theory]
    [InlineData("PROXY TCP4 192.0.2.1 10.0.0.1 99999 1\r\n")]
    [InlineData("PROXY TCP4 192.0.2.1 10.0.0.1 1 1\n")]
    public async Task Closes_a_connection_with_a_malformed_header(string header)
    {
        await using var server = await StartAsync(ProxyProtocolMode.Optional);

        Assert.Equal(string.Empty, await SendRawAsync(server.Port, [.. Encoding.ASCII.GetBytes(header), .. Request()]));
    }

    [Fact]
    public async Task Closes_a_connection_whose_v2_header_is_oversized()
    {
        await using var server = await StartAsync();
        var header = ProxyHeader.V2(ProxyHeader.Ep("192.0.2.1", 1), ProxyHeader.Ep("10.0.0.1", 443), tlvs: ProxyHeader.Tlv(0x04, new byte[8000]));

        Assert.Equal(string.Empty, await SendRawAsync(server.Port, [.. header, .. Request()]));
    }

    [Fact]
    public async Task Closes_a_connection_whose_header_never_finishes()
    {
        await using var server = await StartAsync(timeout: TimeSpan.FromMilliseconds(300));

        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(IPAddress.Loopback, server.Port, Token);
        await socket.SendAsync("PROXY TCP4 192.0"u8.ToArray(), SocketFlags.None, Token);

        var started = DateTime.UtcNow;
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        int read;
        try
        {
            read = await socket.ReceiveAsync(new byte[16], SocketFlags.None, wait.Token);
        }
        catch (SocketException)
        {
            read = 0;
        }

        Assert.Equal(0, read);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(4), "the header timeout should have closed the connection");
    }

    [Fact]
    public async Task A_local_command_keeps_the_balancers_own_address()
    {
        await using var server = await StartAsync();
        var header = ProxyHeader.V2(ProxyHeader.Ep("192.0.2.1", 1), ProxyHeader.Ep("10.0.0.1", 443), command: 0x0);

        var response = await SendRawAsync(server.Port, [.. header, .. Request()]);

        Assert.Contains("127.0.0.1:", response);
        Assert.Contains("|v2|", response);
    }

    [Fact]
    public async Task The_ip_filter_judges_the_proxied_client_not_the_balancer()
    {
        await using var server = await StartAsync(configure: app => app.UseIpFilter(p => p.Allow("203.0.113.0/24")));

        var allowed = await SendRawAsync(server.Port, [.. ProxyHeader.V1Tcp4("203.0.113.7", 1), .. Request()]);
        var denied = await SendRawAsync(server.Port, [.. V2("198.51.100.1", 1), .. Request()]);

        Assert.Contains("200 OK", allowed);
        Assert.Contains("403", denied);
    }

    [Fact]
    public async Task Only_the_endpoint_configured_for_it_expects_a_header()
    {
        var builder = HttpServer.CreateBuilder();
        builder.Options.Listen(IPAddress.Loopback, 0);
        builder.Options.Listen(IPAddress.Loopback, 0).UseProxyProtocol(p => p.Trust("127.0.0.1"));

        await using var server = builder.Build();
        MapWho(server);
        await server.StartAsync(Token);

        var plain = new Uri(server.ListenUrls[0]).Port;
        var proxied = new Uri(server.ListenUrls[1]).Port;

        Assert.Contains("127.0.0.1:", await SendRawAsync(plain, Request()));
        Assert.Contains("192.0.2.5:9|v1", await SendRawAsync(proxied, [.. ProxyHeader.V1Tcp4("192.0.2.5", 9), .. Request()]));
    }

    [Fact]
    public async Task Refuses_to_start_when_no_proxy_is_trusted()
    {
        var builder = HttpServer.CreateBuilder();
        builder.Options.Port = 0;
        builder.Options.ProxyProtocol = new ProxyProtocolOptions { Mode = ProxyProtocolMode.Required };

        await using var server = builder.Build();

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => server.StartAsync(Token));
        Assert.Contains("TrustedProxies", ex.ToString());
    }
}
