using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Shiny.Net.HttpServer.SourceGenerators;

/// <summary>
/// Compile-time view of a route template: is it valid, and which tokens does it capture.
/// <para>
/// Deliberately a second implementation of the runtime parser rather than a shared one. The
/// generator targets netstandard2.0 and cannot reference the net10.0 library, and the two answer
/// different questions anyway — this one only needs the token names and a yes/no on validity. The
/// syntax the two accept is kept identical, and the round-trip is covered by tests.
/// </para>
/// </summary>
public sealed class RouteTemplateInfo
{
    RouteTemplateInfo(string template, IReadOnlyList<string> parameterNames)
    {
        this.Template = template;
        this.ParameterNames = parameterNames;
    }

    /// <summary>The normalized template, always with a leading slash and no trailing slash.</summary>
    public string Template { get; }

    /// <summary>Names of every <c>{token}</c> and <c>{*catchAll}</c> in the template.</summary>
    public IReadOnlyList<string> ParameterNames { get; }

    public bool HasParameter(string name)
        => this.ParameterNames.Any(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Joins a class prefix and a method template into one path, tolerating whichever combination
    /// of leading and trailing slashes the author happened to type.
    /// </summary>
    public static string Combine(string prefix, string template)
    {
        var left = (prefix ?? string.Empty).Trim().Trim('/');
        var right = (template ?? string.Empty).Trim().Trim('/');

        if (left.Length == 0 && right.Length == 0)
            return "/";

        if (left.Length == 0)
            return "/" + right;

        return right.Length == 0 ? "/" + left : "/" + left + "/" + right;
    }

    /// <summary>Parses and validates, returning null and an explanation when the template is malformed.</summary>
    public static RouteTemplateInfo? TryParse(string template, out string error)
    {
        error = string.Empty;

        var normalized = (template ?? string.Empty).Trim('/');
        if (normalized.Length == 0)
            return new RouteTemplateInfo("/", Array.Empty<string>());

        var parts = normalized.Split('/');
        var names = new List<string>();

        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            var isLast = i == parts.Length - 1;

            if (part.Length == 0)
            {
                error = "empty path segment (are there two consecutive '/'?)";
                return null;
            }

            if (part[0] != '{')
            {
                if (part.IndexOf('{') >= 0 || part.IndexOf('}') >= 0)
                {
                    error = $"segment '{part}' mixes literal text and a parameter; give the parameter its own segment";
                    return null;
                }
                continue;
            }

            if (part[part.Length - 1] != '}')
            {
                error = $"segment '{part}' is missing its closing '}}'";
                return null;
            }

            var inner = part.Substring(1, part.Length - 2);
            if (inner.Length == 0)
            {
                error = "parameter name cannot be empty";
                return null;
            }

            var isCatchAll = inner[0] == '*';
            if (isCatchAll)
            {
                inner = inner.Substring(1);
                if (inner.Length == 0)
                {
                    error = "catch-all parameter name cannot be empty";
                    return null;
                }
                if (!isLast)
                {
                    error = $"catch-all parameter '{{*{inner}}}' must be the last segment";
                    return null;
                }
            }

            if (inner.Length > 0 && inner[inner.Length - 1] == '?')
            {
                if (isCatchAll)
                {
                    error = "a catch-all parameter is already optional; drop the '?'";
                    return null;
                }
                if (!isLast)
                {
                    error = $"optional parameter '{part}' must be the last segment";
                    return null;
                }
                inner = inner.Substring(0, inner.Length - 1);
            }

            var colon = inner.IndexOf(':');
            if (colon >= 0)
            {
                var constraint = inner.Substring(colon + 1);
                inner = inner.Substring(0, colon);

                if (!IsKnownConstraint(constraint))
                {
                    error = $"unknown route constraint ':{constraint}'";
                    return null;
                }
            }

            if (inner.Length == 0)
            {
                error = "parameter name cannot be empty";
                return null;
            }

            if (names.Any(n => string.Equals(n, inner, StringComparison.OrdinalIgnoreCase)))
            {
                error = $"parameter '{{{inner}}}' appears more than once";
                return null;
            }

            names.Add(inner);
        }

        return new RouteTemplateInfo("/" + normalized, names);
    }

    /// <summary>
    /// The constraint vocabulary, which must stay identical to
    /// <c>Shiny.Net.HttpServer.Routing.RouteConstraint.Parse</c>. Anything accepted here and
    /// rejected there is a route that compiles and never matches; the reverse is a route the
    /// generator refuses for no reason. Parity is covered by tests.
    /// </summary>
    static bool IsKnownConstraint(string constraint)
    {
        var paren = constraint.IndexOf('(');
        if (paren < 0)
        {
            switch (constraint.ToLowerInvariant())
            {
                case "byte":
                case "short":
                case "int":
                case "long":
                case "float":
                case "double":
                case "decimal":
                case "bool":
                case "guid":
                case "alpha":
                case "datetime":
                case "dateonly":
                case "timeonly":
                case "timespan":
                    return true;
                default:
                    return false;
            }
        }

        if (constraint[constraint.Length - 1] != ')')
            return false;

        var name = constraint.Substring(0, paren).ToLowerInvariant();
        var arguments = constraint.Substring(paren + 1, constraint.Length - paren - 2);

        var comma = arguments.IndexOf(',');
        if (comma < 0)
        {
            if (!TryParseArgument(arguments, out var value))
                return false;

            switch (name)
            {
                // A length cannot be negative; a bound on a value very much can.
                case "minlength":
                case "maxlength":
                case "length":
                    return value >= 0;

                case "min":
                case "max":
                    return true;

                default:
                    return false;
            }
        }

        if (name != "range")
            return false;

        return TryParseArgument(arguments.Substring(0, comma), out var low)
            && TryParseArgument(arguments.Substring(comma + 1), out var high)
            && low <= high;
    }

    static bool TryParseArgument(string text, out long value) => long.TryParse(
        text.Trim(),
        NumberStyles.Integer,
        CultureInfo.InvariantCulture,
        out value
    );
}
