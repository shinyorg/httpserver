using System.Globalization;
using System.Text;
using Shiny.Net.HttpServer.CommandLine.Monitoring;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.DataGrid;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Input;

namespace Shiny.Net.HttpServer.CommandLine.Tui;


/// <summary>One finished exchange, flattened to text for the grid.</summary>
/// <remarks>
/// Named around C# keywords and <c>method</c>: the binding generator escapes those in the
/// parameters it emits and then documents them unescaped, which is a warning per property.
/// </remarks>
public sealed partial class RequestRow
{
    [Bindable] public partial string Time { get; set; }
    [Bindable] public partial string Verb { get; set; }
    [Bindable] public partial string Path { get; set; }
    [Bindable] public partial string Status { get; set; }
    [Bindable] public partial string Received { get; set; }
    [Bindable] public partial string Sent { get; set; }
    [Bindable] public partial string Duration { get; set; }
    [Bindable] public partial string Client { get; set; }
    [Bindable] public partial string User { get; set; }

    public required TrafficEntry Entry { get; init; }
}


/// <summary>
/// Every request the server has answered, newest first, with Enter for the headers of one of them.
/// </summary>
sealed class RequestsTab
{
    readonly Dashboard dashboard;
    readonly DataGridListDocument<RequestRow> document = new();
    readonly DataGridDocumentView gridView;
    readonly DataGridControl grid;
    readonly State<int> count = new(0);

    long seenVersion = -1;
    Visual? view;

    public RequestsTab(Dashboard dashboard)
    {
        this.dashboard = dashboard;

        (BindingAccessor<string> Accessor, string Header, GridLength Width)[] columns =
        [
            (RequestRow.Accessor.Time, "Time", GridLength.Auto),
            (RequestRow.Accessor.Verb, "Method", GridLength.Auto),
            (RequestRow.Accessor.Path, "Path", GridLength.Star(1)),
            (RequestRow.Accessor.Status, "Status", GridLength.Auto),
            (RequestRow.Accessor.Received, "In", GridLength.Auto),
            (RequestRow.Accessor.Sent, "Out", GridLength.Auto),
            (RequestRow.Accessor.Duration, "Time taken", GridLength.Auto),
            (RequestRow.Accessor.Client, "Client", GridLength.Auto),
            (RequestRow.Accessor.User, "User", GridLength.Auto)
        ];

        foreach (var (accessor, _, _) in columns)
            this.document.AddColumn(accessor);

        this.gridView = new DataGridDocumentView(this.document);
        this.grid = new DataGridControl
        {
            View = this.gridView,
            SelectionMode = DataGridSelectionMode.Row,
            ReadOnly = true,
            ShowRowAnchor = false
        }.Stretch();

        foreach (var (accessor, header, width) in columns)
        {
            this.grid.Columns.Add(new DataGridColumn<string>
            {
                Key = accessor.Name,
                TypedValueAccessor = accessor,
                Header = Ui.Text($"[bold]{Ui.Escape(header)}[/]"),
                Width = width,
                Sortable = true,
                ReadOnly = true
            });
        }

        this.grid.AddKeyBinding(new KeyGesture(TerminalKey.Enter), () =>
        {
            if (this.Selected is { } row)
                this.ShowDetail(row.Entry);
        });
        this.grid.AddKeyBinding(new KeyGesture(TerminalChar.CtrlF, TerminalModifiers.Ctrl), this.grid.OpenSearch);
    }

    public Visual View => this.view ??= new Padder(new VStack(
        Ui.Toolbar(
            Ui.Action("Clear", this.Clear),
            Ui.Live(() => $"[dim]{this.count.Value:N0} shown · newest first · Enter for headers · Ctrl+F to search[/]")
        ),
        new ComputedVisual(() => this.count.Value == 0
            ? (Visual)new Center(Ui.Muted("Nothing yet. Open one of the addresses on the Overview tab."))
            : this.grid
        ).Stretch()
    ).Spacing(1)).Padding(new Thickness(1, 1, 1, 0));


    /// <summary>Where the cursor goes when the tab is opened from the keyboard.</summary>
    public Visual FocusTarget => this.grid;


    RequestRow? Selected
    {
        get
        {
            // The cursor, not SelectedRow: that one follows the row anchor, which this grid does not show.
            // A view index, resolved through the view, so it stays right after a sort or a search.
            var snapshot = this.gridView.CurrentSnapshot;
            var index = this.grid.CurrentCell.Row;
            return index >= 0 && index < snapshot.RowCount ? snapshot.GetRowModel(index) as RequestRow : null;
        }
    }


    public void Clear()
    {
        this.dashboard.Monitor.Clear();
        this.Tick();
    }


    /// <summary>Rebuilds the grid when the log has moved since the last look. Render thread.</summary>
    public void Tick()
    {
        var version = this.dashboard.Monitor.Version;
        if (version == this.seenVersion)
            return;

        this.seenVersion = version;
        var entries = this.dashboard.Monitor.GetCompleted();

        using (this.document.BeginUpdate())
        {
            if (this.document.Rows.Count > 0)
                this.document.RemoveRows(0, this.document.Rows.Count);

            foreach (var entry in entries)
                this.document.AddRow(ToRow(entry));
        }
        this.count.Value = entries.Count;

        // A grid with rows but no cursor answers Enter with nothing, which reads as broken.
        if (entries.Count > 0 && this.grid.CurrentCell.Row < 0)
            this.grid.CurrentCell = new DataGridCell(0, 0);
    }


    static RequestRow ToRow(TrafficEntry entry) => new()
    {
        Time = entry.StartedAt.ToString("HH:mm:ss", CultureInfo.CurrentCulture),
        Verb = entry.Method,
        Path = Ui.Cell(entry.Target),
        Status = entry.Error is null ? entry.StatusCode.ToString(CultureInfo.InvariantCulture) : $"{entry.StatusCode} !",
        Received = entry.BytesIn > 0 ? Ui.Bytes(entry.BytesIn) : "",
        Sent = entry.BytesOut > 0 ? Ui.Bytes(entry.BytesOut) : "",
        Duration = Ui.Duration(entry.Elapsed),
        Client = Client(entry),
        User = entry.User ?? "",
        Entry = entry
    };


    internal static string Client(TrafficEntry entry)
        => entry.IsTunneled
            ? entry.RemoteAddress.Length > 0 ? $"tunnel {entry.RemoteAddress}" : "tunnel"
            : entry.RemoteAddress;


    void ShowDetail(TrafficEntry entry)
    {
        var text = new StringBuilder();
        text.AppendLine($"[bold]{Ui.Escape(entry.Method)} {Ui.Escape(entry.Target)}[/] [dim]{Ui.Escape(entry.Protocol)}[/]");
        text.AppendLine(
            $"{Ui.Status(entry.StatusCode)} · {Ui.Duration(entry.Elapsed)} · [dim]in[/] {Ui.Bytes(entry.BytesIn)} · [dim]out[/] {Ui.Bytes(entry.BytesOut)}"
        );
        text.AppendLine(
            $"[dim]from[/] {Ui.Escape(Client(entry))}{(entry.IsEncrypted ? " · TLS" : "")}{(entry.User is { } user ? $" · [dim]as[/] {Ui.Escape(user)}" : "")}"
            + $" · [dim]at[/] {entry.StartedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture)}"
        );

        if (entry.Error is { } error)
            text.AppendLine().AppendLine($"[red]{Ui.Escape(error)}[/]");

        AppendHeaders(text, "Request headers", entry.RequestHeaders);
        AppendHeaders(text, "Response headers", entry.ResponseHeaders);

        var body = new ScrollViewer(Ui.Text(text.ToString().TrimEnd()).Wrap(true))
            .MinWidth(72)
            .MaxWidth(120)
            .MinHeight(10)
            .MaxHeight(30);

        this.dashboard.OpenDialog(
            $"{entry.Method} {Ui.Cell(entry.Target, 60)}",
            body,
            dialog => [Ui.Action("Close", () => this.dashboard.CloseDialog(dialog)).AutoFocus(true)]
        );
    }


    static void AppendHeaders(StringBuilder text, string title, IReadOnlyList<KeyValuePair<string, string>> headers)
    {
        text.AppendLine().AppendLine($"[bold]{title}[/]");

        if (headers.Count == 0)
            text.AppendLine("  [dim]none[/]");

        foreach (var (name, value) in headers)
        {
            // Credentials are the one header that should never be on a screen someone else can see.
            var shown = name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) || name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)
                ? MaskCredentials(value)
                : value;

            text.AppendLine($"  [cyan]{Ui.Escape(name)}[/]: {Ui.Escape(shown)}");
        }
    }


    /// <summary>Keeps the scheme - which says what kind of login it was - and hides the secret.</summary>
    static string MaskCredentials(string value)
    {
        var space = value.IndexOf(' ');
        return space > 0 ? value[..space] + " ••••••" : "••••••";
    }
}
