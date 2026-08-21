using System.Text;

namespace Shiny.Net.HttpServer.CommandLine;


/// <summary>
/// Draws a <see cref="QrCode"/> into the terminal. Two module rows share a text row through the
/// half-block glyphs, which is what keeps a code narrow enough to scan off an ordinary window.
/// </summary>
static class QrConsole
{
    /// <summary>The light border a reader needs to find the code at all. The standard says four modules.</summary>
    const int QuietZone = 4;


    /// <summary>Text columns a rendered code occupies, quiet zone included.</summary>
    public static int Width(QrCode code) => code.Size + QuietZone * 2;


    public static IReadOnlyList<string> Render(QrCode code)
    {
        var width = Width(code);
        var lines = new List<string>();
        var builder = new StringBuilder(width);

        for (var y = 0; y < width; y += 2)
        {
            builder.Clear();

            for (var x = 0; x < width; x++)
            {
                var top = IsDark(code, x, y);
                var bottom = IsDark(code, x, y + 1);

                builder.Append((top, bottom) switch
                {
                    (true, true) => '█',   // full block
                    (true, false) => '▀',  // upper half
                    (false, true) => '▄',  // lower half
                    _ => ' '
                });
            }
            lines.Add(builder.ToString());
        }
        return lines;
    }


    /// <summary>
    /// Written black on white rather than in whatever the terminal happens to use: a reader wants
    /// dark modules on a light field, and a dark themed terminal would otherwise hand it the
    /// negative of the code.
    /// </summary>
    public static void Write(QrCode code, string indent)
    {
        foreach (var line in Render(code))
        {
            Console.Write(indent);
            Console.ForegroundColor = ConsoleColor.Black;
            Console.BackgroundColor = ConsoleColor.White;
            Console.Write(line);
            Console.ResetColor();
            Console.WriteLine();
        }
    }


    /// <summary>Anything outside the code itself is quiet zone, and a code of odd height gets a light last row.</summary>
    static bool IsDark(QrCode code, int x, int y)
    {
        var column = x - QuietZone;
        var row = y - QuietZone;

        if (column < 0 || row < 0 || column >= code.Size || row >= code.Size)
            return false;

        return code[column, row];
    }
}
