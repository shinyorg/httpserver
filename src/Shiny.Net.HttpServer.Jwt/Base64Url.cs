using System.Buffers.Text;
using System.Text;

namespace Shiny.Net.HttpServer.Jwt;

/// <summary>
/// The unpadded, URL-safe base64 every part of a JWT is encoded in (RFC 7515 §2).
/// <para>
/// Not the same as <see cref="Convert.ToBase64String(byte[])"/>: <c>+</c> and <c>/</c> become
/// <c>-</c> and <c>_</c>, and the <c>=</c> padding is dropped. Getting this subtly wrong produces
/// tokens that validate locally and are rejected by everyone else.
/// </para>
/// </summary>
public static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
            return string.Empty;

        var maxLength = Base64.GetMaxEncodedToUtf8Length(bytes.Length);
        char[]? rented = maxLength > 256 ? new char[maxLength] : null;
        Span<char> buffer = rented ?? stackalloc char[256];

        if (!Convert.TryToBase64Chars(bytes, buffer, out var written))
            throw new InvalidOperationException("Failed to base64-encode.");

        var encoded = buffer[..written];

        // Trim the padding first so the character swap has less to walk.
        while (encoded.Length > 0 && encoded[^1] == '=')
            encoded = encoded[..^1];

        for (var i = 0; i < encoded.Length; i++)
        {
            encoded[i] = encoded[i] switch
            {
                '+' => '-',
                '/' => '_',
                var c => c
            };
        }

        return new string(encoded);
    }

    public static string EncodeString(string value) => Encode(Encoding.UTF8.GetBytes(value));

    /// <summary>Decodes, returning false rather than throwing — malformed input is the normal case here.</summary>
    public static bool TryDecode(ReadOnlySpan<char> value, out byte[] bytes)
    {
        bytes = [];

        if (value.IsEmpty)
            return true;

        // Restore the padding base64 needs but a JWT does not carry.
        var padding = (4 - (value.Length % 4)) % 4;
        if (padding == 3)
            return false;

        var buffer = new char[value.Length + padding];
        for (var i = 0; i < value.Length; i++)
        {
            buffer[i] = value[i] switch
            {
                '-' => '+',
                '_' => '/',
                '+' or '/' => '\0',   // strict: a real base64 char here means this is not base64url
                var c => c
            };

            if (buffer[i] == '\0')
                return false;
        }

        for (var i = value.Length; i < buffer.Length; i++)
            buffer[i] = '=';

        var decoded = new byte[(buffer.Length / 4) * 3];
        if (!Convert.TryFromBase64Chars(buffer, decoded, out var written))
            return false;

        bytes = written == decoded.Length ? decoded : decoded[..written];
        return true;
    }

    public static byte[] Decode(string value)
        => TryDecode(value, out var bytes) ? bytes : throw new FormatException("Not valid base64url.");
}
