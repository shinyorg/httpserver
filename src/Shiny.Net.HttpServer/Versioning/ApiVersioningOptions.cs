namespace Shiny.Net.HttpServer.Versioning;

/// <summary>
/// How API versions are read, defaulted, reported and retired.
/// <code>
/// builder.AddApiVersioning(o =>
/// {
///     o.DefaultApiVersion = new ApiVersion(1, 0);
///     o.AssumeDefaultVersionWhenUnspecified = true;
///     o.ReportApiVersions = true;
///     o.ApiVersionReader = ApiVersionReader.Combine(
///         new UrlSegmentApiVersionReader(),
///         new QueryStringApiVersionReader("api-version"),
///         new HeaderApiVersionReader("api-version"),
///         new MediaTypeApiVersionReader("v"));
///
///     o.Policies.Sunset(0.9)
///         .Effective(new DateTimeOffset(2026, 12, 31, 0, 0, 0, TimeSpan.Zero))
///         .Link("https://example.com/api/retirement");
/// });
/// </code>
/// <para>
/// Named and shaped after Asp.Versioning's options on purpose, so configuration moves across as-is.
/// None of it is needed just to version an endpoint — the defaults apply without a call to
/// <c>AddApiVersioning</c> at all.
/// </para>
/// </summary>
public sealed class ApiVersioningOptions
{
    /// <summary>
    /// The version assumed for a request that names none, when
    /// <see cref="AssumeDefaultVersionWhenUnspecified"/> is on. <c>1.0</c> by default.
    /// </summary>
    public ApiVersion DefaultApiVersion { get; set; } = ApiVersion.Default;

    /// <summary>
    /// When true, a request that names no version is served as <see cref="DefaultApiVersion"/> (or
    /// whatever <see cref="ApiVersionSelector"/> chooses). When false — the default — it is answered
    /// 400, because a versioned API that guesses on a client's behalf is making a promise the client
    /// never asked for. Turn it on when versioning an API that already has clients sending nothing.
    /// </summary>
    public bool AssumeDefaultVersionWhenUnspecified { get; set; }

    /// <summary>Where the requested version is read from. <see cref="Versioning.ApiVersionReader.Default"/> by default.</summary>
    public IApiVersionReader ApiVersionReader { get; set; } = Versioning.ApiVersionReader.Default;

    /// <summary>
    /// Chooses the version for a request that named none, when defaults are assumed. Null (the
    /// default) means <see cref="DefaultApiVersion"/>; <see cref="CurrentImplementationApiVersionSelector"/>
    /// picks the newest version the route implements instead.
    /// </summary>
    public IApiVersionSelector? ApiVersionSelector { get; set; }

    /// <summary>
    /// When true, responses from versioned routes carry <c>api-supported-versions</c> and
    /// <c>api-deprecated-versions</c>, so a client can discover what else exists — and that the
    /// version it is on is going away — without reading documentation.
    /// </summary>
    public bool ReportApiVersions { get; set; }

    /// <summary>
    /// The status for a request whose version the matched route does not implement. 400 by default,
    /// as in Asp.Versioning; some APIs prefer 404, on the grounds that the resource does not exist at
    /// that version.
    /// </summary>
    public int UnsupportedApiVersionStatusCode { get; set; } = StatusCodes.Status400BadRequest;

    /// <summary>
    /// Deprecation and sunset policies per version, emitted as <c>Deprecation</c> (RFC 9745),
    /// <c>Sunset</c> (RFC 8594) and <c>Link</c> headers on responses served at that version.
    /// </summary>
    public ApiVersionPolicies Policies { get; } = new();

    /// <summary>
    /// When true (the default), a per-version OpenAPI document writes a <c>{version:apiVersion}</c>
    /// route segment as the version itself — <c>/api/v2/users</c> rather than <c>/api/{version}/users</c>
    /// — since within one version's document the segment can only ever hold one value.
    /// </summary>
    public bool SubstituteApiVersionInUrl { get; set; } = true;

    /// <summary>
    /// Formats a version as it appears in a URL segment and as an OpenAPI document name. The default
    /// writes <c>v</c> plus the short form: <c>v1</c>, <c>v1.1</c>, <c>v2026-01-15</c>.
    /// </summary>
    public Func<ApiVersion, string> FormatGroupName { get; set; } = static version => "v" + version.ToString("S");

    /// <summary>The options used when nothing configured any. Never mutated.</summary>
    internal static ApiVersioningOptions Default { get; } = new();
}

/// <summary>Chooses a version for a request that did not name one.</summary>
public interface IApiVersionSelector
{
    /// <param name="request">The request.</param>
    /// <param name="implementedVersions">Every version the matched route implements, ascending.</param>
    ApiVersion SelectVersion(HttpRequest request, IReadOnlyList<ApiVersion> implementedVersions);
}

/// <summary>Always the configured default — what happens when no selector is set.</summary>
public sealed class DefaultApiVersionSelector(ApiVersioningOptions options) : IApiVersionSelector
{
    public ApiVersion SelectVersion(HttpRequest request, IReadOnlyList<ApiVersion> implementedVersions)
        => options.DefaultApiVersion;
}

/// <summary>
/// The newest version the route implements, preferring releases over pre-releases — so a client
/// that sends nothing gets the current API rather than the first one ever shipped. Falls back to the
/// default when the route implements nothing but pre-releases.
/// </summary>
public sealed class CurrentImplementationApiVersionSelector(ApiVersioningOptions options) : IApiVersionSelector
{
    public ApiVersion SelectVersion(HttpRequest request, IReadOnlyList<ApiVersion> implementedVersions)
    {
        for (var i = implementedVersions.Count - 1; i >= 0; i--)
        {
            if (implementedVersions[i].Status is null)
                return implementedVersions[i];
        }

        return options.DefaultApiVersion;
    }
}

/// <summary>
/// The deprecation and sunset policies, one of each per version at most.
/// <code>
/// o.Policies.Deprecate(1.0).Effective(DateTimeOffset.UtcNow).Link("https://example.com/v1-deprecation");
/// o.Policies.Sunset(1.0).Effective(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));
/// </code>
/// </summary>
public sealed class ApiVersionPolicies
{
    readonly Dictionary<ApiVersion, ApiVersionPolicy> deprecations = [];
    readonly Dictionary<ApiVersion, ApiVersionPolicy> sunsets = [];

    /// <summary>
    /// The deprecation policy for a version, created on first use. Its date goes out as the
    /// <c>Deprecation</c> header (RFC 9745) — which may be in the future, announcing a deprecation
    /// before it takes effect — and its links as <c>Link: &lt;…&gt;; rel="deprecation"</c>.
    /// </summary>
    public ApiVersionPolicy Deprecate(ApiVersion version) => GetOrAdd(this.deprecations, version);

    /// <inheritdoc cref="Deprecate(ApiVersion)"/>
    public ApiVersionPolicy Deprecate(double version) => this.Deprecate(new ApiVersion(version));

    /// <summary>
    /// The sunset policy for a version, created on first use: the date after which it stops
    /// answering, as the <c>Sunset</c> header (RFC 8594), and its links as
    /// <c>Link: &lt;…&gt;; rel="sunset"</c>.
    /// </summary>
    public ApiVersionPolicy Sunset(ApiVersion version) => GetOrAdd(this.sunsets, version);

    /// <inheritdoc cref="Sunset(ApiVersion)"/>
    public ApiVersionPolicy Sunset(double version) => this.Sunset(new ApiVersion(version));

    public IReadOnlyDictionary<ApiVersion, ApiVersionPolicy> Deprecations => this.deprecations;

    public IReadOnlyDictionary<ApiVersion, ApiVersionPolicy> Sunsets => this.sunsets;

    internal bool IsEmpty => this.deprecations.Count == 0 && this.sunsets.Count == 0;

    static ApiVersionPolicy GetOrAdd(Dictionary<ApiVersion, ApiVersionPolicy> policies, ApiVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);

        if (!policies.TryGetValue(version, out var policy))
            policies[version] = policy = new ApiVersionPolicy();

        return policy;
    }
}

/// <summary>A date and the documents that explain it, for a deprecation or a sunset.</summary>
public sealed class ApiVersionPolicy
{
    readonly List<ApiVersionPolicyLink> links = [];

    /// <summary>
    /// When the policy takes effect. A deprecation without a date sends only its links: RFC 9745's
    /// <c>Deprecation</c> header is a date, and inventing one would tell clients something untrue.
    /// </summary>
    public DateTimeOffset? Date { get; private set; }

    public IReadOnlyList<ApiVersionPolicyLink> Links => this.links;

    /// <summary>Sets <see cref="Date"/>.</summary>
    public ApiVersionPolicy Effective(DateTimeOffset date)
    {
        this.Date = date;
        return this;
    }

    /// <summary>Adds a document describing the policy, sent as a <c>Link</c> header.</summary>
    /// <param name="url">Where the document lives. Absolute, since a client resolves it without context.</param>
    /// <param name="title">Optional human-readable title.</param>
    /// <param name="type">Its media type; <c>text/html</c> by default.</param>
    public ApiVersionPolicy Link(string url, string? title = null, string? type = "text/html")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        this.links.Add(new ApiVersionPolicyLink(new Uri(url, UriKind.RelativeOrAbsolute), title, type));
        return this;
    }
}

/// <summary>One document linked from a deprecation or sunset policy.</summary>
public sealed record ApiVersionPolicyLink(Uri Url, string? Title, string? Type);
