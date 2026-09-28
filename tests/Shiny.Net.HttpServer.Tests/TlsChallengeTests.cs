using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace Shiny.Net.HttpServer.Tests;

/// <summary>
/// The two TLS hooks ACME needs from the core: a certificate context read per handshake (hot swap,
/// intermediates) and a ClientHello responder (TLS-ALPN-01). Neither is ACME-specific, so they are
/// tested here on their own.
/// </summary>
public class TlsChallengeTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    static readonly SslApplicationProtocol AcmeTls1 = new("acme-tls/1");

    [Fact]
    public async Task Parses_sni_and_alpn_from_a_real_client_hello()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var client = Task.Run(async () =>
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port, Token);
            await using var ssl = new SslStream(tcp.GetStream());
            try
            {
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = "hello.test",
                    ApplicationProtocols = [SslApplicationProtocol.Http2, AcmeTls1]
                }, Token);
            }
            catch
            {
                // The "server" below never answers; only the hello matters.
            }
        }, Token);

        using var accepted = await listener.AcceptTcpClientAsync(Token);
        var (hello, consumed) = await TlsClientHelloReader.ReadAsync(accepted.GetStream(), Token);
        accepted.Close();
        await client;

        Assert.NotNull(hello);
        Assert.Equal("hello.test", hello.ServerName);
        Assert.Equal([SslApplicationProtocol.Http2, AcmeTls1], hello.ApplicationProtocols);
        Assert.True(hello.Offers(AcmeTls1));
        Assert.Equal(22, consumed[0]);
    }

    [Fact]
    public async Task Yields_no_hello_for_bytes_that_are_not_tls()
    {
        using var stream = new MemoryStream("GET / HTTP/1.1\r\n\r\n"u8.ToArray());

        var (hello, consumed) = await TlsClientHelloReader.ReadAsync(stream, Token);

        Assert.Null(hello);
        Assert.Equal("GET /"u8.ToArray(), consumed);
    }

    [Fact]
    public async Task Answers_a_challenge_hello_with_the_challenge_certificate_and_protocol()
    {
        using var challenge = ServerCertificate.Create(o => o.DnsNames.Add("challenge.test"));
        var seen = new System.Collections.Concurrent.ConcurrentBag<string?>();

        await using var server = await TlsTestServer.StartAsync(
            app => app.MapGet("/hello", ctx => ctx.Response.WriteAsync("hello " + ctx.Request.Protocol)),
            configureOptions: o => o.Https!.ChallengeResponder = hello =>
            {
                seen.Add(hello.ServerName);
                return hello.Offers(AcmeTls1) ? new TlsChallengeResponse(challenge, AcmeTls1) : null;
            }
        );

        var (certificate, protocol) = await Handshake(server.Port, "challenge.test", AcmeTls1);
        Assert.Equal(challenge.Thumbprint, certificate.Thumbprint);
        Assert.Equal(AcmeTls1, protocol);

        // Everyone else gets the real certificate and real HTTP, on both versions ALPN can pick —
        // the hello they sent was read, inspected and replayed without their noticing.
        Assert.Equal(server.Certificate.Thumbprint, (await Handshake(server.Port, "localhost", SslApplicationProtocol.Http11)).Certificate.Thumbprint);
        Assert.Equal("hello HTTP/1.1", await server.Client.GetStringAsync("/hello", Token));

        using var http2 = new HttpRequestMessage(HttpMethod.Get, "/hello")
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        var response = await server.Client.SendAsync(http2, Token);
        Assert.Equal("hello HTTP/2", await response.Content.ReadAsStringAsync(Token));

        // The responder saw every hello, SNI included.
        Assert.Contains("challenge.test", seen);
    }

    [Fact]
    public async Task Swaps_the_certificate_context_between_handshakes_without_a_restart()
    {
        using var first = ServerCertificate.Create();
        using var second = ServerCertificate.Create();
        var current = SslStreamCertificateContext.Create(first, null);

        await using var server = await TlsTestServer.StartAsync(
            app => app.MapGet("/hello", ctx => ctx.Response.WriteAsync("hello")),
            configureOptions: o => o.Https = new HttpsOptions { CertificateContextSelector = _ => current },
            certificate: first
        );

        Assert.Equal(first.Thumbprint, (await Handshake(server.Port, "localhost", SslApplicationProtocol.Http11)).Certificate.Thumbprint);

        current = SslStreamCertificateContext.Create(second, null);

        Assert.Equal(second.Thumbprint, (await Handshake(server.Port, "localhost", SslApplicationProtocol.Http11)).Certificate.Thumbprint);
        Assert.Equal(second.Thumbprint, (await Handshake(server.Port, "localhost", SslApplicationProtocol.Http2)).Certificate.Thumbprint);
    }

    [Fact]
    public async Task Falls_through_to_the_static_certificate_when_the_context_selector_has_nothing()
    {
        using var fallback = ServerCertificate.Create();

        await using var server = await TlsTestServer.StartAsync(
            app => app.MapGet("/hello", ctx => ctx.Response.WriteAsync("hello")),
            configureOptions: o => o.Https = new HttpsOptions { Certificate = fallback, CertificateContextSelector = _ => null },
            certificate: fallback
        );

        Assert.Equal("hello", await server.Client.GetStringAsync("/hello", Token));
    }

    [Fact]
    public async Task Refuses_the_handshake_quietly_when_there_is_no_certificate_yet()
    {
        await using var server = await TlsTestServer.StartAsync(
            app => app.MapGet("/hello", ctx => ctx.Response.WriteAsync("hello")),
            configureOptions: o => o.Https = new HttpsOptions { CertificateContextSelector = _ => null }
        );

        await Assert.ThrowsAnyAsync<Exception>(() => Handshake(server.Port, "localhost", SslApplicationProtocol.Http11));

        // And the server is still fine for the next client once a certificate exists.
        Assert.Equal(HttpServerState.Running, server.Server.State);
    }

    static async Task<(X509Certificate2 Certificate, SslApplicationProtocol Protocol)> Handshake(
        int port,
        string host,
        SslApplicationProtocol protocol
    )
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port, Token);

        X509Certificate2? presented = null;
        await using var ssl = new SslStream(tcp.GetStream());
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = host,
            ApplicationProtocols = [protocol],
            RemoteCertificateValidationCallback = (_, certificate, _, _) =>
            {
                presented = new X509Certificate2(certificate!);
                return true;
            }
        }, Token);

        return (presented!, ssl.NegotiatedApplicationProtocol);
    }
}
