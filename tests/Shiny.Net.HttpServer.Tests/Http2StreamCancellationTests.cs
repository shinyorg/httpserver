using System.Net;

namespace Shiny.Net.HttpServer.Tests;

/// <summary>
/// A client cancelling one HTTP/2 stream - a browser seeking a video, leaving a page, giving up on a
/// download - must cost only that stream. Every frame on the connection goes out through one shared
/// writer, so a write torn part way through by one stream's cancellation corrupts the connection for
/// every other stream on it: over TLS the next record fails to decrypt, and over h2c the frame
/// boundaries are lost.
/// </summary>
public class Http2StreamCancellationTests
{
    const int Size = 16 * 1024 * 1024;
    static readonly byte[] Chunk = CreateChunk();

    static CancellationToken Token => TestContext.Current.CancellationToken;

    static byte[] CreateChunk()
    {
        var chunk = new byte[64 * 1024];
        new Random(7).NextBytes(chunk);
        return chunk;
    }

    static void MapDownload(HttpServer app) => app.MapGet("/big", async ctx =>
    {
        ctx.Response.ContentType = "application/octet-stream";
        ctx.Response.ContentLength = Size;

        for (var sent = 0; sent < Size; sent += Chunk.Length)
            await ctx.Response.Body.WriteAsync(Chunk, ctx.RequestAborted);
    });

    static async Task CancelPartWay(HttpClient client, int keep)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var response = await client.GetAsync("/big", HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.Equal(HttpVersion.Version20, response.Version);

        var body = await response.Content.ReadAsStreamAsync(cts.Token);
        var buffer = new byte[64 * 1024];
        var read = 0;
        while (read < keep)
        {
            var n = await body.ReadAsync(buffer, cts.Token);
            Assert.NotEqual(0, n);
            read += n;
        }

        // Cancelled with the server mid-write: the client sends RST_STREAM.
        await cts.CancelAsync();
        response.Dispose();
    }

    static async Task<long> DownloadAll(HttpClient client, CancellationToken ct)
    {
        using var response = await client.GetAsync("/big", HttpCompletionOption.ResponseHeadersRead, ct);
        Assert.Equal(HttpVersion.Version20, response.Version);
        await using var body = await response.Content.ReadAsStreamAsync(ct);

        var buffer = new byte[256 * 1024];
        long received = 0;
        int n;
        while ((n = await body.ReadAsync(buffer, ct)) > 0)
        {
            for (var i = 0; i < n; i++)
            {
                if (buffer[i] != Chunk[(received + i) % Chunk.Length])
                    throw new InvalidDataException($"Byte {received + i} is wrong.");
            }
            received += n;
        }
        return received;
    }

    static HttpClient Http2(HttpClient client)
    {
        client.DefaultRequestVersion = HttpVersion.Version20;
        client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        return client;
    }

    [Fact]
    public async Task Over_tls_a_cancelled_download_leaves_the_connection_usable()
    {
        await using var server = await TlsTestServer.StartAsync(MapDownload, configureClient: c => Http2(c));

        for (var round = 0; round < 10; round++)
        {
            await CancelPartWay(server.Client, 1024 * 1024);
            Assert.Equal(Size, await DownloadAll(server.Client, Token));
        }
    }

    [Fact]
    public async Task Over_tls_cancelling_one_download_does_not_break_the_others_running_beside_it()
    {
        await using var server = await TlsTestServer.StartAsync(MapDownload, configureClient: c => Http2(c));

        var others = Enumerable.Range(0, 4).Select(_ => DownloadAll(server.Client, Token)).ToList();
        for (var round = 0; round < 5; round++)
            await CancelPartWay(server.Client, 512 * 1024);

        Assert.All(await Task.WhenAll(others), received => Assert.Equal(Size, received));
    }

    [Fact]
    public async Task Over_h2c_a_cancelled_download_leaves_the_connection_usable()
    {
        await using var server = await TestServer.StartAsync(MapDownload);
        using var client = Http2(new HttpClient(new SocketsHttpHandler())
        {
            BaseAddress = server.Client.BaseAddress,
            Timeout = TimeSpan.FromSeconds(30)
        });

        for (var round = 0; round < 10; round++)
        {
            await CancelPartWay(client, 1024 * 1024);
            Assert.Equal(Size, await DownloadAll(client, Token));
        }
    }
}
