using System.Text;
using Sample.Maui.Pages;
using Sample.Maui.Server;

namespace Sample.Maui.ViewModels;

/// <summary>
/// One request, both directions: what arrived, what went back.
/// <para>
/// <see cref="RequestId"/> carries across the navigation rather than the entry itself. That is what
/// <c>[ShellProperty]</c> is for — the source generator turns it into
/// <c>navigator.NavigateToRequestDetail(id)</c>, and an id survives a page being rebuilt in a way an
/// object graph handed between pages does not.
/// </para>
/// </summary>
[ShellMap<RequestDetailPage>("RequestDetail")]
public partial class RequestDetailViewModel(RequestLog log, IDialogs dialogs) : ObservableObject, IPageLifecycleAware
{
    [ShellProperty]
    public int RequestId { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Found))]
    [NotifyPropertyChangedFor(nameof(Missing))]
    RequestLogEntry? entry;

    public bool Found => this.Entry is not null;

    public bool Missing => this.Entry is null;

    public void OnAppearing()
        // Nothing was captured with the entry that needs releasing, so there is no OnDisappearing
        // half to this: the log holds the data and the page just reads it.
        => this.Entry = log.Find(this.RequestId);

    public void OnDisappearing()
    {
    }

    /// <summary>Puts the whole exchange on the clipboard, in the shape a bug report wants.</summary>
    [RelayCommand]
    async Task Copy()
    {
        if (this.Entry is not { } entry)
            return;

        var text = new StringBuilder()
            .AppendLine($"{entry.Method} {entry.Target} {entry.Protocol}")
            .AppendLine($"When:      {entry.LocalDateTime}")
            .AppendLine($"From:      {entry.Peer} ({entry.Origin})")
            .AppendLine($"User:      {entry.User}")
            .AppendLine($"Status:    {entry.StatusText} in {entry.Duration}");

        if (entry.Error is { Length: > 0 } error)
            text.AppendLine($"Error:     {error}");

        text.AppendLine().AppendLine("--- Request headers ---");

        foreach (var header in entry.RequestHeaders)
            text.AppendLine($"{header.Name}: {header.Value}");

        text.AppendLine().AppendLine("--- Response headers ---");

        foreach (var header in entry.ResponseHeaders)
            text.AppendLine($"{header.Name}: {header.Value}");

        await Clipboard.Default.SetTextAsync(text.ToString());
        await dialogs.Alert("Copied", "The request is on your clipboard.");
    }
}
