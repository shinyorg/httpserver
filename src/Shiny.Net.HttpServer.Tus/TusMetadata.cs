using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Shiny.Net.HttpServer.Tus;

/// <summary>
/// The <c>Upload-Metadata</c> a client sent when it created an upload - typically the file name and
/// type, since the bytes arrive later and without a <c>Content-Disposition</c> of their own.
/// <para>
/// On the wire it is <c>key base64value,key2 base64value2,flag</c>. Values are base64 because they
/// may be any bytes at all; the indexer decodes them as UTF-8, which is what every tus client
/// sends for text, and <see cref="GetBytes"/> hands back the raw bytes for anything else. A key may
/// appear with no value, which reads as null.
/// </para>
/// <para>
/// It is whatever the client chose to say, so treat it like any other input: a <c>filename</c> of
/// <c>../../etc/passwd</c> is a string, not an instruction.
/// </para>
/// </summary>
public sealed class TusMetadata : IReadOnlyDictionary<string, string?>
{
    readonly Dictionary<string, byte[]?> values;

    TusMetadata(Dictionary<string, byte[]?> values, string header)
    {
        this.values = values;
        this.Header = header;
    }

    /// <summary>No metadata at all.</summary>
    public static TusMetadata Empty { get; } = new(new Dictionary<string, byte[]?>(StringComparer.Ordinal), string.Empty);

    /// <summary>
    /// The header exactly as the client sent it, which is also what <c>HEAD</c> echoes back and what
    /// a store persists - the parsed form is rebuilt from it, so the two cannot drift.
    /// </summary>
    public string Header { get; }

    /// <summary>The value's raw bytes, or null when the key is absent or was sent without a value.</summary>
    public byte[]? GetBytes(string key)
        => this.values.TryGetValue(key, out var value) ? value : null;

    /// <summary>
    /// Reads an <c>Upload-Metadata</c> header. False for one tus says is malformed: an empty or
    /// repeated key, a key with a space or comma in it, or a value that is not base64.
    /// </summary>
    public static bool TryParse(string? header, [NotNullWhen(true)] out TusMetadata? metadata)
    {
        metadata = null;

        if (string.IsNullOrWhiteSpace(header))
        {
            metadata = Empty;
            return true;
        }

        var values = new Dictionary<string, byte[]?>(StringComparer.Ordinal);

        foreach (var pair in header.Split(','))
        {
            var trimmed = pair.Trim();
            if (trimmed.Length == 0)
                return false;

            var space = trimmed.IndexOf(' ');
            var key = space < 0 ? trimmed : trimmed[..space];
            var encoded = space < 0 ? null : trimmed[(space + 1)..].Trim();

            if (key.Length == 0 || values.ContainsKey(key))
                return false;

            foreach (var c in key)
            {
                if (c is <= ' ' or > '~')
                    return false;
            }

            byte[]? value = null;

            if (!string.IsNullOrEmpty(encoded))
            {
                value = new byte[encoded.Length];

                if (!Convert.TryFromBase64String(encoded, value, out var written))
                    return false;

                value = value[..written];
            }

            values[key] = value;
        }

        metadata = new TusMetadata(values, header.Trim());
        return true;
    }

    /// <summary>Reads an <c>Upload-Metadata</c> header, throwing on one that is malformed.</summary>
    public static TusMetadata Parse(string? header)
        => TryParse(header, out var metadata)
            ? metadata
            : throw new FormatException("Upload-Metadata is not a comma-separated list of 'key base64value' pairs.");

    /// <summary>Builds metadata from text values, encoding them the way a tus client would.</summary>
    public static TusMetadata Create(IEnumerable<KeyValuePair<string, string?>> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var header = string.Join(
            ",",
            values.Select(pair => pair.Value is null
                ? pair.Key
                : pair.Key + " " + Convert.ToBase64String(Encoding.UTF8.GetBytes(pair.Value)))
        );

        return Parse(header);
    }

    /// <summary>The value decoded as UTF-8; null when the key was sent without one.</summary>
    public string? this[string key]
        => this.values[key] is { } bytes ? Encoding.UTF8.GetString(bytes) : null;

    public IEnumerable<string> Keys => this.values.Keys;

    public IEnumerable<string?> Values => this.values.Keys.Select(key => this[key]);

    public int Count => this.values.Count;

    public bool ContainsKey(string key) => this.values.ContainsKey(key);

    public bool TryGetValue(string key, out string? value)
    {
        if (this.values.TryGetValue(key, out var bytes))
        {
            value = bytes is null ? null : Encoding.UTF8.GetString(bytes);
            return true;
        }

        value = null;
        return false;
    }

    public IEnumerator<KeyValuePair<string, string?>> GetEnumerator()
    {
        foreach (var key in this.values.Keys)
            yield return new KeyValuePair<string, string?>(key, this[key]);
    }

    IEnumerator IEnumerable.GetEnumerator() => this.GetEnumerator();

    public override string ToString() => this.Header;
}
