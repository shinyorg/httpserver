using Shiny.Net.HttpServer.CommandLine.Monitoring;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;

namespace Shiny.Net.HttpServer.CommandLine.Tui;


/// <summary>
/// Uploads and downloads in flight, each with how far along it is and how fast it is going, and the
/// last few that finished.
/// </summary>
/// <remarks>
/// A transfer is anything moving a file's worth of body: an upload of any size, or a download that
/// is a file rather than the manager's own page. A request that has been running longer than a
/// second is shown too, whatever it is, because something that slow is worth seeing.
/// </remarks>
sealed class TransfersTab(Dashboard dashboard)
{
    const int RecentCount = 15;
    const int BarWidth = 24;
    static readonly TimeSpan SlowRequest = TimeSpan.FromSeconds(1);

    /// <summary>Last sample per transfer, for the speed column - bytes, when, and the smoothed rate.</summary>
    readonly Dictionary<long, (long Bytes, long At, double Rate)> samples = new();
    readonly State<int> revision = new(0);

    IReadOnlyList<TrafficEntry> active = [];
    IReadOnlyList<TrafficEntry> recent = [];
    long seenVersion = -1;
    Visual? view;

    public Visual View => this.view ??= new ScrollViewer(
        new ComputedVisual(() =>
        {
            _ = this.revision.Value;
            return this.Build();
        })
    );


    /// <summary>
    /// Re-reads the monitor. Anything in flight changes every tick - its byte count moves - so the
    /// view is rebuilt whenever something is running, and otherwise only when the log has moved.
    /// </summary>
    public void Tick()
    {
        var monitor = dashboard.Monitor;
        var version = monitor.Version;
        var running = monitor.ActiveCount > 0;

        if (!running && version == this.seenVersion)
            return;

        this.seenVersion = version;
        this.active = monitor.GetActive().Where(IsTransfer).ToArray();
        this.recent = monitor.GetCompleted().Where(x => x.IsUpload || x.IsDownload).Take(RecentCount).ToArray();

        this.Sample();
        this.revision.Value = this.revision.Value + 1;
    }


    static bool IsTransfer(TrafficEntry entry)
        => entry.IsUpload || entry.IsDownload || entry.Elapsed >= SlowRequest;


    /// <summary>
    /// Speed as an exponential average over the ticks. A raw per-tick figure swings wildly on a
    /// network that delivers in bursts, and a number that will not sit still cannot be read.
    /// </summary>
    void Sample()
    {
        var now = Environment.TickCount64;
        var live = new HashSet<long>();

        foreach (var entry in this.active)
        {
            live.Add(entry.Id);
            var bytes = entry.TransferredBytes;

            if (this.samples.TryGetValue(entry.Id, out var last) && now > last.At)
            {
                var instant = (bytes - last.Bytes) * 1000d / (now - last.At);
                var rate = last.Rate <= 0 ? instant : last.Rate * 0.7 + instant * 0.3;
                this.samples[entry.Id] = (bytes, now, rate);
            }
            else
            {
                this.samples[entry.Id] = (bytes, now, 0);
            }
        }

        foreach (var id in this.samples.Keys.Where(x => !live.Contains(x)).ToArray())
            this.samples.Remove(id);
    }


    Visual Build()
    {
        var stack = new VStack().Spacing(1);

        stack.Add(Ui.Text(this.active.Count == 0
            ? "[bold]In progress[/]"
            : $"[bold]In progress[/] [dim]({this.active.Count})[/]"));

        if (this.active.Count == 0)
        {
            stack.Add(Ui.Muted("Nothing moving. Uploads and downloads show up here while they run."));
        }
        else
        {
            var rows = new VStack();
            foreach (var entry in this.active)
                rows.Add(this.ActiveRow(entry));

            stack.Add(rows);
        }

        stack.Add(new Rule());
        stack.Add(Ui.Text("[bold]Recently finished[/]"));

        if (this.recent.Count == 0)
        {
            stack.Add(Ui.Muted("None yet."));
        }
        else
        {
            var rows = new VStack();
            foreach (var entry in this.recent)
                rows.Add(RecentRow(entry));

            stack.Add(rows);
        }

        return new Padder(stack).Padding(new Thickness(1));
    }


    Visual ActiveRow(TrafficEntry entry)
    {
        var rate = this.samples.TryGetValue(entry.Id, out var sample) ? sample.Rate : 0;
        var done = entry.TransferredBytes;
        var expected = entry.ExpectedBytes;

        var amount = expected is > 0 ? $"{Ui.Bytes(done)} / {Ui.Bytes(expected.Value)}" : Ui.Bytes(done);

        // Time left only means something with both a size and a speed; a guess from either alone is noise.
        var remaining = expected is > 0 && rate > 0 && done < expected
            ? $" · {Ui.Duration(TimeSpan.FromSeconds((expected.Value - done) / rate))} left"
            : "";

        Visual bar = entry.Progress is { } progress
            ? new HStack(
                new ProgressBar(progress).MinWidth(BarWidth).MaxWidth(BarWidth),
                Ui.Text($"{progress * 100,3:0}%")
            ).Spacing(1)
            : Ui.Text("[dim]size unknown[/]").MinWidth(BarWidth + 5);

        return new HStack(
            Ui.Text(Arrow(entry)).MinWidth(2),
            Ui.Text($"{Ui.Escape(entry.Method)} {Ui.Escape(Ui.Cell(entry.Target, 80))}").MinWidth(30).Stretch(),
            bar,
            Ui.Text($"{amount} · {(rate > 0 ? Ui.Rate(rate) : "[dim]…[/]")}{remaining} · [dim]{Ui.Escape(RequestsTab.Client(entry))}[/]")
        ).Spacing(1);
    }


    static Visual RecentRow(TrafficEntry entry)
    {
        var bytes = entry.IsUpload ? entry.BytesIn : entry.BytesOut;
        var seconds = entry.Elapsed.TotalSeconds;
        var average = seconds > 0.05 ? $" · {Ui.Rate(bytes / seconds)}" : "";

        return new HStack(
            Ui.Text(Arrow(entry)).MinWidth(2),
            Ui.Text(Ui.Status(entry.StatusCode)).MinWidth(4),
            Ui.Text($"{Ui.Escape(entry.Method)} {Ui.Escape(Ui.Cell(entry.Target, 80))}").MinWidth(30).Stretch(),
            Ui.Text($"{Ui.Bytes(bytes)} in {Ui.Duration(entry.Elapsed)}{average} · [dim]{Ui.Escape(RequestsTab.Client(entry))}[/]")
        ).Spacing(1);
    }


    static string Arrow(TrafficEntry entry)
        => entry.IsUpload ? "[magenta]↑[/]" : entry.IsDownload ? "[cyan]↓[/]" : "[dim]·[/]";
}
