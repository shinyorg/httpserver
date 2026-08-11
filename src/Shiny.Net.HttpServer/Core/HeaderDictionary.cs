using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.Extensions.Primitives;

namespace Shiny.Net.HttpServer;

/// <summary>
/// A case-insensitive, multi-value header collection. Shaped after ASP.NET Core's
/// <c>IHeaderDictionary</c> so existing muscle memory transfers.
/// </summary>
public sealed class HeaderDictionary : IDictionary<string, StringValues>
{
    readonly Dictionary<string, StringValues> store;

    public HeaderDictionary(int capacity = 8)
        => this.store = new Dictionary<string, StringValues>(capacity, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// When true, mutation throws. Set once a response has begun writing to the wire,
    /// since headers are gone by then and a silent no-op would be a debugging nightmare.
    /// </summary>
    public bool IsReadOnly { get; internal set; }

    /// <summary>
    /// Gets or sets a header. Unlike a plain dictionary, reading a missing header returns
    /// <see cref="StringValues.Empty"/> rather than throwing, and assigning
    /// <see cref="StringValues.Empty"/> removes it.
    /// </summary>
    public StringValues this[string key]
    {
        get => this.store.TryGetValue(key, out var value) ? value : StringValues.Empty;
        set
        {
            this.ThrowIfReadOnly();
            if (StringValues.IsNullOrEmpty(value))
                this.store.Remove(key);
            else
                this.store[key] = value;
        }
    }

    public int Count => this.store.Count;
    public ICollection<string> Keys => this.store.Keys;
    public ICollection<StringValues> Values => this.store.Values;

    /// <summary>Appends a value, preserving any existing values for the same header.</summary>
    public void Append(string key, string value)
    {
        this.ThrowIfReadOnly();
        this.store[key] = this.store.TryGetValue(key, out var existing)
            ? StringValues.Concat(existing, value)
            : new StringValues(value);
    }

    /// <summary>Replaces all values for a header with a single value.</summary>
    public void Set(string key, string? value)
    {
        this.ThrowIfReadOnly();
        if (value is null)
            this.store.Remove(key);
        else
            this.store[key] = new StringValues(value);
    }

    /// <summary>Gets the first value for a header, or null when absent.</summary>
    public string? GetFirst(string key)
        => this.store.TryGetValue(key, out var value) ? value.ToString() : null;

    public void Add(string key, StringValues value)
    {
        this.ThrowIfReadOnly();
        this.store.Add(key, value);
    }

    public void Add(KeyValuePair<string, StringValues> item) => this.Add(item.Key, item.Value);

    public void Clear()
    {
        this.ThrowIfReadOnly();
        this.store.Clear();
    }

    public bool Contains(KeyValuePair<string, StringValues> item)
        => this.store.TryGetValue(item.Key, out var value) && value.Equals(item.Value);

    public bool ContainsKey(string key) => this.store.ContainsKey(key);

    public void CopyTo(KeyValuePair<string, StringValues>[] array, int arrayIndex)
        => ((ICollection<KeyValuePair<string, StringValues>>)this.store).CopyTo(array, arrayIndex);

    public bool Remove(string key)
    {
        this.ThrowIfReadOnly();
        return this.store.Remove(key);
    }

    public bool Remove(KeyValuePair<string, StringValues> item) => this.Remove(item.Key);

    public bool TryGetValue(string key, out StringValues value) => this.store.TryGetValue(key, out value);

    public Dictionary<string, StringValues>.Enumerator GetEnumerator() => this.store.GetEnumerator();

    IEnumerator<KeyValuePair<string, StringValues>> IEnumerable<KeyValuePair<string, StringValues>>.GetEnumerator()
        => this.store.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => this.store.GetEnumerator();

    // ---- Strongly typed access to the headers that actually drive protocol behaviour ----

    public long? ContentLength
    {
        get
        {
            var raw = this.GetFirst(HeaderNames.ContentLength);
            if (raw is null)
                return null;

            return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0
                ? parsed
                : null;
        }
        set => this.Set(
            HeaderNames.ContentLength,
            value?.ToString(CultureInfo.InvariantCulture)
        );
    }

    public string? ContentType
    {
        get => this.GetFirst(HeaderNames.ContentType);
        set => this.Set(HeaderNames.ContentType, value);
    }

    public string? Host
    {
        get => this.GetFirst(HeaderNames.Host);
        set => this.Set(HeaderNames.Host, value);
    }

    internal void Reset()
    {
        this.IsReadOnly = false;
        this.store.Clear();
    }

    void ThrowIfReadOnly()
    {
        if (this.IsReadOnly)
            ThrowReadOnly();

        [DoesNotReturn]
        static void ThrowReadOnly() => throw new InvalidOperationException(
            "Headers are read-only because the response has already started. Set headers before writing to the body."
        );
    }
}
