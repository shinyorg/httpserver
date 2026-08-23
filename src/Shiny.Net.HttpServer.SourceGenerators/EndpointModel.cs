using System;

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
    CancellationToken
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
    EndpointPolicyModel Policies
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
    bool AntiforgeryDisabled
) : IEquatable<EndpointPolicyModel>
{
    public static readonly EndpointPolicyModel None = new(
        null, false, null, false, null, false, null, null, false, null, null, false, false, false
    );

    public bool HasCors => this.CorsDisabled || this.CorsPolicy is not null;

    public bool HasRateLimit => this.RateLimitDisabled || this.RateLimitPolicy is not null;

    public bool HasIpFilter => this.IpFilterDisabled || this.IpFilterPolicy is not null;

    public bool HasRequestTimeout
        => this.RequestTimeoutDisabled || this.RequestTimeoutPolicy is not null || this.RequestTimeoutMilliseconds is not null;

    public bool HasOutputCache
        => this.OutputCacheDisabled || this.OutputCachePolicy is not null || this.OutputCacheSeconds is not null;

    public bool HasAntiforgery => this.AntiforgeryRequired || this.AntiforgeryDisabled;

    public bool HasAny
        => this.HasCors
            || this.HasRateLimit
            || this.HasIpFilter
            || this.HasRequestTimeout
            || this.HasOutputCache
            || this.HasAntiforgery;
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
