using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Shiny.Net.HttpServer.Caching;
using Shiny.Net.HttpServer.Compression;
using Shiny.Net.HttpServer.Http2;
using Shiny.Net.HttpServer.Http2.Hpack;
using Shiny.Net.HttpServer.Http3;
using Shiny.Net.HttpServer.Http3.Qpack;
using Shiny.Net.HttpServer.Testing;
using Shiny.Net.HttpServer.Transports;

namespace Shiny.Net.HttpServer.Tests;

/// <summary>
/// Informational (1xx) responses, and 103 Early Hints in particular.
/// <para>
/// <see cref="HttpClient"/> swallows every 1xx it receives, so most of these read the wire directly:
/// raw bytes on HTTP/1.1, raw frames on HTTP/2, and the frame bytes on HTTP/3. The HttpClient tests
/// that remain prove the other half — that a client which ignores the hint still gets a clean final
/// response behind it.
/// </para>
/// </summary>
public class EarlyHintsTests
{
    const string CssHint = "</app.css>; rel=preload; as=style";
    const string FontHint = "</font.woff2>; rel=preload; as=font; crossorigin";

    static CancellationToken Token => TestContext.Current.CancellationToken;

    static void MapHints(HttpServer app, Action<bool>? sent = null) => app.MapGet("/page", async ctx =>
    {
        var result = await ctx.Response.SendEarlyHintsAsync([CssHint, FontHint], ctx.RequestAborted);
        sent?.Invoke(result);

        ctx.Response.Headers.Append(HeaderNames.Link, CssHint);
        await ctx.Response.WriteTextAsync("<html>final</html>", "text/html", ctx.RequestAborted);
    });

    // ---- HTTP/1.1 ----

    [Fact]
    public async Task Http11_sends_a_103_head_before_the_final_response()
    {
        var sent = false;
        await using var server = await TestServer.StartAsync(app => MapHints(app, r => sent = r));

        var raw = await SendRawToEndAsync(server.Port, "GET /page HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n");

        Assert.True(sent);
        Assert.StartsWith(
            "HTTP/1.1 103 Early Hints\r\n"
                + $"Link: {CssHint}\r\n"
                + $"Link: {FontHint}\r\n"
                + "\r\n"
                + "HTTP/1.1 200 OK\r\n",
            raw
        );
        Assert.EndsWith("<html>final</html>", raw);
    }

    [Fact]
    public async Task Http11_interim_head_carries_no_server_date_or_connection_headers()
    {
        await using var server = await TestServer.StartAsync(app => MapHints(app));

        var raw = await SendRawToEndAsync(server.Port, "GET /page HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n");
        var interim = raw[..raw.IndexOf("\r\n\r\n", StringComparison.Ordinal)];

        Assert.DoesNotContain("Date:", interim);
        Assert.DoesNotContain("Connection:", interim);
        Assert.DoesNotContain("Content-Length:", interim);
    }

    [Fact]
    public async Task Http10_client_never_sees_an_interim_response()
    {
        bool? sent = null;
        await using var server = await TestServer.StartAsync(app => MapHints(app, r => sent = r));

        var raw = await SendRawToEndAsync(server.Port, "GET /page HTTP/1.0\r\nHost: localhost\r\n\r\n");

        // A 1.0 client takes the first status line it sees as the response, so the hint is dropped
        // and the handler is told as much — the final response is untouched.
        Assert.False(sent);
        Assert.StartsWith("HTTP/1.1 200 OK\r\n", raw);
        Assert.DoesNotContain("103", raw);
        Assert.EndsWith("<html>final</html>", raw);
    }

    [Fact]
    public async Task Several_informational_responses_go_out_in_order()
    {
        await using var server = await TestServer.StartAsync(app => app.MapGet("/slow", async ctx =>
        {
            await ctx.Response.SendInformationalResponseAsync(StatusCodes.Status102Processing, cancellationToken: ctx.RequestAborted);
            await ctx.Response.SendEarlyHintsAsync(CssHint);
            await ctx.Response.WriteTextAsync("done", cancellationToken: ctx.RequestAborted);
        }));

        var raw = await SendRawToEndAsync(server.Port, "GET /slow HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n");

        Assert.StartsWith(
            "HTTP/1.1 102 Processing\r\n\r\n"
                + "HTTP/1.1 103 Early Hints\r\n"
                + $"Link: {CssHint}\r\n\r\n"
                + "HTTP/1.1 200 OK\r\n",
            raw
        );
    }

    [Fact]
    public async Task Keep_alive_connection_serves_the_next_request_cleanly_after_hints()
    {
        await using var server = await TestServer.StartAsync(app =>
        {
            MapHints(app);
            app.MapGet("/plain", ctx => ctx.Response.WriteTextAsync("plain", cancellationToken: ctx.RequestAborted));
        });

        var raw = await server.SendRawAsync(
            "GET /page HTTP/1.1\r\nHost: localhost\r\n\r\n"
                + "GET /plain HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n",
            // SendRawAsync counts status lines, and the 103 is one: 103 + 200 + 200. Asking for 2
            // stopped reading after the first 200 whenever the second arrived in a later read.
            expectedResponses: 3
        );

        Assert.Equal(1, CountOccurrences(raw, "HTTP/1.1 103 Early Hints"));
        Assert.Equal(2, CountOccurrences(raw, "HTTP/1.1 200 OK"));
        Assert.EndsWith("plain", raw);
    }

    [Fact]
    public async Task HttpClient_ignores_the_hint_and_reads_the_final_response()
    {
        await using var server = await TestServer.StartAsync(app => MapHints(app));

        var response = await server.Client.GetAsync("/page", Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("<html>final</html>", await response.Content.ReadAsStringAsync(Token));
        Assert.Equal(CssHint, response.Headers.GetValues("Link").Single());
    }

    [Fact]
    public async Task Hints_reach_the_wire_underneath_response_compression()
    {
        await using var server = await TestServer.StartAsync(app =>
        {
            app.UseResponseCompression();
            app.MapGet("/page", async ctx =>
            {
                await ctx.Response.SendEarlyHintsAsync(CssHint);
                await ctx.Response.WriteTextAsync(new string('x', 4096), "text/html", ctx.RequestAborted);
            });
        });

        var raw = await SendRawToEndAsync(
            server.Port,
            "GET /page HTTP/1.1\r\nHost: localhost\r\nAccept-Encoding: gzip\r\nConnection: close\r\n\r\n"
        );

        // Compression wraps the body control; the hint must go past the wrapper, not through it.
        Assert.StartsWith($"HTTP/1.1 103 Early Hints\r\nLink: {CssHint}\r\n\r\nHTTP/1.1 200 OK\r\n", raw);
        Assert.Contains("Content-Encoding: gzip", raw);
    }

    [Fact]
    public async Task A_cache_hit_replays_the_final_response_without_hints()
    {
        var calls = 0;
        await using var server = await TestServer.StartAsync(
            app =>
            {
                app.UseOutputCache();
                app.MapGet("/page", async ctx =>
                    {
                        Interlocked.Increment(ref calls);
                        await ctx.Response.SendEarlyHintsAsync(CssHint);
                        await ctx.Response.WriteTextAsync("cached", cancellationToken: ctx.RequestAborted);
                    })
                    .CacheOutput(TimeSpan.FromMinutes(1));
            },
            builder => builder.AddOutputCache()
        );

        const string request = "GET /page HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n";
        var first = await SendRawToEndAsync(server.Port, request);
        var second = await SendRawToEndAsync(server.Port, request);

        Assert.StartsWith("HTTP/1.1 103 Early Hints", first);
        Assert.EndsWith("cached", first);

        // The handler did not run, so nothing asked for a hint — and the cache stores the final
        // response only.
        Assert.StartsWith("HTTP/1.1 200 OK", second);
        Assert.EndsWith("cached", second);
        Assert.Equal(1, calls);
    }

    // ---- Misuse ----

    [Fact]
    public async Task Sending_after_the_response_started_throws()
    {
        Exception? caught = null;
        await using var server = await TestServer.StartAsync(app => app.MapGet("/late", async ctx =>
        {
            await ctx.Response.StartAsync(ctx.RequestAborted);
            try
            {
                await ctx.Response.SendEarlyHintsAsync(CssHint);
            }
            catch (Exception ex)
            {
                caught = ex;
            }
        }));

        var response = await server.Client.GetAsync("/late", Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.IsType<InvalidOperationException>(caught);
    }

    [Theory]
    [InlineData(101)]
    [InlineData(99)]
    [InlineData(200)]
    [InlineData(304)]
    public async Task Non_informational_status_codes_are_refused(int status)
    {
        Exception? caught = null;
        await using var server = await TestServer.StartAsync(app => app.MapGet("/bad", async ctx =>
        {
            try
            {
                await ctx.Response.SendInformationalResponseAsync(status, cancellationToken: ctx.RequestAborted);
            }
            catch (Exception ex)
            {
                caught = ex;
            }
        }));

        await server.Client.GetAsync("/bad", Token);

        Assert.IsType<ArgumentOutOfRangeException>(caught);
    }

    [Fact]
    public async Task A_line_break_in_a_hint_is_refused_rather_than_written()
    {
        Exception? caught = null;
        await using var server = await TestServer.StartAsync(app => app.MapGet("/inject", async ctx =>
        {
            try
            {
                await ctx.Response.SendEarlyHintsAsync("</a.css>; rel=preload\r\n\r\nHTTP/1.1 200 OK");
            }
            catch (Exception ex)
            {
                caught = ex;
            }

            await ctx.Response.WriteTextAsync("safe", cancellationToken: ctx.RequestAborted);
        }));

        var raw = await SendRawToEndAsync(server.Port, "GET /inject HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n");

        Assert.IsType<ArgumentException>(caught);
        Assert.StartsWith("HTTP/1.1 200 OK", raw);
        Assert.Equal(1, CountOccurrences(raw, "HTTP/1.1"));
    }

    [Fact]
    public async Task Body_framing_headers_are_refused_on_an_interim_response()
    {
        var headers = new HeaderDictionary { [HeaderNames.ContentLength] = "10" };
        var response = new HttpContext().Response;

        await Assert.ThrowsAsync<ArgumentException>(
            () => response.SendInformationalResponseAsync(103, headers, Token).AsTask()
        );
    }

    [Fact]
    public async Task A_response_detached_from_any_connection_reports_nothing_sent()
        => Assert.False(await new HttpContext().Response.SendEarlyHintsAsync(CssHint));

    // ---- Tunnels and the in-memory harness ----

    [Fact]
    public async Task Tunnelled_connection_carries_the_interim_bytes_through()
    {
        var app = HttpServer.CreateBuilder().Build();
        MapHints(app);

        // The shape every tunnel provider uses: a duplex pipe the provider pumps raw bytes through.
        await using var connection = new DuplexPipeConnection("tunnel-test", isTunneled: true);
        var serving = Task.Run(() => app.ServeAsync(connection, Token), Token);

        await connection.TransportWriter.WriteAsync(
            Encoding.ASCII.GetBytes("GET /page HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n"),
            Token
        );

        var raw = new StringBuilder();
        while (true)
        {
            var result = await connection.TransportReader.ReadAsync(Token);
            foreach (var segment in result.Buffer)
                raw.Append(Encoding.ASCII.GetString(segment.Span));

            connection.TransportReader.AdvanceTo(result.Buffer.End);

            if (result.IsCompleted || raw.ToString().EndsWith("<html>final</html>", StringComparison.Ordinal))
                break;
        }

        Assert.StartsWith($"HTTP/1.1 103 Early Hints\r\nLink: {CssHint}\r\n", raw.ToString());
        await serving.WaitAsync(TimeSpan.FromSeconds(5), Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task In_memory_client_gets_the_final_response_after_hints(bool useHttp2)
    {
        var sent = false;
        await using var test = TestHttpServer.Create(app => MapHints(app, r => sent = r), useHttp2: useHttp2);

        var response = await test.Client.GetAsync("/page", Token);

        Assert.True(sent);
        Assert.Equal("<html>final</html>", await response.Content.ReadAsStringAsync(Token));
    }

    // ---- HTTP/2 ----

    [Fact]
    public async Task Http2_sends_a_103_headers_frame_without_end_stream_before_the_final_headers()
    {
        await using var server = await TestServer.StartAsync(app => MapHints(app));

        var frames = await RawHttp2GetAsync(server.Port, "/page");
        var headerBlocks = frames.Where(f => f.Type == Http2FrameType.Headers).ToList();

        Assert.True(headerBlocks.Count >= 2);

        var hint = headerBlocks[0];
        Assert.False(hint.Flags.HasFlag(Http2FrameFlags.EndStream));
        Assert.Equal(new HeaderField(":status", "103"), hint.Fields[0]);
        Assert.Equal([CssHint, FontHint], hint.Fields.Where(f => f.Name == "link").Select(f => f.Value));

        var final = headerBlocks[1];
        Assert.Equal(new HeaderField(":status", "200"), final.Fields[0]);

        var body = string.Concat(frames.Where(f => f.Type == Http2FrameType.Data).Select(f => Encoding.UTF8.GetString(f.Payload)));
        Assert.Equal("<html>final</html>", body);
    }

    [Fact]
    public async Task Http2_HttpClient_reads_the_final_response_behind_the_hint()
    {
        var sent = false;
        await using var server = await TestServer.StartAsync(app => MapHints(app, r => sent = r));

        using var client = new HttpClient(new SocketsHttpHandler())
        {
            BaseAddress = new Uri($"http://127.0.0.1:{server.Port}"),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };

        var response = await client.GetAsync("/page", Token);

        Assert.True(sent);
        Assert.Equal(HttpVersion.Version20, response.Version);
        Assert.Equal("<html>final</html>", await response.Content.ReadAsStringAsync(Token));
    }

    // ---- HTTP/3 ----

    [Fact]
    public void Http3_frames_the_hint_as_a_qpack_headers_frame()
    {
        // The frame bytes, checked directly: QUIC needs msquic, which is not on every test machine,
        // and the write to the stream around this is a plain copy.
        var headers = new HeaderDictionary();
        headers.Append(HeaderNames.Link, CssHint);
        headers.Append(HeaderNames.Link, FontHint);

        var frame = new ArrayBufferWriter<byte>();
        Http3ResponseBodyControl.WriteInformationalFrame(frame, new QpackEncoder(), 103, headers);

        var sequence = new ReadOnlySequence<byte>(frame.WrittenMemory);
        Assert.True(Http3Frame.TryReadHeader(sequence, out var header, out var consumed));
        Assert.Equal(Http3FrameType.Headers, header.KnownType);

        var payload = sequence.Slice(consumed).ToArray();
        Assert.Equal(header.Length, payload.Length);

        var fields = new QpackDecoder().Decode(payload);

        Assert.Equal(new HeaderField(":status", "103"), fields[0]);
        Assert.Equal([CssHint, FontHint], fields.Where(f => f.Name == "link").Select(f => f.Value));
    }

    // ---- Helpers ----

    static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = haystack.IndexOf(needle, i + 1, StringComparison.Ordinal))
            count++;

        return count;
    }

    static async Task<string> SendRawToEndAsync(int port, string request)
    {
        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(IPAddress.Loopback, port, Token);
        await socket.SendAsync(Encoding.ASCII.GetBytes(request), Token);

        var response = new StringBuilder();
        var buffer = new byte[8192];

        while (true)
        {
            var read = await socket.ReceiveAsync(buffer, SocketFlags.None, Token);
            if (read == 0)
                return response.ToString();

            response.Append(Encoding.Latin1.GetString(buffer, 0, read));
        }
    }

    sealed record RawFrame(Http2FrameType Type, Http2FrameFlags Flags, byte[] Payload, List<HeaderField> Fields);

    /// <summary>
    /// A minimal HTTP/2 client over a raw socket: preface, SETTINGS, one GET on stream 1, then every
    /// frame for that stream until END_STREAM. Header blocks are decoded with a single decoder in
    /// arrival order, since HPACK's dynamic table is shared across the whole connection.
    /// </summary>
    static async Task<List<RawFrame>> RawHttp2GetAsync(int port, string path)
    {
        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(IPAddress.Loopback, port, Token);
        await using var network = new NetworkStream(socket, ownsSocket: false);

        var output = new ArrayBufferWriter<byte>();
        output.Write(Http2Frame.Preface);
        Http2Frame.Write(output, Http2FrameType.Settings, Http2FrameFlags.None, 0, []);

        var block = new ArrayBufferWriter<byte>();
        new HpackEncoder().Encode(
            [new(":method", "GET"), new(":scheme", "http"), new(":path", path), new(":authority", "localhost")],
            block
        );
        Http2Frame.Write(output, Http2FrameType.Headers, Http2FrameFlags.EndHeaders | Http2FrameFlags.EndStream, 1, block.WrittenSpan);

        await network.WriteAsync(output.WrittenMemory, Token);

        var decoder = new HpackDecoder();
        var frames = new List<RawFrame>();
        var headerBytes = new byte[Http2FrameHeader.Size];

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        while (true)
        {
            await network.ReadExactlyAsync(headerBytes, timeout.Token);
            var sequence = new ReadOnlySequence<byte>(headerBytes);
            Assert.True(Http2Frame.TryReadHeader(ref sequence, out var header));

            var payload = new byte[header.Length];
            await network.ReadExactlyAsync(payload, timeout.Token);

            if (header.Type == Http2FrameType.Settings && !header.Has(Http2FrameFlags.Ack))
            {
                var ack = new ArrayBufferWriter<byte>();
                Http2Frame.Write(ack, Http2FrameType.Settings, Http2FrameFlags.Ack, 0, []);
                await network.WriteAsync(ack.WrittenMemory, timeout.Token);
                continue;
            }

            if (header.StreamId != 1)
                continue;

            var fields = new List<HeaderField>();
            if (header.Type == Http2FrameType.Headers)
                decoder.Decode(payload, fields);

            frames.Add(new RawFrame(header.Type, header.Flags, payload, fields));

            if (header.Has(Http2FrameFlags.EndStream))
                return frames;
        }
    }
}
