using System.Collections;
using Microsoft.Extensions.Primitives;

namespace Shiny.Net.HttpServer;

/// <summary>
/// Parsed query string values. Lazily built: the raw query string is kept as-is until
/// something actually reads a value, so routes that ignore the query never pay for parsing.
/// </summary>
public sealed class QueryCollection : IEnumerable<KeyValuePair<string, StringValues>>
{
    readonly Dictionary<string, StringValues> store = new(StringComparer.OrdinalIgnoreCase);
    string? raw;
    bool parsed;

    /// <summary>The raw query string, without the leading '?'. Null when there was none.</summary>
    public string? RawValue => this.raw;

    public StringValues this[string key]
    {
        get
        {
            this.EnsureParsed();
            return this.store.TryGetValue(key, out var value) ? value : StringValues.Empty;
        }
    }

    public int Count
    {
        get
        {
            this.EnsureParsed();
            return this.store.Count;
        }
    }

    public ICollection<string> Keys
    {
        get
        {
            this.EnsureParsed();
            return this.store.Keys;
        }
    }

    public bool ContainsKey(string key)
    {
        this.EnsureParsed();
        return this.store.ContainsKey(key);
    }

    public bool TryGetValue(string key, out StringValues value)
    {
        this.EnsureParsed();
        return this.store.TryGetValue(key, out value);
    }

    /// <summary>Gets the first value for a key, or null when absent.</summary>
    public string? GetFirst(string key)
    {
        this.EnsureParsed();
        return this.store.TryGetValue(key, out var value) ? value.ToString() : null;
    }

    public IEnumerator<KeyValuePair<string, StringValues>> GetEnumerator()
    {
        this.EnsureParsed();
        return this.store.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator() => this.GetEnumerator();

    internal void SetRaw(string? queryString)
    {
        this.raw = queryString;
        this.parsed = false;
        this.store.Clear();
    }

    internal void Reset() => this.SetRaw(null);

    void EnsureParsed()
    {
        if (this.parsed)
            return;

        this.parsed = true;
        if (string.IsNullOrEmpty(this.raw))
            return;

        var remaining = this.raw.AsSpan();
        while (!remaining.IsEmpty)
        {
            var pairEnd = remaining.IndexOf('&');
            var pair = pairEnd < 0 ? remaining : remaining[..pairEnd];
            remaining = pairEnd < 0 ? default : remaining[(pairEnd + 1)..];

            if (pair.IsEmpty)
                continue;

            var eq = pair.IndexOf('=');
            var name = eq < 0 ? pair : pair[..eq];
            var value = eq < 0 ? default : pair[(eq + 1)..];

            if (name.IsEmpty)
                continue;

            var key = UrlDecoder.DecodeFormComponent(name);
            var decoded = UrlDecoder.DecodeFormComponent(value);

            this.store[key] = this.store.TryGetValue(key, out var existing)
                ? StringValues.Concat(existing, decoded)
                : new StringValues(decoded);
        }
    }
}
