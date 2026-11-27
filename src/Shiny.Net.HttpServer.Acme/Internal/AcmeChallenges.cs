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
    // Per name, every TLS-ALPN-01 certificate currently pending, newest last. A list rather than one
    // slot because a registry's entries share this responder and may cover the same name: two orders
    // validating it at once each add their own certificate, and with one slot the second overwrote -
    // and disposed - the first's mid-validation, and the first's cleanup then removed the second's.
    readonly Dictionary<string, List<X509Certificate2>> tlsAlpn = new(StringComparer.OrdinalIgnoreCase);

    public void AddHttp(string token, string keyAuthorization) => this.http[token] = keyAuthorization;

    public void RemoveHttp(string token) => this.http.TryRemove(token, out _);

    /// <summary>Adds a TLS-ALPN-01 certificate for <paramref name="name"/>; hand the result back to <see cref="RemoveTlsAlpn"/>.</summary>
    public X509Certificate2 AddTlsAlpn(string name, string keyAuthorization)
    {
        var certificate = CreateTlsAlpnCertificate(name, keyAuthorization);

        lock (this.tlsAlpn)
        {
            if (!this.tlsAlpn.TryGetValue(name, out var pending))
            {
                pending = [];
                this.tlsAlpn[name] = pending;
            }

            pending.Add(certificate);
        }

        return certificate;
    }

    /// <summary>Removes the certificate <see cref="AddTlsAlpn"/> returned, and only that one.</summary>
    public void RemoveTlsAlpn(string name, X509Certificate2 certificate)
    {
        lock (this.tlsAlpn)
        {
            if (this.tlsAlpn.TryGetValue(name, out var pending) && pending.Remove(certificate) && pending.Count == 0)
                this.tlsAlpn.Remove(name);
        }

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
    /// <remarks>
    /// With two challenges pending for one name there is no telling from the hello which the CA is
    /// validating - TLS-ALPN-01 carries no token - so the newest is served. The older order's
    /// validation may then fail and be retried, which is the CA's to judge; what no longer happens is
    /// one order tearing down the other's certificate.
    /// </remarks>
    public TlsChallengeResponse? Respond(TlsClientHello hello)
    {
        if (hello.ServerName is not { } name || !hello.Offers(AcmeTls1))
            return null;

        lock (this.tlsAlpn)
        {
            return this.tlsAlpn.TryGetValue(name, out var pending) && pending.Count > 0
                ? new TlsChallengeResponse(pending[^1], AcmeTls1)
                : null;
        }
    }

    /// <summary>The certificate a hello for <paramref name="name"/> would be answered with now, for tests.</summary>
    internal X509Certificate2? PendingTlsAlpn(string name)
    {
        lock (this.tlsAlpn)
            return this.tlsAlpn.TryGetValue(name, out var pending) && pending.Count > 0 ? pending[^1] : null;
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
