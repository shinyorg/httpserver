using Sample.Maui.Pages;
using Sample.Maui.Server;

namespace Sample.Maui.ViewModels;

/// <summary>
/// The Access tab: the one account this device accepts.
/// <para>
/// Edits take effect on the very next request. Nothing restarts, because the validator is a service
/// that reads the current value rather than a list of credentials handed over at startup.
/// </para>
/// </summary>
[ShellMap<AccessPage>("Access", registerRoute: false)]
public partial class AccessViewModel(CredentialStore credentials) : ObservableObject, IPageLifecycleAware
{
    [ObservableProperty]
    string username = string.Empty;

    [ObservableProperty]
    string password = string.Empty;

    /// <summary>Where the password ended up, which is not always where it was meant to.</summary>
    [ObservableProperty]
    string storageStatus = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    string? lastError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotBusy))]
    bool busy;

    public bool HasError => this.LastError is { Length: > 0 };

    public bool NotBusy => !this.Busy;

    public void OnAppearing() => _ = this.LoadAsync();

    public void OnDisappearing()
    {
    }

    [RelayCommand]
    async Task Save()
    {
        if (string.IsNullOrWhiteSpace(this.Username) || string.IsNullOrWhiteSpace(this.Password))
        {
            this.LastError = "A username and password are both required.";
            return;
        }

        this.Busy = true;

        try
        {
            await credentials.SetAsync(this.Username, this.Password);

            this.LastError = null;
            this.StorageStatus = credentials.IsStoredSecurely
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
    [RelayCommand]
    async Task Regenerate()
    {
        this.Busy = true;

        try
        {
            await credentials.RegenerateAsync();

            this.Password = credentials.Password;
            this.StorageStatus = "New password generated. Anyone using the old one is now locked out.";
        }
        finally
        {
            this.Busy = false;
        }
    }

    /// <summary>Reads the saved account, generating one on first run.</summary>
    async Task LoadAsync()
    {
        await credentials.LoadAsync();

        this.Username = credentials.Username;
        this.Password = credentials.Password;

        this.StorageStatus = credentials.IsStoredSecurely
            ? "Stored in the device keychain."
            : "The keychain was unavailable, so this is stored unencrypted.";
    }
}
