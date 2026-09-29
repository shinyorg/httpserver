using System.Security.Cryptography.X509Certificates;

namespace Shiny.Net.HttpServer.Acme;

/// <summary>
/// The settings every certificate in an <see cref="AcmeCertificateRegistry"/> shares — the CA, the
/// account, the store, the challenges and the renewal timing. The same knobs as <see cref="AcmeOptions"/>
/// without <see cref="AcmeOptions.Domains"/>: each entry brings its own names through
/// <see cref="AcmeCertificateRegistry.Set"/>.
/// <code>
/// var acme = new AcmeCertificateRegistry(new AcmeRegistryOptions
/// {
///     Email = "ops@example.com",
///     AcceptTermsOfService = true,
///     DirectoryUrl = AcmeDirectories.LetsEncryptStaging,
///     FallbackCertificate = ServerCertificate.Create()   // for SNI names no entry covers
/// });
/// </code>
/// </summary>
public sealed class AcmeRegistryOptions
{
    /// <inheritdoc cref="AcmeOptions.Email"/>
    public string? Email { get; set; }

    /// <inheritdoc cref="AcmeOptions.AcceptTermsOfService"/>
    public bool AcceptTermsOfService { get; set; }

    /// <inheritdoc cref="AcmeOptions.DirectoryUrl"/>
    public string DirectoryUrl { get; set; } = AcmeDirectories.LetsEncrypt;

    /// <inheritdoc cref="AcmeOptions.ExternalAccountBinding"/>
    public AcmeExternalAccountBinding? ExternalAccountBinding { get; set; }

    /// <inheritdoc cref="AcmeOptions.Challenges"/>
    public AcmeChallengeTypes Challenges { get; set; } = AcmeChallengeTypes.Http01;

    /// <inheritdoc cref="AcmeOptions.KeyAlgorithm"/>
    public AcmeKeyAlgorithm KeyAlgorithm { get; set; } = AcmeKeyAlgorithm.EcdsaP256;

    /// <inheritdoc cref="AcmeOptions.RsaKeySize"/>
    public int RsaKeySize { get; set; } = 2048;

    /// <inheritdoc cref="AcmeOptions.Profile"/>
    public string? Profile { get; set; }

    /// <inheritdoc cref="AcmeOptions.StorePath"/>
    public string? StorePath { get; set; }

    /// <inheritdoc cref="AcmeOptions.Store"/>
    public IAcmeCertificateStore? Store { get; set; }

    /// <inheritdoc cref="AcmeOptions.RenewAfterLifetimeFraction"/>
    public double RenewAfterLifetimeFraction { get; set; } = 2.0 / 3.0;

    /// <inheritdoc cref="AcmeOptions.UseRenewalInfo"/>
    public bool UseRenewalInfo { get; set; } = true;

    /// <inheritdoc cref="AcmeOptions.RenewalCheckInterval"/>
    public TimeSpan RenewalCheckInterval { get; set; } = TimeSpan.FromHours(12);

    /// <summary>
    /// Delay before retrying a failed <em>renewal</em>; doubles per consecutive failure up to
    /// <see cref="MaxRetryDelay"/>, and never undercuts a <c>Retry-After</c> the CA sent. A failed
    /// <em>first</em> issuance is never retried by the registry.
    /// </summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMinutes(10);

    /// <inheritdoc cref="AcmeOptions.MaxRetryDelay"/>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromHours(12);

    /// <inheritdoc cref="AcmeOptions.PollInterval"/>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <inheritdoc cref="AcmeOptions.OrderTimeout"/>
    public TimeSpan OrderTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <inheritdoc cref="AcmeOptions.BackchannelHttpHandler"/>
    public HttpMessageHandler? BackchannelHttpHandler { get; set; }

    /// <summary>
    /// Served to a TLS client whose SNI name no entry covers (or that sent no SNI at all), so it gets a
    /// certificate error it can report rather than a failed handshake. Must carry its private key. Null
    /// refuses those handshakes.
    /// </summary>
    public X509Certificate2? FallbackCertificate { get; set; }

    internal string ResolveStorePath() => this.ToOptions([]).ResolveStorePath();

    /// <summary>The per-entry options a manager runs with: these settings plus that entry's names.</summary>
    internal AcmeOptions ToOptions(IEnumerable<string> domains) => new()
    {
        Domains = [.. domains],
        Email = this.Email,
        AcceptTermsOfService = this.AcceptTermsOfService,
        DirectoryUrl = this.DirectoryUrl,
        ExternalAccountBinding = this.ExternalAccountBinding,
        Challenges = this.Challenges,
        KeyAlgorithm = this.KeyAlgorithm,
        RsaKeySize = this.RsaKeySize,
        Profile = this.Profile,
        StorePath = this.StorePath,
        Store = this.Store,
        RenewAfterLifetimeFraction = this.RenewAfterLifetimeFraction,
        UseRenewalInfo = this.UseRenewalInfo,
        RenewalCheckInterval = this.RenewalCheckInterval,
        RetryDelay = this.RetryDelay,
        MaxRetryDelay = this.MaxRetryDelay,
        PollInterval = this.PollInterval,
        OrderTimeout = this.OrderTimeout,
        BackchannelHttpHandler = this.BackchannelHttpHandler
    };
}
