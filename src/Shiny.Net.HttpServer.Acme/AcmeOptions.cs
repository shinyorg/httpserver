namespace Shiny.Net.HttpServer.Acme;

/// <summary>
/// What to ask an ACME CA for, and how to prove the names are yours.
/// <code>
/// builder.Options.Listen(IPAddress.Any, 80);
/// builder.Options.Listen(IPAddress.Any, 443).Https = new HttpsOptions();   // certificate comes from ACME
///
/// builder.AddAcme(o =>
/// {
///     o.Domains = ["example.com", "www.example.com"];
///     o.Email = "ops@example.com";
///     o.AcceptTermsOfService = true;
///     o.DirectoryUrl = AcmeDirectories.LetsEncryptStaging;
/// });
/// </code>
/// <para>
/// <b>Who this is for.</b> A desktop, server or Linux box that the CA can reach at the names being
/// requested — public DNS pointing at it, port 80 (HTTP-01) or 443 (TLS-ALPN-01) open to the internet.
/// A phone almost never qualifies: it sits behind carrier NAT with no public address and no DNS name,
/// so there is nothing for the CA to connect to. For a device, use a self-signed certificate with
/// pinning (<see cref="ServerCertificate"/>) or a tunnel that terminates TLS for you.
/// </para>
/// </summary>
public sealed class AcmeOptions
{
    /// <summary>
    /// The names to put on the certificate — one certificate covering all of them. The first is the
    /// subject. IP addresses are accepted too (RFC 8738), for CAs that issue them; they validate over
    /// HTTP-01 only.
    /// <para>No wildcards: <c>*.example.com</c> needs the DNS-01 challenge, which this package does not do.</para>
    /// </summary>
    public IList<string> Domains { get; set; } = [];

    /// <summary>
    /// Contact address for the account. Optional for Let's Encrypt, but it is where expiry and
    /// incident notices go — and the only way the CA can reach you.
    /// </summary>
    public string? Email { get; set; }

    /// <summary>
    /// Agrees to the CA's terms of service (the URL is in its directory, and logged when the account is
    /// created). Must be true: every public CA refuses to open an account without it, and agreeing on
    /// your behalf is not something a library should do silently.
    /// </summary>
    public bool AcceptTermsOfService { get; set; }

    /// <summary>
    /// The CA's directory URL. Defaults to Let's Encrypt production; point it at
    /// <see cref="AcmeDirectories.LetsEncryptStaging"/> until the setup is known to work.
    /// </summary>
    public string DirectoryUrl { get; set; } = AcmeDirectories.LetsEncrypt;

    /// <summary>
    /// External Account Binding (RFC 8555 §7.3.4) — required by ZeroSSL and most commercial CAs,
    /// which tie the ACME account to one in their own system. Leave null for Let's Encrypt.
    /// </summary>
    public AcmeExternalAccountBinding? ExternalAccountBinding { get; set; }

    /// <summary>
    /// Which challenges may be used, in the preference order HTTP-01 then TLS-ALPN-01.
    /// <para>
    /// HTTP-01 (the default) needs this server reachable on <b>port 80</b> at every name — the CA always
    /// connects to port 80, whatever port you would rather use. TLS-ALPN-01 needs it on <b>port 443</b>
    /// with an HTTPS endpoint that uses the ACME certificate. Enable TLS-ALPN-01 when port 80 is closed.
    /// A CA gives one attempt per authorization, so there is no falling back from one to the other
    /// mid-order.
    /// </para>
    /// </summary>
    public AcmeChallengeTypes Challenges { get; set; } = AcmeChallengeTypes.Http01;

    /// <summary>The certificate's key type. ECDSA P-256 by default: smaller, faster and universally supported.</summary>
    public AcmeKeyAlgorithm KeyAlgorithm { get; set; } = AcmeKeyAlgorithm.EcdsaP256;

    /// <summary>RSA key size when <see cref="KeyAlgorithm"/> is <see cref="AcmeKeyAlgorithm.Rsa"/>.</summary>
    public int RsaKeySize { get; set; } = 2048;

    /// <summary>
    /// A certificate profile to request, for CAs that offer them (Let's Encrypt's <c>shortlived</c>,
    /// <c>tlsserver</c>). Null leaves it to the CA.
    /// </summary>
    public string? Profile { get; set; }

    /// <summary>
    /// Where the account key and certificates live when <see cref="Store"/> is not set. Defaults to
    /// <c>{LocalApplicationData}/Shiny.HttpServer/acme</c>. The files hold private keys and are
    /// written owner-only.
    /// </summary>
    public string? StorePath { get; set; }

    /// <summary>A custom store — a database, a secret manager, a shared volume. Overrides <see cref="StorePath"/>.</summary>
    public IAcmeCertificateStore? Store { get; set; }

    /// <summary>
    /// How far into its lifetime a certificate is renewed, when the CA does not suggest a time through
    /// ACME Renewal Information. Two thirds is Let's Encrypt's own advice: 60 days into a 90-day
    /// certificate, 4 days into a 6-day one — early enough that a failing renewal has weeks of retries.
    /// </summary>
    public double RenewAfterLifetimeFraction { get; set; } = 2.0 / 3.0;

    /// <summary>
    /// Ask the CA when to renew (ACME Renewal Information, RFC 9773) when its directory offers it. The
    /// CA's window wins over <see cref="RenewAfterLifetimeFraction"/> — which is how a CA gets a
    /// certificate it has to revoke replaced before it is revoked.
    /// </summary>
    public bool UseRenewalInfo { get; set; } = true;

    /// <summary>How often the background loop wakes to reconsider renewal. Jittered by ±10%.</summary>
    public TimeSpan RenewalCheckInterval { get; set; } = TimeSpan.FromHours(12);

    /// <summary>
    /// Delay before retrying a failed issuance; doubles per consecutive failure up to
    /// <see cref="MaxRetryDelay"/>, and never undercuts a <c>Retry-After</c> the CA sent.
    /// </summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Ceiling for the retry backoff.</summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromHours(12);

    /// <summary>
    /// Interval between polls of a pending authorization or order when the CA does not send
    /// <c>Retry-After</c>.
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How long one issuance may take end to end before it is abandoned and retried later.</summary>
    public TimeSpan OrderTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The handler behind the client that talks to the CA — for a proxy, or, in tests, a CA that is not
    /// on the internet. Null uses a default <see cref="HttpClientHandler"/>.
    /// </summary>
    public HttpMessageHandler? BackchannelHttpHandler { get; set; }

    internal string ResolveStorePath() => this.StorePath
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
            "Shiny.HttpServer",
            "acme"
        );
}

/// <summary>External Account Binding credentials, as issued by the CA's dashboard.</summary>
public sealed class AcmeExternalAccountBinding
{
    public AcmeExternalAccountBinding()
    {
    }

    public AcmeExternalAccountBinding(string keyId, string hmacKey)
    {
        this.KeyId = keyId;
        this.HmacKey = hmacKey;
    }

    /// <summary>The EAB key identifier ("EAB KID").</summary>
    public string KeyId { get; set; } = "";

    /// <summary>The EAB HMAC key, base64url-encoded, exactly as the CA shows it.</summary>
    public string HmacKey { get; set; } = "";
}

/// <summary>The ACME challenges this server can answer by itself.</summary>
[Flags]
public enum AcmeChallengeTypes
{
    /// <summary>
    /// <c>http-01</c>: the CA fetches <c>http://{name}/.well-known/acme-challenge/{token}</c> — port 80,
    /// always.
    /// </summary>
    Http01 = 1,

    /// <summary>
    /// <c>tls-alpn-01</c> (RFC 8737): the CA opens TLS to <c>{name}:443</c> offering only the
    /// <c>acme-tls/1</c> protocol, and expects a special self-signed certificate.
    /// </summary>
    TlsAlpn01 = 2
}

/// <summary>The certificate key type.</summary>
public enum AcmeKeyAlgorithm
{
    EcdsaP256,
    EcdsaP384,
    Rsa
}
