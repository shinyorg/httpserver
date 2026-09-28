namespace Shiny.Net.HttpServer.Acme;

/// <summary>
/// Directory URLs of the public ACME CAs. Any other RFC 8555 CA works too — pass its directory URL
/// to <see cref="AcmeOptions.DirectoryUrl"/>.
/// </summary>
public static class AcmeDirectories
{
    /// <summary>
    /// Let's Encrypt production. Certificates are trusted everywhere, and the rate limits are real:
    /// five failed validations per name per hour, five duplicate certificates per week. Get the setup
    /// right against <see cref="LetsEncryptStaging"/> first.
    /// </summary>
    public const string LetsEncrypt = "https://acme-v02.api.letsencrypt.org/directory";

    /// <summary>
    /// Let's Encrypt staging. Same protocol and behaviour, far higher rate limits, and certificates
    /// signed by a root no client trusts — which is exactly what you want while testing.
    /// </summary>
    public const string LetsEncryptStaging = "https://acme-staging-v02.api.letsencrypt.org/directory";

    /// <summary>
    /// ZeroSSL. Requires External Account Binding: create EAB credentials in the ZeroSSL dashboard
    /// and set <see cref="AcmeOptions.ExternalAccountBinding"/>.
    /// </summary>
    public const string ZeroSsl = "https://acme.zerossl.com/v2/DV90";
}
