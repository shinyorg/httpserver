using System.Globalization;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;

namespace Shiny.Net.HttpServer.CommandLine.Tui;


/// <summary>The handful of shapes and formatters every tab is written in.</summary>
static class Ui
{
    /// <summary>
    /// Escapes text for markup. Paths, headers and error text are all things a client chose, and a
    /// bracket in any of them would otherwise be read as a style.
    /// </summary>
    public static string Escape(string? text) => (text ?? "").Replace("[", "[[").Replace("]", "]]");

    /// <summary>
    /// A markup visual from a finished string. Taking <see cref="string"/> - not the interpolated
    /// handler <see cref="Markup"/> also offers - means the escaping in this file is the only escaping,
    /// done once, where it can be seen.
    /// </summary>
    public static Markup Text(string markup) => new(markup);

    /// <summary>A markup visual that recomputes whenever a state it reads changes.</summary>
    public static Markup Live(Func<string> markup) => new(markup);

    public static Visual Muted(string text) => Text($"[dim]{Escape(text)}[/]").Wrap(true);

    public static Visual Warning(string text) => Text($"[yellow]! [/]{Escape(text)}").Wrap(true);

    /// <summary>Nothing, and no row spent on it - an empty text block still measures a line.</summary>
    public static Visual Nothing() => new VStack();

    public static Button Action(string text, Action onClick) => new Button(new TextBlock(text)).Click(onClick);

    public static Button Primary(string text, Action onClick)
        => new Button(new TextBlock(text)).Tone(XenoAtom.Terminal.UI.Styling.ControlTone.Primary).Click(onClick);

    public static Button Danger(string text, Action onClick)
        => new Button(new TextBlock(text)).Tone(XenoAtom.Terminal.UI.Styling.ControlTone.Error).Click(onClick);

    public static WrapHStack Toolbar(params Visual[] items) => new WrapHStack().Children(items).Spacing(1);

    public static Group Section(string title, Visual content)
        => new Group(Text($"[bold]{Escape(title)}[/]"), content)
            .Padding(new Thickness(1, 0, 1, 0))
            .HorizontalAlignment(Align.Stretch);

    /// <summary>A label column and a value column. Values are markup, already escaped by the caller.</summary>
    public static Visual Facts(IEnumerable<(string Label, string ValueMarkup)> rows)
    {
        var stack = new VStack();
        foreach (var (label, value) in rows)
        {
            stack.Add(new HStack(
                Text($"[dim]{Escape(label)}[/]").MinWidth(12),
                Text(value).Wrap(true)
            ).Spacing(1));
        }
        return stack;
    }


    // ---- formatting ----

    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} B"
            : String.Create(CultureInfo.CurrentCulture, $"{size:0.#} {units[unit]}");
    }

    public static string Rate(double bytesPerSecond) => bytesPerSecond <= 0 ? "" : Bytes((long)bytesPerSecond) + "/s";

    public static string Duration(TimeSpan value) => value switch
    {
        { TotalMilliseconds: < 1000 } => String.Create(CultureInfo.CurrentCulture, $"{value.TotalMilliseconds:0} ms"),
        { TotalSeconds: < 60 } => String.Create(CultureInfo.CurrentCulture, $"{value.TotalSeconds:0.0} s"),
        { TotalHours: < 1 } => $"{(int)value.TotalMinutes}:{value.Seconds:00}",
        _ => $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
    };

    /// <summary>A status code in the colour its class deserves: fine, moved, refused, broken.</summary>
    public static string Status(int code) => code switch
    {
        0 => "[dim]…[/]",
        < 300 => $"[green]{code}[/]",
        < 400 => $"[cyan]{code}[/]",
        < 500 => $"[yellow]{code}[/]",
        _ => $"[red]{code}[/]"
    };

    /// <summary>One line of text for a grid cell - newlines flattened, long values cut with an ellipsis.</summary>
    public static string Cell(string? text, int max = 200)
    {
        if (String.IsNullOrEmpty(text))
            return "";

        var flat = text.ReplaceLineEndings(" ");
        return flat.Length <= max ? flat : flat[..(max - 1)] + "…";
    }
}
