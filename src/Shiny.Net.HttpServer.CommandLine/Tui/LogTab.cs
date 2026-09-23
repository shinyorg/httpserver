using System.Globalization;
using Microsoft.Extensions.Logging;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;

namespace Shiny.Net.HttpServer.CommandLine.Tui;


/// <summary>The server's own log - what the console would have printed - plus the dashboard's notes.</summary>
sealed class LogTab(Dashboard dashboard)
{
    readonly LogControl log = new() { MaxCapacity = 5000, FollowTail = true };
    Visual? view;

    /// <summary>Where the cursor goes when the tab is opened from the keyboard - so the log scrolls and searches.</summary>
    public Visual FocusTarget => this.log;

    public Visual View => this.view ??= new Padder(new VStack(
        Ui.Toolbar(
            Ui.Action("Clear", () => this.log.Clear()),
            Ui.Text("[dim]Warnings and errors; turn on Verbose in Settings for everything.[/]")
        ),
        this.log.Stretch()
    ).Spacing(1)).Padding(new Thickness(1, 1, 1, 0));


    /// <summary>Moves queued lines onto the screen. Render thread.</summary>
    public void Tick()
    {
        foreach (var line in dashboard.Log.Drain())
        {
            var level = line.Level switch
            {
                LogLevel.Critical or LogLevel.Error => "[red]fail[/]",
                LogLevel.Warning => "[yellow]warn[/]",
                LogLevel.Information => "[green]info[/]",
                _ => "[dim]dbug[/]"
            };

            var markup = $"[dim]{line.At.ToString("HH:mm:ss", CultureInfo.CurrentCulture)}[/] {level} [dim]{Ui.Escape(ShortCategory(line.Category))}[/] {Ui.Escape(line.Message)}";
            this.log.AppendMarkupLine(markup);
        }
    }


    /// <summary>The last part of a logger's type name - the namespace is the same on every line.</summary>
    static string ShortCategory(string category)
    {
        var dot = category.LastIndexOf('.');
        return dot >= 0 ? category[(dot + 1)..] : category;
    }
}
