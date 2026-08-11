namespace Sample.Maui;

public partial class MainPage : ContentPage
{
    readonly ShareViewModel model;

    public MainPage(ShareViewModel model)
    {
        this.InitializeComponent();

        this.model = model;
        this.BindingContext = model;
    }

    /// <summary>
    /// Serving on the local network starts with the page. That is what this app is for, and it
    /// exposes nothing beyond the Wi-Fi it is already on — publishing to the internet stays behind
    /// a deliberate tap.
    /// </summary>
    protected override async void OnAppearing()
    {
        base.OnAppearing();

        // Credentials first: the server refuses everything without them, so starting before they
        // are loaded would answer a real request with a password nobody has been told.
        await this.model.LoadCredentialsAsync();
        await this.model.StartLocalAsync();
    }

    async void OnShareClicked(object? sender, EventArgs e) => await this.model.ShareAsync();

    async void OnStartLocalClicked(object? sender, EventArgs e) => await this.model.StartLocalAsync();

    async void OnStopClicked(object? sender, EventArgs e) => await this.model.StopSharingAsync();

    async void OnSaveCredentialsClicked(object? sender, EventArgs e) => await this.model.SaveCredentialsAsync();

    async void OnRegenerateClicked(object? sender, EventArgs e) => await this.model.RegeneratePasswordAsync();

    async void OnCopyClicked(object? sender, EventArgs e)
    {
        await this.model.CopyLinkAsync();
        await this.DisplayAlertAsync("Copied", "The link is on your clipboard.", "OK");
    }

    async void OnOpenClicked(object? sender, EventArgs e) => await this.model.OpenLinkAsync();
}
