namespace Shiny.Net.HttpServer.OpenApi;

/// <summary>Where a documented parameter is read from.</summary>
public enum ApiParameterLocation
{
    Path,
    Query,
    Header
}

/// <summary>One documented parameter.</summary>
public sealed class ApiParameter
{
    public required string Name { get; init; }

    public required ApiParameterLocation In { get; init; }

    /// <summary>
    /// The CLR type the parameter binds to. Its schema is derived from registered JSON metadata,
    /// so a documented type is a type the server can actually serialize.
    /// </summary>
    public required Type Type { get; init; }

    public bool Required { get; init; }

    public string? Description { get; init; }
}

/// <summary>The request body an operation accepts.</summary>
public sealed class ApiRequestBody
{
    public required Type Type { get; init; }

    public string ContentType { get; init; } = "application/json";

    public bool Required { get; init; } = true;

    public string? Description { get; init; }
}

/// <summary>One documented response.</summary>
public sealed class ApiResponse
{
    public required int StatusCode { get; init; }

    /// <summary>The response body type, or null for a response with no body.</summary>
    public Type? Type { get; init; }

    public string ContentType { get; init; } = "application/json";

    public string? Description { get; init; }
}

/// <summary>
/// Everything the OpenAPI document needs about one endpoint, attached to it as metadata.
/// <para>
/// The generator fills this in at compile time for tier-3 endpoints — it already knows the
/// parameter sources, the body type and the return type, because it just wrote the binder for
/// them. Raw routes get whatever <c>Describe()</c> says, plus path parameters inferred from the
/// route template.
/// </para>
/// </summary>
public sealed class ApiOperation
{
    /// <summary>One-line description. Taken from the method's XML doc comment when there is one.</summary>
    public string? Summary { get; set; }

    public string? Description { get; set; }

    /// <summary>Stable identifier for client generators. Defaults to <c>Class_Method</c>.</summary>
    public string? OperationId { get; set; }

    /// <summary>When true the endpoint is omitted from the document entirely.</summary>
    public bool Exclude { get; set; }

    /// <summary>Marks the operation deprecated in the document.</summary>
    public bool Deprecated { get; set; }

    /// <summary>
    /// True when the endpoint requires authorization. Filled in from the endpoint's
    /// <c>AuthorizationMetadata</c>, so an operation is documented as protected because it is
    /// protected — not because someone remembered to say so twice.
    /// </summary>
    public bool RequiresAuthorization { get; set; }

    public IList<string> Tags { get; } = [];

    public IList<ApiParameter> Parameters { get; } = [];

    public ApiRequestBody? RequestBody { get; set; }

    public IList<ApiResponse> Responses { get; } = [];
}
