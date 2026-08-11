using System.Text.RegularExpressions;
using Renci.SshNet;
using Shiny.Net.HttpServer.Ssh;

namespace Shiny.Net.HttpServer.Tests;

/// <summary>
/// The parts that do not need an SSH server: option validation, host key matching and the banner
/// parsing that hosted tunnels depend on. Forwarding itself is verified by hand against a real
/// endpoint — see the package README.
/// </summary>
public class SshTunnelOptionsTests
{
    const string Fingerprint = "SHA256:47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU";

    static SshTunnelOptions Valid() => new()
    {
        Host = "tunnel.example.com",
        Username = "tunnel",
        AcceptAnyHostKey = true
    };

    [Fact]
    public void Requires_a_host()
    {
        var options = Valid();
        options.Host = "";

        var ex = Assert.Throws<InvalidOperationException>(() => options.CreateConnectionInfo());
        Assert.Contains(nameof(SshTunnelOptions.Host), ex.Message);
    }

    [Fact]
    public void Requires_a_username()
    {
        var options = Valid();
        options.Username = "";

        var ex = Assert.Throws<InvalidOperationException>(() => options.CreateConnectionInfo());
        Assert.Contains(nameof(SshTunnelOptions.Username), ex.Message);
    }

    /// <summary>
    /// The point of the package's stricter default: SSH.NET trusts any host key unless told not to,
    /// and a tunnel exists to cross networks nobody controls.
    /// </summary>
    [Fact]
    public void Refuses_to_connect_without_a_host_key_to_verify()
    {
        var options = new SshTunnelOptions { Host = "tunnel.example.com", Username = "tunnel" };

        var ex = Assert.Throws<InvalidOperationException>(() => options.CreateConnectionInfo());

        Assert.Contains(nameof(SshTunnelOptions.HostKeyFingerprints), ex.Message);
        Assert.Contains("ssh-keyscan", ex.Message);
    }

    [Fact]
    public void Accepts_a_pinned_fingerprint_instead()
    {
        var options = new SshTunnelOptions { Host = "tunnel.example.com", Username = "tunnel" };
        options.HostKeyFingerprints.Add(Fingerprint);

        var info = options.CreateConnectionInfo();

        Assert.Equal("tunnel.example.com", info.Host);
        Assert.Equal(22, info.Port);
        Assert.Equal("tunnel", info.Username);
    }

    [Fact]
    public void Offers_password_authentication_when_one_is_set()
    {
        var options = Valid();
        options.Password = "hunter2";

        var info = options.CreateConnectionInfo();

        Assert.Contains(info.AuthenticationMethods, m => m.Name == "password");
    }

    /// <summary>localhost.run authenticates nobody, so an empty method list is not an error there.</summary>
    [Fact]
    public void Falls_back_to_none_authentication()
    {
        var info = Valid().CreateConnectionInfo();

        Assert.Contains(info.AuthenticationMethods, m => m.Name == "none");
    }

    [Fact]
    public void Carries_the_connect_timeout_through()
    {
        var options = Valid();
        options.ConnectTimeout = TimeSpan.FromSeconds(7);

        Assert.Equal(TimeSpan.FromSeconds(7), options.CreateConnectionInfo().Timeout);
    }
}

public class SshHostKeyMatchingTests
{
    const string Bare = "47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU";

    /// <summary>
    /// ssh-keygen prints "SHA256:…", SSH.NET hands back the bare base64, and some tools keep the
    /// padding. All three name the same key, so all three have to match.
    /// </summary>
    [Theory]
    [InlineData("SHA256:" + Bare)]
    [InlineData(Bare)]
    [InlineData(Bare + "=")]
    [InlineData("  SHA256:" + Bare + "  ")]
    [InlineData("sha256:" + Bare)]
    public void Matches_every_spelling_of_the_same_fingerprint(string pinned)
        => Assert.True(SshTunnelProvider.Matches(pinned, Bare));

    [Fact]
    public void Does_not_match_a_different_key()
        => Assert.False(SshTunnelProvider.Matches("SHA256:" + Bare, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));

    /// <summary>Base64 is case-sensitive; treating it otherwise would widen what a pin accepts.</summary>
    [Fact]
    public void Compares_the_fingerprint_case_sensitively()
        => Assert.False(SshTunnelProvider.Matches("SHA256:" + Bare, Bare.ToLowerInvariant()));
}

public class SshUrlCaptureTests
{
    static Regex Pattern => new SshTunnelOptions().UrlPattern;

    /// <summary>
    /// Every hosted tunnel prints the assigned address on the session channel, and there is no other
    /// way to learn it — the address is the server's to choose.
    /// </summary>
    [Theory]
    [InlineData(
        "Starting SSH Forwarding service for http:80. Forwarded connections can be accessed via:\r\nhttps://device-1.tuns.sh\r\n",
        "https://device-1.tuns.sh"
    )]
    [InlineData(
        "{\"address\":\"abc123.lhr.life\",\"status\":\"success\"}\r\nConnect to https://abc123.lhr.life\r\n",
        "https://abc123.lhr.life"
    )]
    [InlineData("Forwarding HTTP traffic from https://device-1.serveo.net\n", "https://device-1.serveo.net")]
    public void Finds_the_assigned_url_in_a_banner(string banner, string expected)
    {
        var match = Pattern.Match(banner);

        Assert.True(match.Success);
        Assert.Equal(expected, match.Value.TrimEnd('.', ',', ';'));
    }

    [Fact]
    public void Finds_nothing_in_a_banner_without_a_url()
        => Assert.DoesNotMatch(Pattern, "Welcome. Your key is not authorized for this account.\r\n");
}

/// <summary>
/// The banner every hosted tunnel prints before the address, and the reason the generic
/// "first https:// wins" pattern is not good enough.
/// <para>
/// These are the real greetings, trimmed. Both arrive on the session channel ahead of the assigned
/// URL — localhost.run's on the channel's error stream, which lands in the same read — and both open
/// with links to the provider's own site. A loose pattern captured those and reported them as the
/// tunnel, so the app displayed a working-looking link to somebody else's dashboard.
/// </para>
/// </summary>
public class QuickTunnelUrlPatternTests
{
    const string LocalhostRunBanner = """
        ===============================================================================
        Welcome to localhost.run!

        Follow your favourite reverse tunnel at [https://twitter.com/localhost_run].

        To set up and manage custom domains go to https://admin.localhost.run/

        More details on custom domains (and how to enable subdomains of your custom
        domain) at https://localhost.run/docs/custom-domains

        To explore using localhost.run visit the documentation site:
        https://localhost.run/docs/
        ===============================================================================
        authenticated as anonymous user
        1e50baebe27f62.lhr.life tunneled with tls termination, https://1e50baebe27f62.lhr.life
        create an account and add your key for a longer lasting domain name. see
        https://localhost.run/docs/forever-free/ for more information.
        """;

    const string PinggyBanner = """
        You are not authenticated.
        Your tunnel will expire in 60 minutes. Upgrade to Pinggy Pro to get unrestricted tunnels. https://dashboard.pinggy.io
        https://tbxhk-99-231-206-87.run.pinggy-free.link
        https://vdtwz-99-231-206-87.free.pinggy.net
        """;

    [Fact]
    public void Ignores_everything_in_the_localhost_run_banner_but_the_tunnel()
    {
        var pattern = QuickTunnel.BuildOptions(QuickTunnelHost.LocalhostRun).UrlPattern;

        Assert.Equal("https://1e50baebe27f62.lhr.life", pattern.Match(LocalhostRunBanner).Value);
    }

    [Fact]
    public void Ignores_the_pinggy_dashboard_link()
    {
        var pattern = QuickTunnel.BuildOptions(QuickTunnelHost.Pinggy).UrlPattern;
        var match = pattern.Match(PinggyBanner);

        Assert.True(match.Success);
        Assert.DoesNotContain("dashboard", match.Value);
        Assert.EndsWith(".pinggy-free.link", match.Value);
    }

    /// <summary>The provider strips what the sentence around a URL leaves attached to it.</summary>
    [Theory]
    [InlineData("Forwarding to https://abc.lhr.life.", "https://abc.lhr.life")]
    [InlineData("Open [https://abc.lhr.life] on your phone", "https://abc.lhr.life")]
    [InlineData("url is \"https://abc.lhr.life\"", "https://abc.lhr.life")]
    public void Strips_the_punctuation_a_provider_wraps_the_url_in(string line, string expected)
    {
        var match = QuickTunnel.BuildOptions(QuickTunnelHost.LocalhostRun).UrlPattern.Match(line);

        Assert.Equal(expected, match.Value.TrimEnd('.', ',', ';', ':', ')', ']', '}', '>', '"', '\''));
    }

    [Fact]
    public void Finds_nothing_when_the_endpoint_only_greeted_us()
    {
        var pattern = QuickTunnel.BuildOptions(QuickTunnelHost.Pinggy).UrlPattern;

        Assert.DoesNotMatch(pattern, "You are not authenticated. https://dashboard.pinggy.io\n");
    }
}

public class QuickTunnelOptionTests
{
    /// <summary>
    /// The default, and the only preset that works with nothing provisioned. It wants a key but not
    /// a registered one, so the options generate one rather than asking the app for it.
    /// </summary>
    [Fact]
    public void Builds_pinggy_as_the_default()
    {
        var options = QuickTunnel.BuildOptions(QuickTunnelHost.Pinggy);

        Assert.Equal("a.pinggy.io", options.Host);
        Assert.Equal(443, options.Port);
        Assert.Equal("a", options.Username);
        Assert.True(options.UseEphemeralKey);

        // pinggy assigns the port as well as the name.
        Assert.Equal(0, options.RemotePort);
        Assert.True(options.CaptureUrlFromSession);
    }

    [Fact]
    public void Reads_the_pinggy_subdomain_argument_as_an_access_token()
    {
        var options = QuickTunnel.BuildOptions(QuickTunnelHost.Pinggy, "tok_abc123");

        Assert.Equal("tok_abc123", options.Username);
    }

    /// <summary>A generated key is made once, so a reconnect presents the same identity.</summary>
    [Fact]
    public void Reuses_the_key_it_generated()
    {
        var options = QuickTunnel.BuildOptions(QuickTunnelHost.Pinggy);

        var first = options.CreateConnectionInfo();
        var second = options.CreateConnectionInfo();

        var firstKey = Assert.IsType<PrivateKeyAuthenticationMethod>(Assert.Single(first.AuthenticationMethods));
        var secondKey = Assert.IsType<PrivateKeyAuthenticationMethod>(Assert.Single(second.AuthenticationMethods));

        Assert.Same(Assert.Single(firstKey.KeyFiles), Assert.Single(secondKey.KeyFiles));
    }

    /// <summary>An explicit credential is never displaced by a generated one.</summary>
    [Fact]
    public void Prefers_a_supplied_key_over_a_generated_one()
    {
        var options = QuickTunnel.BuildOptions(QuickTunnelHost.Pinggy);
        options.Password = "hunter2";

        var info = options.CreateConnectionInfo();

        Assert.IsType<PasswordAuthenticationMethod>(Assert.Single(info.AuthenticationMethods));
    }

    [Fact]
    public void Builds_localhost_run_with_no_credentials_at_all()
    {
        var options = QuickTunnel.BuildOptions(QuickTunnelHost.LocalhostRun);

        Assert.Equal("localhost.run", options.Host);
        Assert.Equal("localhost", options.RemoteBindAddress);
        Assert.Equal(80, options.RemotePort);

        // The URL is assigned by the host and printed on the session channel; there is no other
        // way to learn it, so capture has to be on.
        Assert.True(options.CaptureUrlFromSession);
        Assert.True(options.AcceptAnyHostKey);
        Assert.True(options.AutoReconnect);
        Assert.Null(options.PrivateKeyPath);
        Assert.Null(options.Password);
    }

    [Fact]
    public void Passes_a_requested_subdomain_to_sish()
    {
        var options = QuickTunnel.BuildOptions(QuickTunnelHost.Sish, "device-1");

        Assert.Equal("tuns.sh", options.Host);

        // sish reads the requested name from the bind address.
        Assert.Equal("device-1", options.RemoteBindAddress);
        Assert.Equal("device-1", options.Username);
    }

    [Fact]
    public void Builds_serveo()
    {
        var options = QuickTunnel.BuildOptions(QuickTunnelHost.Serveo, "device-1");

        Assert.Equal("serveo.net", options.Host);
        Assert.Equal("device-1", options.RemoteBindAddress);
    }

    [Fact]
    public void Lets_the_caller_adjust_what_the_preset_chose()
    {
        var server = new HttpServer();
        var tunnel = QuickTunnel.For(server, QuickTunnelHost.LocalhostRun, configure: o => o.KeepAliveInterval = TimeSpan.FromMinutes(2));

        Assert.NotNull(tunnel);
        Assert.Equal(QuickTunnelState.Stopped, tunnel.State);
        Assert.Null(tunnel.PublicUrl);
    }
}

/// <summary>
/// The UI-facing behaviour, driven against the local sshd rather than a public host — the state
/// machine and its notifications are what a view binds to, and they are the same either way.
/// Nothing here opens a tunnel to the internet.
/// </summary>
public class QuickTunnelNotificationTests(SshdFixture sshd) : IClassFixture<SshdFixture>
{
    /// <summary>The local sshd stands in for a public host; only the address differs.</summary>
    SshTunnelOptions LocalOptions(int remotePort)
    {
        var options = QuickTunnel.BuildOptions(QuickTunnelHost.LocalhostRun);

        options.Host = "127.0.0.1";
        options.Port = sshd.Port;
        options.Username = Environment.UserName;
        options.PrivateKeyPath = sshd.ClientKeyPath;
        options.RemoteBindAddress = "127.0.0.1";
        options.RemotePort = remotePort;
        options.AutoReconnect = false;

        // A plain sshd prints no banner, so the capture times out and the derived URL is used.
        options.CaptureUrlFromSession = false;

        return options;
    }

    [Fact]
    public async Task Reports_the_url_and_the_state_through_property_changed()
    {
        Assert.SkipWhen(sshd.SkipReason is not null, sshd.SkipReason ?? "");

        var server = new HttpServer();
        server.MapGet("/ping", ctx => ctx.Response.WriteTextAsync("pong"));

        var remotePort = SshdFixture.FreePort();
        await using var tunnel = new QuickTunnel(server, this.LocalOptions(remotePort));

        var changes = new List<string>();
        tunnel.PropertyChanged += (_, e) => changes.Add(e.PropertyName ?? "");

        var url = await tunnel.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal($"http://127.0.0.1:{remotePort}", url);
        Assert.Equal(url, tunnel.PublicUrl);
        Assert.Equal(QuickTunnelState.Connected, tunnel.State);

        // A view binds to both, so both have to have raised.
        Assert.Contains(nameof(QuickTunnel.PublicUrl), changes);
        Assert.Contains(nameof(QuickTunnel.State), changes);
    }

    /// <summary>The whole point: a person reads the URL off a screen and it has to work.</summary>
    [Fact]
    public async Task Serves_requests_on_the_url_it_reported()
    {
        Assert.SkipWhen(sshd.SkipReason is not null, sshd.SkipReason ?? "");

        var server = new HttpServer();
        server.MapGet("/ping", ctx => ctx.Response.WriteTextAsync("pong"));

        await using var tunnel = new QuickTunnel(server, this.LocalOptions(SshdFixture.FreePort()));

        var url = await tunnel.StartAsync(TestContext.Current.CancellationToken);

        using var client = new HttpClient { BaseAddress = new Uri(url!) };

        Assert.Equal("pong", await client.GetStringAsync("/ping", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Clears_the_url_and_reports_stopped_when_it_is_closed()
    {
        Assert.SkipWhen(sshd.SkipReason is not null, sshd.SkipReason ?? "");

        var server = new HttpServer();
        await using var tunnel = new QuickTunnel(server, this.LocalOptions(SshdFixture.FreePort()));

        await tunnel.StartAsync(TestContext.Current.CancellationToken);
        await tunnel.StopAsync(TestContext.Current.CancellationToken);

        Assert.Null(tunnel.PublicUrl);
        Assert.Equal(QuickTunnelState.Stopped, tunnel.State);
    }

    [Fact]
    public async Task Starting_twice_is_harmless()
    {
        Assert.SkipWhen(sshd.SkipReason is not null, sshd.SkipReason ?? "");

        var server = new HttpServer();
        await using var tunnel = new QuickTunnel(server, this.LocalOptions(SshdFixture.FreePort()));

        var first = await tunnel.StartAsync(TestContext.Current.CancellationToken);
        var second = await tunnel.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(first, second);
    }

    /// <summary>
    /// A tunnel that cannot report its address hands control back on schedule and admits it.
    /// <para>
    /// Both halves matter and both were wrong. The capture used to be bounded only from the point
    /// the session channel opened, and opening it is a blocking call SSH.NET holds until the server
    /// confirms the request — so an endpoint that never confirms one (localhost.run does not) held
    /// the caller for the whole connect timeout. Then, having failed, it reported
    /// <c>http://{host}:{port}</c> as the public address, which for a hosted tunnel is a link to
    /// the provider's own front page. A UI showed that as "Shared".
    /// </para>
    /// </summary>
    [Fact]
    public async Task Gives_up_on_the_url_within_the_capture_window_and_reports_none()
    {
        Assert.SkipWhen(sshd.SkipReason is not null, sshd.SkipReason ?? "");

        var options = this.LocalOptions(SshdFixture.FreePort());
        options.CaptureUrlFromSession = true;

        // A plain sshd announces nothing, so nothing can ever match — the capture window is the
        // only thing that ends this.
        options.UrlPattern = new Regex(@"https://[a-z0-9-]+\.example\.invalid");
        options.UrlCaptureTimeout = TimeSpan.FromSeconds(3);

        var provider = new SshTunnelProvider(options);

        await using (provider)
        {
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            await provider.BindAsync(TestContext.Current.CancellationToken);
            elapsed.Stop();

            Assert.Null(provider.PublicUrl);
            Assert.True(
                elapsed.Elapsed < options.ConnectTimeout,
                $"BindAsync took {elapsed.Elapsed}, which is past the capture window it should be bounded by."
            );
        }
    }

    /// <summary>
    /// And the QuickTunnel above it refuses to call that connected — the whole class exists to put
    /// an address on a screen.
    /// </summary>
    [Fact]
    public async Task Fails_when_the_tunnel_is_up_but_nameless()
    {
        Assert.SkipWhen(sshd.SkipReason is not null, sshd.SkipReason ?? "");

        var options = this.LocalOptions(SshdFixture.FreePort());
        options.CaptureUrlFromSession = true;
        options.UrlPattern = new Regex(@"https://[a-z0-9-]+\.example\.invalid");
        options.UrlCaptureTimeout = TimeSpan.FromSeconds(3);

        await using var tunnel = new QuickTunnel(new HttpServer(), options);

        Assert.Null(await tunnel.StartAsync(TestContext.Current.CancellationToken));
        Assert.Equal(QuickTunnelState.Failed, tunnel.State);
        Assert.NotNull(tunnel.LastError);
        Assert.Null(tunnel.PublicUrl);
    }

    /// <summary>A UI needs something to show when it fails, not just an exception in a log.</summary>
    [Fact]
    public async Task Records_why_it_failed()
    {
        var server = new HttpServer();

        var options = QuickTunnel.BuildOptions(QuickTunnelHost.LocalhostRun);
        options.Host = "127.0.0.1";
        options.Port = SshdFixture.FreePort();      // nothing is listening there
        options.ConnectTimeout = TimeSpan.FromSeconds(2);
        options.AutoReconnect = false;

        await using var tunnel = new QuickTunnel(server, options);

        await Assert.ThrowsAnyAsync<Exception>(() => tunnel.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal(QuickTunnelState.Failed, tunnel.State);
        Assert.NotNull(tunnel.LastError);
        Assert.Null(tunnel.PublicUrl);
    }
}
