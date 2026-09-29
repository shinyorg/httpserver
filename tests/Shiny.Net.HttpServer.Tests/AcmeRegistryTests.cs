using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shiny.Net.HttpServer.Acme;
using Shiny.Net.HttpServer.Http3;
using Shiny.Net.HttpServer.Telemetry;
using static Shiny.Net.HttpServer.Tests.AcmeTests;

namespace Shiny.Net.HttpServer.Tests;

/// <summary>
/// <see cref="AcmeCertificateRegistry"/> against <see cref="FakeAcmeCa"/>: many certificates on one
/// server, picked by SNI on real TLS handshakes, with entries added while it runs.
/// </summary>
public class AcmeRegistryTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Issues_distinct_certificates_and_picks_each_by_sni()
    {
        await using var ca = await FakeAcmeCa.StartAsync();
        await using var host = await RegistryHost.StartAsync(ca);

        // Both added after the server started: the one HTTP-01 middleware answers for them.
        host.Registry.Set("a", ["a.test"]);
        host.Registry.Set("b", ["b.test", "www.b.test"]);

        var a = await host.Registry.IssueAsync("a", Token);
        var b = await host.Registry.IssueAsync("b", Token);

        Assert.NotEqual(a.Thumbprint, b.Thumbprint);
        Assert.Equal(2, ca.CertificatesIssued);
        Assert.Contains(ca.Validations, x => x == "http-01 a.test ok");
        Assert.Contains(ca.Validations, x => x == "http-01 www.b.test ok");

        Assert.Equal(a.Thumbprint, (await Handshake(host.HttpsPort, "a.test")).Certificate.Thumbprint);
        Assert.Equal(b.Thumbprint, (await Handshake(host.HttpsPort, "b.test")).Certificate.Thumbprint);
        Assert.Equal(b.Thumbprint, (await Handshake(host.HttpsPort, "WWW.B.TEST", SslApplicationProtocol.Http2)).Certificate.Thumbprint);

        // The intermediate goes out with each.
        Assert.Contains((await Handshake(host.HttpsPort, "a.test")).Chain, x => x.Thumbprint == ca.Intermediate.Thumbprint);

        var entries = host.Registry.Entries;
        Assert.Equal(["a", "b"], entries.Select(x => x.Name));
        Assert.All(entries, x => Assert.Equal(AcmeCertificateState.Issued, x.State));
        Assert.Equal(["b.test", "www.b.test"], entries[1].Domains);
        Assert.NotNull(entries[0].NotAfter);
    }

    [Fact]
    public async Task Set_never_orders_a_certificate_by_itself()
    {
        await using var ca = await FakeAcmeCa.StartAsync();
        await using var host = await RegistryHost.StartAsync(ca);

        host.Registry.Set("quiet", ["quiet.test"]);
        Assert.Null(await host.Registry.LoadAsync("quiet", Token));

        // Renewal is running and checks every 100 ms; give it plenty of chances to misbehave.
        await Task.Delay(750, Token);

        Assert.Equal(0, ca.CertificatesIssued);
        Assert.Equal(0, ca.AccountsCreated);
        Assert.Equal(AcmeCertificateState.None, host.Registry.GetEntry("quiet")!.State);

        // No certificate and no fallback: the handshake is refused.
        await Assert.ThrowsAnyAsync<Exception>(() => Handshake(host.HttpsPort, "quiet.test"));
    }

    [Fact]
    public async Task A_failed_first_issuance_throws_is_reported_and_is_not_retried()
    {
        await using var ca = await FakeAcmeCa.StartAsync();
        ca.OfferedChallenges = ["dns-01"];
        await using var host = await RegistryHost.StartAsync(ca);

        var failures = new ConcurrentQueue<AcmeIssuanceFailedEventArgs>();
        host.Registry.IssuanceFailed += (_, e) => failures.Enqueue(e);

        host.Registry.Set("broken", ["broken.test"]);
        var ex = await Assert.ThrowsAsync<AcmeException>(() => host.Registry.IssueAsync("broken", Token));
        Assert.Contains("dns-01", ex.Message);

        await Task.Delay(750, Token);

        var failure = Assert.Single(failures);
        Assert.Equal("broken", failure.Name);
        Assert.False(failure.IsRenewal);

        var entry = host.Registry.GetEntry("broken")!;
        Assert.Equal(AcmeCertificateState.Failed, entry.State);
        Assert.Same(ex, entry.LastError);
    }

    [Fact]
    public async Task Loads_the_stored_certificate_on_restart_without_ordering()
    {
        await using var ca = await FakeAcmeCa.StartAsync();
        var store = TempDirectory();

        string thumbprint;
        await using (var first = await RegistryHost.StartAsync(ca, storePath: store))
        {
            first.Registry.Set("site", ["site.test"]);
            thumbprint = (await first.Registry.IssueAsync("site", Token)).Thumbprint;
        }

        await using var second = await RegistryHost.StartAsync(ca, storePath: store);
        var changed = new ConcurrentQueue<AcmeCertificateEventArgs>();
        second.Registry.CertificateChanged += (_, e) => changed.Enqueue(e);

        second.Registry.Set("site", ["site.test"]);
        var loaded = await second.Registry.LoadAsync("site", Token);

        Assert.Equal(thumbprint, loaded!.Thumbprint);
        Assert.Equal(thumbprint, (await Handshake(second.HttpsPort, "site.test")).Certificate.Thumbprint);
        Assert.Equal(1, ca.CertificatesIssued);
        Assert.Equal("site", Assert.Single(changed).Name);
        Assert.Equal(AcmeCertificateState.Issued, second.Registry.GetEntry("site")!.State);

        // A forced renewal reuses the stored account.
        await second.Registry.RenewAsync("site", Token);
        Assert.Equal(1, ca.AccountsCreated);
    }

    [Fact]
    public async Task Adopts_an_imported_certificate_and_keeps_it_across_restarts()
    {
        await using var ca = await FakeAcmeCa.StartAsync();
        var store = TempDirectory();
        using var outside = ServerCertificate.Create(o =>
        {
            o.IncludeLocalAddresses = false;
            o.CommonName = "imported.test";
            o.DnsNames.Add("imported.test");
        });

        await using (var first = await RegistryHost.StartAsync(ca, storePath: store))
        {
            first.Registry.Set("imported", ["imported.test"]);
            var served = await first.Registry.ImportAsync("imported", outside, cancellationToken: Token);

            Assert.Equal(outside.Thumbprint, served.Thumbprint);
            Assert.Equal(outside.Thumbprint, (await Handshake(first.HttpsPort, "imported.test")).Certificate.Thumbprint);
            Assert.Equal(AcmeCertificateState.Issued, first.Registry.GetEntry("imported")!.State);
        }

        await using var second = await RegistryHost.StartAsync(ca, storePath: store);
        second.Registry.Set("imported", ["imported.test"]);

        Assert.Equal(outside.Thumbprint, (await second.Registry.LoadAsync("imported", Token))!.Thumbprint);
        Assert.Equal(0, ca.CertificatesIssued);

        // And it renews through the CA like anything else it holds.
        var renewed = await second.Registry.RenewAsync("imported", Token);
        Assert.Equal(ca.Issued[0].Thumbprint, renewed.Thumbprint);
    }

    [Fact]
    public async Task Imports_a_pem_full_chain_and_key()
    {
        await using var ca = await FakeAcmeCa.StartAsync();
        await using var host = await RegistryHost.StartAsync(ca);

        using var outside = ServerCertificate.Create(o =>
        {
            o.IncludeLocalAddresses = false;
            o.DnsNames.Add("pem.test");
        });

        var chainPem = outside.ExportCertificatePem();
        var keyPem = outside.GetRSAPrivateKey()!.ExportPkcs8PrivateKeyPem();

        host.Registry.Set("pem", ["pem.test"]);
        await host.Registry.ImportAsync("pem", chainPem, keyPem, Token);

        Assert.Equal(outside.Thumbprint, (await Handshake(host.HttpsPort, "pem.test")).Certificate.Thumbprint);
    }

    [Fact]
    public async Task Removing_an_entry_stops_serving_its_certificate()
    {
        await using var ca = await FakeAcmeCa.StartAsync();
        await using var host = await RegistryHost.StartAsync(ca);

        host.Registry.Set("gone", ["gone.test"]);
        var issued = await host.Registry.IssueAsync("gone", Token);
        Assert.Equal(issued.Thumbprint, (await Handshake(host.HttpsPort, "gone.test")).Certificate.Thumbprint);

        Assert.True(await host.Registry.RemoveAsync("gone"));
        Assert.False(await host.Registry.RemoveAsync("gone"));

        Assert.Null(host.Registry.SelectCertificate("gone.test"));
        await Assert.ThrowsAnyAsync<Exception>(() => Handshake(host.HttpsPort, "gone.test"));
        Assert.Empty(host.Registry.Entries);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => host.Registry.IssueAsync("gone", Token));
    }

    [Fact]
    public async Task Serves_the_fallback_certificate_for_names_no_entry_covers()
    {
        await using var ca = await FakeAcmeCa.StartAsync();
        using var fallback = ServerCertificate.Create(o =>
        {
            o.IncludeLocalAddresses = false;
            o.DnsNames.Add("fallback.test");
        });
        await using var host = await RegistryHost.StartAsync(ca, o => o.FallbackCertificate = fallback);

        host.Registry.Set("known", ["known.test"]);
        var known = await host.Registry.IssueAsync("known", Token);

        Assert.Equal(known.Thumbprint, (await Handshake(host.HttpsPort, "known.test")).Certificate.Thumbprint);
        Assert.Equal(fallback.Thumbprint, (await Handshake(host.HttpsPort, "stranger.test")).Certificate.Thumbprint);

        // An entry with no certificate yet falls back too, rather than refusing.
        host.Registry.Set("pending", ["pending.test"]);
        Assert.Equal(fallback.Thumbprint, (await Handshake(host.HttpsPort, "pending.test")).Certificate.Thumbprint);
    }

    [Fact]
    public async Task Matches_a_wildcard_name_on_an_imported_certificate()
    {
        await using var ca = await FakeAcmeCa.StartAsync();
        await using var host = await RegistryHost.StartAsync(ca);

        using var wildcard = ServerCertificate.Create(o =>
        {
            o.IncludeLocalAddresses = false;
            o.DnsNames.Add("wild.test");
            o.DnsNames.Add("*.wild.test");
        });

        host.Registry.Set("wild", ["wild.test"]);
        await host.Registry.ImportAsync("wild", wildcard, cancellationToken: Token);

        Assert.Equal(wildcard.Thumbprint, host.Registry.SelectCertificate("anything.wild.test")!.TargetCertificate.Thumbprint);
        Assert.Null(host.Registry.SelectCertificate("too.deep.wild.test"));
        Assert.Equal(wildcard.Thumbprint, (await Handshake(host.HttpsPort, "app.wild.test")).Certificate.Thumbprint);
    }

    [Fact]
    public async Task Changing_the_domains_keeps_the_old_certificate_until_the_next_issue()
    {
        await using var ca = await FakeAcmeCa.StartAsync();
        await using var host = await RegistryHost.StartAsync(ca);

        host.Registry.Set("grow", ["grow.test"]);
        var first = await host.Registry.IssueAsync("grow", Token);

        host.Registry.Set("grow", ["grow.test", "more.grow.test"]);
        await host.Registry.LoadAsync("grow", Token);
        await Task.Delay(300, Token);

        Assert.Equal(1, ca.CertificatesIssued);
        Assert.Equal(first.Thumbprint, (await Handshake(host.HttpsPort, "grow.test")).Certificate.Thumbprint);

        var second = await host.Registry.IssueAsync("grow", Token);
        Assert.Contains("more.grow.test", second.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single().EnumerateDnsNames());
        Assert.Equal(second.Thumbprint, (await Handshake(host.HttpsPort, "more.grow.test")).Certificate.Thumbprint);
    }

    [Fact]
    public async Task Answers_tls_alpn01_for_every_entry_from_one_responder()
    {
        await using var ca = await FakeAcmeCa.StartAsync();
        ca.OfferedChallenges = ["tls-alpn-01"];
        await using var host = await RegistryHost.StartAsync(ca, o => o.Challenges = AcmeChallengeTypes.TlsAlpn01);

        host.Registry.Set("x", ["x.test"]);
        host.Registry.Set("y", ["y.test"]);
        await Task.WhenAll(host.Registry.IssueAsync("x", Token), host.Registry.IssueAsync("y", Token));

        Assert.Contains(ca.Validations, x => x == "tls-alpn-01 x.test ok");
        Assert.Contains(ca.Validations, x => x == "tls-alpn-01 y.test ok");
        Assert.Equal(1, ca.AccountsCreated);
        Assert.Equal(host.Registry.GetEntry("y")!.Certificate!.Thumbprint, (await Handshake(host.HttpsPort, "y.test")).Certificate.Thumbprint);
        Assert.Equal("hello HTTP/2", await host.GetAsync("x.test", "/hello", HttpVersion.Version20));
    }

    [Fact]
    public async Task Selects_by_sni_on_the_http3_listener()
    {
        await using var ca = await FakeAcmeCa.StartAsync();
        await using var host = await RegistryHost.StartAsync(ca);

        host.Registry.Set("h3a", ["h3a.test"]);
        host.Registry.Set("h3b", ["h3b.test"]);
        var a = await host.Registry.IssueAsync("h3a", Token);
        var b = await host.Registry.IssueAsync("h3b", Token);

        var options = new Http3Options { Port = 0 }.UseAcme(host.Server);
        await using var listener = new Http3Listener(host.Server, options);

        [System.Runtime.Versioning.SupportedOSPlatform("linux")]
        [System.Runtime.Versioning.SupportedOSPlatform("macos")]
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        X509Certificate2? For(string name) => listener
            .BuildConnectionOptions(new SslClientHelloInfo(name, System.Security.Authentication.SslProtocols.Tls13))
            .ServerAuthenticationOptions.ServerCertificateContext?.TargetCertificate;

        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows())
            return;

        Assert.Equal(a.Thumbprint, For("h3a.test")!.Thumbprint);
        Assert.Equal(b.Thumbprint, For("h3b.test")!.Thumbprint);
    }

    [Fact]
    public async Task Reports_issuances_and_days_to_expiry()
    {
        await using var ca = await FakeAcmeCa.StartAsync();
        await using var host = await RegistryHost.StartAsync(ca);
        var name = "metrics-" + Guid.NewGuid().ToString("N")[..8];

        var measurements = new ConcurrentQueue<(string Instrument, double Value, Dictionary<string, object?> Tags)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == HttpServerTelemetry.MeterName && instrument.Name.StartsWith("shiny.acme.", StringComparison.Ordinal))
                    l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((i, v, t, _) => measurements.Enqueue((i.Name, v, t.ToArray().ToDictionary(x => x.Key, x => x.Value))));
        listener.SetMeasurementEventCallback<double>((i, v, t, _) => measurements.Enqueue((i.Name, v, t.ToArray().ToDictionary(x => x.Key, x => x.Value))));
        listener.Start();

        host.Registry.Set(name, [name + ".test"]);
        await host.Registry.IssueAsync(name, Token);
        listener.RecordObservableInstruments();

        var mine = measurements.Where(x => Equals(x.Tags.GetValueOrDefault("acme.certificate.name"), name)).ToList();

        var issuance = Assert.Single(mine, x => x.Instrument == "shiny.acme.certificate.issuances");
        Assert.Equal("success", issuance.Tags["acme.issuance.result"]);

        var expiry = Assert.Single(mine, x => x.Instrument == "shiny.acme.certificate.expiry");
        Assert.InRange(expiry.Value, 88, 90.1);
    }

    [Fact]
    public void Registers_once_and_composes_the_options()
    {
        var builder = HttpServer.CreateBuilder();
        builder.AddAcmeRegistry(o => o.Email = "ops@a.test");
        builder.AddAcmeRegistry(o => o.AcceptTermsOfService = true);

        var server = builder.Build();
        var registry = server.GetAcmeCertificateRegistry();

        Assert.Equal("ops@a.test", registry.Options.Email);
        Assert.True(registry.Options.AcceptTermsOfService);
        Assert.Single(builder.Services, x => x.ServiceType == typeof(AcmeCertificateRegistry));
    }

    [Fact]
    public async Task Refuses_wildcards_and_unknown_entries()
    {
        await using var registry = new AcmeCertificateRegistry(new AcmeRegistryOptions { StorePath = TempDirectory() });

        Assert.Throws<AcmeException>(() => registry.Set("w", ["*.example.com"]));
        Assert.Throws<AcmeException>(() => registry.Set("empty", []));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => registry.IssueAsync("missing", Token));
        Assert.Null(registry.GetEntry("missing"));
    }

    /// <summary>The server under test: a cleartext "port 80" and a TLS "port 443" served from the registry.</summary>
    sealed class RegistryHost : IAsyncDisposable
    {
        public required HttpServer Server { get; init; }
        public required AcmeCertificateRegistry Registry { get; init; }
        public int HttpPort => Port(this.Server, "http://");
        public int HttpsPort => Port(this.Server, "https://");

        public static async Task<RegistryHost> StartAsync(FakeAcmeCa ca, Action<AcmeRegistryOptions>? configure = null, string? storePath = null)
        {
            var builder = HttpServer.CreateBuilder();
            builder.Services.AddSingleton<ILoggerFactory>(new ListLoggerFactory());
            builder.Options.HideExceptionDetails = false;
            builder.Options.Listen(IPAddress.Loopback, 0);
            builder.Options.Listen(IPAddress.Loopback, 0).Https = new HttpsOptions();

            builder.AddAcmeRegistry(o =>
            {
                o.Email = "ops@acme.test";
                o.AcceptTermsOfService = true;
                o.DirectoryUrl = ca.DirectoryUrl;
                o.StorePath = storePath ?? TempDirectory();
                o.PollInterval = TimeSpan.FromMilliseconds(25);
                o.RetryDelay = TimeSpan.FromMilliseconds(300);
                o.MaxRetryDelay = TimeSpan.FromSeconds(2);
                o.OrderTimeout = TimeSpan.FromSeconds(20);
                o.RenewalCheckInterval = TimeSpan.FromMilliseconds(100);
                configure?.Invoke(o);
            });

            var server = builder.Build();
            server.MapGet("/hello", ctx => ctx.Response.WriteAsync("hello " + ctx.Request.Protocol));

            ca.HttpPort = () => Port(server, "http://");
            ca.TlsPort = () => Port(server, "https://");

            await server.StartAsync();
            return new RegistryHost { Server = server, Registry = server.GetAcmeCertificateRegistry() };
        }

        public async Task<string> GetAsync(string host, string path, Version version)
        {
            // The request names the host, so SNI carries it; the connection goes to loopback regardless.
            using var client = new HttpClient(new SocketsHttpHandler
            {
                SslOptions = new SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = (_, _, _, _) => true
                },
                ConnectCallback = async (context, ct) =>
                {
                    var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
                    await socket.ConnectAsync(IPAddress.Loopback, context.DnsEndPoint.Port, ct);
                    return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
                }
            })
            {
                BaseAddress = new Uri($"https://{host}:{this.HttpsPort}"),
                DefaultRequestVersion = version,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
            };

            return await client.GetStringAsync(path, Token);
        }

        static int Port(HttpServer server, string scheme)
            => new Uri(server.ListenUrls.Single(x => x.StartsWith(scheme, StringComparison.Ordinal))).Port;

        public async ValueTask DisposeAsync()
        {
            await this.Server.DisposeAsync();
            await this.Registry.DisposeAsync();
        }
    }
}
