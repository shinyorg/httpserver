using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace Shiny.Net.HttpServer.Versioning;

/// <summary>
/// An API version: <c>major[.minor][-status]</c>, a date (<c>2026-01-15</c>), or both
/// (<c>2026-01-15.1.0-beta</c>). The same shape and text form Asp.Versioning uses, so a client that
/// already sends <c>api-version=1.0</c> to an ASP.NET Core service sends it here unchanged.
/// <para>
/// <c>1</c> and <c>1.0</c> are the same version. A missing minor is a zero minor for comparison and
/// equality — otherwise a client asking for <c>v1</c> would miss an endpoint declared as
/// <c>1.0</c> for no reason anybody could explain. The text a version was written with is kept for
/// display.
/// </para>
/// <para>
/// A leading <c>v</c> is accepted when parsing (<c>v2</c>, <c>v2.1</c>), which is what lets a
/// <c>{version:apiVersion}</c> route segment match the <c>/api/v2/…</c> URLs people actually write.
/// </para>
/// </summary>
public sealed class ApiVersion :
    IEquatable<ApiVersion>,
    IComparable<ApiVersion>,
    IComparable,
    ISpanParsable<ApiVersion>,
    IFormattable
{
    /// <summary>
    /// Creates a numeric version.
    /// </summary>
    /// <param name="majorVersion">The major version. Cannot be negative.</param>
    /// <param name="minorVersion">The minor version, or null to write the version as just its major.</param>
    /// <param name="status">An optional alphanumeric status such as <c>beta</c> or <c>rc1</c>.</param>
    public ApiVersion(int majorVersion, int? minorVersion = null, string? status = null)
        : this(null, majorVersion, minorVersion, status)
    {
    }

    /// <summary>
    /// Creates a numeric version from a number, as in <c>new ApiVersion(1.1)</c>. The digits after the
    /// point are the minor version, written as they read: <c>1.10</c> as a double is <c>1.1</c>, so
    /// write <c>new ApiVersion(1, 10)</c> for minor ten.
    /// </summary>
    public ApiVersion(double version, string? status = null)
        : this(null, MajorOf(version), MinorOf(version), status)
    {
    }

    /// <summary>Creates a date-based ("group") version, e.g. <c>2026-01-15</c>.</summary>
    public ApiVersion(DateOnly groupVersion, string? status = null)
        : this(groupVersion, null, null, status)
    {
    }

    /// <summary>Creates a version with both a date and a number, e.g. <c>2026-01-15.1.0</c>.</summary>
    public ApiVersion(DateOnly groupVersion, int majorVersion, int? minorVersion = null, string? status = null)
        : this((DateOnly?)groupVersion, majorVersion, minorVersion, status)
    {
    }

    ApiVersion(DateOnly? groupVersion, int? majorVersion, int? minorVersion, string? status)
    {
        if (majorVersion is < 0)
            throw new ArgumentOutOfRangeException(nameof(majorVersion), "A major version cannot be negative.");

        if (minorVersion is < 0)
            throw new ArgumentOutOfRangeException(nameof(minorVersion), "A minor version cannot be negative.");

        if (minorVersion is not null && majorVersion is null)
            throw new ArgumentException("A minor version needs a major version.", nameof(minorVersion));

        if (groupVersion is null && majorVersion is null)
            throw new ArgumentException("A version needs a date, a major version, or both.");

        if (status is not null && !IsValidStatus(status))
            throw new ArgumentException(
                $"'{status}' is not a valid version status. A status is letters and digits only, such as 'beta' or 'rc1'.",
                nameof(status)
            );

        this.GroupVersion = groupVersion;
        this.MajorVersion = majorVersion;
        this.MinorVersion = minorVersion;
        this.Status = String.IsNullOrEmpty(status) ? null : status;
    }

    /// <summary><c>1.0</c> — what <see cref="ApiVersioningOptions.DefaultApiVersion"/> starts as.</summary>
    public static ApiVersion Default { get; } = new(1, 0);

    /// <summary>The date part of a date-based version, or null for a purely numeric one.</summary>
    public DateOnly? GroupVersion { get; }

    public int? MajorVersion { get; }

    public int? MinorVersion { get; }

    /// <summary>A pre-release label such as <c>beta</c>. A version with no status sorts after any with one.</summary>
    public string? Status { get; }

    // ---- Parsing ----

    public static ApiVersion Parse(string text) => Parse(text, provider: null);

    public static ApiVersion Parse(string text, IFormatProvider? provider)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Parse(text.AsSpan(), provider);
    }

    public static ApiVersion Parse(ReadOnlySpan<char> text, IFormatProvider? provider = null)
        => TryParse(text, provider, out var version)
            ? version
            : throw new FormatException($"'{text}' is not a valid API version. Expected a form such as 1.0, 2, 1.0-beta or 2026-01-15.");

    public static bool TryParse([NotNullWhen(true)] string? text, [MaybeNullWhen(false)] out ApiVersion version)
        => TryParse(text.AsSpan(), null, out version);

    public static bool TryParse([NotNullWhen(true)] string? text, IFormatProvider? provider, [MaybeNullWhen(false)] out ApiVersion version)
        => TryParse(text.AsSpan(), provider, out version);

    public static bool TryParse(ReadOnlySpan<char> text, [MaybeNullWhen(false)] out ApiVersion version)
        => TryParse(text, null, out version);

    public static bool TryParse(ReadOnlySpan<char> text, IFormatProvider? provider, [MaybeNullWhen(false)] out ApiVersion version)
    {
        version = null;

        if (!TryParseParts(text, out var group, out var major, out var minor, out var status))
            return false;

        version = new ApiVersion(group, major, minor, status.IsEmpty ? null : new string(status));
        return true;
    }

    /// <summary>
    /// Validates without allocating. The route constraint runs this on every request that reaches a
    /// versioned segment, so it has to cost no more than the <c>int</c> constraint beside it.
    /// </summary>
    internal static bool IsValid(ReadOnlySpan<char> text)
        => TryParseParts(text, out _, out _, out _, out _);

    static bool TryParseParts(
        ReadOnlySpan<char> text,
        out DateOnly? group,
        out int? major,
        out int? minor,
        out ReadOnlySpan<char> status
    )
    {
        group = null;
        major = null;
        minor = null;
        status = default;

        text = text.Trim();

        // "v2" is how a version is written in a URL. Only when a digit follows, so a status-only
        // string such as "vbeta" is not quietly read as something else.
        if (text.Length > 1 && (text[0] == 'v' || text[0] == 'V') && char.IsAsciiDigit(text[1]))
            text = text[1..];

        if (text.IsEmpty)
            return false;

        // A date is exactly yyyy-MM-dd, recognisable by its dashes before any status dash could be.
        if (text.Length >= 10 && text[4] == '-' && text[7] == '-')
        {
            if (!DateOnly.TryParseExact(text[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                return false;

            group = date;
            text = text[10..];

            if (text.IsEmpty)
                return true;

            if (text[0] == '-')
                return TryStatus(text[1..], out status);

            if (text[0] != '.')
                return false;

            text = text[1..];
        }

        var dash = text.IndexOf('-');
        if (dash >= 0)
        {
            if (!TryStatus(text[(dash + 1)..], out status))
                return false;

            text = text[..dash];
        }

        var dot = text.IndexOf('.');
        var majorText = dot < 0 ? text : text[..dot];

        if (!TryNumber(majorText, out var majorValue))
            return false;

        major = majorValue;

        if (dot >= 0)
        {
            if (!TryNumber(text[(dot + 1)..], out var minorValue))
                return false;

            minor = minorValue;
        }

        return true;
    }

    static bool TryNumber(ReadOnlySpan<char> text, out int value)
    {
        value = 0;

        // Digits only: no sign, no whitespace, no exponent — int.TryParse would accept all three.
        if (text.IsEmpty)
            return false;

        foreach (var c in text)
        {
            if (!char.IsAsciiDigit(c))
                return false;
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    static bool TryStatus(ReadOnlySpan<char> text, out ReadOnlySpan<char> status)
    {
        status = text;
        return IsValidStatus(text);
    }

    static bool IsValidStatus(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
            return false;

        foreach (var c in text)
        {
            if (!char.IsAsciiLetterOrDigit(c))
                return false;
        }

        return true;
    }

    static int MajorOf(double version)
    {
        if (double.IsNaN(version) || double.IsInfinity(version) || version < 0)
            throw new ArgumentOutOfRangeException(nameof(version), "A version number must be a finite, non-negative number.");

        return (int)Math.Truncate(version);
    }

    static int MinorOf(double version)
    {
        // Through the shortest round-trip text rather than arithmetic, so 1.1 is minor 1 and not
        // 0.1 * 10 = 0.9999999.
        var text = version.ToString("R", CultureInfo.InvariantCulture);
        var dot = text.IndexOf('.');

        return dot < 0 ? 0 : int.Parse(text.AsSpan(dot + 1), NumberStyles.None, CultureInfo.InvariantCulture);
    }

    // ---- Formatting ----

    /// <summary>The canonical text: <c>1.0</c>, <c>2</c>, <c>1.0-beta</c>, <c>2026-01-15</c>, <c>2026-01-15.1.0</c>.</summary>
    public override string ToString() => this.ToString(null, null);

    /// <summary>
    /// Formats the version.
    /// <list type="bullet">
    /// <item><c>null</c> or <c>"F"</c> — the full form, as <see cref="ToString()"/>.</item>
    /// <item><c>"S"</c> — the short form for URLs and document names: the minor is dropped when it is
    /// zero, so <c>1.0</c> becomes <c>1</c> and <c>1.1</c> stays <c>1.1</c>.</item>
    /// </list>
    /// </summary>
    public string ToString(string? format, IFormatProvider? formatProvider = null)
    {
        var shortForm = format switch
        {
            null or "" or "F" or "f" => false,
            "S" or "s" => true,
            _ => throw new FormatException($"Unknown API version format '{format}'. Use \"F\" or \"S\".")
        };

        var builder = new StringBuilder(16);

        if (this.GroupVersion is { } group)
            builder.Append(group.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        if (this.MajorVersion is { } major)
        {
            if (builder.Length > 0)
                builder.Append('.');

            builder.Append(major.ToString(CultureInfo.InvariantCulture));

            if (this.MinorVersion is { } minor && !(shortForm && minor == 0))
                builder.Append('.').Append(minor.ToString(CultureInfo.InvariantCulture));
        }

        if (this.Status is { } status)
            builder.Append('-').Append(status);

        return builder.ToString();
    }

    // ---- Equality and ordering ----

    public bool Equals(ApiVersion? other)
        => other is not null
            && this.GroupVersion == other.GroupVersion
            && this.MajorVersion == other.MajorVersion
            && (this.MinorVersion ?? 0) == (other.MinorVersion ?? 0)
            && string.Equals(this.Status, other.Status, StringComparison.OrdinalIgnoreCase);

    public override bool Equals(object? obj) => obj is ApiVersion other && this.Equals(other);

    public override int GetHashCode() => HashCode.Combine(
        this.GroupVersion,
        this.MajorVersion,
        this.MinorVersion ?? 0,
        this.Status is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(this.Status)
    );

    /// <summary>
    /// Orders by date, then major, then minor, then status — where no status (a release) sorts
    /// after any status (a pre-release), so <c>1.0-beta</c> &lt; <c>1.0</c>. A version with no date
    /// sorts before one with a date; a version with no major before one with a major.
    /// </summary>
    public int CompareTo(ApiVersion? other)
    {
        if (other is null)
            return 1;

        var result = Nullable.Compare(this.GroupVersion, other.GroupVersion);
        if (result != 0)
            return result;

        result = Nullable.Compare(this.MajorVersion, other.MajorVersion);
        if (result != 0)
            return result;

        result = (this.MinorVersion ?? 0).CompareTo(other.MinorVersion ?? 0);
        if (result != 0)
            return result;

        return (this.Status, other.Status) switch
        {
            (null, null) => 0,
            (null, _) => 1,
            (_, null) => -1,
            _ => StringComparer.OrdinalIgnoreCase.Compare(this.Status, other.Status)
        };
    }

    int IComparable.CompareTo(object? obj) => obj switch
    {
        null => 1,
        ApiVersion other => this.CompareTo(other),
        _ => throw new ArgumentException("Can only compare to another ApiVersion.", nameof(obj))
    };

    public static bool operator ==(ApiVersion? left, ApiVersion? right)
        => left is null ? right is null : left.Equals(right);

    public static bool operator !=(ApiVersion? left, ApiVersion? right) => !(left == right);

    public static bool operator <(ApiVersion? left, ApiVersion? right)
        => left is null ? right is not null : left.CompareTo(right) < 0;

    public static bool operator <=(ApiVersion? left, ApiVersion? right)
        => left is null || left.CompareTo(right) <= 0;

    public static bool operator >(ApiVersion? left, ApiVersion? right)
        => left is not null && left.CompareTo(right) > 0;

    public static bool operator >=(ApiVersion? left, ApiVersion? right)
        => left is null ? right is null : left.CompareTo(right) >= 0;
}
