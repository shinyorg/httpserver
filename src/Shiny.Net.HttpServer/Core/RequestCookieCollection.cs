using System.Collections;

namespace Shiny.Net.HttpServer;

/// <summary>Cookies sent by the client, parsed lazily from the Cookie header.</summary>
public sealed class RequestCookieCollection : IEnumerable<KeyValuePair<string, string>>
{
    readonly Dictionary<string, string> store = new(StringComparer.Ordinal);
    string? raw;
    bool parsed;

    public string? this[string key]
    {
        get
        {
            this.EnsureParsed();
            return this.store.TryGetValue(key, out var value) ? value : null;
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

    public bool ContainsKey(string key)
    {
        this.EnsureParsed();
        return this.store.ContainsKey(key);
    }

    public bool TryGetValue(string key, out string value)
    {
        this.EnsureParsed();
        return this.store.TryGetValue(key, out value!);
    }

    public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
    {
        this.EnsureParsed();
        return this.store.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator() => this.GetEnumerator();

    internal void SetRaw(string? cookieHeader)
    {
        this.raw = cookieHeader;
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
            var end = remaining.IndexOf(';');
            var pair = end < 0 ? remaining : remaining[..end];
            remaining = end < 0 ? default : remaining[(end + 1)..];

            pair = pair.Trim();
            if (pair.IsEmpty)
                continue;

            var eq = pair.IndexOf('=');
            if (eq <= 0)
                continue;

            var name = new string(pair[..eq].Trim());
            var value = pair[(eq + 1)..].Trim();

            // Quoted cookie values are legal; strip the quotes so callers see the payload.
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                value = value[1..^1];

            this.store[name] = UrlDecoder.DecodeFormComponent(value);
        }
    }
}
