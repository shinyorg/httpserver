using System.Collections.Concurrent;
using System.Formats.Asn1;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Shiny.Net.HttpServer.Acme.Internal;

/// <summary>
/// The challenges currently being validated, and the two ways this server answers them.
/// <para>
/// Entries live only for the length of one validation: added just before the challenge is triggered,
/// removed when the authorization settles. Nothing here is persisted — a restart mid-order simply
/// restarts the order.
/// </para>
/// </summary>
sealed class AcmeChallengeResponder
{
    /// <summary>RFC 8737 §6.1: id-pe-acmeIdentifier.</summary>
    public const string AcmeIdentifierOid = "1.3.6.1.5.5.7.1.31";

    public const string HttpChallengePrefix = "/.well-known/acme-challenge/";

    public static readonly SslApplicationProtocol AcmeTls1 = new("acme-tls/1");

    readonly ConcurrentDictionary<string, string> http = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, X509Certificate2> tlsAlpn = new(StringComparer.OrdinalIgnoreCase);

    public void AddHttp(string token, string keyAuthorization) => this.http[token] = keyAuthorization;

    public void RemoveHttp(string token) => this.http.TryRemove(token, out _);

    public void AddTlsAlpn(string name, string keyAuthorization)
    {
        var certificate = CreateTlsAlpnCertificate(name, keyAuthorization);
        if (this.tlsAlpn.TryGetValue(name, out var old))
            old.Dispose();

        this.tlsAlpn[name] = certificate;
    }

    public void RemoveTlsAlpn(string name)
    {
        if (this.tlsAlpn.TryRemove(name, out var certificate))
            certificate.Dispose();
    }

    public bool TryGetHttp(string token, out string keyAuthorization)
        => this.http.TryGetValue(token, out keyAuthorization!);

    /// <summary>
    /// The <see cref="HttpsOptions.ChallengeResponder"/>. Only a hello that offers <c>acme-tls/1</c>
    /// <em>and</em> names a host with a pending challenge is answered; everything else, including a
    /// stray <c>acme-tls/1</c> probe for a name we are not validating, is left to the normal handshake
    /// — which has no protocol in common with it and refuses it.
    /// </summary>
    public TlsChallengeResponse? Respond(TlsClientHello hello)
    {
        if (this.tlsAlpn.IsEmpty || hello.ServerName is not { } name || !hello.Offers(AcmeTls1))
            return null;

        return this.tlsAlpn.TryGetValue(name, out var certificate)
            ? new TlsChallengeResponse(certificate, AcmeTls1)
            : null;
    }

    /// <summary>
    /// The TLS-ALPN-01 certificate (RFC 8737 §3): self-signed, the name as its only SAN, and a critical
    /// acmeIdentifier extension whose value is the DER OCTET STRING of SHA-256(key authorization).
    /// The CA checks exactly those three things and nothing else about it.
    /// </summary>
    internal static X509Certificate2 CreateTlsAlpnCertificate(string name, string keyAuthorization)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(name);
        request.CertificateExtensions.Add(san.Build());

        var writer = new AsnWriter(AsnEncodingRules.DER);
        writer.WriteOctetString(SHA256.HashData(Encoding.ASCII.GetBytes(keyAuthorization)));
        request.CertificateExtensions.Add(new X509Extension(AcmeIdentifierOid, writer.Encode(), critical: true));

        var now = DateTimeOffset.UtcNow;
        using var generated = request.CreateSelfSigned(now.AddDays(-1), now.AddDays(7));

        // Round-tripped through PKCS#12 for the same reason ServerCertificate does it: on Apple
        // platforms a freshly created key is not usable as a server credential until reloaded.
        return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
    }
}
