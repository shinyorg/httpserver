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

    // Headers appended to more than once, with every value so far. Repeating a header used to copy
    // the whole value array on each append, and HPACK lets a client repeat one for a single byte, so
    // a 15 KB header block cost hundreds of megabytes. Values collect here instead and become a
    // StringValues once, the first time anything reads them.
    Dictionary<string, List<string>>? appended;
    bool stale;

    public HeaderDictionary(int capacity = 8)
        => this.store = new Dictionary<string, StringValues>(capacity, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// When true, mutation throws. Set once a response has begun writing to the wire,
    /// since headers are gone by then and a silent no-op would be a debugging nightmare.
    /// </summary>
    public bool IsReadOnly { get; internal set; }

    /// <summary>
    /// True for headers that will be written to the wire: names must be tokens and values must not
    /// carry CR, LF or NUL. Checked as they are set, so the handler that put a line break into a
    /// header is the one that hears about it — not the connection, half way through a head it can no
    /// longer take back. Request headers leave it off; the parser has already vetted them.
    /// </summary>
    internal bool ValidatesValues { get; init; }

    /// <summary>
    /// Gets or sets a header. Unlike a plain dictionary, reading a missing header returns
    /// <see cref="StringValues.Empty"/> rather than throwing, and assigning
    /// <see cref="StringValues.Empty"/> removes it.
    /// </summary>
    public StringValues this[string key]
    {
        get => this.TryGetValue(key, out var value) ? value : StringValues.Empty;
        set
        {
            this.ThrowIfReadOnly();
            this.Forget(key);
            if (StringValues.IsNullOrEmpty(value))
            {
                this.store.Remove(key);
            }
            else
            {
                this.Validate(key, value);
                this.store[key] = value;
            }
        }
    }

    public int Count => this.store.Count;
    public ICollection<string> Keys => this.store.Keys;

    public ICollection<StringValues> Values
    {
        get
        {
            this.Materialise();
            return this.store.Values;
        }
    }

    /// <summary>Appends a value, preserving any existing values for the same header.</summary>
    public void Append(string key, string value)
    {
        this.ThrowIfReadOnly();
        this.Validate(key, value);

        if (this.appended is not null && this.appended.TryGetValue(key, out var values))
        {
            values.Add(value);
            this.stale = true;
            return;
        }

        if (!this.store.TryGetValue(key, out var existing))
        {
            this.store[key] = new StringValues(value);
            return;
        }

        // The second value for this header. From here on its values live in a list that grows
        // amortised, rather than in an array copied whole on every append.
        var list = new List<string>(existing.Count + 4);
        foreach (var item in existing)
        {
            if (item is not null)
                list.Add(item);
        }
        list.Add(value);

        this.appended ??= new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        this.appended[key] = list;
        this.stale = true;
    }

    /// <summary>Replaces all values for a header with a single value.</summary>
    public void Set(string key, string? value)
    {
        this.ThrowIfReadOnly();
        this.Forget(key);
        if (value is null)
        {
            this.store.Remove(key);
        }
        else
        {
            this.Validate(key, value);
            this.store[key] = new StringValues(value);
        }
    }

    /// <summary>Gets the first value for a header, or null when absent.</summary>
    public string? GetFirst(string key)
        => this.TryGetValue(key, out var value) ? value.ToString() : null;

    public void Add(string key, StringValues value)
    {
        this.ThrowIfReadOnly();
        this.Validate(key, value);
        this.store.Add(key, value);
    }

    public void Add(KeyValuePair<string, StringValues> item) => this.Add(item.Key, item.Value);

    public void Clear()
    {
        this.ThrowIfReadOnly();
        this.store.Clear();
        this.appended?.Clear();
        this.stale = false;
    }

    public bool Contains(KeyValuePair<string, StringValues> item)
        => this.TryGetValue(item.Key, out var value) && value.Equals(item.Value);

    public bool ContainsKey(string key) => this.store.ContainsKey(key);

    public void CopyTo(KeyValuePair<string, StringValues>[] array, int arrayIndex)
    {
        this.Materialise();
        ((ICollection<KeyValuePair<string, StringValues>>)this.store).CopyTo(array, arrayIndex);
    }

    public bool Remove(string key)
    {
        this.ThrowIfReadOnly();
        this.Forget(key);
        return this.store.Remove(key);
    }

    public bool Remove(KeyValuePair<string, StringValues> item) => this.Remove(item.Key);

    public bool TryGetValue(string key, out StringValues value)
    {
        this.Materialise();
        return this.store.TryGetValue(key, out value);
    }

    public Dictionary<string, StringValues>.Enumerator GetEnumerator()
    {
        this.Materialise();
        return this.store.GetEnumerator();
    }

    IEnumerator<KeyValuePair<string, StringValues>> IEnumerable<KeyValuePair<string, StringValues>>.GetEnumerator()
        => this.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => this.GetEnumerator();

    // ---- Strongly typed access to the headers that actually drive protocol behaviour ----

    /// <summary>
    /// The Content-Length header, or null when it is absent or not a plain run of digits. A sign,
    /// whitespace or a list is not a length (RFC 9110 §8.6) — "+3" accepted here would be a body
    /// length another parser in the chain reads differently.
    /// </summary>
    public long? ContentLength
    {
        get
        {
            var raw = this.GetFirst(HeaderNames.ContentLength);
            if (raw is null)
                return null;

            return TryParseContentLength(raw, out var parsed) ? parsed : null;
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
        this.appended?.Clear();
        this.stale = false;
    }

    static bool TryParseContentLength(string raw, out long value)
    {
        value = 0;
        if (raw.Length == 0)
            return false;

        foreach (var c in raw)
        {
            if (c is < '0' or > '9')
                return false;

            var digit = c - '0';
            if (value > (long.MaxValue - digit) / 10)
                return false;

            value = (value * 10) + digit;
        }
        return true;
    }

    void Materialise()
    {
        if (!this.stale)
            return;

        foreach (var (key, values) in this.appended!)
            this.store[key] = new StringValues([.. values]);

        this.stale = false;
    }

    void Forget(string key)
    {
        if (this.appended is not null && this.appended.Remove(key))
            this.stale = this.appended.Count > 0 && this.stale;
    }

    void Validate(string key, StringValues values)
    {
        if (!this.ValidatesValues)
            return;

        ValidateName(key);
        foreach (var value in values)
        {
            if (value is not null)
                ValidateValue(key, value);
        }
    }

    void Validate(string key, string value)
    {
        if (!this.ValidatesValues)
            return;

        ValidateName(key);
        ValidateValue(key, value);
    }

    static void ValidateName(string name)
    {
        if (name.Length == 0)
            throw new InvalidOperationException("A header name cannot be empty.");

        foreach (var c in name)
        {
            if (!IsTokenChar(c))
                throw new InvalidOperationException(
                    $"Invalid character 0x{(int)c:X2} in header name '{name}'. Header names must be RFC 9110 tokens."
                );
        }
    }

    static void ValidateValue(string name, string value)
    {
        // A CR or LF would end the header line early and let the rest of the value write headers of
        // its own — or a whole forged response. NUL is refused for the same reason every parser does.
        var bad = value.AsSpan().IndexOfAny('\r', '\n', '\0');
        if (bad >= 0)
            throw new InvalidOperationException(
                $"Invalid control character 0x{(int)value[bad]:X2} in the value of header '{name}'."
            );
    }

    internal static bool IsTokenChar(char c)
        => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9')
            or '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~';

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
