using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Sample.Maui.Server;
using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.Ssh;

namespace Sample.Maui;

/// <summary>
/// What the screen shows.
/// <para>
/// The only interesting part is the threading: <see cref="QuickTunnel"/> raises its changes from the
/// background — a reconnect happens whenever the network does — and MAUI will not marshal that for
/// you. Every hop back to the UI thread here is for that reason.
/// </para>
/// </summary>
public sealed class ShareViewModel : INotifyPropertyChanged
{
    readonly HttpServer server;
    readonly QuickTunnel tunnel;
    readonly NoteStore notes;
    readonly RequestCounter counter;
    readonly CredentialStore credentials;

    string status = "Stopped";
    string? publicUrl;
    string? localUrl;
    string? lastError;
    string lastNote = "—";
    string usernameInput = string.Empty;
    string passwordInput = string.Empty;
    string credentialStatus = string.Empty;
    int requestCount;
    bool busy;

    public ShareViewModel(
        HttpServer server,
        QuickTunnel tunnel,
        NoteStore notes,
        RequestCounter counter,
        CredentialStore credentials
    )
    {
        this.server = server;
        this.tunnel = tunnel;
        this.notes = notes;
        this.counter = counter;
        this.credentials = credentials;

        this.tunnel.PropertyChanged += (_, _) => this.OnMainThread(() =>
        {
            this.PublicUrl = this.tunnel.PublicUrl;
            this.Status = Describe(this.tunnel.State);
            this.LastError = this.tunnel.LastError;
        });

        this.counter.Counted += (_, total) => this.OnMainThread(() => this.RequestCount = total);
        this.notes.Changed += (_, note) => this.OnMainThread(() => this.LastNote = note.Text);
    }

    public string Status
    {
        get => this.status;
        private set => this.Set(ref this.status, value);
    }

    /// <summary>
    /// The address to hand a customer. Null until the tunnel is up, and it changes on every
    /// reconnect — which is exactly why the view binds to it instead of reading it once.
    /// </summary>
    public string? PublicUrl
    {
        get => this.publicUrl;
        private set
        {
            if (this.Set(ref this.publicUrl, value))
            {
                this.OnPropertyChanged(nameof(this.HasPublicUrl));
                this.OnPropertyChanged(nameof(this.McpUrl));
            }
        }
    }

    public bool HasPublicUrl => this.PublicUrl is { Length: > 0 };

    /// <summary>The address on this Wi-Fi network. Works with no internet at all.</summary>
    public string? LocalUrl
    {
        get => this.localUrl;
        private set
        {
            if (this.Set(ref this.localUrl, value))
                this.OnPropertyChanged(nameof(this.McpUrl));
        }
    }

    /// <summary>
    /// What to paste into an MCP client. The public address when the tunnel is up, because that is
    /// the one that works from wherever the client happens to be running; otherwise the LAN address.
    /// </summary>
    public string? McpUrl => (this.PublicUrl ?? this.LocalUrl) is { Length: > 0 } url
        ? $"{url.TrimEnd('/')}/mcp"
        : null;

    public string? LastError
    {
        get => this.lastError;
        private set
        {
            if (this.Set(ref this.lastError, value))
                this.OnPropertyChanged(nameof(this.HasError));
        }
    }

    public bool HasError => this.LastError is { Length: > 0 };

    public int RequestCount
    {
        get => this.requestCount;
        private set => this.Set(ref this.requestCount, value);
    }

    /// <summary>The last note posted, so traffic from the browser is visible in the app.</summary>
    public string LastNote
    {
        get => this.lastNote;
        private set => this.Set(ref this.lastNote, value);
    }

    public bool Busy
    {
        get => this.busy;
        private set
        {
            if (this.Set(ref this.busy, value))
                this.OnPropertyChanged(nameof(this.NotBusy));
        }
    }

    public bool NotBusy => !this.Busy;

    // ---- The account visitors have to present ----

    /// <summary>Edited in the settings box. Nothing changes until Save.</summary>
    public string UsernameInput
    {
        get => this.usernameInput;
        set => this.Set(ref this.usernameInput, value);
    }

    public string PasswordInput
    {
        get => this.passwordInput;
        set => this.Set(ref this.passwordInput, value);
    }

    /// <summary>Where the password ended up, which is not always where it was meant to.</summary>
    public string CredentialStatus
    {
        get => this.credentialStatus;
        private set => this.Set(ref this.credentialStatus, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Reads the saved account, generating one on first run.</summary>
    public async Task LoadCredentialsAsync()
    {
        await this.credentials.LoadAsync();

        this.UsernameInput = this.credentials.Username;
        this.PasswordInput = this.credentials.Password;

        this.CredentialStatus = this.credentials.IsStoredSecurely
            ? "Stored in the device keychain."
            : "The keychain was unavailable, so this is stored unencrypted.";
    }

    /// <summary>
    /// Applies the edited account. It takes effect on the very next request — the validator reads
    /// the current value rather than a copy taken at startup, so nothing has to restart.
    /// </summary>
    public async Task SaveCredentialsAsync()
    {
        if (string.IsNullOrWhiteSpace(this.UsernameInput) || string.IsNullOrWhiteSpace(this.PasswordInput))
        {
            this.LastError = "A username and password are both required.";
            return;
        }

        this.Busy = true;

        try
        {
            await this.credentials.SetAsync(this.UsernameInput, this.PasswordInput);

            this.LastError = null;
            this.CredentialStatus = this.credentials.IsStoredSecurely
                ? "Saved to the device keychain."
                : "Saved, but the keychain was unavailable so this is stored unencrypted.";
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

    /// <summary>Replaces the password with a fresh random one, which also revokes the old link.</summary>
    public async Task RegeneratePasswordAsync()
    {
        this.Busy = true;

        try
        {
            await this.credentials.RegenerateAsync();

            this.PasswordInput = this.credentials.Password;
            this.CredentialStatus = "New password generated. Anyone using the old one is now locked out.";
        }
        finally
        {
            this.Busy = false;
        }
    }

    /// <summary>Starts the server on this network. No internet involved.</summary>
    public async Task StartLocalAsync()
    {
        if (this.server.IsRunning)
            return;

        this.Busy = true;

        try
        {
            await this.server.StartAsync();
            this.LocalUrl = this.BuildLocalUrl();
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

    /// <summary>Opens the public tunnel, so the link works from anywhere.</summary>
    public async Task ShareAsync()
    {
        this.Busy = true;
        this.LastError = null;

        try
        {
            await this.StartLocalAsync();
            await this.tunnel.StartAsync();
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

    /// <summary>Closes the tunnel. The server keeps serving on this network.</summary>
    public async Task StopSharingAsync()
    {
        this.Busy = true;

        try
        {
            await this.tunnel.StopAsync();
        }
        finally
        {
            this.Busy = false;
        }
    }

    public async Task CopyLinkAsync()
    {
        if (this.PublicUrl is { Length: > 0 } url)
            await Clipboard.Default.SetTextAsync(url);
    }

    public async Task OpenLinkAsync()
    {
        if (this.PublicUrl is { Length: > 0 } url)
            await Browser.Default.OpenAsync(url, BrowserLaunchMode.SystemPreferred);
    }

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
        if (this.server.ListenUrl is not { } listen)
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

    void OnMainThread(Action action)
    {
        if (MainThread.IsMainThread)
            action();
        else
            MainThread.BeginInvokeOnMainThread(action);
    }

    bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        this.OnPropertyChanged(propertyName);

        return true;
    }

    void OnPropertyChanged(string? propertyName)
        => this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
