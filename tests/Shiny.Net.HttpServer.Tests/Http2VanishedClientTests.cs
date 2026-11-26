using System.Net;
using System.Net.Sockets;

namespace Shiny.Net.HttpServer.Tests;

/// <summary>
/// A client that vanishes - a laptop lid closing, a phone handing over from Wi-Fi to cellular, a NAT
/// forgetting the mapping - sends no reset and no FIN. Over HTTP/1.1 the body-read and write
/// timeouts notice. HTTP/2 must too, or the stream, its handler and whatever the handler holds (an
/// open file, a parallel-transfer slot, an upload lock) wait for ever.
/// </summary>
public class Http2VanishedClientTests
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_stream_the_client_stops_giving_credit_to_is_reset_after_the_write_timeout()
    {
        var aborted = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var server = await TestServer.StartAsync(
            app =>
            {
                app.MapGet("/big", async ctx =>
                {
                    var chunk = new byte[16 * 1024];
                    try
                    {
                        for (var i = 0; i < 1024; i++)
                            await ctx.Response.Body.WriteAsync(chunk, ctx.RequestAborted);
                        aborted.TrySetResult(null);
                    }
                    catch (Exception ex)
                    {
                        aborted.TrySetResult(ex);
                    }
                });
                app.MapGet("/ok", ctx => ctx.Response.WriteAsync("ok"));
            },
            builder => builder.Options.Limits.ResponseWriteTimeout = TimeSpan.FromMilliseconds(500)
        );

        // A 16 KB window and never a WINDOW_UPDATE: what a vanished peer looks like from here.
        await using var client = await Http2RawClient.ConnectAsync(server.Port, initialWindow: 16 * 1024);
        await client.HeadersAsync(1, "GET", "/big", endStream: true);

        var reset = await client.WaitForResetAsync(1, Wait);
        Assert.NotNull(reset);
        Assert.Equal(8u, reset.ErrorCode); // CANCEL

        // The handler is told, so it lets go of what it holds.
        var outcome = await aborted.Task.WaitAsync(Wait, Token);
        Assert.NotNull(outcome);

        // One stream's stall is that stream's: the connection carries on.
        await client.HeadersAsync(3, "GET", "/ok", endStream: true);
        Assert.NotNull(await client.WaitForEndAsync(3, Wait));
    }

    [Fact]
    public async Task A_stream_that_keeps_getting_credit_is_not_reset_however_long_it_runs()
    {
        const int Frames = 16;

        await using var server = await TestServer.StartAsync(
            app => app.MapGet("/big", async ctx =>
            {
                var chunk = new byte[16 * 1024];
                for (var i = 0; i < Frames; i++)
                    await ctx.Response.Body.WriteAsync(chunk, ctx.RequestAborted);
            }),
            builder => builder.Options.Limits.ResponseWriteTimeout = TimeSpan.FromSeconds(1)
        );

        await using var client = await Http2RawClient.ConnectAsync(server.Port, initialWindow: 16 * 1024);

        // A slow reader: credit for each frame, but only after a pause, for well over the timeout in
        // total. The pause is a quarter of the timeout so a loaded CI runner has room to be late.
        client.OnFrame = frame =>
        {
            if (frame.Type == Http2RawFrame.Data && frame.Length > 0)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(250);
                        await client.WindowUpdateAsync(1, frame.Length);
                        await client.WindowUpdateAsync(0, frame.Length);
                    }
                    catch
                    {
                        // The client may be disposed by the time the pause ends; a fault left here
                        // would surface as an unobserved task exception in some other test.
                    }
                });
            }
        };

        await client.HeadersAsync(1, "GET", "/big", endStream: true);

        var end = await client.WaitForEndAsync(1, TimeSpan.FromSeconds(30));
        Assert.NotNull(end);
        Assert.NotEqual(Http2RawFrame.RstStream, end.Type);
        Assert.Equal(Frames * 16 * 1024, client.DataBytes(1));
    }

    [Fact]
    public async Task A_request_body_that_stops_arriving_fails_the_read_after_the_body_timeout()
    {
        var failure = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var server = await TestServer.StartAsync(
            app => app.MapPost("/upload", async ctx =>
            {
                try
                {
                    await ctx.Request.Body.CopyToAsync(Stream.Null, ctx.RequestAborted);
                    failure.TrySetResult(null);
                }
                catch (Exception ex)
                {
                    failure.TrySetResult(ex);
                    throw;
                }
            }),
            builder => builder.Options.Limits.RequestBodyTimeout = TimeSpan.FromMilliseconds(500)
        );

        await using var client = await Http2RawClient.ConnectAsync(server.Port);
        await client.HeadersAsync(1, "POST", "/upload", endStream: false);
        await client.BodyAsync(1, 10 * 1024, endStream: false);

        // ...and then nothing more, ever.
        var outcome = await failure.Task.WaitAsync(Wait, Token);
        var bad = Assert.IsType<BadHttpRequestException>(outcome);
        Assert.Equal(StatusCodes.Status408RequestTimeout, bad.StatusCode);
        Assert.NotNull(await client.WaitForEndAsync(1, Wait));
    }

    [Fact]
    public async Task A_request_body_that_keeps_arriving_slowly_is_read_to_the_end()
    {
        var received = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var server = await TestServer.StartAsync(
            app => app.MapPost("/upload", async ctx =>
            {
                var buffer = new byte[4096];
                long total = 0;
                int read;
                while ((read = await ctx.Request.Body.ReadAsync(buffer, ctx.RequestAborted)) > 0)
                    total += read;
                received.TrySetResult(total);
            }),
            builder => builder.Options.Limits.RequestBodyTimeout = TimeSpan.FromSeconds(1)
        );

        // A chunk every quarter of the timeout, for well over the timeout in total. The gap leaves a
        // loaded CI runner room to be late without the read timing out.
        await using var client = await Http2RawClient.ConnectAsync(server.Port);
        await client.HeadersAsync(1, "POST", "/upload", endStream: false);
        for (var i = 0; i < 8; i++)
        {
            await Task.Delay(250, Token);
            await client.DataAsync(1, 1000, endStream: i == 7);
        }

        Assert.Equal(8000, await received.Task.WaitAsync(Wait, Token));
    }

    [Fact]
    public async Task A_connection_whose_client_stops_reading_entirely_is_closed_after_the_write_timeout()
    {
        var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var server = await TestServer.StartAsync(
            app => app.MapGet("/big", async ctx =>
            {
                using var registration = ctx.RequestAborted.Register(() => aborted.TrySetResult());
                var chunk = new byte[64 * 1024];
                try
                {
                    for (var i = 0; i < 4096; i++)
                        await ctx.Response.Body.WriteAsync(chunk, ctx.RequestAborted);
                }
                catch (Exception)
                {
                }
            }),
            builder =>
            {
                builder.Options.Limits.ResponseWriteTimeout = TimeSpan.FromMilliseconds(500);
                builder.Options.Http2.InitialConnectionWindowSize = 64 * 1024 * 1024;
            }
        );

        // Plenty of flow-control credit, then the socket is never read again: the server's writes
        // fill the TCP buffers and its flush blocks, which is the whole connection stalling.
        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        socket.ReceiveBufferSize = 4096;
        await socket.ConnectAsync(IPAddress.Loopback, server.Port, Token);

        var settings = new List<byte>();
        Http2RawClient.AddSetting(settings, 0x4, 0x7FFFFFFF);
        var hello = new List<byte>();
        hello.AddRange("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"u8.ToArray());
        hello.AddRange(Http2RawClient.BuildFrame(Http2RawFrame.Settings, 0, 0, settings.ToArray()));
        var increment = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(increment, 0x7FFFFFFF - 65_535);
        hello.AddRange(Http2RawClient.BuildFrame(Http2RawFrame.WindowUpdate, 0, 0, increment));
        hello.AddRange(Http2RawClient.BuildFrame(Http2RawFrame.Headers, 0x5, 1, Http2RawClient.RequestBlock("GET", "/big").ToArray()));
        await socket.SendAsync(hello.ToArray(), SocketFlags.None, Token);

        await aborted.Task.WaitAsync(TimeSpan.FromSeconds(15), Token);
    }
}
