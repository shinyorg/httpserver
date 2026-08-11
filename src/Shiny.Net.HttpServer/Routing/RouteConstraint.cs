using System.Globalization;

namespace Shiny.Net.HttpServer.Routing;

/// <summary>
/// An inline route constraint such as <c>{id:int}</c>.
/// <para>
/// Deliberately a closed set evaluated by a switch rather than a pluggable
/// <c>IRouteConstraint</c> resolved from a container. A closed set is trim-safe, allocation-free,
/// and covers what route matching is actually for — anything richer belongs in the handler, where
/// it can return a meaningful error instead of a bare 404.
/// </para>
/// </summary>
public sealed class RouteConstraint
{
    enum Kind
    {
        None,
        Int,
        Long,
        Guid,
        Bool,
        Double,
        Decimal,
        Alpha,
        MinLength,
        MaxLength,
        Length
    }

    readonly Kind kind;
    readonly int argument;

    RouteConstraint(Kind kind, int argument = 0)
    {
        this.kind = kind;
        this.argument = argument;
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
                "int" => new RouteConstraint(Kind.Int),
                "long" => new RouteConstraint(Kind.Long),
                "guid" => new RouteConstraint(Kind.Guid),
                "bool" => new RouteConstraint(Kind.Bool),
                "double" => new RouteConstraint(Kind.Double),
                "decimal" => new RouteConstraint(Kind.Decimal),
                "alpha" => new RouteConstraint(Kind.Alpha),
                _ => null
            };
        }

        if (text[^1] != ')')
            return null;

        var name = text[..paren].ToLowerInvariant();
        var arg = text[(paren + 1)..^1];
        if (!int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < 0)
            return null;

        return name switch
        {
            "minlength" => new RouteConstraint(Kind.MinLength, value),
            "maxlength" => new RouteConstraint(Kind.MaxLength, value),
            "length" => new RouteConstraint(Kind.Length, value),
            _ => null
        };
    }

    public bool Matches(ReadOnlySpan<char> value)
    {
        switch (this.kind)
        {
            case Kind.None:
                return true;

            case Kind.Int:
                return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);

            case Kind.Long:
                return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);

            case Kind.Guid:
                return Guid.TryParse(value, out _);

            case Kind.Bool:
                return bool.TryParse(value, out _);

            case Kind.Double:
                return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

            case Kind.Decimal:
                return decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _);

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
        _ => this.kind.ToString().ToLowerInvariant()
    };
}
