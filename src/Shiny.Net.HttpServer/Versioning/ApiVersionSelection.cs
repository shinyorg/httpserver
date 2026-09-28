using System.Globalization;
using Shiny.Net.HttpServer.Routing;

namespace Shiny.Net.HttpServer.Versioning;

/// <summary>Why a request could not be given one of a route's versions.</summary>
enum ApiVersionError
{
    None,

    /// <summary>No version was named, and none is assumed.</summary>
    Unspecified,

    /// <summary>A version was named but is not a version.</summary>
    Invalid,

    /// <summary>Two different versions were named — say one in the query and another in a header.</summary>
    Ambiguous,

    /// <summary>A valid version the route does not serve.</summary>
    Unsupported
}

readonly record struct ApiVersionSelectionResult(
    RouteEndpoint? Endpoint,
    ApiVersion? Version,
    ApiVersionError Error,
    string? RequestedText
);

/// <summary>
/// Picks which of a route's endpoints serves a request, by the version the request asked for, and
/// writes what versioning adds to the response.
/// <para>
/// This sits between matching and invocation rather than inside the route table: the table only
/// sees a method and a path, and the version can be in the query, a header or a media type. The
/// router hands back every endpoint registered on the route — normally one — and this chooses.
/// A route none of whose endpoints are versioned never reaches the readers at all, so an app that
/// does not version anything pays for one metadata lookup per request.
/// </para>
/// </summary>
static class ApiVersionSelection
{
    static readonly object RequestedVersionKey = new();

    public static bool RequiresSelection(IReadOnlyList<RouteEndpoint> candidates)
        => candidates.Count > 1
            || (candidates.Count == 1 && candidates[0].GetMetadata<ApiVersionMetadata>() is { IsVersioned: true });

    public static ApiVersionSelectionResult Select(
        ApiVersioningOptions options,
        HttpContext context,
        IReadOnlyList<RouteEndpoint> candidates
    )
    {
        var raw = ReadRequested(options, context, candidates[0]);

        ApiVersion? requested = null;
        string? requestedText = null;

        foreach (var text in raw)
        {
            if (!ApiVersion.TryParse(text, out var parsed))
                return new(null, null, ApiVersionError.Invalid, text);

            if (requested is null)
            {
                requested = parsed;
                requestedText = text;
            }
            else if (requested != parsed)
            {
                return new(null, null, ApiVersionError.Ambiguous, string.Join(", ", raw));
            }
        }

        RouteEndpoint? neutral = null;

        foreach (var candidate in candidates)
        {
            if (candidate.GetMetadata<ApiVersionMetadata>() is not { IsVersioned: true })
                neutral ??= candidate;
        }

        if (requested is null)
        {
            if (!options.AssumeDefaultVersionWhenUnspecified)
            {
                // A neutral endpoint serves "no version" as happily as any other.
                return neutral is not null
                    ? new(neutral, null, ApiVersionError.None, null)
                    : new(null, null, ApiVersionError.Unspecified, null);
            }

            requested = options.ApiVersionSelector?.SelectVersion(context.Request, ImplementedVersions(candidates))
                ?? options.DefaultApiVersion;
        }

        // An endpoint that maps the version explicitly beats one serving it only because its set
        // declares it — one class declaring 1.0 and 2.0, a general handler, and a 2.0 override.
        var selected = Pick(candidates, requested, explicitOnly: true)
            ?? Pick(candidates, requested, explicitOnly: false)
            ?? neutral;

        return selected is null
            ? new(null, requested, ApiVersionError.Unsupported, requestedText ?? requested.ToString())
            : new(selected, requested, ApiVersionError.None, requestedText);
    }

    static RouteEndpoint? Pick(IReadOnlyList<RouteEndpoint> candidates, ApiVersion requested, bool explicitOnly)
    {
        RouteEndpoint? selected = null;

        foreach (var candidate in candidates)
        {
            if (candidate.GetMetadata<ApiVersionMetadata>() is not { IsVersioned: true } metadata ||
                !metadata.IsMappedTo(requested) ||
                metadata.IsExplicitlyMappedTo(requested) != explicitOnly)
                continue;

            // The route table refuses two endpoints claiming the same version at registration. This
            // is the case it cannot see: a version attached to the most recently mapped route after
            // the table was built. Failing loudly beats serving whichever came first.
            if (selected is not null)
                throw new InvalidOperationException(
                    $"'{selected.DisplayName}' and '{candidate.DisplayName}' both serve API version {requested}. " +
                    "Each version of a route must be served by exactly one endpoint."
                );

            selected = candidate;
        }

        return selected;
    }

    /// <summary>
    /// Runs the configured reader. The URL-segment reader needs to know which template matched to
    /// find the version segment in it; every candidate shares the one template, so the first stands
    /// in for all of them while the reader runs.
    /// </summary>
    static IReadOnlyList<string> ReadRequested(ApiVersioningOptions options, HttpContext context, RouteEndpoint template)
    {
        var previous = context.Endpoint;
        context.Endpoint = template;

        try
        {
            return options.ApiVersionReader.Read(context.Request);
        }
        finally
        {
            context.Endpoint = previous;
        }
    }

    /// <summary>Every version any candidate serves, ascending.</summary>
    static IReadOnlyList<ApiVersion> ImplementedVersions(IReadOnlyList<RouteEndpoint> candidates)
    {
        var set = new SortedSet<ApiVersion>();

        foreach (var candidate in candidates)
        {
            if (candidate.GetMetadata<ApiVersionMetadata>() is { IsVersioned: true } metadata)
                set.UnionWith(metadata.ImplementedVersions);
        }

        return [.. set];
    }

    /// <summary>The version the request is being served at, recorded for the handler.</summary>
    public static void SetRequestedVersion(HttpContext context, ApiVersion version)
        => context.Items[RequestedVersionKey] = version;

    public static ApiVersion? GetRequestedVersion(HttpContext context)
        => context.Items.TryGetValue(RequestedVersionKey, out var value) ? value as ApiVersion : null;

    // ---- Response ----

    /// <summary>
    /// Adds <c>api-supported-versions</c> / <c>api-deprecated-versions</c> (when reporting is on) and
    /// the policy headers for the version being served. Called before the endpoint runs, since
    /// headers cannot be added once the endpoint has started its response.
    /// </summary>
    public static void WriteHeaders(
        ApiVersioningOptions options,
        HttpContext context,
        IReadOnlyList<RouteEndpoint> candidates,
        ApiVersion? version
    )
    {
        var headers = context.Response.Headers;

        if (options.ReportApiVersions)
        {
            var (supported, deprecated) = Report(candidates);

            if (supported.Count > 0)
                headers[ApiVersionHeaderNames.SupportedVersions] = string.Join(", ", supported);

            if (deprecated.Count > 0)
                headers[ApiVersionHeaderNames.DeprecatedVersions] = string.Join(", ", deprecated);
        }

        if (version is null || options.Policies.IsEmpty)
            return;

        if (options.Policies.Deprecations.TryGetValue(version, out var deprecation))
        {
            // RFC 9745: a Structured Field Date — '@' then seconds since the epoch.
            if (deprecation.Date is { } date)
                headers[ApiVersionHeaderNames.Deprecation] = "@" + date.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

            AppendLinks(headers, deprecation, "deprecation");
        }

        if (options.Policies.Sunsets.TryGetValue(version, out var sunset))
        {
            // RFC 8594: an HTTP-date.
            if (sunset.Date is { } date)
                headers[ApiVersionHeaderNames.Sunset] = date.UtcDateTime.ToString("r", CultureInfo.InvariantCulture);

            AppendLinks(headers, sunset, "sunset");
        }
    }

    static void AppendLinks(HeaderDictionary headers, ApiVersionPolicy policy, string relation)
    {
        foreach (var link in policy.Links)
        {
            var value = $"<{link.Url.OriginalString}>; rel=\"{relation}\"";

            if (link.Type is { Length: > 0 } type)
                value += $"; type=\"{type}\"";

            if (link.Title is { Length: > 0 } title)
                value += $"; title=\"{title.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

            headers.Append(ApiVersionHeaderNames.Link, value);
        }
    }

    /// <summary>
    /// What the route advertises: the union of every candidate's declared versions. A version
    /// deprecated in one declaration but supported in another counts as supported.
    /// </summary>
    internal static (List<ApiVersion> Supported, List<ApiVersion> Deprecated) Report(IReadOnlyList<RouteEndpoint> candidates)
    {
        var supported = new SortedSet<ApiVersion>();
        var deprecated = new SortedSet<ApiVersion>();

        foreach (var candidate in candidates)
        {
            if (candidate.GetMetadata<ApiVersionMetadata>() is not { IsVersioned: true } metadata)
                continue;

            supported.UnionWith(metadata.SupportedVersions);
            deprecated.UnionWith(metadata.DeprecatedVersions);

            foreach (var mapped in metadata.MappedVersions)
            {
                if (!metadata.DeprecatedVersions.Contains(mapped))
                    supported.Add(mapped);
            }
        }

        deprecated.ExceptWith(supported);
        return ([.. supported], [.. deprecated]);
    }

    /// <summary>Answers a request that could not be given a version, as RFC 9457 problem details.</summary>
    public static ValueTask WriteErrorAsync(
        ApiVersioningOptions options,
        HttpContext context,
        IReadOnlyList<RouteEndpoint> candidates,
        ApiVersionSelectionResult result
    )
    {
        // Reported on the error as well: a client told its version is unsupported most wants to know
        // which ones are.
        WriteHeaders(options, context, candidates, version: null);

        var path = context.Request.Path;

        // Type URIs and codes match Asp.Versioning's, so a client already written against an ASP.NET
        // Core API recognises the errors without change.
        var problem = result.Error switch
        {
            ApiVersionError.Unspecified => Problem(
                StatusCodes.Status400BadRequest,
                "https://docs.api-versioning.org/problems#unspecified",
                "Unspecified API version",
                $"An API version is required, but was not specified for '{path}'.",
                "ApiVersionUnspecified"
            ),

            ApiVersionError.Invalid => Problem(
                StatusCodes.Status400BadRequest,
                "https://docs.api-versioning.org/problems#invalid",
                "Invalid API version",
                $"The HTTP resource that matches the request URI '{path}' does not support the API version '{result.RequestedText}', which is not a valid version.",
                "InvalidApiVersion"
            ),

            ApiVersionError.Ambiguous => Problem(
                StatusCodes.Status400BadRequest,
                "https://docs.api-versioning.org/problems#ambiguous",
                "Ambiguous API version",
                $"The following API versions were requested: {result.RequestedText}. At most, only a single API version may be specified. Please update the intended API version and retry the request.",
                "AmbiguousApiVersion"
            ),

            _ => Problem(
                options.UnsupportedApiVersionStatusCode,
                "https://docs.api-versioning.org/problems#unsupported",
                "Unsupported API version",
                $"The HTTP resource that matches the request URI '{path}' does not support the API version '{result.RequestedText}'.",
                "UnsupportedApiVersion"
            )
        };

        return new ProblemResult(problem).ExecuteAsync(context);
    }

    static ProblemDetails Problem(int status, string type, string title, string detail, string code)
    {
        var problem = new ProblemDetails
        {
            Status = status,
            Type = type,
            Title = title,
            Detail = detail
        };

        problem.Extensions["code"] = code;
        return problem;
    }
}

/// <summary>The response headers API versioning writes.</summary>
public static class ApiVersionHeaderNames
{
    /// <summary>Versions the route serves and is not retiring.</summary>
    public const string SupportedVersions = "api-supported-versions";

    /// <summary>Versions the route still serves but has deprecated.</summary>
    public const string DeprecatedVersions = "api-deprecated-versions";

    /// <summary>RFC 9745.</summary>
    public const string Deprecation = "Deprecation";

    /// <summary>RFC 8594.</summary>
    public const string Sunset = "Sunset";

    /// <summary>RFC 8288, carrying the policies' documents.</summary>
    public const string Link = "Link";
}
