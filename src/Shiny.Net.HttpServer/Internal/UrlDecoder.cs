using System.Buffers;
using System.Text;

namespace Shiny.Net.HttpServer;

/// <summary>Percent-decoding for request targets and form/query components.</summary>
static class UrlDecoder
{
    /// <summary>
    /// Decodes a query-string or form component: percent escapes plus '+' meaning space.
    /// </summary>
    public static string DecodeFormComponent(ReadOnlySpan<char> value)
        => Decode(value, decodePlusAsSpace: true);

    /// <summary>
    /// Decodes a URL path segment. '+' is a literal plus in a path, so it is left alone.
    /// </summary>
    public static string DecodePath(ReadOnlySpan<char> value)
        => Decode(value, decodePlusAsSpace: false);

    static string Decode(ReadOnlySpan<char> value, bool decodePlusAsSpace)
    {
        if (value.IsEmpty)
            return string.Empty;

        // Fast path: nothing to decode, which is the overwhelmingly common case.
        var needsWork = value.IndexOf('%') >= 0 || (decodePlusAsSpace && value.IndexOf('+') >= 0);
        if (!needsWork)
            return new string(value);

        // Percent escapes are byte-oriented and may combine into multi-byte UTF-8 sequences,
        // so decode into bytes first and run the whole thing through UTF-8 once at the end.
        // Worst case is 3 bytes per char: an already-decoded BMP char re-encoded as UTF-8.
        var rented = value.Length <= 64 ? null : ArrayPool<byte>.Shared.Rent(value.Length * 3);
        Span<byte> bytes = rented ?? stackalloc byte[192];
        try
        {
            var written = 0;
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (c == '%' && i + 2 < value.Length &&
                    TryParseHex(value[i + 1], out var high) &&
                    TryParseHex(value[i + 2], out var low))
                {
                    bytes[written++] = (byte)((high << 4) | low);
                    i += 2;
                }
                else if (c == '+' && decodePlusAsSpace)
                {
                    bytes[written++] = (byte)' ';
                }
                else if (c <= 0x7F)
                {
                    bytes[written++] = (byte)c;
                }
                else
                {
                    // Already-decoded non-ASCII text. Re-encode it so the single UTF-8
                    // decode at the end sees a consistent byte stream.
                    written += Encoding.UTF8.GetBytes(value.Slice(i, 1), bytes[written..]);
                }
            }
            return Encoding.UTF8.GetString(bytes[..written]);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented);
        }
    }

    static bool TryParseHex(char c, out int value)
    {
        switch (c)
        {
            case >= '0' and <= '9':
                value = c - '0';
                return true;
            case >= 'a' and <= 'f':
                value = c - 'a' + 10;
                return true;
            case >= 'A' and <= 'F':
                value = c - 'A' + 10;
                return true;
            default:
                value = 0;
                return false;
        }
    }
}
