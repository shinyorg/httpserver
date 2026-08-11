using System.Net;
using System.Security.Cryptography.X509Certificates;

namespace Shiny.Net.HttpServer.Tests;

/// <summary>
/// The generated certificate has to satisfy rules no test of ours enforces — Apple's validity
/// ceiling, the SAN requirement, the EKU requirement — and a certificate that violates one is
/// rejected on a device long after it passed here. So the extensions are asserted directly.
/// </summary>
public class ServerCertificateTests
{
    [Fact]
    public void Creates_a_certificate_with_a_usable_private_key()
    {
        using var certificate = ServerCertificate.Create();

        Assert.True(certificate.HasPrivateKey);
        Assert.Contains("Shiny.Net.HttpServer", certificate.Subject);
    }

    [Fact]
    public void Names_localhost_and_the_loopback_addresses()
    {
        using var certificate = ServerCertificate.Create();

        var (names, addresses) = GetSubjectAlternativeName(certificate);

        Assert.Contains("localhost", names);
        Assert.Contains(IPAddress.Loopback, addresses);
        Assert.Contains(IPAddress.IPv6Loopback, addresses);
    }

    [Fact]
    public void Covers_the_names_and_addresses_it_is_given()
    {
        using var certificate = ServerCertificate.Create(o =>
        {
            o.DnsNames.Add("phone.local");
            o.IPAddresses.Add(IPAddress.Parse("192.168.7.7"));
        });

        var (names, addresses) = GetSubjectAlternativeName(certificate);

        Assert.Contains("phone.local", names);
        Assert.Contains(IPAddress.Parse("192.168.7.7"), addresses);
    }

    [Fact]
    public void Can_be_limited_to_explicit_names_only()
    {
        using var certificate = ServerCertificate.Create(o =>
        {
            o.IncludeLocalAddresses = false;
            o.DnsNames.Add("only.this");
        });

        var (names, addresses) = GetSubjectAlternativeName(certificate);

        Assert.Equal(["only.this"], names);
        Assert.Empty(addresses);
    }

    [Fact]
    public void Refuses_to_produce_a_certificate_with_no_names()
    {
        // Without a subject alternative name nothing modern will accept it, so failing loudly here
        // beats failing mysteriously on a device.
        Assert.Throws<InvalidOperationException>(
            () => ServerCertificate.Create(o => o.IncludeLocalAddresses = false)
        );
    }

    [Fact]
    public void Is_marked_for_server_authentication()
    {
        using var certificate = ServerCertificate.Create();

        var eku = certificate.Extensions
            .OfType<X509EnhancedKeyUsageExtension>()
            .Single();

        Assert.Contains(eku.EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>(), x => x.Value == "1.3.6.1.5.5.7.3.1");
        Assert.DoesNotContain(eku.EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>(), x => x.Value == "1.3.6.1.5.5.7.3.2");
    }

    [Fact]
    public void Adds_client_authentication_on_request()
    {
        using var certificate = ServerCertificate.Create(o => o.AllowClientAuthentication = true);

        var eku = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single();

        Assert.Contains(eku.EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>(), x => x.Value == "1.3.6.1.5.5.7.3.2");
    }

    [Fact]
    public void Is_not_a_certificate_authority()
    {
        using var certificate = ServerCertificate.Create();

        var basic = certificate.Extensions.OfType<X509BasicConstraintsExtension>().Single();

        Assert.False(basic.CertificateAuthority);
        Assert.True(basic.Critical);
    }

    [Fact]
    public void Stays_under_the_398_day_ceiling_apple_enforces()
    {
        using var certificate = ServerCertificate.Create();

        var lifetime = certificate.NotAfter - certificate.NotBefore;

        Assert.True(lifetime <= TimeSpan.FromDays(398), $"Lifetime was {lifetime.TotalDays} days");
        Assert.True(certificate.NotBefore.ToUniversalTime() <= DateTime.UtcNow, "Should be backdated for clock skew");
    }

    [Fact]
    public void Generates_an_ecdsa_key_when_asked()
    {
        using var certificate = ServerCertificate.Create(o => o.KeyAlgorithm = CertificateKeyAlgorithm.EcdsaP256);

        Assert.True(certificate.HasPrivateKey);
        Assert.Equal("1.2.840.10045.2.1", certificate.PublicKey.Oid.Value);
    }

    [Fact]
    public void CreateOrLoad_returns_the_same_certificate_on_a_second_call()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "server.pfx");

        using var first = ServerCertificate.CreateOrLoad(path);
        using var second = ServerCertificate.CreateOrLoad(path);

        // The whole point of persisting: a pinned client keeps working across a restart.
        Assert.Equal(first.Thumbprint, second.Thumbprint);
        Assert.True(second.HasPrivateKey);
    }

    [Fact]
    public void CreateOrLoad_creates_the_directory_it_needs()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "nested", "deeper", "server.pfx");

        using var certificate = ServerCertificate.CreateOrLoad(path);

        Assert.True(File.Exists(path));
        Assert.True(certificate.HasPrivateKey);
    }

    [Fact]
    public void CreateOrLoad_replaces_a_certificate_that_is_near_expiry()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "server.pfx");

        using var first = ServerCertificate.CreateOrLoad(path);

        // A renewal window wider than the certificate's own lifetime makes it due immediately,
        // which is the same code path as one that genuinely aged out.
        using var second = ServerCertificate.CreateOrLoad(path, o => o.RenewBefore = TimeSpan.FromDays(10_000));

        Assert.NotEqual(first.Thumbprint, second.Thumbprint);
    }

    [Fact]
    public void CreateOrLoad_replaces_a_corrupt_file()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "server.pfx");
        File.WriteAllText(path, "this is not a pkcs12 blob");

        using var certificate = ServerCertificate.CreateOrLoad(path);

        Assert.True(certificate.HasPrivateKey);
    }

    [Fact]
    public void CreateOrLoad_writes_the_key_owner_readable_only()
    {
        // A private key that inherits the process umask is world-readable on a normal desktop.
        if (OperatingSystem.IsWindows())
            return;

        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "server.pfx");

        using var certificate = ServerCertificate.CreateOrLoad(path);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    public void CreateOrLoad_leaves_no_temporary_file_behind()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "server.pfx");

        using var certificate = ServerCertificate.CreateOrLoad(path);

        Assert.Equal(["server.pfx"], Directory.GetFiles(directory.Path).Select(Path.GetFileName));
    }

    [Fact]
    public void CreateOrLoad_round_trips_a_password_protected_file()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "server.pfx");

        using var first = ServerCertificate.CreateOrLoad(path, o => o.ExportPassword = "hunter2");
        using var second = ServerCertificate.CreateOrLoad(path, o => o.ExportPassword = "hunter2");

        Assert.Equal(first.Thumbprint, second.Thumbprint);
    }

    [Fact]
    public void Exports_a_public_certificate_with_no_private_key()
    {
        using var certificate = ServerCertificate.Create();

        var exported = certificate.ExportPublicCertificate();
        using var loaded = X509CertificateLoader.LoadCertificate(exported);

        Assert.False(loaded.HasPrivateKey);
        Assert.Equal(certificate.Thumbprint, loaded.Thumbprint);
    }

    // Read through the typed accessors rather than Format(), which expands IPv6 addresses to their
    // long form and would make a substring assertion quietly wrong.
    static (List<string> Names, List<IPAddress> Addresses) GetSubjectAlternativeName(X509Certificate2 certificate)
    {
        var san = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();
        return ([.. san.EnumerateDnsNames()], [.. san.EnumerateIPAddresses()]);
    }
}

public class CertificatePinningTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void A_pin_is_stable_and_specific()
    {
        using var certificate = ServerCertificate.Create();
        using var other = ServerCertificate.Create();

        Assert.Equal(certificate.GetPublicKeyPin(), certificate.GetPublicKeyPin());
        Assert.NotEqual(certificate.GetPublicKeyPin(), other.GetPublicKeyPin());
    }

    [Fact]
    public void A_pin_survives_stripping_the_private_key()
    {
        using var certificate = ServerCertificate.Create();
        using var publicOnly = X509CertificateLoader.LoadCertificate(certificate.ExportPublicCertificate());

        // The client only ever sees the public half, so the two must agree or nothing matches.
        Assert.Equal(certificate.GetPublicKeyPin(), publicOnly.GetPublicKeyPin());
    }

    [Fact]
    public void The_validator_accepts_the_pinned_certificate_and_nothing_else()
    {
        using var pinned = ServerCertificate.Create();
        using var impostor = ServerCertificate.Create();

        var validator = CertificatePinning.CreateValidator(pinned);

        // Chain errors are exactly what a self-signed certificate produces; the pin is the check.
        Assert.True(validator(this, pinned, null, System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(validator(this, impostor, null, System.Net.Security.SslPolicyErrors.None));
        Assert.False(validator(this, null, null, System.Net.Security.SslPolicyErrors.RemoteCertificateNotAvailable));
    }

    [Fact]
    public void The_validator_accepts_any_of_several_pins()
    {
        using var first = ServerCertificate.Create();
        using var second = ServerCertificate.Create();
        using var third = ServerCertificate.Create();

        var validator = CertificatePinning.CreateValidator(first.GetPublicKeyPin(), second.GetPublicKeyPin());

        Assert.True(validator(this, first, null, System.Net.Security.SslPolicyErrors.None));
        Assert.True(validator(this, second, null, System.Net.Security.SslPolicyErrors.None));
        Assert.False(validator(this, third, null, System.Net.Security.SslPolicyErrors.None));
    }

    [Fact]
    public void Rejects_an_empty_pin_list()
    {
        Assert.Throws<ArgumentException>(() => CertificatePinning.CreateValidator(Array.Empty<string>()));
        Assert.Throws<ArgumentException>(() => CertificatePinning.CreateValidator(Array.Empty<X509Certificate2>()));
    }

    [Fact]
    public async Task A_pinned_handler_reaches_the_server_a_default_client_cannot()
    {
        await using var server = await TlsTestServer.StartAsync(
            app => app.OnGet("/ping", ctx => ctx.Response.WriteAsync("pong"))
        );

        Assert.Equal("pong", await server.Client.GetStringAsync("/ping", Token));

        // Same server, same request, no pin: the device trust store has never heard of this
        // certificate and there is nothing for the client to fall back on.
        using var unpinned = new HttpClient { BaseAddress = server.BaseAddress };
        await Assert.ThrowsAsync<HttpRequestException>(() => unpinned.GetStringAsync("/ping", Token));
    }
}

/// <summary>A directory that cleans up after itself, for the certificate persistence tests.</summary>
sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        this.Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"shiny-certs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(this.Path, true);
        }
        catch (IOException)
        {
        }
    }
}
