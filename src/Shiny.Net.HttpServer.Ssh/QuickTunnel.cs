using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Shiny.Net.HttpServer.Ssh;

/// <summary>Where a quick tunnel terminates.</summary>
public enum QuickTunnelHost
{
    /// <summary>
    /// <c>pinggy.io</c>, and the default: no account, no signup, and it reports the address it
    /// assigns in a way this library can actually read. A key is required but not registered, so
    /// <see cref="SshTunnelOptions.UseEphemeralKey"/> generates one and nothing has to be
    /// provisioned. Anonymous tunnels are capped at 60 minutes; pass an access token as the
    /// <c>subdomain</c> argument to lift that.
    /// </summary>
    Pinggy,

    /// <summary>
    /// <c>localhost.run</c>. Forwards fine, but <b>cannot report its own address here</b>: it never
    /// confirms the session request that carries the URL, and SSH.NET — unlike the <c>ssh</c>
    /// binary — waits for that confirmation. Usable only with a localhost.run account whose custom
    /// domain you already know, set as <see cref="SshTunnelOptions.PublicUrl"/>. Without one, use
    /// <see cref="Pinggy"/>.
    /// </summary>
    LocalhostRun,

    /// <summary>
    /// The public <c>sish</c> instance at <c>tuns.sh</c>, which is run by pico.sh and needs a
    /// <b>registered</b> key — connecting with an unknown one is refused outright. Point
    /// <see cref="SshTunnelOptions.PrivateKeyPath"/> at the key you enrolled there, or at your own
    /// sish deployment, which takes any key and derives the subdomain from it.
    /// </summary>
    Sish,

    /// <summary>
    /// <c>serveo.net</c>. Same idea and the longest-running, but frequently unreachable for days at
    /// a time — do not build a demo on it.
    /// </summary>
    Serveo
}

/// <summary>What the tunnel is doing, for a UI to show.</summary>
public enum QuickTunnelState
{
    Stopped,
    Connecting,

    /// <summary>Up, with a <see cref="QuickTunnel.PublicUrl"/> worth showing.</summary>
    Connected,

    /// <summary>Dropped and trying again. The previous URL is dead; a new one is coming.</summary>
    Reconnecting,

    /// <summary>Gave up. <see cref="QuickTunnel.LastError"/> says why.</summary>
    Failed
}

/// <summary>
/// A public HTTPS address for an embedded server, with nothing to install and nothing to sign up for.
/// <para>
/// Built for the case where a person has to read the URL off a screen: it implements
/// <see cref="INotifyPropertyChanged"/> so a view can bind straight to
/// <see cref="PublicUrl"/> and <see cref="State"/>, and it raises those changes on every reconnect —
/// which matters more than it sounds, because a free tunnel hands out a <b>different address every
/// time it reconnects</b>. An app that captured the URL once and painted it on a label would be
/// showing a dead link after the first time the phone changed networks.
/// </para>
/// <code>
/// public sealed class SharingViewModel
/// {
///     public SharingViewModel(QuickTunnel tunnel)
///     {
///         tunnel.PropertyChanged += (_, _) => MainThread.BeginInvokeOnMainThread(() =>
///         {
///             this.Url = tunnel.PublicUrl;          // bind a Label, or render it as a QR code
///             this.Status = tunnel.State.ToString();
///         });
///     }
/// }
/// </code>
/// <para>
/// Everything here is managed code — SSH.NET and sockets — so it runs on iOS and Android, where an
/// agent-based tunnel cannot.
/// </para>
/// </summary>
public sealed partial class QuickTunnel : IAsyncDisposable, INotifyPropertyChanged
{
    readonly HttpServer server;
    readonly SshTunnelOptions options;
    readonly ILoggerFactory? loggerFactory;
    readonly SemaphoreSlim gate = new(1, 1);

    SshTunnelProvider? provider;
    CancellationTokenSource? running;
    Task? runner;
    string? publicUrl;
    QuickTunnelState state = QuickTunnelState.Stopped;
    string? lastError;

    public QuickTunnel(HttpServer server, SshTunnelOptions options, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(options);

        this.server = server;
        this.options = options;
        this.loggerFactory = loggerFactory;
    }

    /// <summary>Builds a tunnel against one of the public hosts.</summary>
    /// <param name="subdomain">
    /// Requested name, where the host supports one. sish and Serveo read it as the subdomain;
    /// Pinggy has no notion of one and reads it as an access token instead, which is what buys a
    /// tunnel that outlives the 60-minute anonymous cap; localhost.run ignores it entirely.
    /// </param>
    public static QuickTunnel For(
        HttpServer server,
        QuickTunnelHost host = QuickTunnelHost.Pinggy,
        string? subdomain = null,
        Action<SshTunnelOptions>? configure = null,
        ILoggerFactory? loggerFactory = null
    )
    {
        ArgumentNullException.ThrowIfNull(server);

        var options = BuildOptions(host, subdomain);
        configure?.Invoke(options);

        return new QuickTunnel(server, options, loggerFactory);
    }

    /// <summary>
    /// The address to show. Null until connected, and <b>it changes on reconnect</b> — bind to it
    /// rather than reading it once.
    /// </summary>
    public string? PublicUrl
    {
        get => this.publicUrl;
        private set => this.SetField(ref this.publicUrl, value);
    }

    public QuickTunnelState State
    {
        get => this.state;
        private set => this.SetField(ref this.state, value);
    }

    /// <summary>Why it failed, when <see cref="State"/> is <see cref="QuickTunnelState.Failed"/>.</summary>
    public string? LastError
    {
        get => this.lastError;
        private set => this.SetField(ref this.lastError, value);
    }

    /// <summary>
    /// Raised on a background thread. Marshal to the UI thread before touching anything a view is
    /// bound to — MAUI will not do it for you.
    /// </summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Opens the tunnel and returns the address, or null if it could not be established.</summary>
    public async Task<string?> StartAsync(CancellationToken cancellationToken = default)
    {
        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (this.runner is not null)
                return this.PublicUrl;

            this.LastError = null;
            this.State = QuickTunnelState.Connecting;

            var tunnelProvider = new SshTunnelProvider(
                this.options,
                this.loggerFactory?.CreateLogger<SshTunnelProvider>()
            );

            // Subscribed before binding, so the first address is reported through the same path as
            // every later one — a UI that only learned about reconnects would show nothing at all
            // until the first drop.
            tunnelProvider.PublicUrlChanged += this.OnPublicUrlChanged;
            tunnelProvider.ConnectivityChanged += this.OnConnectivityChanged;

            try
            {
                await tunnelProvider.BindAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                tunnelProvider.PublicUrlChanged -= this.OnPublicUrlChanged;
                tunnelProvider.ConnectivityChanged -= this.OnConnectivityChanged;

                await tunnelProvider.DisposeAsync().ConfigureAwait(false);

                this.LastError = ex.Message;
                this.State = QuickTunnelState.Failed;

                throw;
            }

            // A forward with no address to hand anyone is not a success here, whatever the SSH
            // layer thinks. This class exists to put a URL on a screen, so it says so plainly
            // rather than reporting Connected against a link nobody can use.
            if (tunnelProvider.PublicUrl is not { Length: > 0 })
            {
                tunnelProvider.PublicUrlChanged -= this.OnPublicUrlChanged;
                tunnelProvider.ConnectivityChanged -= this.OnConnectivityChanged;

                await tunnelProvider.DisposeAsync().ConfigureAwait(false);

                this.PublicUrl = null;
                this.LastError =
                    "The tunnel connected, but the endpoint never reported a public address. "
                    + "Set SshTunnelOptions.PublicUrl if you know where it answers, or use "
                    + $"{nameof(QuickTunnelHost)}.{nameof(QuickTunnelHost.Pinggy)}.";

                this.State = QuickTunnelState.Failed;

                return null;
            }

            var cts = new CancellationTokenSource();

            this.provider = tunnelProvider;
            this.running = cts;
            this.runner = Task.Run(() => this.ServeAsync(tunnelProvider, cts.Token), CancellationToken.None);

            this.PublicUrl = tunnelProvider.PublicUrl;
            this.State = QuickTunnelState.Connected;

            return this.PublicUrl;
        }
        finally
        {
            this.gate.Release();
        }
    }

    /// <summary>Closes the tunnel. The server keeps running and stays reachable locally.</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (this.running is not { } cts)
                return;

            await cts.CancelAsync().ConfigureAwait(false);

            if (this.provider is { } tunnelProvider)
            {
                tunnelProvider.PublicUrlChanged -= this.OnPublicUrlChanged;
                tunnelProvider.ConnectivityChanged -= this.OnConnectivityChanged;

                await tunnelProvider.DisposeAsync().ConfigureAwait(false);
            }

            if (this.runner is { } task)
            {
                try
                {
                    await task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
                {
                }
            }

            cts.Dispose();

            this.running = null;
            this.runner = null;
            this.provider = null;

            this.PublicUrl = null;
            this.State = QuickTunnelState.Stopped;
        }
        finally
        {
            this.gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await this.StopAsync(CancellationToken.None).ConfigureAwait(false);
        this.gate.Dispose();
    }

    /// <summary>The options a given host needs. Public so an app can start from one and adjust.</summary>
    public static SshTunnelOptions BuildOptions(QuickTunnelHost host, string? subdomain = null)
    {
        var options = new SshTunnelOptions
        {
            RemotePort = 80,
            CaptureUrlFromSession = true,

            // These hosts publish no stable key to pin, and there is no first-use to trust. Said
            // plainly rather than hidden: traffic through a free tunnel passes through someone
            // else's server, and this is the setting that admits it.
            AcceptAnyHostKey = true,
            AutoReconnect = true
        };

        switch (host)
        {
            case QuickTunnelHost.Pinggy:
                options.Host = "a.pinggy.io";
                options.Port = 443;

                // pinggy carries the access token in the username; "a" is the anonymous account.
                options.Username = subdomain is { Length: > 0 } ? subdomain : "a";
                options.RemoteBindAddress = "localhost";

                // It assigns the port as well as the name; asking for 80 is refused.
                options.RemotePort = 0;

                // It wants a key but does not care which, and will not accept "none".
                options.UseEphemeralKey = true;
                options.UrlPattern = PinggyUrl();
                break;

            case QuickTunnelHost.LocalhostRun:
                options.Host = "localhost.run";

                // It authenticates nobody; the name is a convention, not a credential.
                options.Username = "nokey";
                options.RemoteBindAddress = "localhost";
                options.UrlPattern = LocalhostRunUrl();

                // Capture stays on so this starts working the day the endpoint answers a session
                // request, but it is expected to fail today — so it fails quickly rather than
                // making the caller wait out the full window for a foregone conclusion.
                options.UrlCaptureTimeout = TimeSpan.FromSeconds(5);
                break;

            case QuickTunnelHost.Sish:
                options.Host = "tuns.sh";
                options.Username = subdomain is { Length: > 0 } ? subdomain : "shiny";

                // sish reads the requested subdomain from the bind address.
                options.RemoteBindAddress = subdomain is { Length: > 0 } ? subdomain : "localhost";
                options.UseEphemeralKey = true;
                options.UrlPattern = SishUrl();
                break;

            case QuickTunnelHost.Serveo:
                options.Host = "serveo.net";
                options.Username = subdomain is { Length: > 0 } ? subdomain : "shiny";
                options.RemoteBindAddress = subdomain is { Length: > 0 } ? subdomain : "localhost";
                options.UseEphemeralKey = true;
                options.UrlPattern = ServeoUrl();
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(host));
        }

        return options;
    }

    // Each host gets a pattern that only its own tunnel addresses can satisfy. The generic
    // "first https:// you see" default is wrong here: these endpoints print a welcome banner full
    // of links to their own documentation, dashboard and social media, and it arrives on the
    // channel's error stream *ahead* of the address — so the loose pattern reliably captured
    // something like https://admin.localhost.run/ and reported it as the tunnel.
    [GeneratedRegex(@"https://[a-z0-9.-]+\.(?:free\.pinggy\.net|pinggy-free\.link|pinggy\.link|pinggy\.online)", RegexOptions.IgnoreCase)]
    private static partial Regex PinggyUrl();

    [GeneratedRegex(@"https://[a-z0-9-]+\.lhr\.life", RegexOptions.IgnoreCase)]
    private static partial Regex LocalhostRunUrl();

    [GeneratedRegex(@"https://[a-z0-9.-]+\.tuns\.sh", RegexOptions.IgnoreCase)]
    private static partial Regex SishUrl();

    [GeneratedRegex(@"https://[a-z0-9.-]+\.serveo\.net", RegexOptions.IgnoreCase)]
    private static partial Regex ServeoUrl();

    void OnPublicUrlChanged(object? sender, string? url)
    {
        this.PublicUrl = url;

        if (url is { Length: > 0 })
            this.State = QuickTunnelState.Connected;
    }

    void OnConnectivityChanged(object? sender, bool online)
    {
        if (online)
        {
            this.State = QuickTunnelState.Connected;
            return;
        }

        // The address that was on screen is dead from this moment, so it goes now rather than when
        // a replacement arrives — showing a stale link is worse than showing none.
        this.PublicUrl = null;
        this.State = QuickTunnelState.Reconnecting;
    }

    async Task ServeAsync(SshTunnelProvider tunnelProvider, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var connection = await tunnelProvider.AcceptAsync(cancellationToken).ConfigureAwait(false);
                if (connection is null)
                    break;

                _ = Task.Run(() => this.server.ServeAsync(connection, cancellationToken), CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            this.LastError = ex.Message;
            this.State = QuickTunnelState.Failed;
        }
    }

    void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;

        field = value;
        this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>Registering a quick tunnel.</summary>
public static class QuickTunnelExtensions
{
    /// <summary>
    /// Registers a <see cref="QuickTunnel"/> for the registered <see cref="HttpServer"/>, so a view
    /// model can take one in its constructor and bind to it.
    /// <code>
    /// builder.Services.AddHttpServer(autoStart: false, configureServer: s => s.MapGet("/", …));
    /// builder.Services.AddQuickTunnel(autoStart: false);
    ///
    /// // then, from a "Share" button:
    /// var url = await tunnel.StartAsync();
    /// </code>
    /// </summary>
    /// <param name="autoStart">
    /// Start with the host. Off by default, which is almost always right here: putting the server on
    /// the public internet is a decision a person makes by tapping something, not a side effect of
    /// the app launching.
    /// </param>
    public static ShinyHttpServerBuilder AddQuickTunnel(
        this ShinyHttpServerBuilder builder,
        QuickTunnelHost host = QuickTunnelHost.Pinggy,
        string? subdomain = null,
        Action<SshTunnelOptions>? configure = null,
        bool autoStart = false
    )
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddSingleton(sp => QuickTunnel.For(
            sp.GetRequiredService<HttpServer>(),
            host,
            subdomain,
            configure,
            sp.GetService<ILoggerFactory>()
        ));

        if (autoStart)
            builder.Services.AddHostedService<QuickTunnelHostedService>();

        return builder;
    }

    /// <summary>
    /// Opens a quick tunnel and serves through it until cancelled — the one-liner for a console app
    /// or a sample.
    /// <code>
    /// await app.RunQuickTunnelAsync(url => Console.WriteLine($"Reachable at {url}"));
    /// </code>
    /// </summary>
    public static async Task RunQuickTunnelAsync(
        this HttpServer server,
        Action<string?>? onUrl = null,
        QuickTunnelHost host = QuickTunnelHost.Pinggy,
        string? subdomain = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(server);

        await using var tunnel = QuickTunnel.For(server, host, subdomain);

        if (onUrl is not null)
            tunnel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(QuickTunnel.PublicUrl))
                    onUrl(tunnel.PublicUrl);
            };

        await tunnel.StartAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }
}

sealed class QuickTunnelHostedService(QuickTunnel tunnel) : Microsoft.Extensions.Hosting.IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => tunnel.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => tunnel.StopAsync(cancellationToken);
}
