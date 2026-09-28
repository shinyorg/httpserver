using System.Buffers.Text;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Shiny.Net.HttpServer.Acme.Internal;

/// <summary>A name to validate: a DNS name, or an IP address (RFC 8738).</summary>
readonly record struct AcmeIdentifier(string Type, string Value)
{
    public const string Dns = "dns";
    public const string Ip = "ip";

    /// <summary>
    /// Validates and normalizes the configured names: lower-case, IDNs to their A-label form (the only
    /// form a CA or a certificate accepts), duplicates removed, and wildcards refused up front rather
    /// than after a round trip to the CA.
    /// </summary>
    public static IReadOnlyList<AcmeIdentifier> Parse(IEnumerable<string> names)
    {
        var result = new List<AcmeIdentifier>();
        var idn = new IdnMapping();

        foreach (var raw in names)
        {
            var name = raw?.Trim().TrimEnd('.') ?? "";
            if (name.Length == 0)
                continue;

            if (name.StartsWith("*.", StringComparison.Ordinal))
                throw new AcmeException(
                    $"'{name}' is a wildcard. Wildcard certificates can only be validated with the DNS-01 challenge, " +
                    "which needs write access to your DNS and is not something this server can answer. List the names explicitly."
                );

            AcmeIdentifier identifier;
            if (IPAddress.TryParse(name.Trim('[', ']'), out var address))
                identifier = new AcmeIdentifier(Ip, address.ToString());
            else
            {
                string ascii;
                try
                {
                    ascii = idn.GetAscii(name).ToLowerInvariant();
                }
                catch (ArgumentException ex)
                {
                    throw new AcmeException($"'{name}' is not a valid DNS name.", ex);
                }

                identifier = new AcmeIdentifier(Dns, ascii);
            }

            if (!result.Contains(identifier))
                result.Add(identifier);
        }

        if (result.Count == 0)
            throw new AcmeException($"No names to certify. Set {nameof(AcmeOptions)}.{nameof(AcmeOptions.Domains)}.");

        return result;
    }
}

/// <summary>Everything done to certificates: keys, CSRs, chains, and reading them back.</summary>
static class AcmeCertificates
{
    /// <summary>
    /// The name a certificate is stored under: the first identifier, plus a short hash of the whole
    /// set, so changing <see cref="AcmeOptions.Domains"/> produces a new file rather than loading a
    /// certificate that no longer covers what was asked for.
    /// </summary>
    public static string StorageName(IReadOnlyList<AcmeIdentifier> identifiers)
    {
        var all = string.Join(",", identifiers.Select(x => x.Value).Order(StringComparer.Ordinal));
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(all)))[..8];

        return $"{identifiers[0].Value.Replace(':', '_')}-{hash}";
    }

    public static AsymmetricAlgorithm CreateKey(AcmeOptions options) => options.KeyAlgorithm switch
    {
        AcmeKeyAlgorithm.EcdsaP256 => ECDsa.Create(ECCurve.NamedCurves.nistP256),
        AcmeKeyAlgorithm.EcdsaP384 => ECDsa.Create(ECCurve.NamedCurves.nistP384),
        AcmeKeyAlgorithm.Rsa => RSA.Create(Math.Max(2048, options.RsaKeySize)),
        _ => throw new ArgumentOutOfRangeException(nameof(options), options.KeyAlgorithm, "Unknown key algorithm.")
    };

    /// <summary>
    /// The certificate signing request: every name as a SAN (the subject CN is advisory, and CAs
    /// ignore it), DER encoded, base64url'd for the finalize payload.
    /// </summary>
    public static string CreateCsr(AsymmetricAlgorithm key, IReadOnlyList<AcmeIdentifier> identifiers)
    {
        var subject = new X500DistinguishedName("CN=" + identifiers[0].Value);

        var request = key switch
        {
            ECDsa ecdsa => new CertificateRequest(subject, ecdsa, ecdsa.KeySize > 256 ? HashAlgorithmName.SHA384 : HashAlgorithmName.SHA256),
            RSA rsa => new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
            _ => throw new ArgumentException("Unsupported key.", nameof(key))
        };

        var san = new SubjectAlternativeNameBuilder();
        foreach (var identifier in identifiers)
        {
            if (identifier.Type == AcmeIdentifier.Ip)
                san.AddIpAddress(IPAddress.Parse(identifier.Value));
            else
                san.AddDnsName(identifier.Value);
        }

        request.CertificateExtensions.Add(san.Build());

        return Base64Url.EncodeToString(request.CreateSigningRequest());
    }

    /// <summary>
    /// Pairs the downloaded PEM chain with the key that was used for the CSR and packs the lot into
    /// PKCS#12 — the form it is stored in, and loaded from, from here on.
    /// </summary>
    public static byte[] BuildPkcs12(string pemChain, AsymmetricAlgorithm key)
    {
        var chain = new X509Certificate2Collection();
        chain.ImportFromPem(pemChain);

        if (chain.Count == 0)
            throw new AcmeException("The CA's certificate download contained no certificates.");

        // RFC 8555 §7.4.2: the end-entity certificate comes first.
        using var leaf = chain[0];
        using var withKey = key switch
        {
            ECDsa ecdsa => leaf.CopyWithPrivateKey(ecdsa),
            RSA rsa => leaf.CopyWithPrivateKey(rsa),
            _ => throw new ArgumentException("Unsupported key.", nameof(key))
        };

        var bundle = new X509Certificate2Collection { withKey };
        for (var i = 1; i < chain.Count; i++)
            bundle.Add(chain[i]);

        var pkcs12 = bundle.Export(X509ContentType.Pkcs12)
            ?? throw new AcmeException("Exporting the issued certificate failed.");

        for (var i = 1; i < chain.Count; i++)
            chain[i].Dispose();

        return pkcs12;
    }

    /// <summary>
    /// Loads a stored PKCS#12 into what the TLS layer serves: the leaf with its key, and a certificate
    /// context carrying the intermediates explicitly. <c>offline</c>, because the chain is right here and
    /// fetching it again over AIA on a server's hot path would only add a failure mode.
    /// </summary>
    public static AcmeIssuedCertificate Load(byte[] pkcs12)
    {
        var collection = X509CertificateLoader.LoadPkcs12Collection(pkcs12, null, X509KeyStorageFlags.Exportable);

        X509Certificate2? leaf = null;
        var intermediates = new X509Certificate2Collection();

        foreach (var certificate in collection)
        {
            if (leaf is null && certificate.HasPrivateKey)
                leaf = certificate;
            else
                intermediates.Add(certificate);
        }

        if (leaf is null)
        {
            foreach (var certificate in collection)
                certificate.Dispose();

            throw new AcmeException("The stored certificate has no private key.");
        }

        var context = SslStreamCertificateContext.Create(leaf, intermediates, offline: true);
        return new AcmeIssuedCertificate(leaf, intermediates, context);
    }

    /// <summary>
    /// The certificate identifier ACME Renewal Information uses (RFC 9773 §4.1):
    /// base64url(authority key identifier) "." base64url(serial number), or null when the certificate
    /// has no AKI (a CA's certificate always does).
    /// </summary>
    public static string? RenewalInfoId(X509Certificate2 certificate)
    {
        foreach (var extension in certificate.Extensions)
        {
            if (extension is X509AuthorityKeyIdentifierExtension aki && aki.KeyIdentifier is { } keyId)
                return Base64Url.EncodeToString(keyId.Span) + "." + Base64Url.EncodeToString(certificate.SerialNumberBytes.Span);
        }

        return null;
    }

    /// <summary>The DNS names and IP addresses the certificate covers.</summary>
    public static HashSet<string> Names(X509Certificate2 certificate)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var extension in certificate.Extensions)
        {
            if (extension is X509SubjectAlternativeNameExtension san)
            {
                foreach (var dns in san.EnumerateDnsNames())
                    names.Add(dns);

                foreach (var ip in san.EnumerateIPAddresses())
                    names.Add(ip.ToString());
            }
        }

        return names;
    }
}

/// <summary>
/// A certificate ready to serve, with its chain.
/// <para>
/// Never disposed once it has been served. A handshake that selected it a moment before a swap may
/// still be using it; the old one is left for the GC once the last reference goes.
/// </para>
/// </summary>
sealed class AcmeIssuedCertificate(
    X509Certificate2 certificate,
    X509Certificate2Collection intermediates,
    SslStreamCertificateContext context
)
{
    public X509Certificate2 Certificate { get; } = certificate;
    public X509Certificate2Collection Intermediates { get; } = intermediates;
    public SslStreamCertificateContext Context { get; } = context;

    public DateTimeOffset NotBefore => new(this.Certificate.NotBefore.ToUniversalTime(), TimeSpan.Zero);
    public DateTimeOffset NotAfter => new(this.Certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero);
}
