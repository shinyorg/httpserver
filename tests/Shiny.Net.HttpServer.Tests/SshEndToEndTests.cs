using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Shiny.Net.HttpServer.Ssh;
using Shiny.Net.HttpServer.Tunneling;

namespace Shiny.Net.HttpServer.Tests;

/// <summary>
/// A real sshd, a real forward, a real HTTP request.
/// <para>
/// Nothing about SSH forwarding can be verified against a mock — what matters is whether an actual
/// server accepts the forwarding request and delivers connections down it. sshd runs unprivileged
/// on a loopback port with a throwaway key pair, and the tests skip where that is not possible.
/// </para>
/// </summary>
public sealed class SshdFixture : IAsyncLifetime
{
    Process? sshd;

    public string Directory { get; private set; } = "";

    public int Port { get; private set; }

    public string ClientKeyPath => Path.Combine(this.Directory, "client");

    /// <summary>The host key's SHA-256 fingerprint, as ssh-keygen prints it.</summary>
    public string HostKeyFingerprint { get; private set; } = "";

    /// <summary>Why the fixture is unusable, or null when it is running.</summary>
    public string? SkipReason { get; private set; }

    public async ValueTask InitializeAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            this.SkipReason = "The fixture drives OpenSSH's sshd directly, which this test does not do on Windows.";
            return;
        }

        var sshdPath = new[] { "/usr/sbin/sshd", "/usr/local/sbin/sshd", "/usr/bin/sshd" }.FirstOrDefault(File.Exists);
        if (sshdPath is null)
        {
            this.SkipReason = "sshd is not installed.";
            return;
        }

        this.Directory = Path.Combine(Path.GetTempPath(), "shiny-sshd-" + Guid.NewGuid().ToString("n")[..8]);
        System.IO.Directory.CreateDirectory(this.Directory);

        try
        {
            await this.GenerateKeysAsync();
            this.Port = FreePort();

            var config = Path.Combine(this.Directory, "sshd_config");
            await File.WriteAllTextAsync(config, $"""
                Port {this.Port}
                ListenAddress 127.0.0.1
                HostKey {Path.Combine(this.Directory, "host")}
                AuthorizedKeysFile {Path.Combine(this.Directory, "authorized_keys")}
                PidFile {Path.Combine(this.Directory, "sshd.pid")}
                StrictModes no
                UsePAM no
                PasswordAuthentication no
                PubkeyAuthentication yes
                AllowTcpForwarding yes
                GatewayPorts no
                """);

            this.sshd = Process.Start(new ProcessStartInfo(sshdPath, ["-D", "-f", config, "-e"])
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true
            });

            if (this.sshd is null)
            {
                this.SkipReason = "sshd would not start.";
                return;
            }

            if (!await WaitForListenerAsync(this.Port))
            {
                var error = await this.sshd.StandardError.ReadToEndAsync();
                this.SkipReason = $"sshd did not come up (it usually wants root): {error.Trim()}";
            }
        }
        catch (Exception ex)
        {
            this.SkipReason = $"The sshd fixture could not be prepared: {ex.Message}";
        }
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            if (this.sshd is { HasExited: false })
                this.sshd.Kill(entireProcessTree: true);

            this.sshd?.Dispose();
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
        }

        try
        {
            if (this.Directory.Length > 0 && System.IO.Directory.Exists(this.Directory))
                System.IO.Directory.Delete(this.Directory, recursive: true);
        }
        catch (IOException)
        {
        }

        return default;
    }

    async Task GenerateKeysAsync()
    {
        await RunAsync("ssh-keygen", ["-q", "-t", "ed25519", "-f", Path.Combine(this.Directory, "host"), "-N", ""]);
        await RunAsync("ssh-keygen", ["-q", "-t", "ed25519", "-f", this.ClientKeyPath, "-N", ""]);

        File.Copy(this.ClientKeyPath + ".pub", Path.Combine(this.Directory, "authorized_keys"));

        // "256 SHA256:47DEQ…  comment (ED25519)" — the middle field is what gets pinned.
        var listed = await RunAsync("ssh-keygen", ["-lf", Path.Combine(this.Directory, "host.pub")]);
        this.HostKeyFingerprint = listed.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1];
    }

    static async Task<string> RunAsync(string file, string[] arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(file, arguments) { RedirectStandardOutput = true })
            ?? throw new InvalidOperationException($"Could not start {file}.");

        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{file} exited {process.ExitCode}.");

        return output;
    }

    static async Task<bool> WaitForListenerAsync(int port)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                using var probe = new TcpClient();
                await probe.ConnectAsync(IPAddress.Loopback, port);

                return true;
            }
            catch (SocketException)
            {
                await Task.Delay(100);
            }
        }

        return false;
    }

    internal static int FreePort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }
}

public class SshEndToEndTests(SshdFixture sshd) : IClassFixture<SshdFixture>
{
    SshTunnelOptions Options(int remotePort) => new()
    {
        Host = "127.0.0.1",
        Port = sshd.Port,
        Username = Environment.UserName,
        PrivateKeyPath = sshd.ClientKeyPath,
        RemoteBindAddress = "127.0.0.1",
        RemotePort = remotePort,
        AutoReconnect = false,
        HostKeyFingerprints = { sshd.HostKeyFingerprint }
    };

    [Fact]
    public async Task Serves_a_request_that_arrived_through_the_forward()
    {
        Assert.SkipWhen(sshd.SkipReason is not null, sshd.SkipReason ?? "");

        var remotePort = SshdFixture.FreePort();
        var server = new HttpServer();
        server.MapGet("/ping", ctx => ctx.Response.WriteTextAsync("pong"));

        await using var provider = new SshTunnelProvider(this.Options(remotePort));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var running = server.RunTunnelAsync(provider, cancellationToken: cts.Token);

        // Give the forward a moment to be registered before the first request.
        await WaitForListenerAsync(remotePort, cts.Token);

        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{remotePort}") };
        var body = await client.GetStringAsync("/ping", cts.Token);

        Assert.Equal("pong", body);

        await cts.CancelAsync();
        await running.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The connection is tunnelled, and the pipeline has to be told so — the loopback socket it
    /// arrives on cannot know.
    /// </summary>
    [Fact]
    public async Task Marks_forwarded_connections_as_tunnelled()
    {
        Assert.SkipWhen(sshd.SkipReason is not null, sshd.SkipReason ?? "");

        var remotePort = SshdFixture.FreePort();
        var server = new HttpServer();
        server.MapGet("/how", ctx => ctx.Response.WriteTextAsync(
            $"{ctx.Connection.IsTunneled}:{ctx.Connection.IsEncrypted}"
        ));

        await using var provider = new SshTunnelProvider(this.Options(remotePort));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var running = server.RunTunnelAsync(provider, cancellationToken: cts.Token);
        await WaitForListenerAsync(remotePort, cts.Token);

        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{remotePort}") };

        Assert.Equal("True:False", await client.GetStringAsync("/how", cts.Token));

        await cts.CancelAsync();
        await running.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Asking for port 0 makes the server allocate one, and it is only knowable after connecting —
    /// which is why the provider reports it back.
    /// </summary>
    [Fact]
    public async Task Reports_a_server_assigned_port()
    {
        Assert.SkipWhen(sshd.SkipReason is not null, sshd.SkipReason ?? "");

        var options = this.Options(0);
        await using var provider = new SshTunnelProvider(options);

        await provider.BindAsync(TestContext.Current.CancellationToken);

        Assert.InRange(provider.RemotePort, 1, 65535);
        Assert.Equal($"http://127.0.0.1:{provider.RemotePort}", provider.PublicUrl);
        Assert.True(provider.IsConnected);
    }

    /// <summary>
    /// The security property the whole package leans on: a key that is not the pinned one is
    /// refused, rather than quietly trusted the way SSH.NET does by default.
    /// </summary>
    [Fact]
    public async Task Refuses_a_host_key_that_is_not_pinned()
    {
        Assert.SkipWhen(sshd.SkipReason is not null, sshd.SkipReason ?? "");

        var options = this.Options(SshdFixture.FreePort());
        options.HostKeyFingerprints.Clear();
        options.HostKeyFingerprints.Add("SHA256:47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU");

        await using var provider = new SshTunnelProvider(options);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            provider.BindAsync(TestContext.Current.CancellationToken).AsTask()
        );
    }

    [Fact]
    public async Task Reports_the_configured_public_url()
    {
        Assert.SkipWhen(sshd.SkipReason is not null, sshd.SkipReason ?? "");

        var options = this.Options(SshdFixture.FreePort());
        options.PublicUrl = "https://device-1.example.com";

        await using var provider = new SshTunnelProvider(options);
        await provider.BindAsync(TestContext.Current.CancellationToken);

        Assert.Equal("https://device-1.example.com", provider.PublicUrl);
    }

    static async Task WaitForListenerAsync(int port, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                using var probe = new TcpClient();
                await probe.ConnectAsync(IPAddress.Loopback, port, cancellationToken);

                return;
            }
            catch (SocketException)
            {
                await Task.Delay(100, cancellationToken);
            }
        }

        throw new TimeoutException($"Nothing came up on port {port}.");
    }
}
