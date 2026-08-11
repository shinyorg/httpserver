namespace Shiny.Net.HttpServer.Routing;

/// <summary>What a single <c>/</c>-delimited piece of a route template matches.</summary>
public enum RouteSegmentKind
{
    /// <summary>Fixed text, compared case-insensitively.</summary>
    Literal,

    /// <summary>Captures exactly one path segment, e.g. <c>{id}</c>.</summary>
    Parameter,

    /// <summary>Captures the remainder of the path including slashes, e.g. <c>{*path}</c>.</summary>
    CatchAll
}

/// <summary>One parsed piece of a route template.</summary>
public sealed class RouteSegment
{
    internal RouteSegment(RouteSegmentKind kind, string text, RouteConstraint constraint, bool isOptional)
    {
        this.Kind = kind;
        this.Text = text;
        this.Constraint = constraint;
        this.IsOptional = isOptional;
    }

    public RouteSegmentKind Kind { get; }

    /// <summary>Literal text, or the parameter name for parameter and catch-all segments.</summary>
    public string Text { get; }

    public RouteConstraint Constraint { get; }

    /// <summary>True for a trailing <c>{id?}</c>, which lets the route match with the segment absent.</summary>
    public bool IsOptional { get; }
}

/// <summary>
/// A parsed route template such as <c>/users/{id:int}/orders/{*rest}</c>.
/// <para>
/// Parsing happens once at registration, never per request. Supported syntax: literal segments,
/// <c>{name}</c>, <c>{name:constraint}</c>, a trailing <c>{name?}</c>, and a trailing <c>{*name}</c>.
/// </para>
/// </summary>
public sealed class RouteTemplate
{
    RouteTemplate(string rawText, RouteSegment[] segments)
    {
        this.RawText = rawText;
        this.Segments = segments;
    }

    /// <summary>The template exactly as written, used for display names and diagnostics.</summary>
    public string RawText { get; }

    public IReadOnlyList<RouteSegment> Segments { get; }

    public static RouteTemplate Parse(string template)
    {
        ArgumentNullException.ThrowIfNull(template);

        var normalized = Normalize(template);
        if (normalized.Length == 0)
            return new RouteTemplate("/", []);

        var parts = normalized.Split('/');
        var segments = new RouteSegment[parts.Length];

        for (var i = 0; i < parts.Length; i++)
        {
            var segment = ParseSegment(parts[i], template);
            var isLast = i == parts.Length - 1;

            if (segment.Kind == RouteSegmentKind.CatchAll && !isLast)
                throw new RouteTemplateException(
                    template,
                    $"Catch-all parameter '{{*{segment.Text}}}' must be the last segment."
                );

            if (segment.IsOptional && !isLast)
                throw new RouteTemplateException(
                    template,
                    $"Optional parameter '{{{segment.Text}?}}' must be the last segment."
                );

            segments[i] = segment;
        }

        return new RouteTemplate(EnsureLeadingSlash(template), segments);
    }

    static RouteSegment ParseSegment(string part, string template)
    {
        if (part.Length == 0)
            throw new RouteTemplateException(template, "Empty path segment (are there two consecutive '/'?).");

        if (part[0] != '{')
        {
            // A '{' anywhere but the start means something like "v{version}" — real ASP.NET
            // supports mixed segments, but the generated binder gets much simpler if we don't,
            // and rejecting loudly beats matching in a way nobody predicted.
            if (part.Contains('{') || part.Contains('}'))
                throw new RouteTemplateException(
                    template,
                    $"Segment '{part}' mixes literal text and a parameter. Give the parameter its own segment."
                );

            return new RouteSegment(RouteSegmentKind.Literal, part, RouteConstraint.None, false);
        }

        if (part[^1] != '}')
            throw new RouteTemplateException(template, $"Segment '{part}' is missing its closing '}}'.");

        var inner = part[1..^1];
        if (inner.Length == 0)
            throw new RouteTemplateException(template, "Parameter name cannot be empty.");

        var kind = RouteSegmentKind.Parameter;
        if (inner[0] == '*')
        {
            kind = RouteSegmentKind.CatchAll;
            inner = inner[1..];
            if (inner.Length == 0)
                throw new RouteTemplateException(template, "Catch-all parameter name cannot be empty.");
        }

        var isOptional = false;
        if (inner[^1] == '?')
        {
            if (kind == RouteSegmentKind.CatchAll)
                throw new RouteTemplateException(
                    template,
                    "A catch-all parameter is already optional; drop the '?'."
                );

            isOptional = true;
            inner = inner[..^1];
        }

        var constraint = RouteConstraint.None;
        var colon = inner.IndexOf(':');
        if (colon >= 0)
        {
            var constraintName = inner[(colon + 1)..];
            inner = inner[..colon];
            constraint = RouteConstraint.Parse(constraintName)
                ?? throw new RouteTemplateException(template, $"Unknown route constraint ':{constraintName}'.");
        }

        if (inner.Length == 0)
            throw new RouteTemplateException(template, "Parameter name cannot be empty.");

        return new RouteSegment(kind, inner, constraint, isOptional);
    }

    static string Normalize(string template)
        => template.Trim('/');

    static string EnsureLeadingSlash(string template)
        => template.Length > 0 && template[0] == '/' ? template : "/" + template;

    /// <summary>
    /// True when two templates would match exactly the same requests. Compares the parsed shape
    /// rather than the raw text, so <c>"users/{id}"</c> and <c>"/users/{id}/"</c> — which route
    /// identically — are recognised as the same route when removing one.
    /// </summary>
    public bool IsEquivalentTo(RouteTemplate other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (ReferenceEquals(this, other))
            return true;

        if (this.Segments.Count != other.Segments.Count)
            return false;

        for (var i = 0; i < this.Segments.Count; i++)
        {
            var a = this.Segments[i];
            var b = other.Segments[i];

            if (a.Kind != b.Kind || a.IsOptional != b.IsOptional)
                return false;

            // Parameter names are part of the shape: two routes capturing different names are
            // different routes even though they match the same URLs.
            var comparison = a.Kind == RouteSegmentKind.Literal
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            if (!string.Equals(a.Text, b.Text, comparison))
                return false;

            if (a.Constraint.ToString() != b.Constraint.ToString())
                return false;
        }

        return true;
    }

    public override string ToString() => this.RawText;
}

/// <summary>Thrown at registration time when a route template cannot be parsed.</summary>
public sealed class RouteTemplateException(string template, string message)
    : ArgumentException($"Invalid route template '{template}': {message}")
{
    public string Template { get; } = template;
}
