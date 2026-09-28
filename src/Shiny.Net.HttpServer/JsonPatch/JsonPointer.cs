using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json.Nodes;

namespace Shiny.Net.HttpServer.JsonPatch;

/// <summary>
/// An RFC 6901 JSON Pointer — <c>/orders/0/lines/-</c> — parsed once into its reference tokens.
/// <para>
/// Parsing up front is the point. A pointer is text with two escapes (<c>~1</c> for <c>/</c> and
/// <c>~0</c> for <c>~</c>), and every bug in hand-rolled pointer code is one of the same three: the
/// escapes decoded in the wrong order (so <c>~01</c> becomes <c>/</c> instead of <c>~1</c>), a
/// leading-zero index like <c>01</c> accepted as an array position, or the empty pointer (the whole
/// document) confused with <c>/</c> (the member whose name is the empty string). Doing it here, once,
/// means a patch and a caller evaluating a pointer by hand agree about what it refers to.
/// </para>
/// </summary>
public sealed class JsonPointer : IEquatable<JsonPointer>
{
    readonly string[] segments;
    readonly string text;

    JsonPointer(string text, string[] segments)
    {
        this.text = text;
        this.segments = segments;
    }

    /// <summary>The empty pointer, which refers to the whole document — not to be confused with <c>/</c>.</summary>
    public static JsonPointer Root { get; } = new(string.Empty, []);

    /// <summary>The unescaped reference tokens, in order. Empty for <see cref="Root"/>.</summary>
    public IReadOnlyList<string> Segments => this.segments;

    /// <summary>True for the empty pointer, the whole document.</summary>
    public bool IsRoot => this.segments.Length == 0;

    /// <summary>The pointer to the containing value, or null for <see cref="Root"/>, which has no container.</summary>
    public JsonPointer? Parent => this.segments.Length == 0
        ? null
        : Create(this.segments.AsSpan(0, this.segments.Length - 1).ToArray());

    /// <summary>The last (unescaped) reference token, or null for <see cref="Root"/>.</summary>
    public string? LastSegment => this.segments.Length == 0 ? null : this.segments[^1];

    /// <summary>Parses a pointer, throwing <see cref="FormatException"/> when it is not valid RFC 6901.</summary>
    public static JsonPointer Parse(string pointer)
    {
        ArgumentNullException.ThrowIfNull(pointer);

        return TryParse(pointer, out var parsed, out var error)
            ? parsed
            : throw new FormatException(error);
    }

    /// <summary>Parses a pointer, or returns false when it is not valid RFC 6901.</summary>
    public static bool TryParse([NotNullWhen(true)] string? pointer, [NotNullWhen(true)] out JsonPointer? result)
        => TryParse(pointer, out result, out _);

    internal static bool TryParse(
        [NotNullWhen(true)] string? pointer,
        [NotNullWhen(true)] out JsonPointer? result,
        [NotNullWhen(false)] out string? error
    )
    {
        result = null;

        if (pointer is null)
        {
            error = "A JSON Pointer is required.";
            return false;
        }

        if (pointer.Length == 0)
        {
            result = Root;
            error = null;
            return true;
        }

        // Anything other than the empty string must start with '/'. "a/b" is not a relative pointer,
        // it is not a pointer at all — accepting it would be guessing at what the caller meant.
        if (pointer[0] != '/')
        {
            error = $"'{pointer}' is not a JSON Pointer: a non-empty pointer must start with '/'.";
            return false;
        }

        var raw = pointer[1..].Split('/');
        var segments = new string[raw.Length];

        for (var i = 0; i < raw.Length; i++)
        {
            if (!TryUnescape(raw[i], out var segment))
            {
                error = $"'{pointer}' is not a JSON Pointer: '~' must be followed by '0' or '1'.";
                return false;
            }
            segments[i] = segment;
        }

        result = new JsonPointer(pointer, segments);
        error = null;
        return true;
    }

    /// <summary>Builds a pointer from unescaped reference tokens, escaping them as it goes.</summary>
    public static JsonPointer Create(params IEnumerable<string> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);

        var array = segments.ToArray();
        if (array.Length == 0)
            return Root;

        var builder = new StringBuilder();
        foreach (var segment in array)
        {
            ArgumentNullException.ThrowIfNull(segment, nameof(segments));
            builder.Append('/').Append(Escape(segment));
        }

        return new JsonPointer(builder.ToString(), array);
    }

    /// <summary>A pointer one level deeper — <paramref name="segment"/> is unescaped and escaped here.</summary>
    public JsonPointer Append(string segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        return new JsonPointer(this.text + "/" + Escape(segment), [.. this.segments, segment]);
    }

    /// <summary>
    /// Escapes one reference token: <c>~</c> to <c>~0</c> first, then <c>/</c> to <c>~1</c>. The
    /// order matters — the other way round turns a literal <c>/</c> into <c>~01</c>.
    /// </summary>
    public static string Escape(string segment)
    {
        ArgumentNullException.ThrowIfNull(segment);

        return segment.AsSpan().IndexOfAny('~', '/') < 0
            ? segment
            : segment.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
    }

    /// <summary>
    /// Unescapes one reference token. <c>~1</c> becomes <c>/</c> and <c>~0</c> becomes <c>~</c>, scanned
    /// left to right in one pass so <c>~01</c> is <c>~1</c> — RFC 6901 §4's "~1 first, then ~0" rule,
    /// without the second pass that would get it wrong.
    /// </summary>
    static bool TryUnescape(string raw, out string segment)
    {
        if (raw.IndexOf('~') < 0)
        {
            segment = raw;
            return true;
        }

        var builder = new StringBuilder(raw.Length);
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (c != '~')
            {
                builder.Append(c);
                continue;
            }

            if (i + 1 >= raw.Length)
            {
                segment = string.Empty;
                return false;
            }

            switch (raw[++i])
            {
                case '0':
                    builder.Append('~');
                    break;
                case '1':
                    builder.Append('/');
                    break;
                default:
                    segment = string.Empty;
                    return false;
            }
        }

        segment = builder.ToString();
        return true;
    }

    /// <summary>
    /// Parses an array index token as RFC 6901 defines it: <c>0</c>, or digits with no leading zero.
    /// <c>-</c> ("past the end") is not an index and is handled by the caller that allows it.
    /// </summary>
    internal static bool TryParseArrayIndex(string segment, out int index)
    {
        index = -1;

        if (segment.Length == 0 || (segment.Length > 1 && segment[0] == '0'))
            return false;

        foreach (var c in segment)
        {
            if (c is < '0' or > '9')
                return false;
        }

        return int.TryParse(segment, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out index);
    }

    /// <summary>
    /// True when this pointer is a proper prefix of <paramref name="other"/> — <c>/a</c> of <c>/a/b</c>, but
    /// not of <c>/ab</c> and not of itself. RFC 6902 forbids moving a value into one of its own children.
    /// </summary>
    public bool IsProperPrefixOf(JsonPointer other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (this.segments.Length >= other.segments.Length)
            return false;

        for (var i = 0; i < this.segments.Length; i++)
        {
            if (!string.Equals(this.segments[i], other.segments[i], StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Resolves the pointer against <paramref name="document"/>.
    /// <para>
    /// Returns false when the target does not exist. A target that exists and holds JSON <c>null</c>
    /// returns true with a null <paramref name="value"/> — "there, and null" and "not there" are
    /// different answers, and a <c>test</c> or <c>remove</c> has to be able to tell them apart.
    /// </para>
    /// </summary>
    public bool TryEvaluate(JsonNode? document, out JsonNode? value)
    {
        var current = document;

        foreach (var segment in this.segments)
        {
            switch (current)
            {
                case JsonObject obj when obj.TryGetPropertyValue(segment, out var child):
                    current = child;
                    break;

                case JsonArray array when TryParseArrayIndex(segment, out var index) && index < array.Count:
                    current = array[index];
                    break;

                default:
                    value = null;
                    return false;
            }
        }

        value = current;
        return true;
    }

    public bool Equals(JsonPointer? other)
        => other is not null && this.segments.AsSpan().SequenceEqual(other.segments);

    public override bool Equals(object? obj) => obj is JsonPointer other && this.Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var segment in this.segments)
            hash.Add(segment, StringComparer.Ordinal);

        return hash.ToHashCode();
    }

    /// <summary>The escaped pointer text, e.g. <c>/a~1b/0</c>.</summary>
    public override string ToString() => this.text;
}
