using Sample.Maui.Pages;
using Sample.Maui.Server;

namespace Sample.Maui.ViewModels;

/// <summary>
/// The Traffic tab: every request the server has answered, newest first.
/// <para>
/// The list is filled from <see cref="RequestLog.Snapshot"/> when the tab appears and kept up to
/// date by its event while the tab is on screen. Off screen it unsubscribes — a phone answering a
/// file browser listing does not need to be rebuilding a list nobody is looking at.
/// </para>
/// </summary>
[ShellMap<TrafficPage>("Traffic", registerRoute: false)]
public partial class TrafficViewModel(
    RequestLog log,
    TrafficBadge badge,
    INavigator navigator,
    IDialogs dialogs,
    IMainThread mainThread
) : ObservableObject, IPageLifecycleAware
{
    /// <summary>How many entries the list holds. The log keeps more; a list view shows less.</summary>
    const int Shown = 100;

    [ObservableProperty]
    int total;

    public ObservableCollection<RequestLogEntry> Requests { get; } = [];

    public bool IsEmpty => this.Requests.Count == 0;

    public void OnAppearing()
    {
        log.Added += this.OnRequest;
        log.Cleared += this.OnCleared;

        this.Reload();

        // Looking at the tab is what "seen" means.
        badge.Clear();
    }

    public void OnDisappearing()
    {
        log.Added -= this.OnRequest;
        log.Cleared -= this.OnCleared;
    }

    /// <summary>Opens one request. The id is the parameter; the entry itself stays in the log.</summary>
    [RelayCommand]
    Task Open(RequestLogEntry entry) => navigator.NavigateToRequestDetail(entry.Id);

    [RelayCommand]
    async Task Clear()
    {
        if (await dialogs.Confirm("Clear traffic?", "This only clears the list on this screen."))
            log.Clear();
    }

    void Reload()
    {
        this.Requests.Clear();

        foreach (var entry in log.Snapshot().Take(Shown))
            this.Requests.Add(entry);

        this.Total = log.Total;
        this.OnPropertyChanged(nameof(this.IsEmpty));
    }

    void OnRequest(object? sender, RequestLogEntry entry) => mainThread.BeginInvokeOnMainThread(() =>
    {
        this.Requests.Insert(0, entry);

        while (this.Requests.Count > Shown)
            this.Requests.RemoveAt(this.Requests.Count - 1);

        this.Total = log.Total;
        this.OnPropertyChanged(nameof(this.IsEmpty));

        // The tab is on screen, so nothing here is unseen.
        badge.Clear();
    });

    void OnCleared(object? sender, EventArgs e) => mainThread.BeginInvokeOnMainThread(this.Reload);
}
