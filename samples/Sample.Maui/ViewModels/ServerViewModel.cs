using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Sample.Maui.Pages;
using Sample.Maui.Server;
using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.Ssh;

namespace Sample.Maui.ViewModels;

/// <summary>
/// The Server tab: what is running, where it can be reached, and the buttons that change that.
/// <para>
/// The only interesting part is the threading. <see cref="QuickTunnel"/> raises its changes from the
/// background — a reconnect happens whenever the network does — and MAUI will not marshal that for
/// you. <see cref="IMainThread"/> is Shiny's abstraction over that hop; it exists because MAUI's own
/// <c>MainThread.InvokeOnMainThreadAsync</c> misbehaves on some desktop targets.
/// </para>
/// </summary>
[ShellMap<ServerPage>("Server", registerRoute: false)]
public partial class ServerViewModel(
    HttpServer server,
    QuickTunnel tunnel,
    RequestLog log,
    CredentialStore credentials,
    IDialogs dialogs,
    IMainThread mainThread
) : ObservableObject, IPageLifecycleAware
{
    /// <summary>Live while a share is being opened, so the user can call it off.</summary>
    CancellationTokenSource? sharing;

    [ObservableProperty]
    string status = "Stopped";

    /// <summary>
    /// The address to hand a customer. Null until the tunnel is up, and it changes on every
    /// reconnect — which is exactly why the view binds to it instead of reading it once.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPublicUrl))]
    [NotifyPropertyChangedFor(nameof(CanStopSharing))]
    [NotifyPropertyChangedFor(nameof(McpUrl))]
    [NotifyPropertyChangedFor(nameof(WebDavUrl))]
    string? publicUrl;

    /// <summary>The address on this Wi-Fi network. Works with no internet at all.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(McpUrl))]
    [NotifyPropertyChangedFor(nameof(WebDavUrl))]
    string? localUrl;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    string? lastError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotBusy))]
    bool busy;

    [ObservableProperty]
    int requestCount;

    public bool HasPublicUrl => this.PublicUrl is { Length: > 0 };

    public bool HasError => this.LastError is { Length: > 0 };

    public bool NotBusy => !this.Busy;

    /// <summary>
    /// What to paste into an MCP client. The public address when the tunnel is up, because that is
    /// the one that works from wherever the client happens to be running; otherwise the LAN address.
    /// </summary>
    public string? McpUrl => (this.PublicUrl ?? this.LocalUrl) is { Length: > 0 } url
        ? $"{url.TrimEnd('/')}/mcp"
        : null;

    /// <summary>
    /// What to type into Finder's "Connect to Server" or Explorer's "Map network drive".
    /// <para>
    /// The LAN address first, the opposite way round from <see cref="McpUrl"/>. A mounted drive is
    /// a long-lived connection that sends the password on every request, so the one that stays on
    /// the Wi-Fi is the one to offer — the tunnel is cleartext HTTP to a shared host.
    /// </para>
    /// </summary>
    public string? WebDavUrl => (this.LocalUrl ?? this.PublicUrl) is { Length: > 0 } url
        ? $"{url.TrimEnd('/')}/dav"
        : null;

    /// <summary>
    /// Whether there is anything to stop — which includes a share still being opened.
    /// <para>
    /// Not the same as <see cref="HasPublicUrl"/>, and the difference is the whole point: opening a
    /// tunnel takes seconds and can fail, and while it was in flight every button on the screen was
    /// disabled — the action ones by <see cref="Busy"/>, this one by there being no URL yet. The
    /// screen had no way out of its own waiting state.
    /// </para>
    /// </summary>
    public bool CanStopSharing => this.sharing is not null || this.HasPublicUrl;

    /// <summary>
    /// Subscriptions live for as long as the tab is on screen. The view model outlives one visit —
    /// Shell keeps a tab's page around — so subscribing in the constructor would work, and then
    /// leak the day someone reuses this on a page that gets popped.
    /// </summary>
    public void OnAppearing()
    {
        tunnel.PropertyChanged += this.OnTunnelChanged;
        log.Added += this.OnRequest;

        this.Sync();

        // Serving on the local network starts with the app. That is what this sample is for, and it
        // exposes nothing beyond the Wi-Fi it is already on — publishing to the internet stays
        // behind a deliberate tap.
        _ = this.EnsureStartedAsync();
    }

    public void OnDisappearing()
    {
        tunnel.PropertyChanged -= this.OnTunnelChanged;
        log.Added -= this.OnRequest;
    }

    /// <summary>Starts the server on this network. No internet involved.</summary>
    [RelayCommand]
    async Task StartLocal()
    {
        this.Busy = true;

        try
        {
            await this.StartServerAsync();
        }
        finally
        {
            this.Busy = false;
        }
    }

    /// <summary>
    /// Opens the public tunnel, so the link works from anywhere.
    /// <para>
    /// Cancellable, because it is the one action here that talks to a machine on the other side of
    /// the internet and can sit there for seconds before it says anything.
    /// </para>
    /// </summary>
    [RelayCommand]
    async Task Share()
    {
        if (this.sharing is not null)
            return;

        using var cts = new CancellationTokenSource();

        this.sharing = cts;
        this.Busy = true;
        this.LastError = null;
        this.OnPropertyChanged(nameof(this.CanStopSharing));

        try
        {
            await this.StartServerAsync();

            // Null is a failure the tunnel already described — it connected but never learned an
            // address to hand anyone. Reporting its reason beats a blank screen labelled "Failed".
            if (await tunnel.StartAsync(cts.Token) is null)
                this.LastError = tunnel.LastError ?? "The tunnel did not return a public address.";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            this.LastError = ex.Message;
        }
        finally
        {
            this.sharing = null;
            this.Busy = false;
            this.OnPropertyChanged(nameof(this.CanStopSharing));
        }
    }

    /// <summary>
    /// Closes the tunnel, or calls off one that is still opening. The server keeps serving on this
    /// network.
    /// </summary>
    [RelayCommand]
    async Task StopSharing()
    {
        // Cancel first: this is what a person taps when the app has been saying "Connecting…" for
        // longer than they are willing to wait.
        if (this.sharing is { } cts)
            await cts.CancelAsync();

        this.Busy = true;

        try
        {
            await tunnel.StopAsync();
        }
        catch (Exception ex)
        {
            this.LastError = ex.Message;
        }
        finally
        {
            this.Busy = false;
        }
    }

    [RelayCommand]
    async Task CopyLink()
    {
        if (this.PublicUrl is { Length: > 0 } url)
        {
            await Clipboard.Default.SetTextAsync(url);
            await dialogs.Alert("Copied", "The link is on your clipboard.");
        }
    }

    [RelayCommand]
    async Task OpenLink()
    {
        if (this.PublicUrl is { Length: > 0 } url)
            await Browser.Default.OpenAsync(url, BrowserLaunchMode.SystemPreferred);
    }

    async Task EnsureStartedAsync()
    {
        // Credentials first: the server refuses everything without them, so starting before they
        // are loaded would answer a real request with a password nobody has been told.
        await credentials.LoadAsync();
        await this.StartLocalCommand.ExecuteAsync(null);
    }

    /// <summary>
    /// The server half of both actions, without the <see cref="Busy"/> bookkeeping.
    /// <para>
    /// Sharing starts the server too, and when that ran through the command its <c>finally</c>
    /// cleared <see cref="Busy"/> while the tunnel was still opening — re-enabling every button
    /// mid-flight.
    /// </para>
    /// </summary>
    async Task StartServerAsync()
    {
        if (server.IsRunning)
        {
            this.LocalUrl ??= this.BuildLocalUrl();
            return;
        }

        try
        {
            await server.StartAsync();
            this.LocalUrl = this.BuildLocalUrl();
        }
        catch (Exception ex)
        {
            this.LastError = ex.Message;
        }
    }

    /// <summary>Reads the current state of things the tab does not own, on every appearance.</summary>
    void Sync()
    {
        this.PublicUrl = tunnel.PublicUrl;
        this.Status = Describe(tunnel.State);
        this.LastError = tunnel.LastError;
        this.RequestCount = log.Total;

        if (server.IsRunning)
            this.LocalUrl = this.BuildLocalUrl();
    }

    void OnTunnelChanged(object? sender, PropertyChangedEventArgs e) => mainThread.BeginInvokeOnMainThread(() =>
    {
        this.PublicUrl = tunnel.PublicUrl;
        this.Status = Describe(tunnel.State);
        this.LastError = tunnel.LastError;
    });

    void OnRequest(object? sender, RequestLogEntry entry)
        => mainThread.BeginInvokeOnMainThread(() => this.RequestCount = log.Total);

    static string Describe(QuickTunnelState state) => state switch
    {
        QuickTunnelState.Stopped => "Not shared",
        QuickTunnelState.Connecting => "Connecting…",
        QuickTunnelState.Connected => "Shared",
        QuickTunnelState.Reconnecting => "Reconnecting…",
        QuickTunnelState.Failed => "Failed",
        _ => state.ToString()
    };

    /// <summary>
    /// The address another device on this network would use.
    /// <para>
    /// <c>ListenUrl</c> reports <c>0.0.0.0</c>, which is correct and useless to read out loud, so
    /// the first real IPv4 address on an up, non-loopback interface is substituted.
    /// </para>
    /// </summary>
    string? BuildLocalUrl()
    {
        if (server.ListenUrl is not { } listen)
            return null;

        var port = new Uri(listen).Port;
        var address = LocalAddress();

        return address is null ? listen : $"http://{address}:{port}";
    }

    static IPAddress? LocalAddress()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up
                    || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                foreach (var ip in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ip.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip.Address))
                        return ip.Address;
                }
            }
        }
        catch (NetworkInformationException)
        {
            // Some platforms restrict interface enumeration; the tunnel URL still works.
        }

        return null;
    }
}
