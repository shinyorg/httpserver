using System.Text;

namespace Shiny.Net.HttpServer.Files;

/// <summary>
/// The <c>name</c> and <c>filename</c> out of a <c>Content-Disposition</c> header.
/// <para>
/// Handles the RFC 5987 <c>filename*</c> form as well as the plain one, because a browser uploading
/// a file whose name is not ASCII sends the extended form and only the extended form.
/// </para>
/// </summary>
public readonly struct ContentDisposition
{
    ContentDisposition(string? name, string? fileName)
    {
        this.Name = name;
        this.FileName = fileName;
    }

    /// <summary>The form field name.</summary>
    public string? Name { get; }

    /// <summary>The client's file name, if any. Never trust it as a path — see <see cref="SafeFileName"/>.</summary>
    public string? FileName { get; }

    /// <summary>
    /// The file name with any directory component removed.
    /// <para>
    /// A client is free to send <c>../../etc/passwd</c> as a filename, and joining that onto an
    /// upload directory is the classic path-traversal hole. This strips everything up to the last
    /// separator of either flavour, so a Windows client's backslashes are handled on Linux too.
    /// </para>
    /// </summary>
    public string? SafeFileName
    {
        get
        {
            if (this.FileName is not { Length: > 0 } name)
                return null;

            var cut = name.LastIndexOfAny(['/', '\\']);
            var trimmed = cut >= 0 ? name[(cut + 1)..] : name;

            // "." and ".." are not file names, whatever the client says.
            return trimmed is "" or "." or ".." ? null : trimmed;
        }
    }

    public static ContentDisposition Parse(string? header)
    {
        if (string.IsNullOrEmpty(header))
            return default;

        string? name = null;
        string? fileName = null;
        string? extendedFileName = null;

        foreach (var part in Split(header))
        {
            var equals = part.IndexOf('=');
            if (equals <= 0)
                continue;

            var key = part[..equals].Trim();
            var value = Unquote(part[(equals + 1)..].Trim());

            if (key.Equals("name", StringComparison.OrdinalIgnoreCase))
                name = value;
            else if (key.Equals("filename", StringComparison.OrdinalIgnoreCase))
                fileName = value;
            else if (key.Equals("filename*", StringComparison.OrdinalIgnoreCase))
                extendedFileName = DecodeExtended(value);
        }

        // filename* wins when both are present: the plain one is the deliberately lossy fallback.
        return new ContentDisposition(name, extendedFileName ?? fileName);
    }

    /// <summary>Splits on semicolons that are not inside a quoted string.</summary>
    static List<string> Split(string header)
    {
        var parts = new List<string>();
        var start = 0;
        var quoted = false;

        for (var i = 0; i < header.Length; i++)
        {
            switch (header[i])
            {
                case '"':
                    quoted = !quoted;
                    break;

                case ';' when !quoted:
                    parts.Add(header[start..i]);
                    start = i + 1;
                    break;
            }
        }

        parts.Add(header[start..]);
        return parts;
    }

    static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            return value[1..^1].Replace("\\\"", "\"");

        return value;
    }

    /// <summary>Decodes <c>UTF-8''na%C3%AFve.txt</c> into <c>naïve.txt</c>.</summary>
    static string? DecodeExtended(string value)
    {
        var firstQuote = value.IndexOf('\'');
        if (firstQuote < 0)
            return null;

        var secondQuote = value.IndexOf('\'', firstQuote + 1);
        if (secondQuote < 0)
            return null;

        var charset = value[..firstQuote];
        var encoded = value[(secondQuote + 1)..];

        var encoding = charset.Equals("UTF-8", StringComparison.OrdinalIgnoreCase)
            ? Encoding.UTF8
            : charset.Equals("ISO-8859-1", StringComparison.OrdinalIgnoreCase)
                ? Encoding.Latin1
                : null;

        if (encoding is null)
            return null;

        var bytes = new List<byte>(encoded.Length);

        for (var i = 0; i < encoded.Length; i++)
        {
            if (encoded[i] == '%' && i + 2 < encoded.Length &&
                byte.TryParse(encoded.AsSpan(i + 1, 2), System.Globalization.NumberStyles.HexNumber, null, out var decoded))
            {
                bytes.Add(decoded);
                i += 2;
                continue;
            }

            bytes.Add((byte)encoded[i]);
        }

        return encoding.GetString([.. bytes]);
    }

    /// <summary>
    /// Builds a <c>Content-Disposition</c> value for a download, emitting both the plain and
    /// extended file-name forms so old and new clients each get one they understand.
    /// </summary>
    public static string ForDownload(string fileName, bool inline = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileName);

        var disposition = inline ? "inline" : "attachment";
        var ascii = new StringBuilder(fileName.Length);

        foreach (var c in fileName)
            ascii.Append(c is >= ' ' and < (char)127 && c != '"' && c != '\\' ? c : '_');

        var encoded = Uri.EscapeDataString(fileName);

        return $"{disposition}; filename=\"{ascii}\"; filename*=UTF-8''{encoded}";
    }
}
