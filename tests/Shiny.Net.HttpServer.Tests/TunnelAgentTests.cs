using System.Runtime.InteropServices;
using Shiny.Net.HttpServer.Tunnels;

namespace Shiny.Net.HttpServer.Tests;

/// <summary>
/// The agents are supervised vendor binaries, so what is worth testing is everything around the
/// binary: the command line built for it, the line of output that carries the URL, and what happens
/// when it is missing or never says anything. A stub script stands in for the real agent — the
/// alternative is a test that only runs on a machine with cloudflared installed and a working
/// internet connection, which is a test nobody runs.
/// </summary>
public class TunnelAgentTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void Cloudflare_builds_a_quick_tunnel_command_line()
    {
        var arguments = new StubbedCloudflare(new CloudflareTunnelOptions { Port = 8080 }).Arguments();

        Assert.Equal(["--no-autoupdate", "tunnel", "--url", "http://127.0.0.1:8080"], arguments);
    }

    [Fact]
    public void Cloudflare_builds_a_named_tunnel_command_line()
    {
        var arguments = new StubbedCloudflare(new CloudflareTunnelOptions
        {
            Port = 8080,
            Token = "a-token",
            Hostname = "device.example.com"
        }).Arguments();

        Assert.Contains("run", arguments);
        Assert.Contains("--token", arguments);
        Assert.Contains("a-token", arguments);
        Assert.Contains("device.example.com", arguments);
    }

    [Fact]
    public void Cloudflare_reads_the_quick_tunnel_url_out_of_its_banner()
    {
        var agent = new StubbedCloudflare(new CloudflareTunnelOptions { Port = 1 });

        Assert.Equal(
            "https://raw-fresh-mint-42.trycloudflare.com",
            agent.Parse("2026-08-23T10:00:00Z INF |  https://raw-fresh-mint-42.trycloudflare.com   |")
        );
        Assert.Null(agent.Parse("2026-08-23T10:00:00Z INF Requesting new quick Tunnel on trycloudflare.com..."));
    }

    [Fact]
    public void Ngrok_asks_for_parseable_logs_and_reads_the_url_from_them()
    {
        var agent = new StubbedNgrok(new NgrokTunnelOptions { Port = 5000, Domain = "device.ngrok.app" });
        var arguments = agent.Arguments();

        Assert.Equal(["http", "127.0.0.1:5000", "--log", "stdout", "--log-format", "logfmt", "--domain", "device.ngrok.app"], arguments);

        Assert.Equal(
            "https://device.ngrok.app",
            agent.Parse("t=2026-08-23T10:00:00+0000 lvl=info msg=\"started tunnel\" obj=tunnels name=command_line addr=http://127.0.0.1:5000 url=https://device.ngrok.app")
        );

        // Every other log line, of which there are many, must not be mistaken for the answer.
        Assert.Null(agent.Parse("t=2026-08-23T10:00:00+0000 lvl=info msg=\"client session established\""));
    }

    [Fact]
    public void Tailscale_serves_to_the_tailnet_when_told_to()
    {
        var funnel = new StubbedTailscale(new TailscaleFunnelOptions { Port = 3000 });
        var serve = new StubbedTailscale(new TailscaleFunnelOptions { Port = 3000, TailnetOnly = true });

        Assert.Equal("funnel", funnel.Arguments()[0]);
        Assert.Equal("serve", serve.Arguments()[0]);

        Assert.Equal(
            "https://laptop.tail1234.ts.net",
            funnel.Parse("Available on the internet:\nhttps://laptop.tail1234.ts.net/")
        );
    }

    [Fact]
    public async Task A_missing_binary_says_what_to_install()
    {
        await using var agent = new CloudflareTunnel(new CloudflareTunnelOptions
        {
            Port = 8080,
            ExecutablePath = "/nonexistent/cloudflared-that-is-not-here"
        });

        var error = await Assert.ThrowsAsync<TunnelAgentException>(() => agent.StartAsync(Token));

        Assert.Equal("cloudflared", error.Agent);
        Assert.Contains("not found", error.Message);
        Assert.Contains("PATH", error.Message);
    }

    [Fact]
    public async Task An_agent_with_no_port_refuses_to_start()
    {
        await using var agent = new NgrokTunnel(new NgrokTunnelOptions());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => agent.StartAsync(Token));

        Assert.Contains("no port to publish", error.Message);
    }

    [Fact]
    public async Task The_port_comes_off_a_running_server()
    {
        await using var test = await TestServer.StartAsync(server => server.MapGet("/", ctx =>
            ctx.Response.WriteTextAsync("hi", cancellationToken: ctx.RequestAborted)));

        var options = new CloudflareTunnelOptions { ExecutablePath = "/nonexistent/cloudflared" };
        await using var agent = new CloudflareTunnel(options);

        // Fails on the missing binary, having already read the port off the server.
        await Assert.ThrowsAsync<TunnelAgentException>(() => agent.StartAsync(test.Server, Token));

        Assert.Equal(test.Port, options.Port);
    }

    [Fact]
    public async Task A_stub_agent_reports_its_url_and_is_killed_on_dispose()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        using var script = StubScript("""
            #!/bin/sh
            echo "INF Requesting new quick Tunnel on trycloudflare.com..."
            echo "INF |  https://stub-tunnel-1.trycloudflare.com  |"
            while true; do sleep 1; done
            """);

        var agent = new CloudflareTunnel(new CloudflareTunnelOptions
        {
            Port = 8080,
            ExecutablePath = script.Path,
            StartTimeout = TimeSpan.FromSeconds(10)
        });

        var url = await agent.StartAsync(Token);

        Assert.Equal("https://stub-tunnel-1.trycloudflare.com", url);
        Assert.True(agent.IsRunning);

        await agent.DisposeAsync();

        Assert.False(agent.IsRunning);
        Assert.Null(agent.PublicUrl);
    }

    /// <summary>An agent that dies during startup must fail immediately, not sit out the timeout.</summary>
    [Fact]
    public async Task An_agent_that_exits_early_fails_the_start()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        using var script = StubScript("""
            #!/bin/sh
            echo "ERR failed to authenticate"
            exit 3
            """);

        await using var agent = new NgrokTunnel(new NgrokTunnelOptions
        {
            Port = 8080,
            ExecutablePath = script.Path,
            StartTimeout = TimeSpan.FromSeconds(30)
        });

        var error = await Assert.ThrowsAsync<TunnelAgentException>(() => agent.StartAsync(Token));

        Assert.Contains("exited with code 3", error.Message);
    }

    [Fact]
    public async Task An_agent_that_never_says_anything_times_out()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        using var script = StubScript("""
            #!/bin/sh
            while true; do sleep 1; done
            """);

        await using var agent = new TailscaleFunnel(new TailscaleFunnelOptions
        {
            Port = 8080,
            ExecutablePath = script.Path,
            StartTimeout = TimeSpan.FromMilliseconds(300)
        });

        var error = await Assert.ThrowsAsync<TunnelAgentException>(() => agent.StartAsync(Token));

        Assert.Contains("did not announce a URL", error.Message);
    }

    static StubbedScript StubScript(string contents)
    {
        var path = Path.Combine(Path.GetTempPath(), "shiny-stub-agent-" + Guid.NewGuid().ToString("n") + ".sh");
        File.WriteAllText(path, contents);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        return new StubbedScript(path);
    }

    sealed class StubbedScript(string path) : IDisposable
    {
        public string Path { get; } = path;

        public void Dispose()
        {
            try
            {
                File.Delete(this.Path);
            }
            catch (IOException)
            {
                // The stub is in the temp directory; a leftover is not worth failing a test over.
            }
        }
    }

    /// <summary>The agents' command line and parsing are protected; these expose them to the test.</summary>
    sealed class StubbedCloudflare(CloudflareTunnelOptions options) : CloudflareTunnel(options)
    {
        public string[] Arguments() => [.. this.BuildArguments(this.Options.Port)];

        public string? Parse(string line) => this.ParseUrl(line);
    }

    sealed class StubbedNgrok(NgrokTunnelOptions options) : NgrokTunnel(options)
    {
        public string[] Arguments() => [.. this.BuildArguments(this.Options.Port)];

        public string? Parse(string line) => this.ParseUrl(line);
    }

    sealed class StubbedTailscale(TailscaleFunnelOptions options) : TailscaleFunnel(options)
    {
        public string[] Arguments() => [.. this.BuildArguments(this.Options.Port)];

        public string? Parse(string line) => this.ParseUrl(line);
    }
}
