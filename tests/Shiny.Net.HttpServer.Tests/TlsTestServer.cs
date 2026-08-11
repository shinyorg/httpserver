using System.Net;
using System.Security.Cryptography.X509Certificates;

namespace Shiny.Net.HttpServer.Tests;

/// <summary>
/// <see cref="TestServer"/>'s TLS twin: a real listener with a real certificate, and a client that
/// trusts it by pin rather than by anything installed on the machine running the tests.
/// </summary>
sealed class TlsTestServer : IAsyncDisposable
{
    TlsTestServer(HttpServer server, HttpClient client, X509Certificate2 certificate)
    {
        this.Server = server;
        this.Client = client;
        this.Certificate = certificate;
    }

    public HttpServer Server { get; }
    public HttpClient Client { get; }
    public X509Certificate2 Certificate { get; }
    public int Port { get; private init; }
    public Uri BaseAddress => new($"https://127.0.0.1:{this.Port}");

    public static async Task<TlsTestServer> StartAsync(
        Action<HttpServer> configure,
        Action<HttpServerOptions>? configureOptions = null,
        Action<HttpClient>? configureClient = null,
        X509Certificate2? certificate = null
    )
    {
        certificate ??= ServerCertificate.Create();

        var options = new HttpServerOptions
        {
            Address = IPAddress.Loopback,
            Port = 0,
            HideExceptionDetails = false,
            Https = new HttpsOptions { Certificate = certificate }
        };
        configureOptions?.Invoke(options);

        var server = new HttpServer(options);
        configure(server);

        await server.StartAsync();

        var port = new Uri(server.ListenUrl!).Port;
        var client = new HttpClient(CertificatePinning.CreateHandler(certificate))
        {
            BaseAddress = new Uri($"https://127.0.0.1:{port}"),
            Timeout = TimeSpan.FromSeconds(30)
        };
        configureClient?.Invoke(client);

        return new TlsTestServer(server, client, certificate) { Port = port };
    }

    public async ValueTask DisposeAsync()
    {
        this.Client.Dispose();
        await this.Server.DisposeAsync();
        this.Certificate.Dispose();
    }
}
