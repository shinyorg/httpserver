using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Shiny.Net.HttpServer.Tests;

/// <summary>
/// A real server on a real ephemeral port, with a real <see cref="HttpClient"/> in front of it.
/// <para>
/// Deliberately not an in-memory harness. Most of what this server does that could break — framing,
/// keep-alive, chunked bodies, connection reuse — only exists at the socket boundary, and a test
/// that bypasses the socket cannot see any of it.
/// </para>
/// </summary>
sealed class TestServer : IAsyncDisposable
{
    readonly HttpServer server;
    readonly HttpClient client;

    TestServer(HttpServer server, HttpClient client)
    {
        this.server = server;
        this.client = client;
    }

    public HttpClient Client => this.client;

    public HttpServer Server => this.server;

    public int Port { get; private init; }

    public static async Task<TestServer> StartAsync(
        Action<HttpServer> configure,
        Action<ShinyHttpServerBuilder>? configureBuilder = null
    )
    {
        var builder = HttpServer.CreateBuilder();
        builder.Options.Port = 0;
        builder.Options.Address = IPAddress.Loopback;
        builder.Options.HideExceptionDetails = false;
        configureBuilder?.Invoke(builder);

        var server = builder.Build();
        configure(server);

        await server.StartAsync();

        var port = new Uri(server.ListenUrl!).Port;
        var client = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}"),
            Timeout = TimeSpan.FromSeconds(30)
        };

        return new TestServer(server, client) { Port = port };
    }

    /// <summary>
    /// Speaks raw HTTP over a socket, for the cases <see cref="HttpClient"/> refuses to produce —
    /// pipelined requests, deliberately malformed heads, and anything about connection reuse.
    /// </summary>
    public async Task<string> SendRawAsync(string request, int expectedResponses = 1)
    {
        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(IPAddress.Loopback, this.Port);
        await socket.SendAsync(Encoding.ASCII.GetBytes(request));

        var response = new StringBuilder();
        var buffer = new byte[8192];

        while (true)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            int read;
            try
            {
                read = await socket.ReceiveAsync(buffer, SocketFlags.None, timeout.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (read == 0)
                break;

            response.Append(Encoding.ASCII.GetString(buffer, 0, read));

            if (CountResponses(response) >= expectedResponses && !HasPendingBody(response))
                break;
        }

        return response.ToString();
    }

    static int CountResponses(StringBuilder builder)
    {
        var text = builder.ToString();
        var count = 0;
        var index = 0;

        while ((index = text.IndexOf("HTTP/1.", index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += 7;
        }

        return count;
    }

    static bool HasPendingBody(StringBuilder builder)
    {
        // Good enough for the raw cases below: they all use Content-Length or no body at all.
        var text = builder.ToString();
        var headerEnd = text.LastIndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (headerEnd < 0)
            return true;

        var head = text[..headerEnd];
        var marker = head.LastIndexOf("Content-Length: ", StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
            return false;

        var value = head[(marker + 16)..];
        var lineEnd = value.IndexOf('\r');
        if (lineEnd >= 0)
            value = value[..lineEnd];

        return int.TryParse(value, out var length) && text.Length - (headerEnd + 4) < length;
    }

    public async ValueTask DisposeAsync()
    {
        this.client.Dispose();
        await this.server.DisposeAsync();
    }
}
