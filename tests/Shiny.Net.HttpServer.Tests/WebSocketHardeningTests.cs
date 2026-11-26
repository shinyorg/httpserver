using System.Buffers.Binary;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Shiny.Net.HttpServer.WebSockets;
using ServerCloseStatus = Shiny.Net.HttpServer.WebSockets.WebSocketCloseStatus;

namespace Shiny.Net.HttpServer.Tests;

/// <summary>
/// Protocol violations a browser never produces, so these speak raw frames over a socket — the BCL
/// client refuses to send most of them.
/// </summary>
public class WebSocketHardeningTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    static Task<TestServer> StartAsync(TaskCompletionSource<string> outcome, Action<WebSocketAcceptOptions>? configure = null)
        => TestServer.StartAsync(app => app.MapGet("/ws", async ctx =>
        {
            var options = new WebSocketAcceptOptions { KeepAliveInterval = null };
            configure?.Invoke(options);

            await using var socket = await ctx.AcceptWebSocketAsync(options, ctx.RequestAborted);

            try
            {
                var message = await socket.ReceiveAsync(CancellationToken.None);
                outcome.TrySetResult(message is null ? "closed:" + socket.CloseResult?.Status : "message:" + message.Text);
            }
            catch (WebSocketProtocolException ex)
            {
                outcome.TrySetResult("error:" + ex.Status);
            }
        }));

    /// <summary>A bare-bones client: handshake, then whatever frames the test wants.</summary>
    sealed class RawSocket : IAsyncDisposable
    {
        readonly TcpClient tcp = new();
        NetworkStream stream = null!;

        public static async Task<RawSocket> ConnectAsync(int port, string? extensions = null)
        {
            var raw = new RawSocket();
            await raw.tcp.ConnectAsync("127.0.0.1", port, Token);
            raw.stream = raw.tcp.GetStream();

            var head =
                "GET /ws HTTP/1.1\r\nHost: localhost\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n" +
                (extensions is null ? "" : $"Sec-WebSocket-Extensions: {extensions}\r\n") +
                "\r\n";

            await raw.stream.WriteAsync(Encoding.ASCII.GetBytes(head), Token);

            // Read the 101 head byte by byte so no frame bytes are swallowed with it.
            var received = new List<byte>();
            var one = new byte[1];
            while (!EndsWithBlankLine(received) && await raw.stream.ReadAsync(one, Token) == 1)
                received.Add(one[0]);

            Assert.StartsWith("HTTP/1.1 101", Encoding.ASCII.GetString([.. received]));
            return raw;
        }

        static bool EndsWithBlankLine(List<byte> b)
            => b.Count >= 4 && b[^4] == '\r' && b[^3] == '\n' && b[^2] == '\r' && b[^1] == '\n';

        public async Task SendFrameAsync(byte opcode, byte[] payload, bool rsv1 = false)
        {
            var mask = new byte[] { 0x11, 0x22, 0x33, 0x44 };
            var frame = new List<byte> { (byte)(0x80 | (rsv1 ? 0x40 : 0) | opcode), (byte)(0x80 | payload.Length) };
            frame.AddRange(mask);
            frame.AddRange(payload.Select((b, i) => (byte)(b ^ mask[i % 4])));

            await this.stream.WriteAsync(frame.ToArray(), Token);
        }

        /// <summary>Reads frames until a close arrives and returns its status code.</summary>
        public async Task<int> ReadCloseCodeAsync()
        {
            while (true)
            {
                var header = await this.ReadExactlyAsync(2);
                var opcode = header[0] & 0x0F;
                var payload = await this.ReadExactlyAsync(header[1] & 0x7F);

                if (opcode == 0x8)
                    return payload.Length >= 2 ? BinaryPrimitives.ReadUInt16BigEndian(payload) : 1005;
            }
        }

        async Task<byte[]> ReadExactlyAsync(int count)
        {
            var buffer = new byte[count];
            await this.stream.ReadExactlyAsync(buffer, Token).AsTask().WaitAsync(TimeSpan.FromSeconds(10), Token);
            return buffer;
        }

        public ValueTask DisposeAsync()
        {
            this.tcp.Dispose();
            return default;
        }
    }

    static byte[] ClosePayload(ushort code)
    {
        var payload = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(payload, code);
        return payload;
    }

    [Fact]
    public async Task A_text_message_that_is_not_utf8_fails_with_1007()
    {
        var outcome = new TaskCompletionSource<string>();
        await using var server = await StartAsync(outcome);
        await using var client = await RawSocket.ConnectAsync(server.Port);

        await client.SendFrameAsync(0x1, [0x68, 0xC3, 0x28]);   // "h" then a broken two-byte sequence

        Assert.Equal(1007, await client.ReadCloseCodeAsync());
        Assert.Equal("error:InvalidPayloadData", await outcome.Task.WaitAsync(TimeSpan.FromSeconds(10), Token));
    }

    [Theory]
    [InlineData(1005)]
    [InlineData(1006)]
    [InlineData(1015)]
    [InlineData(999)]
    [InlineData(2000)]
    [InlineData(5000)]
    public async Task A_reserved_close_code_is_not_echoed(int code)
    {
        var outcome = new TaskCompletionSource<string>();
        await using var server = await StartAsync(outcome);
        await using var client = await RawSocket.ConnectAsync(server.Port);

        await client.SendFrameAsync(0x8, ClosePayload((ushort)code));

        Assert.Equal(1002, await client.ReadCloseCodeAsync());
        Assert.Equal("closed:ProtocolError", await outcome.Task.WaitAsync(TimeSpan.FromSeconds(10), Token));
    }

    [Fact]
    public async Task A_valid_close_code_is_still_echoed()
    {
        var outcome = new TaskCompletionSource<string>();
        await using var server = await StartAsync(outcome);
        await using var client = await RawSocket.ConnectAsync(server.Port);

        await client.SendFrameAsync(0x8, ClosePayload(4001));

        Assert.Equal(4001, await client.ReadCloseCodeAsync());
    }

    [Fact]
    public async Task A_close_reason_that_is_not_utf8_fails_with_1002()
    {
        var outcome = new TaskCompletionSource<string>();
        await using var server = await StartAsync(outcome);
        await using var client = await RawSocket.ConnectAsync(server.Port);

        await client.SendFrameAsync(0x8, [.. ClosePayload(1000), 0xFF, 0xFE]);

        Assert.Equal(1002, await client.ReadCloseCodeAsync());
    }

    [Fact]
    public async Task A_corrupt_compressed_payload_fails_the_connection_instead_of_escaping()
    {
        var outcome = new TaskCompletionSource<string>();
        await using var server = await StartAsync(outcome);
        await using var client = await RawSocket.ConnectAsync(server.Port, "permessage-deflate");

        // 0xFF opens a block of reserved type 3: not deflate.
        await client.SendFrameAsync(0x2, [0xFF, 0xFF, 0xFF, 0xFF], rsv1: true);

        Assert.Equal(1007, await client.ReadCloseCodeAsync());
        Assert.Equal("error:InvalidPayloadData", await outcome.Task.WaitAsync(TimeSpan.FromSeconds(10), Token));
    }

    [Fact]
    public async Task A_handshake_from_a_disallowed_origin_is_refused()
    {
        var outcome = new TaskCompletionSource<string>();
        await using var server = await StartAsync(outcome, o => o.AllowedOrigins.Add("https://app.example"));

        using var evil = new ClientWebSocket();
        evil.Options.SetRequestHeader("Origin", "https://evil.example");

        evil.Options.CollectHttpResponseDetails = true;

        await Assert.ThrowsAnyAsync<WebSocketException>(
            () => evil.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/ws"), Token)
        );
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, evil.HttpStatusCode);

        using var good = new ClientWebSocket();
        good.Options.SetRequestHeader("Origin", "https://APP.example/");
        await good.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/ws"), Token);

        Assert.Equal(System.Net.WebSockets.WebSocketState.Open, good.State);
    }
}
