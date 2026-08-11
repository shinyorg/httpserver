namespace Shiny.Net.HttpServer.Http2.Hpack;

/// <summary>One header field.</summary>
public readonly record struct HeaderField(string Name, string Value)
{
    /// <summary>
    /// What this entry costs against the dynamic table's size budget: the two strings plus 32 bytes
    /// of assumed overhead (RFC 7541 §4.1). The constant is arbitrary but shared, so both ends agree
    /// on when an eviction happens.
    /// </summary>
    public int Size => this.Name.Length + this.Value.Length + 32;
}

/// <summary>
/// The 61 predefined entries (RFC 7541 Appendix A).
/// <para>
/// Fixed and shared by both ends, which is why a request for a well-known header costs one byte.
/// </para>
/// </summary>
static class HpackStaticTable
{
    public static readonly HeaderField[] Entries =
    [
        default,                                            // index 0 is unused
        new(":authority", ""),
        new(":method", "GET"),
        new(":method", "POST"),
        new(":path", "/"),
        new(":path", "/index.html"),
        new(":scheme", "http"),
        new(":scheme", "https"),
        new(":status", "200"),
        new(":status", "204"),
        new(":status", "206"),
        new(":status", "304"),
        new(":status", "400"),
        new(":status", "404"),
        new(":status", "500"),
        new("accept-charset", ""),
        new("accept-encoding", "gzip, deflate"),
        new("accept-language", ""),
        new("accept-ranges", ""),
        new("accept", ""),
        new("access-control-allow-origin", ""),
        new("age", ""),
        new("allow", ""),
        new("authorization", ""),
        new("cache-control", ""),
        new("content-disposition", ""),
        new("content-encoding", ""),
        new("content-language", ""),
        new("content-length", ""),
        new("content-location", ""),
        new("content-range", ""),
        new("content-type", ""),
        new("cookie", ""),
        new("date", ""),
        new("etag", ""),
        new("expect", ""),
        new("expires", ""),
        new("from", ""),
        new("host", ""),
        new("if-match", ""),
        new("if-modified-since", ""),
        new("if-none-match", ""),
        new("if-range", ""),
        new("if-unmodified-since", ""),
        new("last-modified", ""),
        new("link", ""),
        new("location", ""),
        new("max-forwards", ""),
        new("proxy-authenticate", ""),
        new("proxy-authorization", ""),
        new("range", ""),
        new("referer", ""),
        new("refresh", ""),
        new("retry-after", ""),
        new("server", ""),
        new("set-cookie", ""),
        new("strict-transport-security", ""),
        new("transfer-encoding", ""),
        new("user-agent", ""),
        new("vary", ""),
        new("via", ""),
        new("www-authenticate", "")
    ];

    public static int Count => Entries.Length - 1;

    /// <summary>
    /// Finds an entry by name and value. Returns a positive index for an exact match, the negative
    /// of an index when only the name matched, and 0 for nothing — which is exactly the three cases
    /// the encoder has to distinguish.
    /// </summary>
    public static int Find(string name, string value)
    {
        var nameOnly = 0;

        for (var i = 1; i < Entries.Length; i++)
        {
            if (!string.Equals(Entries[i].Name, name, StringComparison.Ordinal))
                continue;

            if (string.Equals(Entries[i].Value, value, StringComparison.Ordinal))
                return i;

            if (nameOnly == 0)
                nameOnly = i;
        }

        return -nameOnly;
    }
}

/// <summary>
/// The dynamic table: entries the peer has told us to remember.
/// <para>
/// A FIFO of bounded total size, indexed from the most recent. Both ends run identical copies, and
/// they must stay identical — an eviction on one side that does not happen on the other desynchronises
/// every subsequent index, which is why the size accounting is spelled out rather than approximated.
/// </para>
/// </summary>
sealed class HpackDynamicTable(int maxSize)
{
    readonly Deque entries = new();

    public int MaxSize { get; private set; } = maxSize;

    public int Size { get; private set; }

    public int Count => this.entries.Count;

    /// <summary>Index 1 is the most recently added entry.</summary>
    public HeaderField this[int index] => this.entries[index - 1];

    public void Add(HeaderField field)
    {
        // An entry larger than the whole table is not an error: the table is simply emptied and the
        // entry is not added (RFC 7541 §4.4).
        while (this.Size + field.Size > this.MaxSize && this.entries.Count > 0)
        {
            this.Size -= this.entries.RemoveLast().Size;
        }

        if (field.Size > this.MaxSize)
            return;

        this.entries.AddFirst(field);
        this.Size += field.Size;
    }

    /// <summary>Applies a dynamic table size update, evicting whatever no longer fits.</summary>
    public void Resize(int maxSize)
    {
        this.MaxSize = maxSize;

        while (this.Size > this.MaxSize && this.entries.Count > 0)
            this.Size -= this.entries.RemoveLast().Size;
    }

    /// <summary>See <see cref="HpackStaticTable.Find"/> for the return convention.</summary>
    public int Find(string name, string value)
    {
        var nameOnly = 0;

        for (var i = 0; i < this.entries.Count; i++)
        {
            var entry = this.entries[i];
            if (!string.Equals(entry.Name, name, StringComparison.Ordinal))
                continue;

            if (string.Equals(entry.Value, value, StringComparison.Ordinal))
                return i + 1;

            if (nameOnly == 0)
                nameOnly = i + 1;
        }

        return -nameOnly;
    }

    /// <summary>A small ring buffer. Additions are at the front and evictions at the back.</summary>
    sealed class Deque
    {
        HeaderField[] items = new HeaderField[16];
        int head;

        public int Count { get; private set; }

        public HeaderField this[int index] => this.items[(this.head + index) % this.items.Length];

        public void AddFirst(HeaderField item)
        {
            if (this.Count == this.items.Length)
                this.Grow();

            this.head = (this.head - 1 + this.items.Length) % this.items.Length;
            this.items[this.head] = item;
            this.Count++;
        }

        public HeaderField RemoveLast()
        {
            var index = (this.head + this.Count - 1) % this.items.Length;
            var item = this.items[index];

            this.items[index] = default;
            this.Count--;

            return item;
        }

        void Grow()
        {
            var grown = new HeaderField[this.items.Length * 2];
            for (var i = 0; i < this.Count; i++)
                grown[i] = this[i];

            this.items = grown;
            this.head = 0;
        }
    }
}
