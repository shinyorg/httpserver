using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace Shiny.Net.HttpServer.Tests;

public class TlsTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Serves_a_request_over_tls()
    {
        await using var server = await TlsTestServer.StartAsync(
            app => app.OnGet("/ping", ctx => ctx.Response.WriteAsync("pong"))
        );

        Assert.Equal("pong", await server.Client.GetStringAsync("/ping", Token));
    }

    [Fact]
    public async Task Reports_the_https_scheme_and_an_encrypted_connection()
    {
        await using var server = await TlsTestServer.StartAsync(
            app => app.OnGet("/info", ctx => ctx.Response.WriteAsync($"{ctx.Request.Scheme}/{ctx.Connection.IsEncrypted}"))
        );

        Assert.Equal("https/True", await server.Client.GetStringAsync("/info", Token));
        Assert.StartsWith("https://", server.Server.ListenUrl);
    }

    [Fact]
    public async Task Negotiates_http2_over_alpn()
    {
        await using var server = await TlsTestServer.StartAsync(
            app => app.OnGet("/proto", ctx => ctx.Response.WriteAsync(ctx.Request.Protocol)),
            configureClient: client =>
            {
                client.DefaultRequestVersion = HttpVersion.Version20;
                client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher;
            }
        );

        var response = await server.Client.GetAsync("/proto", Token);

        Assert.Equal(HttpVersion.Version20, response.Version);
        Assert.Equal("HTTP/2", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Falls_back_to_http11_when_http2_is_turned_off()
    {
        await using var server = await TlsTestServer.StartAsync(
            app => app.OnGet("/proto", ctx => ctx.Response.WriteAsync(ctx.Request.Protocol)),
            configureOptions: o => o.Http2.Enabled = false,
            configureClient: client =>
            {
                // Asks for HTTP/2 but will settle: ALPN never offers h2, so the client takes 1.1.
                client.DefaultRequestVersion = HttpVersion.Version20;
                client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
            }
        );

        var response = await server.Client.GetAsync("/proto", Token);

        Assert.Equal(HttpVersion.Version11, response.Version);
        Assert.Equal("HTTP/1.1", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Keeps_serving_http11_clients_on_a_server_offering_http2()
    {
        await using var server = await TlsTestServer.StartAsync(
            app => app.OnGet("/proto", ctx => ctx.Response.WriteAsync(ctx.Request.Protocol))
        );

        // Default client version is 1.1 — offering h2 must not break a client that never asks.
        var response = await server.Client.GetAsync("/proto", Token);

        Assert.Equal(HttpVersion.Version11, response.Version);
    }

    [Fact]
    public async Task Selects_a_certificate_by_sni()
    {
        using var alpha = ServerCertificate.Create(o =>
        {
            o.CommonName = "alpha";
            o.DnsNames.Add("alpha.test");
        });
        using var beta = ServerCertificate.Create(o =>
        {
            o.CommonName = "beta";
            o.DnsNames.Add("beta.test");
        });

        var options = new HttpServerOptions
        {
            Address = IPAddress.Loopback,
            Port = 0,
            Https = new HttpsOptions
            {
                CertificateSelector = host => host switch
                {
                    "alpha.test" => alpha,
                    "beta.test" => beta,
                    _ => null
                }
            }
        };

        await using var server = new HttpServer(options);
        server.OnGet("/", ctx => ctx.Response.WriteAsync("ok"));
        await server.StartAsync(Token);

        var port = new Uri(server.ListenUrl!).Port;

        Assert.Contains("alpha", await GetServedSubjectAsync(port, "alpha.test"));
        Assert.Contains("beta", await GetServedSubjectAsync(port, "beta.test"));
    }

    [Fact]
    public async Task Fails_the_handshake_when_sni_matches_no_certificate()
    {
        using var only = ServerCertificate.Create(o =>
        {
            o.CommonName = "known";
            o.DnsNames.Add("known.test");
        });

        var options = new HttpServerOptions
        {
            Address = IPAddress.Loopback,
            Port = 0,
            Https = new HttpsOptions { CertificateSelector = host => host == "known.test" ? only : null }
        };

        await using var server = new HttpServer(options);
        server.OnGet("/", ctx => ctx.Response.WriteAsync("ok"));
        await server.StartAsync(Token);

        var port = new Uri(server.ListenUrl!).Port;

        await Assert.ThrowsAnyAsync<Exception>(() => GetServedSubjectAsync(port, "unknown.test"));

        // The listener has to survive it — one client asking for a host we cannot serve is not a
        // reason to stop serving the ones we can.
        Assert.Contains("known", await GetServedSubjectAsync(port, "known.test"));
    }

    [Fact]
    public async Task Accepts_a_client_certificate_and_surfaces_it()
    {
        using var clientCertificate = ServerCertificate.Create(o =>
        {
            o.CommonName = "the-client";
            o.AllowClientAuthentication = true;
        });

        await using var server = await TlsTestServer.StartAsync(
            app => app.OnGet("/who", ctx => ctx.Response.WriteAsync(ctx.Connection.ClientCertificate?.Subject ?? "anonymous")),
            configureOptions: o => o.Https!.ClientCertificateMode = ClientCertificateMode.RequireCertificate
        );

        using var client = CreateClient(server, clientCertificate);

        Assert.Contains("the-client", await client.GetStringAsync("/who", Token));
    }

    [Fact]
    public async Task Rejects_a_client_that_presents_no_certificate_when_one_is_required()
    {
        await using var server = await TlsTestServer.StartAsync(
            app => app.OnGet("/who", ctx => ctx.Response.WriteAsync("in")),
            configureOptions: o => o.Https!.ClientCertificateMode = ClientCertificateMode.RequireCertificate
        );

        // server.Client pins the server certificate but offers none of its own.
        await Assert.ThrowsAsync<HttpRequestException>(() => server.Client.GetStringAsync("/who", Token));
    }

    [Fact]
    public async Task Serves_a_client_with_no_certificate_when_one_is_merely_allowed()
    {
        await using var server = await TlsTestServer.StartAsync(
            app => app.OnGet("/who", ctx => ctx.Response.WriteAsync(ctx.Connection.ClientCertificate?.Subject ?? "anonymous")),
            configureOptions: o => o.Https!.ClientCertificateMode = ClientCertificateMode.AllowCertificate
        );

        Assert.Equal("anonymous", await server.Client.GetStringAsync("/who", Token));
    }

    [Fact]
    public async Task Consults_custom_client_certificate_validation()
    {
        using var clientCertificate = ServerCertificate.Create(o =>
        {
            o.CommonName = "not-welcome";
            o.AllowClientAuthentication = true;
        });

        await using var server = await TlsTestServer.StartAsync(
            app => app.OnGet("/who", ctx => ctx.Response.WriteAsync("in")),
            configureOptions: o =>
            {
                o.Https!.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                o.Https.ClientCertificateValidation = (_, certificate, _, _) =>
                    certificate?.Subject.Contains("welcome-please", StringComparison.Ordinal) == true;
            }
        );

        using var client = CreateClient(server, clientCertificate);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStringAsync("/who", Token));
    }

    [Fact]
    public async Task A_stalled_handshake_does_not_block_other_connections()
    {
        await using var server = await TlsTestServer.StartAsync(
            app => app.OnGet("/ping", ctx => ctx.Response.WriteAsync("pong"))
        );

        // Connects and then says nothing at all. If the handshake ran on the accept loop this
        // would hold up every client behind it until the handshake timeout fired.
        using var silent = new TcpClient();
        await silent.ConnectAsync(IPAddress.Loopback, server.Port, Token);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        Assert.Equal("pong", await server.Client.GetStringAsync("/ping", timeout.Token));
    }

    [Fact]
    public async Task Survives_a_client_speaking_cleartext_to_a_tls_port()
    {
        await using var server = await TlsTestServer.StartAsync(
            app => app.OnGet("/ping", ctx => ctx.Response.WriteAsync("pong"))
        );

        using (var confused = new TcpClient())
        {
            await confused.ConnectAsync(IPAddress.Loopback, server.Port, Token);
            await confused.GetStream().WriteAsync("GET /ping HTTP/1.1\r\nHost: x\r\n\r\n"u8.ToArray(), Token);
        }

        Assert.Equal("pong", await server.Client.GetStringAsync("/ping", Token));
    }

    [Fact]
    public async Task Times_out_a_handshake_that_never_completes()
    {
        await using var server = await TlsTestServer.StartAsync(
            app => app.OnGet("/ping", ctx => ctx.Response.WriteAsync("pong")),
            configureOptions: o => o.Https!.HandshakeTimeout = TimeSpan.FromMilliseconds(250)
        );

        using var silent = new TcpClient();
        await silent.ConnectAsync(IPAddress.Loopback, server.Port, Token);
        var stream = silent.GetStream();

        // The server gives up and closes, so the read completes with zero bytes rather than hanging.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        var read = await stream.ReadAsync(new byte[1], timeout.Token);

        Assert.Equal(0, read);
    }

    static HttpClient CreateClient(TlsTestServer server, X509Certificate2 clientCertificate) =>
        new(
            new SocketsHttpHandler
            {
                SslOptions = new SslClientAuthenticationOptions
                {
                    TargetHost = "localhost",
                    RemoteCertificateValidationCallback = CertificatePinning.CreateValidator(server.Certificate),
                    ClientCertificates = [clientCertificate]
                }
            }
        )
        {
            BaseAddress = server.BaseAddress,
            Timeout = TimeSpan.FromSeconds(30)
        };

    static async Task<string> GetServedSubjectAsync(int port, string hostName)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port, Token);

        using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false, (_, _, _, _) => true);
        await ssl.AuthenticateAsClientAsync(
            new SslClientAuthenticationOptions { TargetHost = hostName },
            Token
        );

        return ssl.RemoteCertificate!.Subject;
    }
}
