using System.Globalization;

namespace Shiny.Net.HttpServer.Routing;

/// <summary>
/// An inline route constraint such as <c>{id:int}</c> or <c>{page:range(1,100)}</c>.
/// <para>
/// Deliberately a closed set evaluated by a switch rather than a pluggable
/// <c>IRouteConstraint</c> resolved from a container. A closed set is trim-safe, allocation-free,
/// and covers what route matching is actually for — anything richer belongs in the handler, where
/// it can return a meaningful error instead of a bare 404.
/// </para>
/// <para>
/// There is no <c>regex</c> constraint, and that is the same decision rather than an omission: it
/// would put an attacker-influenced pattern on the routing hot path for every request, which is a
/// denial-of-service surface, and a route that needs a regular expression is a route whose handler
/// should be explaining what was wrong with the input.
/// </para>
/// <para>
/// A constraint decides whether a route <em>matches</em>. It does not convert anything — binding a
/// segment to a parameter's type is the binder's job, and it already handles every
/// <c>IParsable&lt;T&gt;</c>. <c>{id:int}</c> on an endpoint taking a <c>long</c> is legal and does
/// what it says: match integers, hand the handler a long.
/// </para>
/// </summary>
public sealed class RouteConstraint
{
    enum Kind
    {
        None,

        // Integers, by width. A narrower constraint is a real filter: {id:byte} does not match 300.
        Byte,
        Short,
        Int,
        Long,

        // Reals.
        Float,
        Double,
        Decimal,

        Bool,
        Guid,
        Alpha,

        // Temporal. Parsed with the invariant culture, so a route means the same thing wherever the
        // server happens to be running.
        DateTime,
        DateOnly,
        TimeOnly,
        TimeSpan,

        // Length of the text.
        MinLength,
        MaxLength,
        Length,

        // Value of the number.
        Min,
        Max,
        Range
    }

    readonly Kind kind;
    readonly long argument;
    readonly long argument2;

    RouteConstraint(Kind kind, long argument = 0, long argument2 = 0)
    {
        this.kind = kind;
        this.argument = argument;
        this.argument2 = argument2;
    }

    /// <summary>No constraint — any single segment matches.</summary>
    public static readonly RouteConstraint None = new(Kind.None);

    public bool IsUnconstrained => this.kind == Kind.None;

    /// <summary>Parses a constraint name, returning null when it is not recognised.</summary>
    public static RouteConstraint? Parse(string text)
    {
        var paren = text.IndexOf('(');
        if (paren < 0)
        {
            return text.ToLowerInvariant() switch
            {
                "byte" => new RouteConstraint(Kind.Byte),
                "short" => new RouteConstraint(Kind.Short),
                "int" => new RouteConstraint(Kind.Int),
                "long" => new RouteConstraint(Kind.Long),
                "float" => new RouteConstraint(Kind.Float),
                "double" => new RouteConstraint(Kind.Double),
                "decimal" => new RouteConstraint(Kind.Decimal),
                "bool" => new RouteConstraint(Kind.Bool),
                "guid" => new RouteConstraint(Kind.Guid),
                "alpha" => new RouteConstraint(Kind.Alpha),
                "datetime" => new RouteConstraint(Kind.DateTime),
                "dateonly" => new RouteConstraint(Kind.DateOnly),
                "timeonly" => new RouteConstraint(Kind.TimeOnly),
                "timespan" => new RouteConstraint(Kind.TimeSpan),
                _ => null
            };
        }

        if (text[^1] != ')')
            return null;

        var name = text[..paren].ToLowerInvariant();
        var arguments = text[(paren + 1)..^1];

        var comma = arguments.IndexOf(',');
        if (comma < 0)
        {
            if (!TryParseArgument(arguments, out var value))
                return null;

            return name switch
            {
                // A length cannot be negative; a bound on a value very much can.
                "minlength" => value < 0 ? null : new RouteConstraint(Kind.MinLength, value),
                "maxlength" => value < 0 ? null : new RouteConstraint(Kind.MaxLength, value),
                "length" => value < 0 ? null : new RouteConstraint(Kind.Length, value),
                "min" => new RouteConstraint(Kind.Min, value),
                "max" => new RouteConstraint(Kind.Max, value),
                _ => null
            };
        }

        if (name != "range")
            return null;

        if (!TryParseArgument(arguments[..comma], out var low) ||
            !TryParseArgument(arguments[(comma + 1)..], out var high))
            return null;

        // An inverted range matches nothing, which is never what someone meant to type.
        return low > high ? null : new RouteConstraint(Kind.Range, low, high);
    }

    public bool Matches(ReadOnlySpan<char> value)
    {
        switch (this.kind)
        {
            case Kind.None:
                return true;

            case Kind.Byte:
                return byte.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);

            case Kind.Short:
                return short.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);

            case Kind.Int:
                return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);

            case Kind.Long:
                return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);

            case Kind.Float:
                return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

            case Kind.Double:
                return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

            case Kind.Decimal:
                return decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _);

            case Kind.Bool:
                return bool.TryParse(value, out _);

            case Kind.Guid:
                return Guid.TryParse(value, out _);

            case Kind.DateTime:
                return System.DateTime.TryParse(
                    value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out _
                );

            case Kind.DateOnly:
                return System.DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

            case Kind.TimeOnly:
                return System.TimeOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

            case Kind.TimeSpan:
                return System.TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out _);

            case Kind.Alpha:
                if (value.IsEmpty)
                    return false;
                foreach (var c in value)
                {
                    if (!char.IsAsciiLetter(c))
                        return false;
                }
                return true;

            case Kind.MinLength:
                return value.Length >= this.argument;

            case Kind.MaxLength:
                return value.Length <= this.argument;

            case Kind.Length:
                return value.Length == this.argument;

            case Kind.Min:
                return TryParseValue(value, out var atLeast) && atLeast >= this.argument;

            case Kind.Max:
                return TryParseValue(value, out var atMost) && atMost <= this.argument;

            case Kind.Range:
                return TryParseValue(value, out var within)
                    && within >= this.argument
                    && within <= this.argument2;

            default:
                return false;
        }
    }

    public override string ToString() => this.kind switch
    {
        Kind.None => string.Empty,
        Kind.MinLength => $"minlength({this.argument})",
        Kind.MaxLength => $"maxlength({this.argument})",
        Kind.Length => $"length({this.argument})",
        Kind.Min => $"min({this.argument})",
        Kind.Max => $"max({this.argument})",
        Kind.Range => $"range({this.argument},{this.argument2})",
        _ => this.kind.ToString().ToLowerInvariant()
    };

    static bool TryParseArgument(ReadOnlySpan<char> text, out long value)
        => long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    static bool TryParseValue(ReadOnlySpan<char> text, out long value)
        => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
}
