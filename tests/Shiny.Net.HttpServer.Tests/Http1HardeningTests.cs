using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Shiny.Net.HttpServer.Http1;
using Shiny.Net.HttpServer.Transports;

namespace Shiny.Net.HttpServer.Tests;

/// <summary>
/// Framing, smuggling, timeout and lifecycle cases at the socket boundary. Raw sockets throughout:
/// every one of these is something a well-behaved client like <see cref="HttpClient"/> will not send.
/// </summary>
public class Http1HardeningTests
{
    static async Task<(HttpServer Server, int Port)> StartAsync(Action<HttpServer> map, Action<HttpServerOptions>? configure = null)
    {
        var options = new HttpServerOptions { Port = 0, Address = IPAddress.Loopback, HideExceptionDetails = true };
        configure?.Invoke(options);

        var server = new HttpServer(options);
        map(server);
        await server.StartAsync();

        return (server, new Uri(server.ListenUrl!).Port);
    }

    static async Task<Socket> ConnectAsync(int port)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(IPAddress.Loopback, port);
        return socket;
    }

    static Task SendAsync(Socket socket, string text) => socket.SendAsync(Encoding.Latin1.GetBytes(text));

    /// <summary>Reads until the server closes or goes quiet. The marker says which, so a test can tell "closed" from "still open".</summary>
    static async Task<string> ReadAllAsync(Socket socket, TimeSpan quiet)
    {
        var text = new StringBuilder();
        var buffer = new byte[65536];

        while (true)
        {
            using var timeout = new CancellationTokenSource(quiet);
            int read;
            try
            {
                read = await socket.ReceiveAsync(buffer, SocketFlags.None, timeout.Token);
            }
            catch (OperationCanceledException)
            {
                text.Append("<<OPEN>>");
                return text.ToString();
            }
            catch (SocketException)
            {
                text.Append("<<RESET>>");
                return text.ToString();
            }

            if (read == 0)
            {
                text.Append("<<EOF>>");
                return text.ToString();
            }

            text.Append(Encoding.Latin1.GetString(buffer, 0, read));
        }
    }

    /// <summary>Closed by the server, by FIN or — when the client had sent bytes the server never read — by reset.</summary>
    static void AssertClosed(string result)
        => Assert.True(result.EndsWith("<<EOF>>") || result.EndsWith("<<RESET>>"), result);

    static int CountResponses(string text)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf("HTTP/1.1 ", index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += 9;
        }
        return count;
    }

    // ---- 1. Idle cleartext connections ----

    [Fact]
    public async Task A_cleartext_connection_that_never_speaks_is_closed_after_the_keep_alive_timeout()
    {
        var (server, port) = await StartAsync(_ => { }, o => o.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(1));
        await using var _ = server;

        using var socket = await ConnectAsync(port);
        var clock = Stopwatch.StartNew();
        var result = await ReadAllAsync(socket, TimeSpan.FromSeconds(8));

        AssertClosed(result);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(6), $"Closed after {clock.Elapsed}");
        await Wait.UntilAsync(() => server.ActiveConnections == 0, CancellationToken.None);
        Assert.Equal(0, server.ActiveConnections);
    }

    [Fact]
    public async Task A_partial_http2_preface_is_closed_after_the_request_headers_timeout()
    {
        var (server, port) = await StartAsync(_ => { }, o =>
        {
            o.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(60);
            o.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(1);
        });
        await using var _ = server;

        using var socket = await ConnectAsync(port);
        await SendAsync(socket, "PRI * HTTP/2.0\r\n");
        var result = await ReadAllAsync(socket, TimeSpan.FromSeconds(8));

        AssertClosed(result);
    }

    // ---- 2 & 11. Chunked framing ----

    [Fact]
    public async Task Chunk_data_not_followed_by_crlf_is_rejected_rather_than_buffered()
    {
        var (server, port) = await StartAsync(app => app.MapPost("/echo", async ctx =>
        {
            var buffer = new byte[4096];
            while (await ctx.Request.Body.ReadAsync(buffer) > 0)
            {
            }
            await ctx.Response.WriteTextAsync("done");
        }));
        await using var _ = server;

        using var socket = await ConnectAsync(port);
        await SendAsync(socket, "POST /echo HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\n\r\n1\r\na" + new string('A', 64 * 1024));
        var result = await ReadAllAsync(socket, TimeSpan.FromSeconds(5));

        Assert.StartsWith("HTTP/1.1 400", result);
        AssertClosed(result);
    }

    [Fact]
    public async Task Chunk_data_not_followed_by_crlf_is_rejected_on_an_unrouted_request_too()
    {
        // The drain runs for every request with a body, matched or not, authenticated or not.
        var (server, port) = await StartAsync(app => app.MapGet("/only", ctx => ctx.Response.WriteTextAsync("x")));
        await using var _ = server;

        using var socket = await ConnectAsync(port);
        await SendAsync(socket, "POST /nope HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\n\r\n1\r\na" + new string('A', 64 * 1024));
        var result = await ReadAllAsync(socket, TimeSpan.FromSeconds(5));

        Assert.StartsWith("HTTP/1.1 404", result);
        AssertClosed(result);
    }

    [Theory]
    [InlineData("3\r\nabcGET /evil HTTP/1.1\r\n0\r\n\r\n")]
    [InlineData("3\r\nabc\rX0\r\n\r\n")]
    [InlineData("3 junk\r\nabc\r\n0\r\n\r\n")]
    [InlineData("3\r\nabc\r\n0\r\nnot a trailer\r\n\r\n")]
    [InlineData("3\r\nabc\r\n0\r\nBad Name: x\r\n\r\n")]
    public async Task Chunked_stream_rejects_loose_framing(string raw)
    {
        var reader = await PipeOf(raw);
        await Assert.ThrowsAsync<BadHttpRequestException>(async () => await ReadAll(new ChunkedReadStream(reader, null)));
    }

    [Theory]
    [InlineData("3\nabc\n0\n\n")]
    [InlineData("3;name=value\r\nabc\r\n0\r\nX-Checksum: 1\r\n\r\n")]
    [InlineData("3 ;ext\r\nabc\r\n0\r\n\r\n")]
    public async Task Chunked_stream_still_accepts_bare_lf_extensions_and_trailers(string raw)
    {
        var reader = await PipeOf(raw);
        Assert.Equal("abc", await ReadAll(new ChunkedReadStream(reader, null)));
    }

    [Fact]
    public async Task An_endless_trailer_section_is_refused()
    {
        var reader = await PipeOf("1\r\na\r\n0\r\nX-Big: " + new string('b', 4096), complete: false);
        var stream = new ChunkedReadStream(reader, null, maxTrailersSize: 1024);

        var ex = await Assert.ThrowsAsync<BadHttpRequestException>(async () => await ReadAll(stream));
        Assert.Equal(StatusCodes.Status431RequestHeaderFieldsTooLarge, ex.StatusCode);
    }

    static async Task<PipeReader> PipeOf(string content, bool complete = true)
    {
        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(Encoding.ASCII.GetBytes(content));

        if (complete)
            await pipe.Writer.CompleteAsync();

        return pipe.Reader;
    }

    static async Task<string> ReadAll(Stream stream)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return Encoding.ASCII.GetString(buffer.ToArray());
    }

    // ---- 3. Transfer-Encoding smuggling ----

    [Theory]
    [InlineData("HTTP/1.1", "gzip", "400")]
    [InlineData("HTTP/1.1", "identity", "400")]
    [InlineData("HTTP/1.1", "chunked, gzip", "400")]
    [InlineData("HTTP/1.1", "xchunked", "400")]
    [InlineData("HTTP/1.1", "chunked, chunked", "400")]
    [InlineData("HTTP/1.1", "gzip, chunked", "501")]
    [InlineData("HTTP/1.0", "chunked", "400")]
    public async Task Transfer_encoding_that_is_not_exactly_chunked_is_refused_and_never_smuggles(string version, string codings, string status)
    {
        var smuggled = 0;
        var (server, port) = await StartAsync(app =>
        {
            app.MapPost("/a", ctx => ctx.Response.WriteTextAsync("A"));
            app.MapGet("/smuggled", ctx =>
            {
                Interlocked.Increment(ref smuggled);
                return ctx.Response.WriteTextAsync("SMUGGLED");
            });
        });
        await using var _ = server;

        using var socket = await ConnectAsync(port);
        await SendAsync(socket, $"POST /a {version}\r\nHost: x\r\nTransfer-Encoding: {codings}\r\n\r\nGET /smuggled HTTP/1.1\r\nHost: x\r\n\r\n");
        var result = await ReadAllAsync(socket, TimeSpan.FromSeconds(3));

        Assert.StartsWith($"HTTP/1.1 {status}", result);
        Assert.DoesNotContain("SMUGGLED", result);
        Assert.Equal(1, CountResponses(result));
        Assert.Equal(0, smuggled);
    }

    [Fact]
    public async Task Transfer_encoding_with_content_length_is_refused_whatever_the_coding()
    {
        var (server, port) = await StartAsync(app => app.MapPost("/a", ctx => ctx.Response.WriteTextAsync("A")));
        await using var _ = server;

        using var socket = await ConnectAsync(port);
        await SendAsync(socket, "POST /a HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: gzip\r\nContent-Length: 3\r\n\r\nabc");
        var result = await ReadAllAsync(socket, TimeSpan.FromSeconds(3));

        Assert.StartsWith("HTTP/1.1 400", result);
    }

    // ---- 4. Response header injection ----

    [Fact]
    public async Task A_line_break_in_a_response_header_cannot_inject_headers()
    {
        var (server, port) = await StartAsync(app => app.MapGet("/r", ctx =>
        {
            ctx.Response.StatusCode = 302;
            ctx.Response.Headers.Set("Location", ctx.Request.Query["to"].ToString());
            return ctx.Response.WriteTextAsync("");
        }));
        await using var _ = server;

        using var socket = await ConnectAsync(port);
        await SendAsync(socket, "GET /r?to=/x%0d%0aSet-Cookie:%20pwned=1 HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n");
        var result = await ReadAllAsync(socket, TimeSpan.FromSeconds(3));

        Assert.StartsWith("HTTP/1.1 500", result);
        Assert.DoesNotContain("pwned", result);
    }

    [Fact]
    public async Task A_line_break_in_the_reason_phrase_cannot_inject_headers()
    {
        var (server, port) = await StartAsync(app => app.MapGet("/r", ctx =>
        {
            ctx.Response.ReasonPhrase = "OK\r\nSet-Cookie: pwned=1";
            return ctx.Response.WriteTextAsync("body");
        }));
        await using var _ = server;

        using var socket = await ConnectAsync(port);
        await SendAsync(socket, "GET /r HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n");
        var result = await ReadAllAsync(socket, TimeSpan.FromSeconds(3));

        Assert.StartsWith("HTTP/1.1 500", result);
        Assert.DoesNotContain("pwned", result);
    }

    [Theory]
    [InlineData("X-Ok", "a\rb")]
    [InlineData("X-Ok", "a\nb")]
    [InlineData("X-Ok", "a\0b")]
    [InlineData("Bad Name", "v")]
    [InlineData("Bad:Name", "v")]
    [InlineData("", "v")]
    public void Response_headers_refuse_names_and_values_that_would_break_the_head(string name, string value)
    {
        var headers = new HttpContext().Response.Headers;

        Assert.Throws<InvalidOperationException>(() => headers.Set(name, value));
        Assert.Throws<InvalidOperationException>(() => headers.Append(name, value));
        Assert.Throws<InvalidOperationException>(() => headers[name] = value);
        Assert.Throws<InvalidOperationException>(() => new HttpContext().Response.Trailers.Append(name, value));
    }

    [Fact]
    public void Request_headers_are_not_held_to_response_rules()
    {
        // HTTP/2 pseudo-headers and anything a parser has already vetted pass straight through.
        var headers = new HttpContext().Request.Headers;
        headers.Append(":authority", "h");
        Assert.Equal("h", headers.GetFirst(":authority"));
    }

    // ---- 5. Handler exceptions that look like disconnects ----

    [Theory]
    [InlineData("/fnf")]
    [InlineData("/tce")]
    [InlineData("/ode")]
    [InlineData("/io")]
    public async Task A_handler_exception_that_looks_like_a_disconnect_is_still_a_500(string path)
    {
        var (server, port) = await StartAsync(app =>
        {
            app.MapGet("/fnf", _ => throw new FileNotFoundException("missing.txt"));
            app.MapGet("/tce", async _ =>
            {
                await Task.Yield();
                throw new TaskCanceledException("upstream HttpClient timed out");
            });
            app.MapGet("/ode", _ => throw new ObjectDisposedException("svc"));
            app.MapGet("/io", _ => throw new IOException("disk full"));
        });
        await using var _ = server;

        using var socket = await ConnectAsync(port);
        await SendAsync(socket, $"GET {path} HTTP/1.1\r\nHost: x\r\n\r\n");
        var result = await ReadAllAsync(socket, TimeSpan.FromSeconds(3));

        Assert.StartsWith("HTTP/1.1 500", result);
    }

    // ---- 6. HEAD ----

    [Fact]
    public async Task Head_with_a_declared_length_and_no_body_keeps_the_connection()
    {
        var (server, port) = await StartAsync(app => app.MapGet("/f", async ctx =>
        {
            ctx.Response.ContentLength = 100;
            if (ctx.Request.Method != "HEAD")
                await ctx.Response.WriteBytesAsync(new byte[100]);
        }));
        await using var _ = server;

        using var socket = await ConnectAsync(port);
        await SendAsync(socket, "HEAD /f HTTP/1.1\r\nHost: x\r\n\r\nGET /f HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n");
        var result = await ReadAllAsync(socket, TimeSpan.FromSeconds(3));

        Assert.Equal(2, CountResponses(result));
        Assert.Contains("Content-Length: 100", result);
        AssertClosed(result);
        Assert.Equal(100, result.Length - result.LastIndexOf("\r\n\r\n", StringComparison.Ordinal) - 4 - result[result.LastIndexOf("<<", StringComparison.Ordinal)..].Length);
    }

    [Fact]
    public async Task An_error_response_to_head_has_no_body()
    {
        var (server, port) = await StartAsync(app => app.MapGet("/boom", _ => throw new InvalidOperationException("boom")));
        await using var _ = server;

        using var socket = await ConnectAsync(port);
        await SendAsync(socket, "HEAD /boom HTTP/1.1\r\nHost: x\r\n\r\n");
        var result = await ReadAllAsync(socket, TimeSpan.FromSeconds(3));

        Assert.StartsWith("HTTP/1.1 500", result);
        Assert.True(result.EndsWith("\r\n\r\n<<EOF>>") || result.EndsWith("\r\n\r\n<<RESET>>"), result);
    }

    // ---- 7. Bodies on 204 / 304 ----

    [Theory]
    [InlineData(204)]
    [InlineData(304)]
    public async Task A_body_written_to_a_bodiless_status_never_reaches_the_wire(int status)
    {
        var (server, port) = await StartAsync(app =>
        {
            app.MapGet("/nm", async ctx =>
            {
                ctx.Response.StatusCode = status;
                await ctx.Response.WriteTextAsync("HTTP/1.1 200 OK\r\nX-Injected: yes\r\nContent-Length: 0\r\n\r\n");
            });
            app.MapGet("/ok", ctx => ctx.Response.WriteTextAsync("second"));
        });
        await using var _ = server;

        using var socket = await ConnectAsync(port);
        await SendAsync(socket, "GET /nm HTTP/1.1\r\nHost: x\r\n\r\nGET /ok HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n");
        var result = await ReadAllAsync(socket, TimeSpan.FromSeconds(3));

        Assert.DoesNotContain("X-Injected", result);
        Assert.StartsWith("HTTP/1.1 500", result);
    }

    // ---- 8. Graceful shutdown ----

    [Fact]
    public async Task Stopping_lets_an_in_flight_request_finish_without_cancelling_it()
    {
        var sawAbort = true;
        var (server, port) = await StartAsync(app => app.MapGet("/slow", async ctx =>
        {
            await Task.Delay(1000);
            sawAbort = ctx.RequestAborted.IsCancellationRequested;
            await ctx.Response.WriteTextAsync("finished");
        }));

        using var socket = await ConnectAsync(port);
        await SendAsync(socket, "GET /slow HTTP/1.1\r\nHost: x\r\n\r\n");
        await Task.Delay(200);

        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var stop = server.StopAsync(budget.Token);
        var result = await ReadAllAsync(socket, TimeSpan.FromSeconds(5));
        await stop;

        Assert.StartsWith("HTTP/1.1 200", result);
        Assert.Contains("finished", result);
        Assert.Contains("Connection: close", result);
        AssertClosed(result);
        Assert.False(sawAbort);
        await server.DisposeAsync();
    }

    [Fact]
    public async Task Stopping_closes_idle_keep_alive_connections_at_once()
    {
        var (server, port) = await StartAsync(app => app.MapGet("/", ctx => ctx.Response.WriteTextAsync("hi")));

        using var socket = await ConnectAsync(port);
        await SendAsync(socket, "GET / HTTP/1.1\r\nHost: x\r\n\r\n");
        await Task.Delay(200);

        var clock = Stopwatch.StartNew();
        await server.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);
        var result = await ReadAllAsync(socket, TimeSpan.FromSeconds(3));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"Stop took {clock.Elapsed}");
        AssertClosed(result);
        await server.DisposeAsync();
    }

    [Fact]
    public async Task A_request_still_running_when_the_stop_runs_out_is_cancelled_and_its_connection_aborted()
    {
        var cancelled = new TaskCompletionSource();
        var (server, port) = await StartAsync(
            app => app.MapGet("/hang", async ctx =>
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, ctx.RequestAborted);
                }
                catch (OperationCanceledException)
                {
                    cancelled.TrySetResult();
                    throw;
                }
            }),
            o => o.ShutdownTimeout = TimeSpan.FromMilliseconds(300)
        );

        using var socket = await ConnectAsync(port);
        await SendAsync(socket, "GET /hang HTTP/1.1\r\nHost: x\r\n\r\n");
        await Task.Delay(200);

        var clock = Stopwatch.StartNew();
        await server.StopAsync();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"Stop took {clock.Elapsed}");
        Assert.Equal(HttpServerState.Stopped, server.State);
        Assert.Equal(0, server.ActiveConnections);
        Assert.True(cancelled.Task.IsCompleted);

        var result = await ReadAllAsync(socket, TimeSpan.FromSeconds(3));
        Assert.DoesNotContain("<<OPEN>>", result);
        await server.DisposeAsync();
    }

    [Fact]
    public async Task Disposing_with_a_connection_that_ignores_cancellation_does_not_break_its_slot()
    {
        var finished = new TaskCompletionSource();
        var unobserved = 0;
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e) => Interlocked.Increment(ref unobserved);
        TaskScheduler.UnobservedTaskException += OnUnobserved;

        try
        {
            var (server, port) = await StartAsync(
                app => app.MapGet("/stubborn", async _ =>
                {
                    try
                    {
                        await Task.Delay(2500, CancellationToken.None);
                    }
                    finally
                    {
                        finished.TrySetResult();
                    }
                }),
                o => o.ShutdownTimeout = TimeSpan.FromMilliseconds(100)
            );

            using var socket = await ConnectAsync(port);
            await SendAsync(socket, "GET /stubborn HTTP/1.1\r\nHost: x\r\n\r\n");
            await Task.Delay(200);

            await server.DisposeAsync();
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(200);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            Assert.Equal(0, unobserved);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    // ---- 9. RequestAborted ----

    [Fact]
    public async Task RequestAborted_fires_when_the_client_hangs_up()
    {
        var fired = new TaskCompletionSource();
        var (server, port) = await StartAsync(app => app.MapGet("/wait", async ctx =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, ctx.RequestAborted);
            }
            catch (OperationCanceledException)
            {
                fired.TrySetResult();
            }
        }));
        await using var _ = server;

        var socket = await ConnectAsync(port);
        await SendAsync(socket, "GET /wait HTTP/1.1\r\nHost: x\r\n\r\n");
        await Task.Delay(200);
        socket.Close();

        await fired.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RequestAborted_fires_when_the_client_hangs_up_after_sending_its_body()
    {
        var fired = new TaskCompletionSource();
        var (server, port) = await StartAsync(app => app.MapPost("/wait", async ctx =>
        {
            using var body = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(body);
            try
            {
                await Task.Delay(Timeout.Infinite, ctx.RequestAborted);
            }
            catch (OperationCanceledException)
            {
                fired.TrySetResult();
            }
        }));
        await using var _ = server;

        var socket = await ConnectAsync(port);
        await SendAsync(socket, "POST /wait HTTP/1.1\r\nHost: x\r\nContent-Length: 5\r\n\r\nhello");
        await Task.Delay(200);
        socket.Close();

        await fired.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Pipelined_requests_still_work_with_the_disconnect_watch()
    {
        var (server, port) = await StartAsync(app => app.MapGet("/n/{id}", async ctx =>
        {
            await Task.Delay(50);
            await ctx.Response.WriteTextAsync("id=" + ctx.Request.Path);
        }));
        await using var _ = server;

        using var socket = await ConnectAsync(port);
        await SendAsync(socket, "GET /n/1 HTTP/1.1\r\nHost: x\r\n\r\nGET /n/2 HTTP/1.1\r\nHost: x\r\n\r\nGET /n/3 HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n");
        var result = await ReadAllAsync(socket, TimeSpan.FromSeconds(3));

        Assert.Equal(3, CountResponses(result));
        Assert.True(result.IndexOf("id=/n/1", StringComparison.Ordinal) < result.IndexOf("id=/n/2", StringComparison.Ordinal));
        Assert.True(result.IndexOf("id=/n/2", StringComparison.Ordinal) < result.IndexOf("id=/n/3", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Abort_cancels_RequestAborted()
    {
        var wasCancelled = false;
        var (server, port) = await StartAsync(app => app.MapGet("/abort", ctx =>
        {
            ctx.Abort();
            wasCancelled = ctx.RequestAborted.IsCancellationRequested;
            return ValueTask.CompletedTask;
        }));
        await using var _ = server;

        using var socket = await ConnectAsync(port);
        await SendAsync(socket, "GET /abort HTTP/1.1\r\nHost: x\r\n\r\n");
        await ReadAllAsync(socket, TimeSpan.FromSeconds(3));

        Assert.True(wasCancelled);
    }

    // ---- 10. Body, drain and write timeouts ----

    [Fact]
    public async Task A_stalled_request_body_times_out_with_a_408()
    {
        var (server, port) = await StartAsync(
            app => app.MapPost("/up", async ctx =>
            {
                using var body = new MemoryStream();
                await ctx.Request.Body.CopyToAsync(body);
                await ctx.Response.WriteTextAsync("got " + body.Length);
            }),
            o => o.Limits.RequestBodyTimeout = TimeSpan.FromSeconds(1)
        );
        await using var _ = server;

        using var socket = await ConnectAsync(port);
        await SendAsync(socket, "POST /up HTTP/1.1\r\nHost: x\r\nContent-Length: 4\r\n\r\nz");
        var result = await ReadAllAsync(socket, TimeSpan.FromSeconds(6));

        Assert.StartsWith("HTTP/1.1 408", result);
        AssertClosed(result);
    }

    [Fact]
    public async Task A_stalled_unread_body_does_not_hold_the_connection_after_the_response()
    {
        var (server, port) = await StartAsync(
            app => app.MapPost("/ignore", ctx => ctx.Response.WriteTextAsync("ignored")),
            o => o.Limits.RequestBodyTimeout = TimeSpan.FromSeconds(1)
        );
        await using var _ = server;

        using var socket = await ConnectAsync(port);
        await SendAsync(socket, "POST /ignore HTTP/1.1\r\nHost: x\r\nContent-Length: 100\r\n\r\n0123456789");
        var result = await ReadAllAsync(socket, TimeSpan.FromSeconds(6));

        Assert.StartsWith("HTTP/1.1 200", result);
        AssertClosed(result);
    }

    [Fact]
    public async Task A_client_that_stops_reading_fails_the_handlers_write()
    {
        var failed = new TaskCompletionSource<Exception>();
        var (server, port) = await StartAsync(
            app => app.MapGet("/flood", async ctx =>
            {
                var chunk = new byte[256 * 1024];
                try
                {
                    for (var i = 0; i < 4096; i++)
                        await ctx.Response.Body.WriteAsync(chunk);

                    failed.TrySetException(new Exception("Every write succeeded"));
                }
                catch (Exception ex)
                {
                    failed.TrySetResult(ex);
                    throw;
                }
            }),
            o => o.Limits.ResponseWriteTimeout = TimeSpan.FromSeconds(1)
        );
        await using var _ = server;

        using var socket = await ConnectAsync(port);
        socket.ReceiveBufferSize = 4096;
        await SendAsync(socket, "GET /flood HTTP/1.1\r\nHost: x\r\n\r\n");

        var ex = await failed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(ex is IOException, ex.ToString());
        await Wait.UntilAsync(() => server.ActiveConnections == 0, CancellationToken.None);
        Assert.Equal(0, server.ActiveConnections);
    }

    // ---- 12. Content-Length ----

    [Theory]
    [InlineData("+3")]
    [InlineData("-3")]
    [InlineData(" 3x")]
    [InlineData("3 3")]
    [InlineData("0x3")]
    public async Task A_content_length_that_is_not_plain_digits_is_refused(string length)
    {
        var (server, port) = await StartAsync(app => app.MapPost("/echo", async ctx =>
        {
            using var body = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(body);
            await ctx.Response.WriteTextAsync("body=" + Encoding.ASCII.GetString(body.ToArray()));
        }));
        await using var _ = server;

        using var socket = await ConnectAsync(port);
        await SendAsync(socket, $"POST /echo HTTP/1.1\r\nHost: x\r\nContent-Length: {length}\r\nConnection: close\r\n\r\nabc");
        var result = await ReadAllAsync(socket, TimeSpan.FromSeconds(3));

        Assert.StartsWith("HTTP/1.1 400", result);
    }

    [Fact]
    public void Content_length_parses_only_digits()
    {
        var headers = new HttpContext().Request.Headers;

        headers.Set(HeaderNames.ContentLength, "+3");
        Assert.Null(headers.ContentLength);

        headers.Set(HeaderNames.ContentLength, "99999999999999999999");
        Assert.Null(headers.ContentLength);

        headers.Set(HeaderNames.ContentLength, "42");
        Assert.Equal(42, headers.ContentLength);
    }

    // ---- 13. Accept loop ----

    [Fact]
    public async Task The_listener_surfaces_a_listener_level_accept_failure_instead_of_spinning()
    {
        var options = new HttpServerOptions { Port = 0, Address = IPAddress.Loopback };
        var endpoint = new HttpServerEndpoint(IPAddress.Loopback, 0);
        await using var listener = new SocketConnectionListener(
            options,
            endpoint,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SocketConnectionListener>.Instance,
            0
        );
        await listener.BindAsync();

        // A bound socket that is not listening fails every accept the same way, as descriptor
        // exhaustion does: retrying it on the spot cannot help.
        using var broken = new Socket(SocketType.Stream, ProtocolType.Tcp);
        broken.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var field = typeof(SocketConnectionListener).GetField("listenSocket", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var original = (Socket)field.GetValue(listener)!;
        field.SetValue(listener, broken);
        original.Dispose();

        var accept = listener.AcceptAsync().AsTask();
        var completed = await Task.WhenAny(accept, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Same(accept, completed);
        await Assert.ThrowsAnyAsync<Exception>(() => accept);
    }

    // ---- 14. Absolute-form and Host ----

    [Fact]
    public async Task An_absolute_form_authority_overrides_the_host_header()
    {
        var (server, port) = await StartAsync(app => app.MapGet("/p", ctx => ctx.Response.WriteTextAsync("host=" + ctx.Request.Host + " path=" + ctx.Request.Path)));
        await using var _ = server;

        using var socket = await ConnectAsync(port);
        await SendAsync(socket, "GET http://example.com:8080/p HTTP/1.1\r\nHost: other.test\r\nConnection: close\r\n\r\n");
        var result = await ReadAllAsync(socket, TimeSpan.FromSeconds(3));

        Assert.Contains("host=example.com:8080 path=/p", result);
    }

    [Theory]
    [InlineData("GET /x?u=http://h/p HTTP/1.1\r\nHost: x\r\n", "/x")]
    [InlineData("GET http://h?q=1 HTTP/1.1\r\nHost: x\r\n", "/")]
    [InlineData("GET HTTP://H/a HTTP/1.1\r\nHost: x\r\n", "/a")]
    public void Only_a_leading_scheme_makes_a_target_absolute(string head, string path)
    {
        var context = new HttpContext();
        var parser = new Http1RequestParser();
        var sequence = new System.Buffers.ReadOnlySequence<byte>(Encoding.ASCII.GetBytes(head + "\r\n"));
        var reader = new System.Buffers.SequenceReader<byte>(sequence);

        Assert.True(parser.TryParseRequestHead(ref reader, context.Request, new HttpServerLimits()));
        Assert.Equal(path, context.Request.Path);
    }

    [Theory]
    [InlineData("GET http:///p HTTP/1.1\r\nHost: x\r\n\r\n")]
    [InlineData("GET http://user@evil/p HTTP/1.1\r\nHost: x\r\n\r\n")]
    [InlineData("GET ftp://h/p HTTP/1.1\r\nHost: x\r\n\r\n")]
    [InlineData("GET / HTTP/1.1\r\nHost: a b\r\n\r\n")]
    [InlineData("GET / HTTP/1.1\r\nHost: a/b\r\n\r\n")]
    [InlineData("GET / HTTP/1.1\r\nHost: h:80x\r\n\r\n")]
    [InlineData("GET / HTTP/1.1\r\nHost: [::1\r\n\r\n")]
    public async Task A_malformed_host_or_authority_is_refused(string request)
    {
        var (server, port) = await StartAsync(app => app.MapGet("/", ctx => ctx.Response.WriteTextAsync("ok")));
        await using var _ = server;

        using var socket = await ConnectAsync(port);
        await SendAsync(socket, request);
        var result = await ReadAllAsync(socket, TimeSpan.FromSeconds(3));

        Assert.StartsWith("HTTP/1.1 400", result);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("localhost:5000")]
    [InlineData("[::1]:5000")]
    [InlineData("xn--bcher-kva.example")]
    [InlineData("")]
    public void Valid_hosts_pass(string host) => Assert.True(HostValidator.IsValid(host));

    // ---- 15. Connection tokens ----

    [Fact]
    public async Task Close_anywhere_in_the_connection_header_wins()
    {
        var (server, port) = await StartAsync(app => app.MapGet("/", ctx => ctx.Response.WriteTextAsync("hi")));
        await using var _ = server;

        using var socket = await ConnectAsync(port);
        await SendAsync(socket, "GET / HTTP/1.1\r\nHost: x\r\nConnection: keep-alive, close\r\n\r\n");
        var result = await ReadAllAsync(socket, TimeSpan.FromSeconds(3));

        Assert.Contains("Connection: close", result);
        AssertClosed(result);
    }

    // ---- 16. Repeated headers ----

    [Fact]
    public void Appending_a_repeated_header_is_not_quadratic()
    {
        var headers = new HeaderDictionary();
        var clock = Stopwatch.StartNew();

        for (var i = 0; i < 100_000; i++)
            headers.Append("X-Repeat", "v");

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"100k appends took {clock.Elapsed}");
        Assert.Equal(100_000, headers["X-Repeat"].Count);
    }

    [Fact]
    public void Appended_values_keep_their_order_through_reads_and_replacement()
    {
        var headers = new HeaderDictionary();
        headers.Append("X-A", "1");
        headers.Append("x-a", "2");
        Assert.Equal(["1", "2"], headers["X-A"].ToArray());

        headers.Append("X-A", "3");
        Assert.Equal("1,2,3", headers.GetFirst("X-A"));
        Assert.Equal(1, headers.Count);

        headers.Set("X-A", "fresh");
        headers.Append("X-A", "4");
        Assert.Equal(["fresh", "4"], headers["X-A"].ToArray());

        headers.Remove("X-A");
        Assert.False(headers.ContainsKey("X-A"));
        headers.Append("X-A", "5");
        Assert.Equal(["5"], headers["X-A"].ToArray());

        var seen = headers.ToDictionary(x => x.Key, x => x.Value.ToString());
        Assert.Equal("5", seen["X-A"]);
    }
}
