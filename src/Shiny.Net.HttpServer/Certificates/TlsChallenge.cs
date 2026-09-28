using System.Buffers.Binary;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Shiny.Net.HttpServer;

/// <summary>
/// What a client said in its TLS ClientHello, read before the handshake begins.
/// <para>
/// .NET hands a server the SNI name at certificate-selection time, but never the ALPN protocols the
/// client offered — and a TLS-ALPN-01 challenge (RFC 8737) is recognised by exactly that: a
/// ClientHello offering <c>acme-tls/1</c> and nothing else. So the hello is read off the socket
/// here, parsed, and then replayed into <see cref="SslStream"/> untouched.
/// </para>
/// </summary>
public sealed class TlsClientHello
{
    internal TlsClientHello(string? serverName, IReadOnlyList<SslApplicationProtocol> applicationProtocols)
    {
        this.ServerName = serverName;
        this.ApplicationProtocols = applicationProtocols;
    }

    /// <summary>The SNI host name, or null when the client sent none.</summary>
    public string? ServerName { get; }

    /// <summary>The ALPN protocols the client offered, in its order of preference. Empty when it offered none.</summary>
    public IReadOnlyList<SslApplicationProtocol> ApplicationProtocols { get; }

    /// <summary>True when the client offered <paramref name="protocol"/>.</summary>
    public bool Offers(SslApplicationProtocol protocol)
    {
        foreach (var offered in this.ApplicationProtocols)
        {
            if (offered == protocol)
                return true;
        }

        return false;
    }
}

/// <summary>
/// How to answer a connection that <see cref="HttpsOptions.ChallengeResponder"/> recognised as a
/// challenge rather than as HTTP: complete the handshake with this certificate, agree on this ALPN
/// protocol, and close.
/// </summary>
public sealed class TlsChallengeResponse
{
    public TlsChallengeResponse(X509Certificate2 certificate, SslApplicationProtocol applicationProtocol)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        this.Certificate = certificate;
        this.ApplicationProtocol = applicationProtocol;
    }

    /// <summary>The certificate to present. Must carry its private key.</summary>
    public X509Certificate2 Certificate { get; }

    /// <summary>The single ALPN protocol to agree on.</summary>
    public SslApplicationProtocol ApplicationProtocol { get; }
}

/// <summary>
/// Raised out of connection initialization after a challenge handshake has been answered. The
/// connection did what it was opened for and is closed; it was never going to carry HTTP.
/// An <see cref="OperationCanceledException"/> so that every existing "the handshake did not lead to
/// a request" path already treats it as routine.
/// </summary>
sealed class TlsChallengeAnsweredException() : OperationCanceledException("A TLS challenge handshake was answered.");

/// <summary>
/// Reads the ClientHello off a stream without consuming it: returns what it parsed, plus every byte
/// it read so they can be replayed to <see cref="SslStream"/>.
/// <para>
/// Anything it does not understand — not TLS at all, a malformed or truncated hello — yields a null
/// hello and the bytes as read. The real handshake then fails on them exactly as it would have
/// without this in front of it, which is the point: this is an observer, never a gatekeeper.
/// </para>
/// </summary>
static class TlsClientHelloReader
{
    const byte HandshakeRecord = 22;
    const byte ClientHelloMessage = 1;
    const ushort ServerNameExtension = 0;
    const ushort AlpnExtension = 16;

    // A hello is a few hundred bytes, a couple of KiB with post-quantum key shares. Anything past
    // this is not a hello worth waiting for.
    const int MaxHelloBytes = 64 * 1024;

    public static async ValueTask<(TlsClientHello? Hello, byte[] Consumed)> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new MemoryStream();
        var handshake = new MemoryStream();
        var header = new byte[5];

        while (true)
        {
            var got = await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
            buffer.Write(header, 0, got);

            if (got < header.Length || header[0] != HandshakeRecord)
                return (null, buffer.ToArray());

            var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(3));
            if (buffer.Length + length > MaxHelloBytes)
                return (null, buffer.ToArray());

            var fragment = new byte[length];
            got = await stream.ReadAtLeastAsync(fragment, length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
            buffer.Write(fragment, 0, got);
            handshake.Write(fragment, 0, got);

            if (got < length)
                return (null, buffer.ToArray());

            // A hello may be fragmented across records; keep reading until the whole message is in.
            var available = handshake.GetBuffer().AsSpan(0, (int)handshake.Length);
            if (available.Length >= 4)
            {
                if (available[0] != ClientHelloMessage)
                    return (null, buffer.ToArray());

                var messageLength = (available[1] << 16) | (available[2] << 8) | available[3];
                if (messageLength + 4 > MaxHelloBytes)
                    return (null, buffer.ToArray());

                if (available.Length >= messageLength + 4)
                    return (Parse(available.Slice(4, messageLength)), buffer.ToArray());
            }
        }
    }

    internal static TlsClientHello? Parse(ReadOnlySpan<byte> body)
    {
        try
        {
            var reader = new SpanReader(body);

            reader.Skip(2);                      // legacy_version
            reader.Skip(32);                     // random
            reader.Skip(reader.ReadUInt8());     // legacy_session_id
            reader.Skip(reader.ReadUInt16());    // cipher_suites
            reader.Skip(reader.ReadUInt8());     // legacy_compression_methods

            string? serverName = null;
            var protocols = new List<SslApplicationProtocol>();

            if (reader.Remaining == 0)
                return new TlsClientHello(null, protocols);

            var extensions = new SpanReader(reader.ReadBytes(reader.ReadUInt16()));
            while (extensions.Remaining > 0)
            {
                var type = extensions.ReadUInt16();
                var data = new SpanReader(extensions.ReadBytes(extensions.ReadUInt16()));

                if (type == ServerNameExtension)
                {
                    var list = new SpanReader(data.ReadBytes(data.ReadUInt16()));
                    while (list.Remaining > 0)
                    {
                        var nameType = list.ReadUInt8();
                        var name = list.ReadBytes(list.ReadUInt16());
                        if (nameType == 0)
                            serverName = Encoding.ASCII.GetString(name);
                    }
                }
                else if (type == AlpnExtension)
                {
                    var list = new SpanReader(data.ReadBytes(data.ReadUInt16()));
                    while (list.Remaining > 0)
                        protocols.Add(new SslApplicationProtocol(list.ReadBytes(list.ReadUInt8()).ToArray()));
                }
            }

            return new TlsClientHello(serverName, protocols);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            // Truncated or malformed. Not ours to reject — SslStream will, on the same bytes.
            return null;
        }
    }

    ref struct SpanReader(ReadOnlySpan<byte> span)
    {
        ReadOnlySpan<byte> span = span;

        public readonly int Remaining => this.span.Length;

        public byte ReadUInt8()
        {
            var value = this.span[0];
            this.span = this.span[1..];
            return value;
        }

        public ushort ReadUInt16()
        {
            if (this.span.Length < 2)
                throw new ArgumentOutOfRangeException(nameof(span));

            var value = BinaryPrimitives.ReadUInt16BigEndian(this.span);
            this.span = this.span[2..];
            return value;
        }

        public ReadOnlySpan<byte> ReadBytes(int count)
        {
            var value = this.span.Slice(0, count);
            this.span = this.span[count..];
            return value;
        }

        public void Skip(int count) => this.span = this.span[count..];
    }
}
