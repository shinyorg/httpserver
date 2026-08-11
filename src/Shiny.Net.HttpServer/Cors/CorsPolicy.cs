namespace Shiny.Net.HttpServer.Cors;

/// <summary>
/// Which other origins a browser may let read this server's responses.
/// <para>
/// Worth being clear about what CORS is: it is enforced by the browser, not here. These headers are
/// permission slips a browser reads before handing a response to script it loaded from somewhere
/// else. Nothing about them stops a request arriving, and nothing about them is a substitute for
/// authorization — <c>curl</c> and every non-browser client ignore the lot.
/// </para>
/// </summary>
public sealed class CorsPolicy
{
    readonly string[] origins;
    readonly string[] methods;
    readonly string[] headers;
    readonly string[] exposedHeaders;
    readonly Func<string, bool>? originPredicate;

    internal CorsPolicy(
        string[] origins,
        Func<string, bool>? originPredicate,
        bool allowAnyOrigin,
        string[] methods,
        bool allowAnyMethod,
        string[] headers,
        bool allowAnyHeader,
        string[] exposedHeaders,
        bool allowCredentials,
        TimeSpan? preflightMaxAge
    )
    {
        this.origins = origins;
        this.originPredicate = originPredicate;
        this.AllowAnyOrigin = allowAnyOrigin;
        this.methods = methods;
        this.AllowAnyMethod = allowAnyMethod;
        this.headers = headers;
        this.AllowAnyHeader = allowAnyHeader;
        this.exposedHeaders = exposedHeaders;
        this.AllowCredentials = allowCredentials;
        this.PreflightMaxAge = preflightMaxAge;
    }

    public IReadOnlyList<string> Origins => this.origins;

    public bool AllowAnyOrigin { get; }

    public IReadOnlyList<string> Methods => this.methods;

    public bool AllowAnyMethod { get; }

    public IReadOnlyList<string> Headers => this.headers;

    public bool AllowAnyHeader { get; }

    /// <summary>Response headers script is allowed to read beyond the CORS-safelisted ones.</summary>
    public IReadOnlyList<string> ExposedHeaders => this.exposedHeaders;

    /// <summary>Whether the browser may send cookies and <c>Authorization</c> with the request.</summary>
    public bool AllowCredentials { get; }

    /// <summary>How long a browser may cache the preflight result. Null omits the header.</summary>
    public TimeSpan? PreflightMaxAge { get; }

    /// <summary>
    /// True when the response genuinely depends on the request's <c>Origin</c>, and so must carry
    /// <c>Vary: Origin</c> or a shared cache will serve one site's permission slip to another.
    /// </summary>
    internal bool VariesByOrigin => !this.AllowAnyOrigin || this.AllowCredentials;

    public bool IsOriginAllowed(string? origin)
    {
        if (string.IsNullOrEmpty(origin))
            return false;

        if (this.AllowAnyOrigin)
            return true;

        var normalized = CorsPolicyBuilder.NormalizeOrigin(origin);

        foreach (var allowed in this.origins)
        {
            if (string.Equals(allowed, normalized, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return this.originPredicate?.Invoke(origin) == true;
    }

    public bool IsMethodAllowed(string? method)
    {
        if (string.IsNullOrEmpty(method))
            return false;

        if (this.AllowAnyMethod)
            return true;

        foreach (var allowed in this.methods)
        {
            if (HttpMethods.Equals(allowed, method))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Whether every header in a comma-separated <c>Access-Control-Request-Headers</c> list is
    /// allowed. All of them or none: a partial answer would let a browser send a header the server
    /// never agreed to.
    /// </summary>
    public bool AreHeadersAllowed(string? requested)
    {
        if (string.IsNullOrEmpty(requested))
            return true;

        if (this.AllowAnyHeader)
            return true;

        var remaining = requested.AsSpan();
        while (!remaining.IsEmpty)
        {
            var comma = remaining.IndexOf(',');
            var header = (comma < 0 ? remaining : remaining[..comma]).Trim();
            remaining = comma < 0 ? default : remaining[(comma + 1)..];

            if (header.IsEmpty)
                continue;

            var allowed = false;
            foreach (var candidate in this.headers)
            {
                if (header.Equals(candidate, StringComparison.OrdinalIgnoreCase))
                {
                    allowed = true;
                    break;
                }
            }

            if (!allowed)
                return false;
        }

        return true;
    }

    /// <summary>Builds a policy inline, without going through <see cref="CorsOptions"/>.</summary>
    public static CorsPolicy Create(Action<CorsPolicyBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new CorsPolicyBuilder();
        configure(builder);

        return builder.Build();
    }
}

/// <summary>
/// Assembles a <see cref="CorsPolicy"/>.
/// <code>
/// var policy = CorsPolicy.Create(p => p
///     .WithOrigins("https://app.example.com")
///     .AllowAnyHeader()
///     .WithMethods("GET", "POST")
///     .AllowCredentials()
///     .SetPreflightMaxAge(TimeSpan.FromMinutes(10)));
/// </code>
/// </summary>
public sealed class CorsPolicyBuilder
{
    readonly List<string> origins = [];
    readonly List<string> methods = [];
    readonly List<string> headers = [];
    readonly List<string> exposedHeaders = [];

    Func<string, bool>? originPredicate;
    bool allowAnyOrigin;
    bool allowAnyMethod;
    bool allowAnyHeader;
    bool allowCredentials;
    TimeSpan? preflightMaxAge;

    /// <summary>
    /// Adds allowed origins. An origin is scheme, host and port — <c>https://app.example.com</c> —
    /// with no path and no trailing slash; a trailing slash is trimmed rather than silently failing
    /// to match forever.
    /// </summary>
    public CorsPolicyBuilder WithOrigins(params string[] origins)
    {
        ArgumentNullException.ThrowIfNull(origins);

        foreach (var origin in origins)
        {
            if (!string.IsNullOrWhiteSpace(origin))
                this.origins.Add(NormalizeOrigin(origin));
        }

        return this;
    }

    /// <summary>
    /// Allows every origin. Cannot be combined with <see cref="AllowCredentials"/> — see
    /// <see cref="Build"/>.
    /// </summary>
    public CorsPolicyBuilder AllowAnyOrigin()
    {
        this.allowAnyOrigin = true;
        return this;
    }

    /// <summary>
    /// Decides per origin, for the cases a list cannot express — a wildcard subdomain, or a tenant
    /// lookup. Runs on the raw <c>Origin</c> header value.
    /// </summary>
    public CorsPolicyBuilder SetIsOriginAllowed(Func<string, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        this.originPredicate = predicate;
        return this;
    }

    public CorsPolicyBuilder WithMethods(params string[] methods)
    {
        ArgumentNullException.ThrowIfNull(methods);

        foreach (var method in methods)
        {
            if (!string.IsNullOrWhiteSpace(method))
                this.methods.Add(method.Trim().ToUpperInvariant());
        }

        return this;
    }

    public CorsPolicyBuilder AllowAnyMethod()
    {
        this.allowAnyMethod = true;
        return this;
    }

    /// <summary>Adds request headers the browser may send on a cross-origin request.</summary>
    public CorsPolicyBuilder WithHeaders(params string[] headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        foreach (var header in headers)
        {
            if (!string.IsNullOrWhiteSpace(header))
                this.headers.Add(header.Trim());
        }

        return this;
    }

    public CorsPolicyBuilder AllowAnyHeader()
    {
        this.allowAnyHeader = true;
        return this;
    }

    /// <summary>
    /// Adds response headers script may read. Without this, a browser hands script only the
    /// CORS-safelisted headers, which is the usual reason a custom header "disappears".
    /// </summary>
    public CorsPolicyBuilder WithExposedHeaders(params string[] headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        foreach (var header in headers)
        {
            if (!string.IsNullOrWhiteSpace(header))
                this.exposedHeaders.Add(header.Trim());
        }

        return this;
    }

    /// <summary>Lets the browser send cookies and <c>Authorization</c>. Requires named origins.</summary>
    public CorsPolicyBuilder AllowCredentials()
    {
        this.allowCredentials = true;
        return this;
    }

    public CorsPolicyBuilder DisallowCredentials()
    {
        this.allowCredentials = false;
        return this;
    }

    /// <summary>How long a browser may cache the preflight answer.</summary>
    public CorsPolicyBuilder SetPreflightMaxAge(TimeSpan maxAge)
    {
        if (maxAge < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maxAge), maxAge, "Preflight max age cannot be negative.");

        this.preflightMaxAge = maxAge;
        return this;
    }

    public CorsPolicy Build()
    {
        // The one combination the spec forbids outright, and the one everybody reaches for first.
        // A browser rejects "Access-Control-Allow-Origin: *" on a credentialed request, so a policy
        // written this way fails at runtime in a way that looks like the server is broken. Failing
        // here instead says what is actually wrong.
        if (this.allowAnyOrigin && this.allowCredentials)
            throw new InvalidOperationException(
                "A CORS policy cannot both AllowAnyOrigin() and AllowCredentials(): a browser will " +
                "not accept a wildcard origin on a request carrying cookies or Authorization. Name " +
                "the origins with WithOrigins(...), or decide per origin with SetIsOriginAllowed(...)."
            );

        if (!this.allowAnyOrigin && this.origins.Count == 0 && this.originPredicate is null)
            throw new InvalidOperationException(
                "A CORS policy allows no origins, so it can never do anything. Call WithOrigins(...), " +
                "SetIsOriginAllowed(...) or AllowAnyOrigin()."
            );

        return new CorsPolicy(
            [.. this.origins],
            this.originPredicate,
            this.allowAnyOrigin,
            [.. this.methods],
            this.allowAnyMethod,
            [.. this.headers],
            this.allowAnyHeader,
            [.. this.exposedHeaders],
            this.allowCredentials,
            this.preflightMaxAge
        );
    }

    /// <summary>
    /// Trims the trailing slash a copy-pasted origin usually carries. <c>https://app.example.com/</c>
    /// is not an origin, and comparing it as one never matches.
    /// </summary>
    internal static string NormalizeOrigin(string origin)
    {
        var trimmed = origin.Trim();
        return trimmed.Length > 1 && trimmed[^1] == '/' ? trimmed[..^1] : trimmed;
    }
}
