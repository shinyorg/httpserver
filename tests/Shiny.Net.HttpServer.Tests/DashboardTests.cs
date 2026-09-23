using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Shiny.Net.HttpServer.CommandLine;
using Shiny.Net.HttpServer.CommandLine.Monitoring;
using Shiny.Net.HttpServer.Transports;

namespace Shiny.Net.HttpServer.Tests;

/// <summary>
/// What the dashboard shows is only as good as what the monitor saw. These run it in front of real
/// traffic - over a socket on both protocol versions, and through an in-memory tunnel connection -
/// because a byte counter that is right in isolation and wrong on the wire is the failure that matters.
/// </summary>
public class TrafficMonitorTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    static HttpClient ClientFor(TestServer server, bool http2) => http2
        ? new HttpClient(new SocketsHttpHandler())
        {
            BaseAddress = new Uri($"http://127.0.0.1:{server.Port}"),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        }
        : server.Client;

    /// <summary>The log is written after the response is on the wire, so a test reads it once it lands.</summary>
    static async Task<TrafficEntry> WaitForCompletedAsync(TrafficMonitor monitor, int count = 1)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (monitor.GetCompleted().Count < count)
        {
            Assert.True(DateTime.UtcNow < deadline, "the request never reached the log");
            await Task.Delay(10, Token);
        }
        return monitor.GetCompleted()[0];
    }


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Records_a_finished_download_with_what_it_sent(bool http2)
    {
        var monitor = new TrafficMonitor();
        await using var server = await TestServer.StartAsync(app =>
        {
            app.Use(monitor);
            app.MapGet("/file", ctx =>
            {
                ctx.Response.ContentType = "application/octet-stream";
                return ctx.Response.WriteBytesAsync(new byte[10_000]);
            });
        });

        var client = ClientFor(server, http2);
        var response = await client.GetAsync("/file?x=1", Token);
        Assert.Equal(10_000, (await response.Content.ReadAsByteArrayAsync(Token)).Length);

        var entry = await WaitForCompletedAsync(monitor);

        Assert.Equal("GET", entry.Method);
        Assert.Equal("/file?x=1", entry.Target);
        Assert.Equal(200, entry.StatusCode);
        Assert.Equal(10_000, entry.BytesOut);
        Assert.Equal(10_000, entry.ResponseLength);
        Assert.True(entry.IsDownload);
        Assert.False(entry.IsUpload);
        Assert.Equal(http2 ? "HTTP/2" : "HTTP/1.1", entry.Protocol);
        Assert.Contains(entry.ResponseHeaders, x => x.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(1, monitor.TotalRequests);
        Assert.Equal(10_000, monitor.TotalBytesOut);
        Assert.Equal(0, monitor.ActiveCount);
    }


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Counts_an_upload_as_the_handler_reads_it(bool http2)
    {
        var monitor = new TrafficMonitor();
        await using var server = await TestServer.StartAsync(app =>
        {
            app.Use(monitor);
            app.MapPut("/up", async ctx =>
            {
                var read = 0L;
                var buffer = new byte[4096];
                int n;
                while ((n = await ctx.Request.Body.ReadAsync(buffer, ctx.RequestAborted)) > 0)
                    read += n;

                ctx.Response.StatusCode = 201;
                await ctx.Response.WriteAsync(read.ToString());
            });
        });

        var client = ClientFor(server, http2);
        var response = await client.PutAsync("/up", new ByteArrayContent(new byte[50_000]), Token);
        Assert.Equal("50000", await response.Content.ReadAsStringAsync(Token));

        var entry = await WaitForCompletedAsync(monitor);

        Assert.Equal(201, entry.StatusCode);
        Assert.Equal(50_000, entry.BytesIn);
        Assert.Equal(50_000, entry.RequestLength);
        Assert.True(entry.IsUpload);
        Assert.Equal(1d, entry.Progress);
        Assert.Equal(50_000, monitor.TotalBytesIn);
    }


    [Fact]
    public async Task Shows_a_download_while_it_is_still_running()
    {
        var monitor = new TrafficMonitor();
        var release = new TaskCompletionSource();

        await using var server = await TestServer.StartAsync(app =>
        {
            app.Use(monitor);
            app.MapGet("/slow", async ctx =>
            {
                ctx.Response.ContentType = "application/octet-stream";
                ctx.Response.ContentLength = 5_000;
                await ctx.Response.Body.WriteAsync(new byte[1_000], ctx.RequestAborted);
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);

                await release.Task.WaitAsync(ctx.RequestAborted);
                await ctx.Response.Body.WriteAsync(new byte[4_000], ctx.RequestAborted);
            });
        });

        var request = server.Client.GetAsync("/slow", HttpCompletionOption.ResponseHeadersRead, Token);

        TrafficEntry? running = null;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (running is not { BytesOut: 1_000 })
        {
            Assert.True(DateTime.UtcNow < deadline, "the download never showed as in flight");
            running = monitor.GetActive().SingleOrDefault();
            await Task.Delay(10, Token);
        }

        Assert.False(running.IsComplete);
        Assert.True(running.IsDownload);
        Assert.Equal(5_000, running.ExpectedBytes);
        Assert.Equal(0.2, running.Progress!.Value, 3);
        Assert.Empty(monitor.GetCompleted());

        release.SetResult();
        using var response = await request;
        await response.Content.ReadAsByteArrayAsync(Token);

        var finished = await WaitForCompletedAsync(monitor);
        Assert.Same(running, finished);
        Assert.Equal(5_000, finished.BytesOut);
        Assert.Empty(monitor.GetActive());
    }


    [Fact]
    public async Task Records_a_handler_that_threw_as_the_500_the_client_got()
    {
        var monitor = new TrafficMonitor();
        await using var server = await TestServer.StartAsync(app =>
        {
            app.Use(monitor);
            app.MapGet("/boom", _ => throw new InvalidOperationException("broken"));
        });

        var response = await server.Client.GetAsync("/boom", Token);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        var entry = await WaitForCompletedAsync(monitor);
        Assert.Equal(500, entry.StatusCode);
        Assert.Equal("broken", entry.Error);
    }


    [Fact]
    public async Task Keeps_the_newest_entries_up_to_its_capacity()
    {
        var monitor = new TrafficMonitor(capacity: 3);
        await using var server = await TestServer.StartAsync(app =>
        {
            app.Use(monitor);
            app.MapGet("/{n}", ctx => ctx.Response.WriteAsync("ok"));
        });

        for (var i = 1; i <= 5; i++)
            await server.Client.GetStringAsync($"/{i}", Token);

        await WaitForCompletedAsync(monitor, 3);
        while (monitor.TotalRequests < 5)
            await Task.Delay(10, Token);

        Assert.Equal(["/5", "/4", "/3"], monitor.GetCompleted().Select(x => x.Target));
        Assert.Equal(5, monitor.TotalRequests);
    }


    [Fact]
    public async Task Clear_empties_the_log_and_the_totals()
    {
        var monitor = new TrafficMonitor();
        await using var server = await TestServer.StartAsync(app =>
        {
            app.Use(monitor);
            app.MapGet("/", ctx => ctx.Response.WriteAsync("ok"));
        });

        await server.Client.GetStringAsync("/", Token);
        await WaitForCompletedAsync(monitor);
        var before = monitor.Version;

        monitor.Clear();

        Assert.Empty(monitor.GetCompleted());
        Assert.Equal(0, monitor.TotalRequests);
        Assert.Equal(0, monitor.TotalBytesOut);
        Assert.NotEqual(before, monitor.Version);
    }


    [Fact]
    public async Task Reads_the_client_behind_the_tunnel_from_the_last_forwarding_hop()
    {
        var monitor = new TrafficMonitor();
        var server = new HttpServer(new HttpServerOptions { Port = 0 });
        server.Use(monitor);
        server.MapGet("/ping", ctx => ctx.Response.WriteAsync("pong"));

        await using (server)
        {
            var connection = new DuplexPipeConnection("tunnel");
            var serving = server.ServeAsync(connection, Token);

            // The first hop is whatever the client wrote; the tunnel appends the one it saw.
            await connection.TransportWriter.WriteAsync(
                "GET /ping HTTP/1.1\r\nHost: localhost\r\nX-Forwarded-For: 6.6.6.6, 203.0.113.9\r\nConnection: close\r\n\r\n"u8.ToArray(),
                Token
            );

            var entry = await WaitForCompletedAsync(monitor);
            Assert.True(entry.IsTunneled);
            Assert.Equal("203.0.113.9", entry.RemoteAddress);

            await connection.TransportWriter.CompleteAsync();
            await serving.WaitAsync(TimeSpan.FromSeconds(10), Token);
        }
    }


    [Fact]
    public async Task Believes_the_socket_over_a_forwarding_header_on_a_direct_connection()
    {
        var monitor = new TrafficMonitor();
        await using var server = await TestServer.StartAsync(app =>
        {
            app.Use(monitor);
            app.MapGet("/", ctx => ctx.Response.WriteAsync("ok"));
        });

        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("X-Forwarded-For", "6.6.6.6");
        await server.Client.SendAsync(request, Token);

        var entry = await WaitForCompletedAsync(monitor);
        Assert.False(entry.IsTunneled);
        Assert.Equal("127.0.0.1", entry.RemoteAddress);
    }
}


/// <summary>
/// Applying settings to a running server: what rebuilds it, what does not, and what happens when the
/// new settings will not start.
/// </summary>
public class ServerSessionTests : IDisposable
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    readonly string root = Directory.CreateTempSubdirectory("shinyhttpserver-session-").FullName;

    public void Dispose() => Directory.Delete(this.root, true);


    ServeSettings Settings(int port, params BasicUser[] users) => new()
    {
        RootPath = this.root,
        Address = IPAddress.Loopback,
        Port = port,
        UrlPrefix = "/",
        Permissions = Permissions.Read,
        Users = users,
        Realm = "test",
        AuthChangesOnly = false,
        AllowInsecureAuth = false,
        UseHttps = false,
        UseTunnel = false,
        ServeHidden = false,
        MaxUploadBytes = SettingParsers.DefaultMaxUpload,
        Verbose = false
    };

    static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    static async Task<HttpStatusCode?> GetAsync(int port, AuthenticationHeaderValue? auth = null)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/");
        request.Headers.Authorization = auth;
        try
        {
            using var response = await client.SendAsync(request, Token);
            return response.StatusCode;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    static ServerSession NewSession(TrafficMonitor? monitor = null) => new(monitor ?? new TrafficMonitor(), _ => { });


    [Fact]
    public async Task Starts_serving_the_directory()
    {
        await using var session = NewSession();
        var port = FreePort();

        Assert.Null(await session.ApplyAsync(this.Settings(port), Token));

        Assert.Equal(SessionState.Running, session.State);
        Assert.True(session.LastApplyRebuilt);
        Assert.Equal(HttpStatusCode.OK, await GetAsync(port));
    }


    [Fact]
    public async Task Leaves_the_running_server_alone_when_only_the_log_level_changes()
    {
        await using var session = NewSession();
        var port = FreePort();
        await session.ApplyAsync(this.Settings(port), Token);

        Assert.Null(await session.ApplyAsync(this.Settings(port) with { Verbose = true, ShowQr = false }, Token));

        Assert.False(session.LastApplyRebuilt);
        Assert.True(session.Settings!.Verbose);
        Assert.Equal(HttpStatusCode.OK, await GetAsync(port));
    }


    [Fact]
    public async Task Moves_to_a_new_port_and_lets_the_old_one_go()
    {
        await using var session = NewSession();
        var first = FreePort();
        await session.ApplyAsync(this.Settings(first), Token);

        var second = FreePort();
        Assert.Null(await session.ApplyAsync(this.Settings(second), Token));

        Assert.True(session.LastApplyRebuilt);
        Assert.Equal(HttpStatusCode.OK, await GetAsync(second));
        Assert.Null(await GetAsync(first));
    }


    [Fact]
    public async Task Puts_a_login_in_front_of_the_directory_when_a_user_is_added()
    {
        var monitor = new TrafficMonitor();
        await using var session = NewSession(monitor);
        var port = FreePort();
        await session.ApplyAsync(this.Settings(port), Token);

        // loopback, so plain HTTP is allowed to carry the password
        Assert.Null(await session.ApplyAsync(this.Settings(port, new BasicUser("amy", "secret")), Token));

        Assert.Equal(HttpStatusCode.Unauthorized, await GetAsync(port));
        Assert.Equal(
            HttpStatusCode.OK,
            await GetAsync(port, new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("amy:secret"))))
        );

        // the refusal is logged too: the monitor sits ahead of authentication
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (monitor.GetCompleted().Count < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(10, Token);

        var log = monitor.GetCompleted();
        Assert.Equal(200, log[0].StatusCode);
        Assert.Equal("amy", log[0].User);
        Assert.Equal(401, log[1].StatusCode);
    }


    [Fact]
    public async Task Keeps_the_previous_settings_running_when_the_new_ones_cannot_listen()
    {
        await using var session = NewSession();
        var port = FreePort();
        await session.ApplyAsync(this.Settings(port), Token);

        using var squatter = new TcpListener(IPAddress.Loopback, 0);
        squatter.Start();
        var taken = ((IPEndPoint)squatter.LocalEndpoint).Port;

        var error = await session.ApplyAsync(this.Settings(taken), Token);

        Assert.NotNull(error);
        Assert.Contains("previous settings are still running", error);
        Assert.Equal(SessionState.Running, session.State);
        Assert.Equal(port, session.Settings!.Port);
        Assert.Equal(HttpStatusCode.OK, await GetAsync(port));
    }


    [Fact]
    public async Task Refuses_settings_that_do_not_validate_without_touching_the_server()
    {
        await using var session = NewSession();
        var port = FreePort();
        await session.ApplyAsync(this.Settings(port), Token);

        var error = await session.ApplyAsync(this.Settings(port) with { RootPath = Path.Combine(this.root, "missing") }, Token);

        Assert.Contains("is not a directory", error);
        Assert.Equal(this.root, session.Settings!.RootPath);
        Assert.Equal(HttpStatusCode.OK, await GetAsync(port));
    }
}


/// <summary>The settings form reads back what it shows, and knows which changes cost a restart.</summary>
public class SettingsFormTests
{
    static ServeSettings Sample() => new()
    {
        RootPath = "/tmp",
        Address = IPAddress.Any,
        Port = 8080,
        UrlPrefix = "/",
        Permissions = Permissions.Read,
        Users = [new BasicUser("amy", "secret")],
        Realm = "r",
        AuthChangesOnly = false,
        AllowInsecureAuth = false,
        UseHttps = false,
        UseTunnel = false,
        ServeHidden = false,
        MaxUploadBytes = SettingParsers.DefaultMaxUpload,
        Verbose = false
    };


    [Theory]
    [InlineData(64L * 1024 * 1024, "64mb")]
    [InlineData(2L * 1024 * 1024 * 1024, "2gb")]
    [InlineData(500L * 1024, "500kb")]
    [InlineData(1234L, "1234")]
    public void Writes_a_size_the_parser_reads_back(long bytes, string expected)
    {
        var text = SettingParsers.FormatSize(bytes);

        Assert.Equal(expected, text);
        Assert.True(SettingParsers.TryParseSize(text, out var parsed, out _));
        Assert.Equal(bytes, parsed);
    }


    [Theory]
    [InlineData("0.0.0.0", "any")]
    [InlineData("127.0.0.1", "localhost")]
    [InlineData("192.168.1.20", "192.168.1.20")]
    public void Writes_an_address_by_the_name_the_parser_takes(string address, string expected)
    {
        var text = SettingParsers.FormatAddress(IPAddress.Parse(address));

        Assert.Equal(expected, text);
        Assert.True(SettingParsers.TryParseAddress(text, out var parsed, out _));
        Assert.Equal(IPAddress.Parse(address), parsed);
    }


    [Fact]
    public void Opening_the_tunnel_does_not_need_a_new_server()
        => Assert.False((Sample() with { UseTunnel = true, TunnelToken = "t" }).NeedsRebuildFrom(Sample()));


    [Fact]
    public void The_same_users_typed_again_do_not_need_a_new_server()
        => Assert.False((Sample() with { Users = new List<BasicUser> { new("amy", "secret") } }).NeedsRebuildFrom(Sample()));


    [Theory]
    [InlineData("port")]
    [InlineData("https")]
    [InlineData("users")]
    [InlineData("permissions")]
    public void Anything_in_the_pipeline_needs_a_new_server(string change)
    {
        var next = change switch
        {
            "port" => Sample() with { Port = 9090 },
            "https" => Sample() with { UseHttps = true },
            "users" => Sample() with { Users = [new BasicUser("amy", "other")] },
            _ => Sample() with { Permissions = Permissions.Read | Permissions.Create }
        };

        Assert.True(next.NeedsRebuildFrom(Sample()));
    }
}
