using System.Buffers;
using System.Net;
using System.Text;
using Shiny.Net.HttpServer.Http2.Hpack;

namespace Shiny.Net.HttpServer.Tests;

/// <summary>
/// HTTP/2 under clients that do not play nicely: slow or absent body readers, withheld or excess
/// flow-control credit, resets in bulk, oversized and endless header blocks, lies about
/// Content-Length. Most of these need <see cref="Http2RawClient"/>, because <see cref="HttpClient"/>
/// refuses to misbehave.
/// </summary>
public class Http2HardeningTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    static HttpClient CreateClient(int port) => new(new SocketsHttpHandler())
    {
        BaseAddress = new Uri($"http://127.0.0.1:{port}"),
        DefaultRequestVersion = HttpVersion.Version20,
        DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        Timeout = TimeSpan.FromSeconds(15)
    };

    static void MapSize(HttpServer app) => app.MapGet("/size/{n}", async ctx =>
    {
        var n = int.Parse((string)ctx.Request.RouteValues["n"]!);
        await ctx.Response.WriteBytesAsync(new byte[n], cancellationToken: ctx.RequestAborted);
    });

    static CancellationToken Within(TimeSpan timeout)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(Token);
        source.CancelAfter(timeout);

        return source.Token;
    }

    // ---- 1. The read loop never waits on a handler ----

    [Theory]
    [InlineData(70 * 1024)]
    [InlineData(2 * 1024 * 1024)]
    public async Task Answers_before_reading_a_body_bigger_than_the_pipe_would_hold(int size)
    {
        // The handler writes a large response before it touches the body. The read loop used to
        // stop at 64 KiB of unread body, so it never saw the WINDOW_UPDATEs the response needed.
        await using var server = await TestServer.StartAsync(app =>
        {
            app.MapPost("/ignore", async ctx =>
                await ctx.Response.WriteBytesAsync(new byte[1024 * 1024], cancellationToken: ctx.RequestAborted));

            app.MapPost("/read", async ctx =>
            {
                await ctx.Request.Body.CopyToAsync(Stream.Null, ctx.RequestAborted);
                await ctx.Response.WriteBytesAsync(new byte[1024 * 1024], cancellationToken: ctx.RequestAborted);
            });
        });

        using var client = CreateClient(server.Port);

        foreach (var path in new[] { "/ignore", "/read" })
        {
            var response = await client.PostAsync(path, new ByteArrayContent(new byte[size]), Within(Wait));
            var body = await response.Content.ReadAsByteArrayAsync(Within(Wait));

            Assert.Equal(1024 * 1024, body.Length);
        }
    }

    [Fact]
    public async Task A_slow_upload_does_not_hold_up_the_rest_of_the_connection()
    {
        var release = new TaskCompletionSource();

        await using var server = await TestServer.StartAsync(app =>
        {
            app.MapPost("/slow", async ctx =>
            {
                await release.Task.WaitAsync(ctx.RequestAborted);

                var buffer = new MemoryStream();
                await ctx.Request.Body.CopyToAsync(buffer, ctx.RequestAborted);
                await ctx.Response.WriteAsync(buffer.Length.ToString());
            });

            app.MapGet("/ping", ctx => ctx.Response.WriteAsync("pong"));
        });

        using var client = CreateClient(server.Port);
        Assert.Equal("pong", await client.GetStringAsync("/ping", Token));

        var upload = client.PostAsync("/slow", new ByteArrayContent(new byte[1024 * 1024]), Token);
        await Task.Delay(300, Token);

        // Same connection, while the upload sits unread.
        Assert.Equal("pong", await client.GetStringAsync("/ping", Within(TimeSpan.FromSeconds(5))));

        release.SetResult();
        var response = await upload;
        Assert.Equal("1048576", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task A_handler_ends_when_its_client_disconnects_mid_upload()
    {
        var finished = new TaskCompletionSource();

        await using var server = await TestServer.StartAsync(app => app.MapPost("/ignore", async ctx =>
        {
            try
            {
                await ctx.Response.WriteBytesAsync(new byte[1024 * 1024], cancellationToken: ctx.RequestAborted);
            }
            finally
            {
                finished.TrySetResult();
            }
        }));

        var client = await Http2RawClient.ConnectAsync(server.Port);
        await client.HeadersAsync(1, "POST", "/ignore", endStream: false);
        await client.BodyAsync(1, 200 * 1024, endStream: false);
        await Task.Delay(300, Token);

        // Grants no response credit, then goes away.
        await client.DisposeAsync();

        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
    }

    [Fact]
    public async Task Resets_a_stream_that_sends_more_than_its_window()
    {
        await using var server = await TestServer.StartAsync(
            app =>
            {
                app.MapPost("/hold", async ctx => await Task.Delay(Timeout.Infinite, ctx.RequestAborted));
                MapSize(app);
            },
            builder => builder.Options.Http2.InitialStreamWindowSize = 80 * 1024
        );

        await using var client = await Http2RawClient.ConnectAsync(server.Port);
        await client.HeadersAsync(1, "POST", "/hold", endStream: false);
        await client.BodyAsync(1, 100 * 1024, endStream: false);

        var reset = await client.WaitForResetAsync(1, Wait);
        Assert.NotNull(reset);
        Assert.Equal(3u, reset.ErrorCode); // FLOW_CONTROL_ERROR

        // A stream error: the connection carries on.
        await client.HeadersAsync(3, "GET", "/size/10", endStream: true);
        Assert.NotNull(await client.WaitForEndAsync(3, Wait));
        Assert.Null(client.Frames.FirstOrDefault(x => x.Type == Http2RawFrame.GoAway));
    }

    [Fact]
    public async Task Closes_a_connection_that_sends_more_than_its_window()
    {
        await using var server = await TestServer.StartAsync(
            app => app.MapPost("/hold", async ctx => await Task.Delay(Timeout.Infinite, ctx.RequestAborted)),
            builder =>
            {
                builder.Options.Http2.InitialStreamWindowSize = 1024 * 1024;
                builder.Options.Http2.InitialConnectionWindowSize = 65_535;
            }
        );

        await using var client = await Http2RawClient.ConnectAsync(server.Port);
        await client.HeadersAsync(1, "POST", "/hold", endStream: false);
        await client.BodyAsync(1, 100 * 1024, endStream: false);

        var goAway = await client.WaitForGoAwayAsync(Wait);
        Assert.NotNull(goAway);
        Assert.Equal(3u, goAway.ErrorCode); // FLOW_CONTROL_ERROR
    }

    // ---- 2. Rapid reset ----

    [Fact]
    public async Task Resetting_streams_does_not_get_a_client_past_the_concurrency_limit()
    {
        var concurrent = 0;
        var maxConcurrent = 0;

        await using var server = await TestServer.StartAsync(app => app.MapGet("/slow", async ctx =>
        {
            var now = Interlocked.Increment(ref concurrent);
            int seen;
            while ((seen = Volatile.Read(ref maxConcurrent)) < now && Interlocked.CompareExchange(ref maxConcurrent, now, seen) != seen)
            {
            }

            try
            {
                // Ignores RequestAborted, as plenty of real handlers do while a query runs.
                await Task.Delay(1500, CancellationToken.None);
            }
            finally
            {
                Interlocked.Decrement(ref concurrent);
            }
        }));

        await using var client = await Http2RawClient.ConnectAsync(server.Port);

        var flood = new List<byte>();
        var block = Http2RawClient.RequestBlock("GET", "/slow").ToArray();
        var rst = new byte[] { 0, 0, 0, 8 };

        for (uint id = 1; id < 4000; id += 2)
        {
            flood.AddRange(Http2RawClient.BuildFrame(Http2RawFrame.Headers, 0x5, id, block));
            flood.AddRange(Http2RawClient.BuildFrame(Http2RawFrame.RstStream, 0, id, rst));
        }

        try
        {
            await client.SendRawAsync(flood.ToArray());
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException)
        {
            // The server hung up part way through, which is the point.
        }

        var goAway = await client.WaitForGoAwayAsync(Wait);

        Assert.NotNull(goAway);
        Assert.Equal(11u, goAway.ErrorCode); // ENHANCE_YOUR_CALM
        Assert.InRange(Volatile.Read(ref maxConcurrent), 1, 100);
    }

    // ---- 3. Header limits ----

    [Fact]
    public async Task Closes_a_connection_that_never_ends_its_header_block()
    {
        await using var server = await TestServer.StartAsync(MapSize);
        await using var client = await Http2RawClient.ConnectAsync(server.Port);

        await client.SendFrameAsync(Http2RawFrame.Headers, 0, 1, Http2RawClient.RequestBlock("GET", "/size/1").ToArray());

        var continuation = Http2RawClient.BuildFrame(Http2RawFrame.Continuation, 0, 1, new byte[16 * 1024]);
        long sent = 0;

        try
        {
            for (var i = 0; i < 4096 && !client.Closed; i++)
            {
                await client.SendRawAsync(continuation);
                sent += continuation.Length;
            }
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException)
        {
        }

        var goAway = await client.WaitForGoAwayAsync(Wait);

        Assert.NotNull(goAway);
        Assert.Equal(11u, goAway.ErrorCode); // ENHANCE_YOUR_CALM
        Assert.True(sent < 64L * 1024 * 1024, "The server buffered the whole 64 MiB.");
    }

    [Fact]
    public async Task Refuses_a_header_block_that_decodes_past_the_list_limit()
    {
        // 16 KB on the wire: one 4 KB cookie added to the dynamic table, then thousands of one-byte
        // references to it - 16 MB of cookies once decoded.
        var invoked = 0;

        await using var server = await TestServer.StartAsync(app =>
        {
            app.MapGet("/cookies", ctx =>
            {
                Interlocked.Increment(ref invoked);
                return ctx.Response.WriteAsync("ok");
            });

            MapSize(app);
        });

        await using var client = await Http2RawClient.ConnectAsync(server.Port);

        var block = Http2RawClient.RequestBlock("GET", "/cookies");
        Http2RawClient.WriteInteger(block, 32, 6, 0x40); // literal, indexed, name "cookie"
        Http2RawClient.WriteString(block, new string('a', 4000));

        while (block.Count < 16_000)
            block.Add(0x80 | 62); // the dynamic entry just added

        var allocated = GC.GetTotalAllocatedBytes(true);
        await client.SendFrameAsync(Http2RawFrame.Headers, 0x5, 1, block.ToArray());

        var response = await client.WaitForAsync(x => x.Type == Http2RawFrame.Headers && x.StreamId == 1, Wait);
        allocated = GC.GetTotalAllocatedBytes(true) - allocated;

        Assert.NotNull(response);
        Assert.Contains("431", Encoding.ASCII.GetString(response.Payload));
        Assert.Equal(0, Volatile.Read(ref invoked));
        Assert.True(allocated < 64L * 1024 * 1024, $"Allocated {allocated / (1024 * 1024)} MiB refusing the request.");

        // Refused, not disconnected: the block was decoded in full, so HPACK is still in step.
        await client.HeadersAsync(3, "GET", "/size/5", endStream: true);
        Assert.NotNull(await client.WaitForEndAsync(3, Wait));
    }

    [Fact]
    public async Task Answers_431_to_headers_over_the_limit_and_keeps_the_connection()
    {
        await using var server = await TestServer.StartAsync(MapSize);
        await using var client = await Http2RawClient.ConnectAsync(server.Port);

        var block = Http2RawClient.RequestBlock("GET", "/size/1");
        Http2RawClient.WriteHeader(block, "x-a", new string('a', 20_000));
        Http2RawClient.WriteHeader(block, "x-b", new string('b', 20_000));

        // Split across HEADERS and CONTINUATION at the minimum maximum frame size.
        var bytes = block.ToArray();
        const int FrameSize = 16 * 1024;

        for (var offset = 0; offset < bytes.Length; offset += FrameSize)
        {
            var size = Math.Min(FrameSize, bytes.Length - offset);
            var type = offset == 0 ? Http2RawFrame.Headers : Http2RawFrame.Continuation;
            var flags = (byte)((offset == 0 ? 0x1 : 0) | (offset + size == bytes.Length ? 0x4 : 0));

            await client.SendFrameAsync(type, flags, 1, bytes.AsSpan(offset, size).ToArray());
        }

        var response = await client.WaitForAsync(x => x.Type == Http2RawFrame.Headers && x.StreamId == 1, Wait);
        Assert.NotNull(response);
        Assert.Contains("431", Encoding.ASCII.GetString(response.Payload));

        await client.HeadersAsync(3, "GET", "/size/5", endStream: true);
        Assert.NotNull(await client.WaitForEndAsync(3, Wait));
    }

    // ---- 4. Every writer wakes when credit arrives ----

    [Fact]
    public async Task One_connection_window_update_wakes_every_waiting_stream()
    {
        await using var server = await TestServer.StartAsync(MapSize);

        for (var round = 0; round < 3; round++)
        {
            // Big stream windows, so only the connection window - never raised - is short.
            await using var client = await Http2RawClient.ConnectAsync(server.Port, initialWindow: 1 << 20);

            await client.HeadersAsync(1, "GET", "/size/65535", endStream: true);
            Assert.NotNull(await client.WaitForEndAsync(1, Wait));

            const int Streams = 20;
            for (uint i = 0; i < Streams; i++)
                await client.HeadersAsync(3 + 2 * i, "GET", "/size/100", endStream: true);

            await Task.Delay(300, Token);
            await client.WindowUpdateAsync(0, Streams * 100);

            for (uint i = 0; i < Streams; i++)
                Assert.NotNull(await client.WaitForEndAsync(3 + 2 * i, TimeSpan.FromSeconds(3)));
        }
    }

    [Fact]
    public async Task A_writer_waiting_on_the_connection_window_is_released_when_its_stream_is_reset()
    {
        var finished = new TaskCompletionSource();

        await using var server = await TestServer.StartAsync(app =>
        {
            MapSize(app);

            app.MapGet("/wait", async ctx =>
            {
                try
                {
                    // No token, like the server's own error responses.
                    await ctx.Response.WriteBytesAsync(new byte[1000], cancellationToken: CancellationToken.None);
                }
                catch
                {
                }
                finally
                {
                    finished.TrySetResult();
                }
            });
        });

        await using var client = await Http2RawClient.ConnectAsync(server.Port, initialWindow: 1 << 20);
        await client.HeadersAsync(1, "GET", "/size/65535", endStream: true);
        Assert.NotNull(await client.WaitForEndAsync(1, Wait));

        await client.HeadersAsync(3, "GET", "/wait", endStream: true);
        await Task.Delay(300, Token);
        await client.ResetAsync(3);

        await finished.Task.WaitAsync(TimeSpan.FromSeconds(3), Token);
    }

    // ---- 5. Staged bytes are not dropped ----

    [Fact]
    public async Task Sends_body_bytes_that_were_written_but_never_flushed()
    {
        await using var server = await TestServer.StartAsync(app => app.MapGet("/unflushed", ctx =>
        {
            ctx.Response.BodyWriter.Write(new byte[100]);
            return ValueTask.CompletedTask;
        }));

        using var client = CreateClient(server.Port);

        Assert.Equal(100, (await client.GetByteArrayAsync("/unflushed", Token)).Length);
    }

    // ---- 6. HEAD and Content-Length ----

    [Fact]
    public async Task Answers_head_without_a_body()
    {
        await using var server = await TestServer.StartAsync(app => app.MapGet("/file", async ctx =>
        {
            ctx.Response.ContentLength = 5000;
            await ctx.Response.Body.WriteAsync(new byte[5000], ctx.RequestAborted);
        }));

        using var http = CreateClient(server.Port);
        using var response = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/file"), Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(5000, response.Content.Headers.ContentLength);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(Token));

        await using var client = await Http2RawClient.ConnectAsync(server.Port);
        await client.HeadersAsync(1, "HEAD", "/file", endStream: true);

        var end = await client.WaitForEndAsync(1, Wait);
        Assert.NotNull(end);
        Assert.Equal(Http2RawFrame.Headers, end.Type);

        await Task.Delay(200, Token);
        Assert.Equal(0, client.DataBytes(1));
    }

    [Fact]
    public async Task Resets_a_response_that_writes_past_its_content_length()
    {
        await using var server = await TestServer.StartAsync(app => app.MapGet("/over", async ctx =>
        {
            ctx.Response.ContentLength = 10;
            await ctx.Response.Body.WriteAsync(new byte[20], ctx.RequestAborted);
        }));

        await using var client = await Http2RawClient.ConnectAsync(server.Port);
        await client.HeadersAsync(1, "GET", "/over", endStream: true);

        var end = await client.WaitForEndAsync(1, Wait);
        Assert.NotNull(end);
        Assert.Equal(Http2RawFrame.RstStream, end.Type);
        Assert.Equal(0, client.DataBytes(1));
    }

    [Fact]
    public async Task Resets_a_response_that_stops_short_of_its_content_length()
    {
        await using var server = await TestServer.StartAsync(app => app.MapGet("/short", async ctx =>
        {
            ctx.Response.ContentLength = 100;
            await ctx.Response.Body.WriteAsync(new byte[50], ctx.RequestAborted);
        }));

        await using var client = await Http2RawClient.ConnectAsync(server.Port);
        await client.HeadersAsync(1, "GET", "/short", endStream: true);

        var end = await client.WaitForEndAsync(1, Wait);
        Assert.NotNull(end);
        Assert.Equal(Http2RawFrame.RstStream, end.Type);
        Assert.Equal(50, client.DataBytes(1));
        Assert.DoesNotContain(client.Frames, x => x.StreamId == 1 && x.EndStream);
    }

    // ---- 7. One END_STREAM per stream ----

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(40_000)]
    public async Task Ends_a_stream_exactly_once(int size)
    {
        // Some bytes flushed, then the rest left staged for completion to send - which is where
        // the last DATA frame used to carry END_STREAM and an empty one after it carried it again.
        await using var server = await TestServer.StartAsync(app => app.MapGet("/tail/{n}", async ctx =>
        {
            await ctx.Response.BodyWriter.WriteAsync(new byte[10], ctx.RequestAborted);
            ctx.Response.BodyWriter.Write(new byte[int.Parse((string)ctx.Request.RouteValues["n"]!)]);
        }));

        await using var client = await Http2RawClient.ConnectAsync(server.Port);

        await client.HeadersAsync(1, "GET", $"/tail/{size}", endStream: true);
        Assert.NotNull(await client.WaitForEndAsync(1, Wait));
        await Task.Delay(300, Token);

        Assert.Single(client.Frames, x => x.StreamId == 1 && x.EndStream);
        Assert.Equal(10 + size, client.DataBytes(1));
    }

    // ---- 8. Frames after the client ended its stream ----

    [Fact]
    public async Task Resets_a_stream_that_sends_data_after_ending_it()
    {
        await using var server = await TestServer.StartAsync(app =>
        {
            app.MapPost("/sink", async ctx =>
            {
                await Task.Delay(500, ctx.RequestAborted);
                await ctx.Response.WriteAsync("ok");
            });

            MapSize(app);
        });

        await using var client = await Http2RawClient.ConnectAsync(server.Port);
        await client.HeadersAsync(1, "POST", "/sink", endStream: false);
        await client.DataAsync(1, 10, endStream: true);
        await client.DataAsync(1, 10, endStream: false);

        var reset = await client.WaitForResetAsync(1, Wait);
        Assert.NotNull(reset);
        Assert.Equal(5u, reset.ErrorCode); // STREAM_CLOSED

        await client.HeadersAsync(3, "GET", "/size/1", endStream: true);
        Assert.NotNull(await client.WaitForEndAsync(3, Wait));
        Assert.DoesNotContain(client.Frames, x => x.Type == Http2RawFrame.GoAway);
    }

    // ---- 9. HPACK integers ----

    [Theory]
    [InlineData(new byte[] { 0xFF, 0x80, 0x80, 0x80, 0x80, 0x0F })] // an index that wraps negative
    [InlineData(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x0F })] // past uint
    [InlineData(new byte[] { 0x3F, 0xE1, 0xFF, 0xFF, 0xFF, 0x0F })] // a table size that wraps negative
    [InlineData(new byte[] { 0x0F, 0x80, 0x80, 0x80, 0x80, 0x08 })] // a name index that wraps negative
    [InlineData(new byte[] { 0x00, 0x7F, 0x80, 0x80, 0x80, 0x80, 0x08 })] // a string length that wraps negative
    public void Rejects_header_integers_that_overflow(byte[] block)
    {
        var decoder = new HpackDecoder();

        Assert.Throws<HpackException>(() => decoder.Decode(block, []));
    }

    [Fact]
    public async Task Closes_with_compression_error_on_an_overflowing_header_integer()
    {
        await using var server = await TestServer.StartAsync(MapSize);
        await using var client = await Http2RawClient.ConnectAsync(server.Port);

        await client.SendFrameAsync(Http2RawFrame.Headers, 0x5, 1, [0xFF, 0x80, 0x80, 0x80, 0x80, 0x0F]);

        var goAway = await client.WaitForGoAwayAsync(Wait);
        Assert.NotNull(goAway);
        Assert.Equal(9u, goAway.ErrorCode); // COMPRESSION_ERROR
    }

    // ---- 10. Timeouts ----

    [Fact]
    public async Task Closes_an_idle_connection_after_the_keep_alive_timeout()
    {
        await using var server = await TestServer.StartAsync(
            MapSize,
            builder => builder.Options.Limits.KeepAliveTimeout = TimeSpan.FromMilliseconds(500)
        );

        await using var client = await Http2RawClient.ConnectAsync(server.Port);

        var goAway = await client.WaitForGoAwayAsync(Wait);
        Assert.NotNull(goAway);
        Assert.Equal(0u, goAway.ErrorCode);
        Assert.True(await client.WaitForCloseAsync(Wait));
    }

    [Fact]
    public async Task Keeps_a_connection_with_an_open_stream_past_the_keep_alive_timeout()
    {
        await using var server = await TestServer.StartAsync(
            app => app.MapGet("/slow", async ctx =>
            {
                await Task.Delay(1200, ctx.RequestAborted);
                await ctx.Response.WriteAsync("done");
            }),
            builder => builder.Options.Limits.KeepAliveTimeout = TimeSpan.FromMilliseconds(400)
        );

        await using var client = await Http2RawClient.ConnectAsync(server.Port);
        await client.HeadersAsync(1, "GET", "/slow", endStream: true);

        var end = await client.WaitForEndAsync(1, Wait);
        Assert.NotNull(end);
        Assert.True(end.EndStream);
        Assert.Equal(4, client.DataBytes(1));
    }

    [Fact]
    public async Task Closes_a_connection_whose_header_block_stalls()
    {
        await using var server = await TestServer.StartAsync(
            MapSize,
            builder => builder.Options.Limits.RequestHeadersTimeout = TimeSpan.FromMilliseconds(500)
        );

        await using var client = await Http2RawClient.ConnectAsync(server.Port);

        // No END_HEADERS, and nothing after it.
        await client.SendFrameAsync(Http2RawFrame.Headers, 0x1, 1, Http2RawClient.RequestBlock("GET", "/size/1").ToArray());

        var goAway = await client.WaitForGoAwayAsync(Wait);
        Assert.NotNull(goAway);
        Assert.Equal(1u, goAway.ErrorCode); // PROTOCOL_ERROR
    }

    // ---- 11. GOAWAY ----

    [Fact]
    public async Task Finishes_open_streams_after_the_client_sends_goaway()
    {
        await using var server = await TestServer.StartAsync(app => app.MapGet("/slow", async ctx =>
        {
            await Task.Delay(500, ctx.RequestAborted);
            await ctx.Response.WriteAsync("done");
        }));

        await using var client = await Http2RawClient.ConnectAsync(server.Port);
        await client.HeadersAsync(1, "GET", "/slow", endStream: true);
        await client.GoAwayAsync(1);

        var end = await client.WaitForEndAsync(1, Wait);
        Assert.NotNull(end);
        Assert.True(end.EndStream);
        Assert.Equal(4, client.DataBytes(1));

        // Nothing left to serve, so the connection closes.
        Assert.True(await client.WaitForCloseAsync(Wait));
    }

    [Fact]
    public async Task Sends_goaway_when_the_server_stops()
    {
        await using var server = await TestServer.StartAsync(MapSize);
        await using var client = await Http2RawClient.ConnectAsync(server.Port);

        await client.HeadersAsync(1, "GET", "/size/1", endStream: true);
        Assert.NotNull(await client.WaitForEndAsync(1, Wait));

        await server.Server.StopAsync(Token);

        var goAway = await client.WaitForGoAwayAsync(Wait);
        Assert.NotNull(goAway);
        Assert.Equal(0u, goAway.ErrorCode);
        Assert.Equal(1u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(goAway.Payload) & 0x7FFFFFFF);
    }

    [Fact]
    public async Task Stopping_lets_an_open_stream_finish_without_cancelling_it()
    {
        // The same courtesy an HTTP/1.1 request in flight gets: GOAWAY goes out straight away, but the
        // stream already being handled keeps its RequestAborted and is allowed to answer.
        var sawAbort = true;
        var started = new TaskCompletionSource();

        await using var server = await TestServer.StartAsync(app => app.MapGet("/slow", async ctx =>
        {
            started.TrySetResult();
            await Task.Delay(1000);
            sawAbort = ctx.RequestAborted.IsCancellationRequested;
            await ctx.Response.WriteAsync("finished");
        }));

        await using var client = await Http2RawClient.ConnectAsync(server.Port);
        await client.HeadersAsync(1, "GET", "/slow", endStream: true);
        await started.Task.WaitAsync(Wait);

        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var stop = server.Server.StopAsync(budget.Token);

        var goAway = await client.WaitForGoAwayAsync(Wait);
        Assert.NotNull(goAway);
        Assert.Equal(0u, goAway.ErrorCode);

        Assert.NotNull(await client.WaitForEndAsync(1, Wait));
        await stop;
        Assert.False(sawAbort);
    }

    // ---- 12. Request Content-Length ----

    [Theory]
    [InlineData(100, 50)]
    [InlineData(10, 20)]
    public async Task Resets_a_request_whose_body_does_not_match_its_content_length(int declared, int sent)
    {
        var outcome = new TaskCompletionSource<string>();

        await using var server = await TestServer.StartAsync(app => app.MapPost("/upload", async ctx =>
        {
            try
            {
                await ctx.Request.Body.CopyToAsync(Stream.Null, ctx.RequestAborted);
                outcome.TrySetResult("complete");
            }
            catch (Exception ex)
            {
                outcome.TrySetResult(ex.GetType().Name);
            }
        }));

        await using var client = await Http2RawClient.ConnectAsync(server.Port);
        await client.HeadersAsync(1, "POST", "/upload", endStream: false, ("content-length", declared.ToString()));
        await client.DataAsync(1, sent, endStream: true);

        var reset = await client.WaitForResetAsync(1, Wait);
        Assert.NotNull(reset);
        Assert.Equal(1u, reset.ErrorCode); // PROTOCOL_ERROR

        Assert.NotEqual("complete", await outcome.Task.WaitAsync(Wait, Token));
    }

    // ---- 13. Flow-control rules ----

    [Fact]
    public async Task Resets_a_stream_whose_window_update_overflows()
    {
        await using var server = await TestServer.StartAsync(app =>
            app.MapPost("/hold", async ctx => await Task.Delay(Timeout.Infinite, ctx.RequestAborted)));

        await using var client = await Http2RawClient.ConnectAsync(server.Port);
        await client.HeadersAsync(1, "POST", "/hold", endStream: false);
        await client.WindowUpdateAsync(1, 0x7FFFFFFF);

        var reset = await client.WaitForResetAsync(1, Wait);
        Assert.NotNull(reset);
        Assert.Equal(3u, reset.ErrorCode); // FLOW_CONTROL_ERROR
        Assert.DoesNotContain(client.Frames, x => x.Type == Http2RawFrame.GoAway);
    }

    [Fact]
    public async Task Resets_only_the_stream_on_a_zero_window_update()
    {
        await using var server = await TestServer.StartAsync(app =>
            app.MapPost("/hold", async ctx => await Task.Delay(Timeout.Infinite, ctx.RequestAborted)));

        await using var client = await Http2RawClient.ConnectAsync(server.Port);
        await client.HeadersAsync(1, "POST", "/hold", endStream: false);
        await client.WindowUpdateAsync(1, 0);

        var reset = await client.WaitForResetAsync(1, Wait);
        Assert.NotNull(reset);
        Assert.Equal(1u, reset.ErrorCode); // PROTOCOL_ERROR
        Assert.DoesNotContain(client.Frames, x => x.Type == Http2RawFrame.GoAway);
    }

    [Fact]
    public async Task Closes_the_connection_when_a_settings_change_overflows_a_stream_window()
    {
        await using var server = await TestServer.StartAsync(app =>
            app.MapPost("/hold", async ctx => await Task.Delay(Timeout.Infinite, ctx.RequestAborted)));

        await using var client = await Http2RawClient.ConnectAsync(server.Port);
        await client.HeadersAsync(1, "POST", "/hold", endStream: false);

        // Window to exactly 2^31-1, then one more through INITIAL_WINDOW_SIZE.
        await client.WindowUpdateAsync(1, 0x7FFFFFFF - 65_535);

        var settings = new List<byte>();
        Http2RawClient.AddSetting(settings, 0x4, 65_536);
        await client.SendFrameAsync(Http2RawFrame.Settings, 0, 0, settings.ToArray());

        var goAway = await client.WaitForGoAwayAsync(Wait);
        Assert.NotNull(goAway);
        Assert.Equal(3u, goAway.ErrorCode); // FLOW_CONTROL_ERROR
    }

    // ---- 14. The connection window is never overdrawn ----

    [Fact]
    public async Task Never_sends_more_than_the_connection_window_allows()
    {
        await using var server = await TestServer.StartAsync(MapSize);

        for (var round = 0; round < 3; round++)
        {
            await using var client = await Http2RawClient.ConnectAsync(server.Port, initialWindow: 16 << 20);

            long granted = 65_535;
            long overdraw = 0;
            var gate = new object();

            client.OnFrame = frame =>
            {
                if (frame.Type != Http2RawFrame.Data)
                    return;

                lock (gate)
                {
                    var total = client.TotalData;
                    if (total > granted)
                        overdraw = Math.Max(overdraw, total - granted);

                    // A small window, kept just above empty, so many writers contend for it.
                    if (granted - total < 8192)
                    {
                        granted += 20_000;
                        _ = client.WindowUpdateAsync(0, 20_000);
                    }
                }
            };

            const int Streams = 40;
            for (uint i = 0; i < Streams; i++)
                await client.HeadersAsync(1 + 2 * i, "GET", "/size/100000", endStream: true);

            for (uint i = 0; i < Streams; i++)
                Assert.NotNull(await client.WaitForEndAsync(1 + 2 * i, Wait));

            lock (gate)
                Assert.Equal(0, overdraw);
        }
    }
}
