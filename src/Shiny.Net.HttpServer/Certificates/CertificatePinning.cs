using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Shiny.Net.HttpServer;

/// <summary>
/// Lets a client trust one specific server certificate without that certificate being trusted by
/// the device.
/// <para>
/// This is the answer to the awkward half of embedded HTTPS. Installing a certificate into a device
/// trust store is a manual ceremony on iOS and impossible to do properly for apps on Android — but
/// none of that is needed when the client already knows exactly which certificate to expect, which
/// is the case whenever an app talks to a server it is itself hosting. Chain validation is skipped
/// and replaced with something stricter: this key or no connection.
/// </para>
/// <code>
/// var certificate = ServerCertificate.CreateOrLoad(path);
/// options.ListenHttps(IPAddress.Loopback, 5001, certificate);
///
/// // ...and on the client side, in the same app
/// using var http = new HttpClient(CertificatePinning.CreateHandler(certificate))
/// {
///     BaseAddress = new Uri("https://127.0.0.1:5001")
/// };
/// </code>
/// <para>
/// Only for clients you control. A browser cannot be pinned, and nothing here helps it.
/// </para>
/// </summary>
public static class CertificatePinning
{
    /// <summary>
    /// The base64 SHA-256 of the certificate's SubjectPublicKeyInfo — the same value HTTP Public Key
    /// Pinning used, and what <see cref="CreateValidator(string[])"/> compares against.
    /// <para>
    /// Pinning the key rather than the whole certificate means a renewal that keeps the key does not
    /// break clients. Ship this string, not the certificate, when all the client needs to do is
    /// recognise the server.
    /// </para>
    /// </summary>
    public static string GetPublicKeyPin(this X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        var spki = certificate.PublicKey.ExportSubjectPublicKeyInfo();
        return Convert.ToBase64String(SHA256.HashData(spki));
    }

    /// <summary>
    /// A validation callback accepting only servers presenting one of <paramref name="pins"/>, as
    /// produced by <see cref="GetPublicKeyPin"/>.
    /// <para>
    /// Chain and name errors are ignored on purpose — a self-signed certificate produces both, and
    /// the pin is what is actually being checked. Everything else is rejected, including a server
    /// that presents no certificate at all.
    /// </para>
    /// </summary>
    public static RemoteCertificateValidationCallback CreateValidator(params string[] pins)
    {
        ArgumentNullException.ThrowIfNull(pins);

        if (pins.Length == 0)
            throw new ArgumentException("At least one pin is required.", nameof(pins));

        // Copied so a later mutation of the caller's array cannot quietly widen what is trusted.
        var expected = new HashSet<string>(pins, StringComparer.Ordinal);

        return (_, certificate, _, _) =>
        {
            if (certificate is null)
                return false;

            using var presented = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
            return expected.Contains(presented.GetPublicKeyPin());
        };
    }

    /// <summary>
    /// A validation callback accepting only servers presenting one of <paramref name="certificates"/>,
    /// compared by public key.
    /// </summary>
    public static RemoteCertificateValidationCallback CreateValidator(params X509Certificate2[] certificates)
    {
        ArgumentNullException.ThrowIfNull(certificates);

        if (certificates.Length == 0)
            throw new ArgumentException("At least one certificate is required.", nameof(certificates));

        return CreateValidator([.. certificates.Select(x => x.GetPublicKeyPin())]);
    }

    /// <summary>
    /// An <see cref="HttpMessageHandler"/> that trusts only <paramref name="certificate"/>. Hand it
    /// to <see cref="HttpClient"/> and the device trust store never comes into it.
    /// </summary>
    /// <remarks>
    /// Not usable on Blazor WebAssembly, where TLS belongs to the browser and no handler can
    /// override it.
    /// </remarks>
    public static HttpMessageHandler CreateHandler(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return CreateHandler(certificate.GetPublicKeyPin());
    }

    /// <summary>
    /// An <see cref="HttpMessageHandler"/> trusting only servers matching one of
    /// <paramref name="pins"/>, as produced by <see cref="GetPublicKeyPin"/>.
    /// </summary>
    public static HttpMessageHandler CreateHandler(params string[] pins) =>
        new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = CreateValidator(pins)
            }
        };
}
