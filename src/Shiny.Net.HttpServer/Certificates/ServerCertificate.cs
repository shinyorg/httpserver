using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Shiny.Net.HttpServer;

/// <summary>
/// Makes the certificate an embedded server needs, because nothing else will.
/// <para>
/// A public CA cannot issue for <c>192.168.1.42</c> or for a phone that moves between networks, so a
/// server living inside an app has to sign its own. That is not a compromise as long as the client
/// is the same app: pin the certificate with <see cref="CertificatePinning"/> and the connection is
/// authenticated more strictly than the public PKI would manage. It only becomes a problem when the
/// client is a browser, which will refuse it until someone installs it by hand — see the remarks.
/// </para>
/// <para>
/// Everything here is managed <see cref="System.Security.Cryptography"/>: no OpenSSL, no platform
/// tooling, nothing to ship alongside the app, and it runs on iOS and Android unchanged.
/// </para>
/// </summary>
/// <remarks>
/// Getting a self-signed certificate <i>trusted</i> is per-client, and the answer differs:
/// <list type="bullet">
/// <item>
/// <b>Your own app's HttpClient</b> — nothing to install. Pin it with
/// <see cref="CertificatePinning.CreateHandler(X509Certificate2)"/>.
/// </item>
/// <item>
/// <b>A browser or WebView</b> — the certificate has to be installed and trusted on each device.
/// On iOS that is a profile install <i>plus</i> a separate switch under Settings › General › About ›
/// Certificate Trust Settings. On Android 7+ a user-installed CA is trusted by Chrome but not by
/// apps, which additionally need a <c>network_security_config</c> opting in.
/// </item>
/// <item>
/// <b>Anything on the public internet</b> — do not use this. Terminate TLS at a tunnel or relay with
/// a real certificate and let the server speak cleartext behind it.
/// </item>
/// </list>
/// </remarks>
public static class ServerCertificate
{
    // serverAuth. Apple has rejected server certificates without it since iOS 13, and it costs
    // nothing to always be correct here.
    const string ServerAuthenticationOid = "1.3.6.1.5.5.7.3.1";

    // clientAuth. Needed on the client's certificate for mutual TLS — .NET will not offer a
    // certificate that does not claim it.
    const string ClientAuthenticationOid = "1.3.6.1.5.5.7.3.2";

    /// <summary>
    /// Generates a certificate valid for this device.
    /// <code>
    /// var certificate = ServerCertificate.Create();
    /// options.ListenHttps(IPAddress.Any, 5001, certificate);
    /// </code>
    /// The result is not persisted; every call produces a new key. Use
    /// <see cref="CreateOrLoad(string, Action{ServerCertificateOptions}?)"/> when clients pin it and
    /// it therefore has to survive a restart.
    /// </summary>
    public static X509Certificate2 Create(Action<ServerCertificateOptions>? configure = null)
    {
        var options = new ServerCertificateOptions();
        configure?.Invoke(options);

        return Create(options);
    }

    /// <summary>Generates a certificate from an already-populated options object.</summary>
    public static X509Certificate2 Create(ServerCertificateOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var subject = new X500DistinguishedName($"CN={options.CommonName}");

        CertificateRequest request;
        AsymmetricAlgorithm key;

        if (options.KeyAlgorithm == CertificateKeyAlgorithm.EcdsaP256)
        {
            var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            key = ecdsa;
            request = new CertificateRequest(subject, ecdsa, HashAlgorithmName.SHA256);
        }
        else
        {
            var rsa = RSA.Create(options.RsaKeySize);
            key = rsa;
            request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }

        using (key)
        {
            // Not a CA. Marked critical so a client that installs this cannot be talked into
            // treating it as one and trusting anything it appears to have signed.
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));

            request.CertificateExtensions.Add(
                new X509KeyUsageExtension(
                    X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                    critical: true
                )
            );

            var usages = new OidCollection { new(ServerAuthenticationOid) };
            if (options.AllowClientAuthentication)
                usages.Add(new Oid(ClientAuthenticationOid));

            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages, critical: false));

            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

            // The subject alternative name is the only name modern clients look at — CN has been
            // ignored by browsers and by Apple for years. A certificate without one is useless.
            var sanBuilder = new SubjectAlternativeNameBuilder();
            var names = ResolveNames(options, out var addresses);

            foreach (var name in names)
                sanBuilder.AddDnsName(name);

            foreach (var address in addresses)
                sanBuilder.AddIpAddress(address);

            request.CertificateExtensions.Add(sanBuilder.Build());

            // Backdated an hour so a client whose clock runs slow does not reject a certificate
            // generated moments ago.
            var notBefore = DateTimeOffset.UtcNow.AddHours(-1);
            var notAfter = notBefore.Add(options.Lifetime);

            using var generated = request.CreateSelfSigned(notBefore, notAfter);

            // Round-tripped through PKCS#12 rather than returned directly. On Apple platforms the
            // key on a freshly created certificate is not in a form SslStream can use as a server
            // credential; exporting and reloading puts it in one. Harmless everywhere else.
            var password = options.ExportPassword ?? string.Empty;
            var pkcs12 = generated.Export(X509ContentType.Pkcs12, password);

            return X509CertificateLoader.LoadPkcs12(pkcs12, password, X509KeyStorageFlags.Exportable);
        }
    }

    /// <summary>
    /// Loads the certificate stored at <paramref name="path"/>, generating and saving a new one when
    /// the file is missing, unreadable, or expired.
    /// <code>
    /// var path = Path.Combine(FileSystem.AppDataDirectory, "server.pfx");
    /// var certificate = ServerCertificate.CreateOrLoad(path);
    /// </code>
    /// <para>
    /// Stability is the point: a client that pinned the certificate keeps working across app
    /// restarts. The file holds an unprotected private key unless
    /// <see cref="ServerCertificateOptions.ExportPassword"/> is set. It is written owner-only and
    /// replaced atomically, but that is defence in depth — put it somewhere only the app can read.
    /// On iOS and Android the app's own data directory already is that.
    /// </para>
    /// </summary>
    public static X509Certificate2 CreateOrLoad(string path, Action<ServerCertificateOptions>? configure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var options = new ServerCertificateOptions();
        configure?.Invoke(options);

        var password = options.ExportPassword ?? string.Empty;

        if (File.Exists(path))
        {
            try
            {
                var existing = X509CertificateLoader.LoadPkcs12FromFile(path, password, X509KeyStorageFlags.Exportable);

                // Renewed early rather than on the day it expires: a certificate that dies while the
                // app is running takes every connection with it.
                if (existing.NotAfter.ToUniversalTime() > DateTime.UtcNow.Add(options.RenewBefore))
                    return existing;

                existing.Dispose();
            }
            catch (CryptographicException)
            {
                // Corrupt, or written with a different password. Either way it is unusable and the
                // only useful thing left to do is replace it.
            }
        }

        var certificate = Create(options);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        Save(path, certificate.Export(X509ContentType.Pkcs12, password));

        return certificate;
    }

    /// <summary>
    /// Writes the key material to a file only its owner can read, and puts it in place atomically.
    /// <para>
    /// Both halves matter. A private key inherits the process umask otherwise, which on a desktop
    /// means world-readable; and a write interrupted partway through leaves a corrupt file, which
    /// this class recovers from by generating a <i>different</i> certificate — silently breaking
    /// every client that had pinned the old one. Writing beside the target and renaming means the
    /// file at <paramref name="path"/> is only ever a complete one.
    /// </para>
    /// </summary>
    static void Save(string path, byte[] pkcs12)
    {
        var temporary = path + ".tmp";

        var stream = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None
        };

        // Set at creation rather than chmod'ed afterwards, so there is no window in which the key
        // sits on disk readable by anyone else.
        if (!OperatingSystem.IsWindows())
            stream.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        try
        {
            using (var file = new FileStream(temporary, stream))
                file.Write(pkcs12);

            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temporary);
            }
            catch (IOException)
            {
            }

            throw;
        }
    }

    /// <summary>
    /// The certificate without its private key, DER encoded — what you hand to someone who has to
    /// install it, or serve for download so a browser on the network can trust this server.
    /// </summary>
    public static byte[] ExportPublicCertificate(this X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return certificate.Export(X509ContentType.Cert);
    }

    static List<string> ResolveNames(ServerCertificateOptions options, out List<IPAddress> addresses)
    {
        var names = new List<string>(options.DnsNames);
        addresses = [.. options.IPAddresses];

        if (options.IncludeLocalAddresses)
        {
            AddIfMissing(names, "localhost");
            AddIfMissing(addresses, IPAddress.Loopback);
            AddIfMissing(addresses, IPAddress.IPv6Loopback);

            try
            {
                AddIfMissing(names, Dns.GetHostName());
            }
            catch (SocketException)
            {
                // Some sandboxed environments have no resolvable host name. The addresses below
                // are what actually matters.
            }

            foreach (var address in EnumerateLocalAddresses())
                AddIfMissing(addresses, address);
        }

        if (names.Count == 0 && addresses.Count == 0)
            throw new InvalidOperationException(
                $"A server certificate needs at least one name. Add to {nameof(ServerCertificateOptions.DnsNames)} " +
                $"or {nameof(ServerCertificateOptions.IPAddresses)}, or leave " +
                $"{nameof(ServerCertificateOptions.IncludeLocalAddresses)} on."
            );

        return names;
    }

    static IEnumerable<IPAddress> EnumerateLocalAddresses()
    {
        NetworkInterface[] interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            yield break;
        }

        foreach (var adapter in interfaces)
        {
            if (adapter.OperationalStatus != OperationalStatus.Up)
                continue;

            if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;

            foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
            {
                var address = unicast.Address;

                // IPv6 link-local addresses carry a scope id that means nothing to a client on
                // another host, so putting one in a certificate helps no one.
                if (address.IsIPv6LinkLocal || address.IsIPv6Multicast)
                    continue;

                if (address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                    yield return address;
            }
        }
    }

    static void AddIfMissing<T>(List<T> items, T value)
    {
        if (!items.Contains(value))
            items.Add(value);
    }
}

/// <summary>Shape of the certificate <see cref="ServerCertificate"/> generates.</summary>
public sealed class ServerCertificateOptions
{
    /// <summary>Subject common name. Cosmetic — clients match on the subject alternative name.</summary>
    public string CommonName { get; set; } = "Shiny.Net.HttpServer";

    /// <summary>Extra DNS names to cover, beyond the ones <see cref="IncludeLocalAddresses"/> adds.</summary>
    public IList<string> DnsNames { get; } = [];

    /// <summary>Extra IP addresses to cover, beyond the ones <see cref="IncludeLocalAddresses"/> adds.</summary>
    public IList<IPAddress> IPAddresses { get; } = [];

    /// <summary>
    /// Adds localhost, this device's host name, and every address on an up, non-loopback interface.
    /// On by default, because a server that is only reachable at an address the certificate does not
    /// name is a server nobody can talk to.
    /// <para>
    /// A phone's address changes with the network it joins, so a long-lived certificate will
    /// eventually not name the address it is being reached on. Regenerate on network change, or
    /// pin the certificate and stop caring what it is named.
    /// </para>
    /// </summary>
    public bool IncludeLocalAddresses { get; set; } = true;

    /// <summary>
    /// How long the certificate is valid. Just under Apple's 398-day ceiling by default — a longer
    /// one is rejected outright by iOS and macOS regardless of whether it is trusted.
    /// </summary>
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromDays(397);

    /// <summary>
    /// How close to expiry <see cref="ServerCertificate.CreateOrLoad(string, Action{ServerCertificateOptions}?)"/>
    /// replaces a stored certificate rather than reusing it.
    /// </summary>
    public TimeSpan RenewBefore { get; set; } = TimeSpan.FromDays(30);

    /// <summary>
    /// Key type. RSA by default: slower to generate, but there is no client anywhere that refuses it.
    /// </summary>
    public CertificateKeyAlgorithm KeyAlgorithm { get; set; } = CertificateKeyAlgorithm.Rsa;

    /// <summary>RSA key size. 2048 is the floor Apple and the CA/Browser Forum accept.</summary>
    public int RsaKeySize { get; set; } = 2048;

    /// <summary>
    /// Also marks the certificate for client authentication, which is what a certificate presented
    /// <i>to</i> a server needs — .NET will not offer one that does not claim it. Set this when
    /// generating the client half of a mutual-TLS pair.
    /// </summary>
    public bool AllowClientAuthentication { get; set; }

    /// <summary>
    /// Password for the PKCS#12 blob, used when persisting and reloading. Null means no password,
    /// which is normal for a file inside an app's private storage and wrong anywhere else.
    /// </summary>
    public string? ExportPassword { get; set; }
}

public enum CertificateKeyAlgorithm
{
    /// <summary>RSA. Universally accepted; generation costs a noticeable moment on a phone.</summary>
    Rsa,

    /// <summary>ECDSA on NIST P-256. Much faster to generate and smaller on the wire.</summary>
    EcdsaP256
}
