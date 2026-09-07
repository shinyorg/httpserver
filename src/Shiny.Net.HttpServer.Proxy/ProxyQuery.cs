using System.Text;

namespace Shiny.Net.HttpServer.Proxy;

/// <summary>
/// The outbound query string, in a shape a transform can change.
/// <para>
/// Keys and values arrive already percent-encoded and are kept that way, so a query nothing touched
/// is forwarded byte for byte. Re-encoding an untouched query is not a no-op: an upstream that signs
/// its own URLs, or one that distinguishes <c>%2F</c> from <c>/</c>, notices the difference.
/// </para>
/// </summary>
public sealed class ProxyQuery
{
    readonly List<Entry> entries = [];

    internal ProxyQuery(string? queryString)
    {
        var query = queryString.AsSpan().TrimStart('?');

        while (!query.IsEmpty)
        {
            var amp = query.IndexOf('&');
            var pair = amp < 0 ? query : query[..amp];
            query = amp < 0 ? default : query[(amp + 1)..];

            if (pair.IsEmpty)
                continue;

            var equals = pair.IndexOf('=');

            this.entries.Add(equals < 0
                ? new Entry(Decode(pair), pair.ToString(), null)
                : new Entry(Decode(pair[..equals]), pair[..equals].ToString(), pair[(equals + 1)..].ToString()));
        }
    }

    /// <summary>Keys currently in the query, in order.</summary>
    public IEnumerable<string> Keys => this.entries.Select(x => x.Key);

    /// <summary>The first value for a key, still percent-encoded, or null when there is none.</summary>
    public string? this[string key]
    {
        get
        {
            foreach (var entry in this.entries)
            {
                if (string.Equals(entry.Key, key, StringComparison.Ordinal))
                    return entry.RawValue;
            }

            return null;
        }
    }

    /// <summary>Replaces every occurrence of a key with one value, or adds it when absent.</summary>
    public ProxyQuery Set(string key, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);

        this.Remove(key);
        this.entries.Add(new Entry(key, Uri.EscapeDataString(key), Uri.EscapeDataString(value)));

        return this;
    }

    /// <summary>Adds another value for a key, leaving any existing ones alone.</summary>
    public ProxyQuery Append(string key, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);

        this.entries.Add(new Entry(key, Uri.EscapeDataString(key), Uri.EscapeDataString(value)));

        return this;
    }

    /// <summary>Drops every occurrence of a key. Returns false when it was not there.</summary>
    public bool Remove(string key)
        => this.entries.RemoveAll(x => string.Equals(x.Key, key, StringComparison.Ordinal)) > 0;

    /// <summary>The query as it will go on the wire, without the leading '?'.</summary>
    public string ToQueryString()
    {
        if (this.entries.Count == 0)
            return string.Empty;

        var builder = new StringBuilder();

        foreach (var entry in this.entries)
        {
            if (builder.Length > 0)
                builder.Append('&');

            builder.Append(entry.RawKey);

            if (entry.RawValue is not null)
                builder.Append('=').Append(entry.RawValue);
        }

        return builder.ToString();
    }

    static string Decode(ReadOnlySpan<char> value)
    {
        var text = value.ToString();
        return text.IndexOf('%') < 0 && text.IndexOf('+') < 0 ? text : Uri.UnescapeDataString(text.Replace('+', ' '));
    }

    // Key is decoded, for comparing. RawKey and RawValue are what goes on the wire.
    readonly record struct Entry(string Key, string RawKey, string? RawValue);
}
