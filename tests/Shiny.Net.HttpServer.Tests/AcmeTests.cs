using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shiny.Net.HttpServer.Acme;
using Shiny.Net.HttpServer.Acme.Internal;
using Shiny.Net.HttpServer.Http3;

namespace Shiny.Net.HttpServer.Tests;

/// <summary>
/// The ACME client end to end, against <see cref="FakeAcmeCa"/>: a real server under test on a
/// cleartext and a TLS endpoint, answering real HTTP-01 fetches and TLS-ALPN-01 handshakes from the
/// CA, and serving whatever it was issued to real TLS clients.
/// </summary>
public class AcmeTests
{
    const string Domain = "acme.test";

    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Issues_a_certificate_over_http01_and_serves_it_on_https()
    {
        await using var ca = await FakeAcmeCa.StartAsync();
        await using var host = await AcmeHost.StartAsync(ca);

        await host.WaitForCertificatesAsync(1);

        var presented = await Handshake(host.HttpsPort, Domain);
        Assert.Equal(ca.Issued[0].Thumbprint, presented.Certificate.Thumbprint);
        Assert.Equal(host.Manager.Certificate!.Thumbprint, presented.Certificate.Thumbprint);
        Assert.Contains(ca.Validations, x => x == $"http-01 {Domain} ok");

        // And it carries HTTP, both versions ALPN can pick.
        Assert.Equal("hello HTTP/1.1", await host.GetAsync("/hello", HttpVersion.Version11));
        Assert.Equal("hello HTTP/2", await host.GetAsync("/hello", HttpVersion.Version20));
    }

    [Fact]
    public async Task Sends_the_intermediate_along_with_the_certificate()
    {
        await using var ca = await FakeAcmeCa.StartAsync();
        await using var host = await AcmeHost.StartAsync(ca);
        await host.WaitForCertificatesAsync(1);

        var presented = await Handshake(host.HttpsPort, Domain);

        // Without it, every client that does not happen to cache the intermediate rejects the server.
        Assert.Contains(presented.Chain, x => x.Thumbprint == ca.Intermediate.Thumbprint);
    }

    [Fact]
    public async Task Issues_a_certificate_over_tls_alpn01()
    {
        await using var ca = await FakeAcmeCa.StartAsync();
        ca.OfferedChallenges = ["tls-alpn-01"];

        await using var host = await AcmeHost.StartAsync(ca, o => o.Challenges = AcmeChallengeTypes.TlsAlpn01);
        await host.WaitForCertificatesAsync(1);

        Assert.Contains(ca.Validations, x => x == $"tls-alpn-01 {Domain} ok");
        Assert.Equal(ca.Issued[0].Thumbprint, (await Handshake(host.HttpsPort, Domain)).Certificate.Thumbprint);

        // The ClientHello is read and replayed on every connection while TLS-ALPN-01 is on; ordinary
        // clients must not notice.
        Assert.Equal("hello HTTP/1.1", await host.GetAsync("/hello", HttpVersion.Version11));
        Assert.Equal("hello HTTP/2", await host.GetAsync("/hello", HttpVersion.Version20));
    }

    [Fact]
    public async Task Refuses_an_acme_tls_probe_when_no_challenge_is_pending()
    {
        await using var ca = await FakeAcmeCa.StartAsync();
        await using var host = await AcmeHost.StartAsync(ca, o => o.Challenges = AcmeChallengeTypes.Http01 | AcmeChallengeTypes.TlsAlpn01);
        await host.WaitForCertificatesAsync(1);

        // No challenge for this name, so the normal handshake runs — with no protocol in common.
        await Assert.ThrowsAnyAsync<Exception>(
            () => Handshake(host.HttpsPort, Domain, new SslApplicationProtocol("acme-tls/1"))
        );
    }

    [Fact]
    public async Task Retries_a_request_the_ca_rejects_for_a_stale_nonce()
    {
        await using var ca = await FakeAcmeCa.StartAsync();
        ca.RejectNextNonces(2);

        await using var host = await AcmeHost.StartAsync(ca);
        await host.WaitForCertificatesAsync(1);

        Assert.Equal(2, ca.BadNoncesSent);
        Assert.NotNull(host.Manager.Certificate);
    }

    [Fact]
    public async Task Registers_with_external_account_binding()
    {
        await using var ca = await FakeAcmeCa.StartAsync();
        var hmac = RandomNumberGenerator.GetBytes(32);
        ca.RequireEab = ("kid-123", hmac);

        await using var host = await AcmeHost.StartAsync(
            ca,
            o => o.ExternalAccountBinding = new AcmeExternalAccountBinding("kid-123", System.Buffers.Text.Base64Url.EncodeToString(hmac))
        );

        await host.WaitForCertificatesAsync(1);
        Assert.Equal(1, ca.AccountsCreated);
    }

    [Fact]
    public async Task Says_so_when_the_ca_requires_external_account_binding_and_none_is_set()
    {
        await using var ca = await FakeAcmeCa.StartAsync();
        ca.RequireEab = ("kid-123", RandomNumberGenerator.GetBytes(32));

        await using var manager = Standalone(ca);

        var ex = await Assert.ThrowsAsync<AcmeException>(() => manager.EnsureCertificateAsync(Token));
        Assert.Contains("External Account Binding", ex.Message);
        Assert.Equal(0, ca.AccountsCreated);
    }

    [Fact]
    public async Task Reports_the_cas_problem_when_the_eab_mac_is_wrong()
    {
        await using var ca = await FakeAcmeCa.StartAsync();
        ca.RequireEab = ("kid-123", RandomNumberGenerator.GetBytes(32));

        await using var manager = Standalone(
            ca,
            o => o.ExternalAccountBinding = new AcmeExternalAccountBinding("kid-123", System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32)))
        );

        var ex = await Assert.ThrowsAsync<AcmeException>(() => manager.EnsureCertificateAsync(Token));
        Assert.Equal("urn:ietf:params:acme:error:unauthorized", ex.ProblemType);
        Assert.Contains("MAC", ex.Detail);
    }

    [Fact]
    public async Task Refuses_to_open_an_account_without_accepting_the_terms()
    {
        await using var ca = await FakeAcmeCa.StartAsync();
        await using var manager = Standalone(ca, o => o.AcceptTermsOfService = false);

        var ex = await Assert.ThrowsAsync<AcmeException>(() => manager.EnsureCertificateAsync(Token));
        Assert.Contains("https://fake-ca.test/terms", ex.Message);
        Assert.Equal(0, ca.AccountsCreated);
    }

    [Fact]
    public async Task Refuses_wildcards_before_contacting_the_ca()
    {
        await using var ca = await FakeAcmeCa.StartAsync();
        await using var manager = Standalone(ca, o => o.Domains = ["*.example.com"]);

        var ex = await Assert.ThrowsAsync<AcmeException>(() => manager.EnsureCertificateAsync(Token));
        Assert.Contains("DNS-01", ex.Message);
    }

    [Fact]
    public async Task Says_which_challenges_the_ca_offered_when_none_is_enabled()
    {
        await using var ca = await FakeAcmeCa.StartAsync();
        ca.OfferedChallenges = ["dns-01"];
        await using var host = await AcmeHost.StartAsync(ca, startRenewal: false);

        var ex = await Assert.ThrowsAsync<AcmeException>(() => host.Manager.EnsureCertificateAsync(Token));
        Assert.Contains("dns-01", ex.Message);
    }

    [Fact]
    public async Task Renews_on_demand_and_swaps_the_certificate_without_a_restart()
    {
        await using var ca = await FakeAcmeCa.StartAsync();
        await using var host = await AcmeHost.StartAsync(ca);
        await host.WaitForCertificatesAsync(1);

        // A connection opened before the swap.
        using var client = host.CreateClient(HttpVersion.Version11);
        Assert.Equal("hello HTTP/1.1", await client.GetStringAsync("/hello", Token));

        var first = (await Handshake(host.HttpsPort, Domain)).Certificate;
        var changed = 0;
        host.Manager.CertificateChanged += (_, _) => Interlocked.Increment(ref changed);

        var renewed = await host.Manager.RenewAsync(Token);

        Assert.NotEqual(first.Thumbprint, renewed.Thumbprint);
        Assert.Equal(1, changed);
        Assert.Equal(HttpServerState.Running, host.Server.State);

        // New handshakes, both protocol versions, see the new certificate at once…
        Assert.Equal(renewed.Thumbprint, (await Handshake(host.HttpsPort, Domain)).Certificate.Thumbprint);
        Assert.Equal(renewed.Thumbprint, (await Handshake(host.HttpsPort, Domain, SslApplicationProtocol.Http2)).Certificate.Thumbprint);

        // …and the connection that was already open keeps working.
        Assert.Equal("hello HTTP/1.1", await client.GetStringAsync("/hello", Token));
    }

    [Fact]
    public async Task Renews_in_the_background_when_the_certificate_is_due()
    {
        await using var ca = await FakeAcmeCa.StartAsync();

        // Issued "80 days ago" of 90: past the two-thirds mark the moment it arrives.
        ca.Backdates.Enqueue(TimeSpan.FromDays(80));

        await using var host = await AcmeHost.StartAsync(ca);
        await host.WaitForCertificatesAsync(2);
        await WaitFor(() => host.Manager.Certificate?.Thumbprint == ca.Issued[1].Thumbprint);

        Assert.Equal(ca.Issued[1].Thumbprint, (await Handshake(host.HttpsPort, Domain)).Certificate.Thumbprint);
        Assert.True(host.Manager.NextRenewal is null || host.Manager.NextRenewal > DateTimeOffset.UtcNow.AddDays(30));
    }

    [Fact]
    public async Task Follows_the_cas_renewal_information_and_names_the_certificate_it_replaces()
    {
        await using var ca = await FakeAcmeCa.StartAsync();

        // The CA wants the first certificate replaced now; after that, not for a month.
        ca.RenewalWindow = () => ca.CertificatesIssued < 2
            ? (DateTimeOffset.UtcNow.AddHours(-2), DateTimeOffset.UtcNow.AddHours(-1))
            : (DateTimeOffset.UtcNow.AddDays(30), DateTimeOffset.UtcNow.AddDays(31));

        await using var host = await AcmeHost.StartAsync(ca);
        await host.WaitForCertificatesAsync(2);

        Assert.Equal(FakeAcmeCa.RenewalInfoId(ca.Issued[0]), ca.LastReplaces);
        await WaitFor(() => host.Manager.NextRenewal > DateTimeOffset.UtcNow.AddDays(29));
    }

    [Fact]
    public async Task Reloads_the_stored_certificate_and_account_instead_of_ordering_again()
    {
        await using var ca = await FakeAcmeCa.StartAsync();
        var store = TempDirectory();

        string thumbprint;
        await using (var first = await AcmeHost.StartAsync(ca, storePath: store))
        {
            await first.WaitForCertificatesAsync(1);
            thumbprint = first.Manager.Certificate!.Thumbprint;
        }

        await using var second = await AcmeHost.StartAsync(ca, storePath: store);
        await WaitFor(() => second.Manager.Certificate is not null);

        Assert.Equal(thumbprint, second.Manager.Certificate!.Thumbprint);
        Assert.Equal(thumbprint, (await Handshake(second.HttpsPort, Domain)).Certificate.Thumbprint);
        Assert.Equal(1, ca.CertificatesIssued);

        // A forced renewal reuses the stored account rather than registering another.
        await second.Manager.RenewAsync(Token);
        Assert.Equal(1, ca.AccountsCreated);
    }

    [Fact]
    public async Task Writes_keys_owner_only()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes do not apply on Windows.");

        await using var ca = await FakeAcmeCa.StartAsync();
        var store = TempDirectory();

        await using var host = await AcmeHost.StartAsync(ca, storePath: store);
        await host.WaitForCertificatesAsync(1);

        var files = Directory.GetFiles(store, "*", SearchOption.AllDirectories);
        Assert.Contains(files, x => x.EndsWith("account.json", StringComparison.Ordinal));
        Assert.Contains(files, x => x.EndsWith(".pfx", StringComparison.Ordinal));

        foreach (var file in files)
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
    }

    [Fact]
    public async Task Certifies_an_ip_address()
    {
        await using var ca = await FakeAcmeCa.StartAsync();
        await using var host = await AcmeHost.StartAsync(ca, o => o.Domains = ["127.0.0.1"]);
        await host.WaitForCertificatesAsync(1);

        var san = host.Manager.Certificate!.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();
        Assert.Equal(IPAddress.Loopback, san.EnumerateIPAddresses().Single());
    }

    [Fact]
    public async Task Issues_an_rsa_certificate_when_asked()
    {
        await using var ca = await FakeAcmeCa.StartAsync();
        await using var host = await AcmeHost.StartAsync(ca, o => o.KeyAlgorithm = AcmeKeyAlgorithm.Rsa);
        await host.WaitForCertificatesAsync(1);

        Assert.NotNull(host.Manager.Certificate!.GetRSAPublicKey());
        Assert.Equal(ca.Issued[0].Thumbprint, (await Handshake(host.HttpsPort, Domain)).Certificate.Thumbprint);
    }

    [Fact]
    public async Task Serves_the_acme_certificate_on_the_http3_listener_and_follows_renewals()
    {
        await using var ca = await FakeAcmeCa.StartAsync();
        await using var host = await AcmeHost.StartAsync(ca);
        await host.WaitForCertificatesAsync(1);

        var options = new Http3Options { Port = 0 }.UseAcme(host.Server);
        await using var listener = new Http3Listener(host.Server, options);

        // The per-connection selection QUIC runs, without needing msquic on this machine.
        var hello = new SslClientHelloInfo(Domain, System.Security.Authentication.SslProtocols.Tls13);
        Assert.Same(host.Manager.CertificateContext, listener.BuildConnectionOptions(hello).ServerAuthenticationOptions.ServerCertificateContext);

        await host.Manager.RenewAsync(Token);
        var context = listener.BuildConnectionOptions(hello).ServerAuthenticationOptions.ServerCertificateContext!;
        Assert.Equal(host.Manager.Certificate!.Thumbprint, context.TargetCertificate.Thumbprint);

        if (!Http3Listener.IsSupported)
            return;

        await listener.BindAsync(Token);
        using var client = new HttpClient(new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                TargetHost = Domain,
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                    certificate is not null && new X509Certificate2(certificate).Thumbprint == host.Manager.Certificate!.Thumbprint
            }
        })
        {
            DefaultRequestVersion = HttpVersion.Version30,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };

        var response = await client.GetStringAsync($"https://127.0.0.1:{listener.BoundEndPoint!.Port}/hello", Token);
        Assert.Equal("hello HTTP/3", response);
    }

    [Fact]
    public async Task Answers_http01_only_for_a_pending_token()
    {
        await using var ca = await FakeAcmeCa.StartAsync();
        await using var host = await AcmeHost.StartAsync(ca, startRenewal: false);

        host.Manager.Challenges.AddHttp("tok", "tok.thumb");
        using var client = new HttpClient();

        var body = await client.GetStringAsync($"http://127.0.0.1:{host.HttpPort}/.well-known/acme-challenge/tok", Token);
        Assert.Equal("tok.thumb", body);

        var missing = await client.GetAsync($"http://127.0.0.1:{host.HttpPort}/.well-known/acme-challenge/other", Token);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        host.Manager.Challenges.RemoveHttp("tok");
        var gone = await client.GetAsync($"http://127.0.0.1:{host.HttpPort}/.well-known/acme-challenge/tok", Token);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public void Builds_a_tls_alpn_certificate_to_rfc_8737()
    {
        using var certificate = AcmeChallengeResponder.CreateTlsAlpnCertificate(Domain, "token.thumbprint");

        var extension = certificate.Extensions[AcmeChallengeResponder.AcmeIdentifierOid]!;
        Assert.True(extension.Critical);

        // DER OCTET STRING (0x04, length 32) wrapping SHA-256 of the key authorization.
        var expected = new byte[] { 0x04, 0x20 }.Concat(SHA256.HashData("token.thumbprint"u8)).ToArray();
        Assert.Equal(expected, extension.RawData);
        Assert.Equal(Domain, certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single().EnumerateDnsNames().Single());
        Assert.True(certificate.HasPrivateKey);
    }

    [Fact]
    public void Computes_the_jwk_thumbprint_from_the_canonical_form()
    {
        using var key = AcmeAccountKey.Create();

        // RFC 7638: members in lexicographic order, no whitespace.
        Assert.Matches("""^\{"crv":"P-256","kty":"EC","x":"[A-Za-z0-9_-]{43}","y":"[A-Za-z0-9_-]{43}"\}$""", key.Jwk);
        Assert.Equal(
            System.Buffers.Text.Base64Url.EncodeToString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key.Jwk))),
            key.Thumbprint
        );

        using var reloaded = AcmeAccountKey.Import(key.ExportPkcs8());
        Assert.Equal(key.Thumbprint, reloaded.Thumbprint);
    }

    [Fact]
    public void Registers_once_when_added_twice_and_composes_the_options()
    {
        var builder = HttpServer.CreateBuilder();
        builder.AddAcme(o => o.Domains = ["a.test"]);
        builder.AddAcme(o => o.Email = "ops@a.test");

        var server = builder.Build();
        var manager = server.GetAcmeCertificateManager();

        Assert.Equal(["a.test"], manager.Options.Domains);
        Assert.Equal("ops@a.test", manager.Options.Email);
        Assert.Single(builder.Services, x => x.ServiceType == typeof(AcmeCertificateManager));
    }

    [Fact]
    public void Leaves_endpoints_with_their_own_certificate_alone()
    {
        using var own = ServerCertificate.Create();

        var builder = HttpServer.CreateBuilder();
        var mine = builder.Options.ListenHttps(IPAddress.Loopback, 0, own);
        var acme = builder.Options.Listen(IPAddress.Loopback, 0);
        acme.Https = new HttpsOptions();
        builder.AddAcme(o => o.Domains = ["a.test"]);

        var server = builder.Build();

        Assert.Null(mine.Https!.CertificateContextSelector);
        Assert.NotNull(acme.Https.CertificateContextSelector);
        Assert.Null(acme.Https.ChallengeResponder);
        _ = server;
    }

    // ---- helpers ----

    static AcmeCertificateManager Standalone(FakeAcmeCa ca, Action<AcmeOptions>? configure = null)
    {
        var options = AcmeHost.Options(ca, TempDirectory());
        configure?.Invoke(options);

        return new AcmeCertificateManager(options);
    }

    static string TempDirectory() => Path.Combine(Path.GetTempPath(), "shiny-acme-" + Guid.NewGuid().ToString("N"));

    static async Task WaitFor(Func<bool> condition, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition not met in time.");

            await Task.Delay(25, Token);
        }
    }

    sealed record Presented(X509Certificate2 Certificate, X509Certificate2[] Chain);

    static async Task<Presented> Handshake(int port, string host, SslApplicationProtocol? protocol = null)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port, Token);

        X509Certificate2? presented = null;
        X509Certificate2[] chain = [];

        await using var ssl = new SslStream(tcp.GetStream());
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = host,
            ApplicationProtocols = [protocol ?? SslApplicationProtocol.Http11],
            RemoteCertificateValidationCallback = (_, certificate, x509Chain, _) =>
            {
                presented = new X509Certificate2(certificate!);
                chain = x509Chain is null ? [] : [.. x509Chain.ChainPolicy.ExtraStore];
                return true;
            }
        }, Token);

        return new Presented(presented!, chain);
    }

    /// <summary>Collects every log line, so a failed wait can say what the renewal loop saw.</summary>
    sealed class ListLoggerFactory : ILoggerFactory, ILogger
    {
        readonly List<string> entries = [];

        public IReadOnlyList<string> Entries
        {
            get
            {
                lock (this.entries)
                    return [.. this.entries];
            }
        }

        public ILogger CreateLogger(string categoryName) => this;
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var line = $"{logLevel}: {formatter(state, exception)}";
            if (exception is not null)
                line += $" [{exception.GetType().Name}: {exception.Message}]";

            lock (this.entries)
                this.entries.Add(line);
        }
    }

    /// <summary>The server under test: port "80" (cleartext) and port "443" (TLS, ACME's certificate).</summary>
    sealed class AcmeHost : IAsyncDisposable
    {
        public required HttpServer Server { get; init; }
        public required AcmeCertificateManager Manager { get; init; }
        public required FakeAcmeCa Ca { get; init; }
        public required ListLoggerFactory Logs { get; init; }
        public int HttpPort => Port(this.Server, "http://");
        public int HttpsPort => Port(this.Server, "https://");

        public static AcmeOptions Options(FakeAcmeCa ca, string storePath) => new()
        {
            Domains = [Domain],
            Email = "ops@acme.test",
            AcceptTermsOfService = true,
            DirectoryUrl = ca.DirectoryUrl,
            StorePath = storePath,
            PollInterval = TimeSpan.FromMilliseconds(25),
            RetryDelay = TimeSpan.FromMilliseconds(300),
            MaxRetryDelay = TimeSpan.FromSeconds(2),
            OrderTimeout = TimeSpan.FromSeconds(20)
        };

        public static async Task<AcmeHost> StartAsync(
            FakeAcmeCa ca,
            Action<AcmeOptions>? configure = null,
            string? storePath = null,
            bool startRenewal = true
        )
        {
            var builder = HttpServer.CreateBuilder();
            var logs = new ListLoggerFactory();
            builder.Services.AddSingleton<ILoggerFactory>(logs);
            builder.Options.HideExceptionDetails = false;
            builder.Options.Listen(IPAddress.Loopback, 0);
            builder.Options.Listen(IPAddress.Loopback, 0).Https = new HttpsOptions();

            var defaults = Options(ca, storePath ?? TempDirectory());
            builder.AddAcme(o =>
            {
                o.Domains = defaults.Domains;
                o.Email = defaults.Email;
                o.AcceptTermsOfService = true;
                o.DirectoryUrl = defaults.DirectoryUrl;
                o.StorePath = defaults.StorePath;
                o.PollInterval = defaults.PollInterval;
                o.RetryDelay = defaults.RetryDelay;
                o.MaxRetryDelay = defaults.MaxRetryDelay;
                o.OrderTimeout = defaults.OrderTimeout;
                configure?.Invoke(o);
            });

            var server = builder.Build();
            server.MapGet("/hello", ctx => ctx.Response.WriteAsync("hello " + ctx.Request.Protocol));

            var manager = server.GetAcmeCertificateManager();
            ca.HttpPort = () => Port(server, "http://");
            ca.TlsPort = () => Port(server, "https://");

            await server.StartAsync();

            if (!startRenewal)
                await manager.StopRenewalAsync();

            return new AcmeHost { Server = server, Manager = manager, Ca = ca, Logs = logs };
        }

        public async Task WaitForCertificatesAsync(int count)
        {
            try
            {
                await WaitFor(() => this.Ca.CertificatesIssued >= count);
                await WaitFor(() => this.Manager.Certificate?.Thumbprint == this.Ca.Issued[count - 1].Thumbprint);
            }
            catch (TimeoutException)
            {
                // The loop's own account of what went wrong is the useful part of a failure here.
                throw new TimeoutException(
                    "No certificate. Validations: " + string.Join(" | ", this.Ca.Validations) +
                    Environment.NewLine + string.Join(Environment.NewLine, this.Logs.Entries)
                );
            }
        }

        public HttpClient CreateClient(Version version) => new(new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                TargetHost = Domain,
                RemoteCertificateValidationCallback = (_, _, _, _) => true
            }
        })
        {
            BaseAddress = new Uri($"https://127.0.0.1:{this.HttpsPort}"),
            DefaultRequestVersion = version,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };

        public async Task<string> GetAsync(string path, Version version)
        {
            using var client = this.CreateClient(version);
            return await client.GetStringAsync(path, Token);
        }

        static int Port(HttpServer server, string scheme)
            => new Uri(server.ListenUrls.Single(x => x.StartsWith(scheme, StringComparison.Ordinal))).Port;

        public async ValueTask DisposeAsync()
        {
            await this.Server.DisposeAsync();
            await this.Manager.StopRenewalAsync();
        }
    }
}
