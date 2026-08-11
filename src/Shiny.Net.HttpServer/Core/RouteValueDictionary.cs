using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Shiny.Net.HttpServer;

/// <summary>
/// Route parameters captured while matching. Values stay as strings — typed conversion is the
/// binder's job (and the generated binder does it without reflection). Backed by a small array
/// rather than a dictionary because real routes have a handful of parameters at most, and the
/// instance is reused across requests on the same connection.
/// </summary>
public sealed class RouteValueDictionary : IEnumerable<KeyValuePair<string, string>>
{
    KeyValuePair<string, string>[] entries = new KeyValuePair<string, string>[4];
    int count;

    public int Count => this.count;

    public string? this[string key] => this.TryGetValue(key, out var value) ? value : null;

    public bool TryGetValue(string key, [NotNullWhen(true)] out string? value)
    {
        for (var i = 0; i < this.count; i++)
        {
            if (string.Equals(this.entries[i].Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = this.entries[i].Value;
                return true;
            }
        }
        value = null;
        return false;
    }

    public bool ContainsKey(string key) => this.TryGetValue(key, out _);

    public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
    {
        for (var i = 0; i < this.count; i++)
            yield return this.entries[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => this.GetEnumerator();

    // ---- Typed accessors used by generated binders ----

    public bool TryGetInt32(string key, out int value)
    {
        value = 0;
        return this.TryGetValue(key, out var raw)
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    public bool TryGetInt64(string key, out long value)
    {
        value = 0;
        return this.TryGetValue(key, out var raw)
            && long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    public bool TryGetGuid(string key, out Guid value)
    {
        value = default;
        return this.TryGetValue(key, out var raw) && Guid.TryParse(raw, out value);
    }

    internal void Add(string key, string value)
    {
        if (this.count == this.entries.Length)
            Array.Resize(ref this.entries, this.entries.Length * 2);

        this.entries[this.count++] = new KeyValuePair<string, string>(key, value);
    }

    /// <summary>
    /// Truncates back to a previous size. The router uses this to undo captures when it
    /// backtracks out of a candidate branch.
    /// </summary>
    internal void TruncateTo(int size) => this.count = size;

    internal void Reset() => this.count = 0;
}
