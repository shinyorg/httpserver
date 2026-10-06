using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Shiny.Net.HttpServer.SourceGenerators;

/// <summary>Where a handler parameter's value comes from. Decided once, at compile time.</summary>
enum BindingSource
{
    Route,
    Query,
    Header,
    Body,
    Services,
    HttpContext,
    HttpRequest,
    HttpResponse,
    CancellationToken,

    /// <summary>The verified delivery from webhook verification — throws at request time when there is none.</summary>
    WebhookContext
}

/// <summary>How a bound string turns into the parameter's type.</summary>
enum ScalarKind
{
    /// <summary>Not a scalar — services, body, and the ambient context types.</summary>
    None,
    String,
    Parsable,
    NullableParsable,
    Enum,
    NullableEnum,
    StringArray,
    ParsableArray
}

/// <summary>What the handler's return value has to be turned into.</summary>
enum ResponsePayload
{
    /// <summary>void, Task, or ValueTask — the handler wrote the response itself, or there isn't one.</summary>
    None,
    Result,
    String,
    Json
}

sealed record ParameterModel(
    string Name,
    string TypeFullyQualified,
    string TypeDisplay,
    BindingSource Source,
    string BindingKey,
    ScalarKind ScalarKind,
    string? ElementTypeFullyQualified,
    string? DefaultLiteral,
    bool AllowsNull
) : IEquatable<ParameterModel>;

/// <summary>One <c>[Produces]</c> declaration, or a response inferred from the return type.</summary>
sealed record ApiResponseModel(
    int StatusCode,
    string? TypeFullyQualified,
    string? Description,
    string ContentType
) : IEquatable<ApiResponseModel>;

sealed record EndpointMethodModel(
    string HttpMethod,
    string RouteTemplate,
    string MethodName,
    EquatableArray<ParameterModel> Parameters,
    bool IsAwaitable,
    ResponsePayload Payload,
    string? PayloadTypeFullyQualified,
    string? Summary,
    EquatableArray<string> Tags,
    bool ApiExcluded,
    EquatableArray<ApiResponseModel> Responses,
    AuthorizationModel Authorization,
    EndpointPolicyModel Policies,
    ApiVersionModel Versions
) : IEquatable<EndpointMethodModel>;

/// <summary>
/// The CORS, rate limit and IP filter policies an endpoint named, as metadata for the middleware
/// that enforces them.
/// <para>
/// Unlike authorization, each of these is a single choice rather than a set of requirements: a
/// method's attribute replaces the class's instead of adding to it, because "two CORS policies" is
/// not a thing a request can have.
/// </para>
/// </summary>
sealed record EndpointPolicyModel(
    string? CorsPolicy,
    bool CorsDisabled,
    string? RateLimitPolicy,
    bool RateLimitDisabled,
    string? IpFilterPolicy,
    bool IpFilterDisabled,
    string? RequestTimeoutPolicy,
    int? RequestTimeoutMilliseconds,
    bool RequestTimeoutDisabled,
    string? OutputCachePolicy,
    int? OutputCacheSeconds,
    bool OutputCacheDisabled,
    bool AntiforgeryRequired,
    bool AntiforgeryDisabled,
    bool Idempotent,
    bool IdempotencyKeyRequired,
    int? IdempotencyExpirationSeconds,
    bool IdempotencyDisabled,
    bool ContentDigest,
    bool ContentDigestRequireRequest,
    bool ContentDigestAlwaysEmit,
    bool ContentDigestDisabled,
    string? WebhookVerifier = null,
    long? RequestSizeLimit = null,
    bool RequestSizeLimitDisabled = false
) : IEquatable<EndpointPolicyModel>
{
    public static readonly EndpointPolicyModel None = new(
        null, false, null, false, null, false, null, null, false, null, null, false, false, false,
        false, false, null, false, false, false, false, false
    );

    public bool HasCors => this.CorsDisabled || this.CorsPolicy is not null;

    public bool HasRateLimit => this.RateLimitDisabled || this.RateLimitPolicy is not null;

    public bool HasIpFilter => this.IpFilterDisabled || this.IpFilterPolicy is not null;

    public bool HasRequestTimeout
        => this.RequestTimeoutDisabled || this.RequestTimeoutPolicy is not null || this.RequestTimeoutMilliseconds is not null;

    public bool HasOutputCache
        => this.OutputCacheDisabled || this.OutputCachePolicy is not null || this.OutputCacheSeconds is not null;

    public bool HasAntiforgery => this.AntiforgeryRequired || this.AntiforgeryDisabled;

    public bool HasWebhook => this.WebhookVerifier is not null;

    public bool HasIdempotency => this.Idempotent || this.IdempotencyDisabled;

    public bool HasContentDigest => this.ContentDigest || this.ContentDigestDisabled;

    public bool HasRequestSizeLimit => this.RequestSizeLimitDisabled || this.RequestSizeLimit is not null;

    public bool HasAny
        => this.HasCors
            || this.HasRateLimit
            || this.HasIpFilter
            || this.HasRequestTimeout
            || this.HasOutputCache
            || this.HasAntiforgery
            || this.HasWebhook
            || this.HasIdempotency
            || this.HasContentDigest
            || this.HasRequestSizeLimit;
}

/// <summary>What <c>[Authorize]</c> and <c>[AllowAnonymous]</c> on a class and method add up to.</summary>
sealed record AuthorizationModel(
    bool Required,
    bool AllowAnonymous,
    EquatableArray<string> Policies,
    EquatableArray<string> Roles
) : IEquatable<AuthorizationModel>
{
    public static readonly AuthorizationModel None = new(
        false,
        false,
        EquatableArray<string>.Empty,
        EquatableArray<string>.Empty
    );

    /// <summary>True when there is anything worth emitting metadata for.</summary>
    public bool HasValue => this.Required || this.AllowAnonymous;
}

sealed record EndpointClassModel(
    string FullyQualifiedName,
    string DisplayName,
    string SafeName,
    EquatableArray<string> ConstructorParameterTypes,
    EquatableArray<EndpointMethodModel> Methods
) : IEquatable<EndpointClassModel>;

/// <summary>A <c>JsonSerializerContext</c> declared in the compilation and the types it covers.</summary>
sealed record JsonContextModel(
    string FullyQualifiedName,
    EquatableArray<string> SerializableTypes
) : IEquatable<JsonContextModel>;

/// <summary>
/// The API versions a generated endpoint declares, read from <c>[ApiVersion]</c>,
/// <c>[MapToApiVersion]</c> and <c>[ApiVersionNeutral]</c>. Versions are kept as their canonical
/// text so the model stays equatable and the emitted code reads like the attribute did.
/// </summary>
sealed record ApiVersionModel(
    EquatableArray<string> Supported,
    EquatableArray<string> Deprecated,
    EquatableArray<string> Mapped,
    bool Neutral
) : IEquatable<ApiVersionModel>
{
    public static readonly ApiVersionModel None = new(
        EquatableArray<string>.Empty,
        EquatableArray<string>.Empty,
        EquatableArray<string>.Empty,
        false
    );

    public bool HasValue => this.Neutral || this.Supported.Count > 0 || this.Deprecated.Count > 0 || this.Mapped.Count > 0;

    public bool IsVersioned => !this.Neutral && this.HasValue;

    /// <summary>
    /// True when this endpoint and <paramref name="other"/> would both claim some version with equal
    /// standing — both mapping it explicitly, or both serving it only because it was declared. An
    /// explicit mapping beside an implicit one is not a clash: the explicit one wins, as at runtime.
    /// </summary>
    public bool ClashesWith(ApiVersionModel other)
    {
        if (!this.IsVersioned || !other.IsVersioned)
            return true;

        return Keys(this.Mapped).Overlaps(Keys(other.Mapped))
            || Keys(this.Implicit()).Overlaps(Keys(other.Implicit()));
    }

    IEnumerable<string> Implicit()
        => this.Mapped.Count > 0 ? Enumerable.Empty<string>() : this.Supported.Concat(this.Deprecated);

    static HashSet<string> Keys(IEnumerable<string> versions)
        => new(versions.Select(v => Key(v)!), StringComparer.Ordinal);

    /// <summary>
    /// A comparison key — <c>1</c>, <c>v1</c> and <c>1.0</c> all give <c>1.0</c> — or null when the
    /// text is not a version the runtime would parse.
    /// <para>
    /// A second implementation of the runtime <c>ApiVersion</c> parser, for the same reason
    /// <see cref="RouteTemplateInfo"/> is one: the generator targets netstandard2.0 and cannot call
    /// the library. The syntax it accepts is kept identical.
    /// </para>
    /// </summary>
    public static string? Key(string? text)
    {
        if (text is null)
            return null;

        text = text.Trim();

        if (text.Length > 1 && (text[0] == 'v' || text[0] == 'V') && char.IsDigit(text[1]))
            text = text.Substring(1);

        if (text.Length == 0)
            return null;

        string? group = null;

        if (text.Length >= 10 && text[4] == '-' && text[7] == '-')
        {
            if (!DateTime.TryParseExact(text.Substring(0, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                return null;

            group = text.Substring(0, 10);
            text = text.Substring(10);

            if (text.Length == 0)
                return group;

            if (text[0] == '-')
                return IsStatus(text.Substring(1)) ? group + "-" + text.Substring(1).ToLowerInvariant() : null;

            if (text[0] != '.')
                return null;

            text = text.Substring(1);
        }

        string? status = null;
        var dash = text.IndexOf('-');
        if (dash >= 0)
        {
            status = text.Substring(dash + 1);
            if (!IsStatus(status))
                return null;

            text = text.Substring(0, dash);
        }

        var dot = text.IndexOf('.');
        var major = dot < 0 ? text : text.Substring(0, dot);
        var minor = dot < 0 ? "0" : text.Substring(dot + 1);

        if (!IsNumber(major) || !IsNumber(minor))
            return null;

        var key = int.Parse(major, CultureInfo.InvariantCulture) + "." + int.Parse(minor, CultureInfo.InvariantCulture);

        if (group is not null)
            key = group + "." + key;

        return status is null ? key : key + "-" + status.ToLowerInvariant();
    }

    static bool IsNumber(string text)
        => text.Length > 0
            && text.All(c => c >= '0' && c <= '9')
            && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out _);

    static bool IsStatus(string text)
        => text.Length > 0 && text.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'));
}
