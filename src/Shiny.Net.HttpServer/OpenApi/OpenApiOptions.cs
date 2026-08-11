using Shiny.Net.HttpServer.Routing;

namespace Shiny.Net.HttpServer.OpenApi;

/// <summary>What goes in the document, beyond what the route table already knows.</summary>
public sealed class OpenApiOptions
{
    public string Title { get; set; } = "API";

    public string Version { get; set; } = "1.0.0";

    public string? Description { get; set; }

    /// <summary>
    /// Base URLs the API is served from. Left empty, no <c>servers</c> block is written, which
    /// tooling reads as "relative to wherever you fetched this from" — usually the right answer for
    /// a server whose address is a tunnel URL decided at runtime.
    /// </summary>
    public IList<string> Servers { get; } = [];

    /// <summary>Pretty-printed by default; documents are read by people at least as often as by tools.</summary>
    public bool Indented { get; set; } = true;

    /// <summary>
    /// When true (the default) raw routes with no description still appear, with path parameters
    /// inferred from the template. Set false to publish only what was described deliberately.
    /// </summary>
    public bool IncludeUndescribedRoutes { get; set; } = true;

    /// <summary>
    /// Security schemes the document advertises, keyed by the name operations reference.
    /// </summary>
    public IDictionary<string, ApiSecurityScheme> SecuritySchemes { get; }
        = new Dictionary<string, ApiSecurityScheme>(StringComparer.Ordinal);

    /// <summary>
    /// The scheme applied to operations that require authorization. Set automatically by
    /// <see cref="AddBearerAuthentication"/>.
    /// </summary>
    public string? DefaultSecurityScheme { get; set; }

    /// <summary>
    /// Adds a <c>Bearer</c> JWT scheme and makes it the default, so every endpoint carrying
    /// <c>[Authorize]</c> is documented as needing a token — and the "Authorize" button in whatever
    /// UI reads this actually does something.
    /// </summary>
    public OpenApiOptions AddBearerAuthentication(string name = "bearer", string? description = null)
    {
        this.SecuritySchemes[name] = new ApiSecurityScheme
        {
            Type = "http",
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = description ?? "JWT bearer token. Send it as: Authorization: Bearer <token>"
        };

        this.DefaultSecurityScheme = name;
        return this;
    }

    /// <summary>
    /// Last chance to adjust an operation before it is written — for conventions that apply across
    /// every endpoint, like adding a 401 wherever an auth middleware is in play.
    /// </summary>
    public Action<ApiOperation, RouteEndpoint>? ConfigureOperation { get; set; }
}

/// <summary>How callers authenticate, as OpenAPI describes it.</summary>
public sealed class ApiSecurityScheme
{
    /// <summary>OpenAPI scheme type: <c>http</c>, <c>apiKey</c>, or <c>openIdConnect</c>.</summary>
    public required string Type { get; init; }

    /// <summary>For <c>http</c>: the HTTP auth scheme, e.g. <c>bearer</c> or <c>basic</c>.</summary>
    public string? Scheme { get; init; }

    /// <summary>For bearer schemes: a hint at the token format, e.g. <c>JWT</c>.</summary>
    public string? BearerFormat { get; init; }

    /// <summary>For <c>apiKey</c>: where the key travels — <c>header</c>, <c>query</c> or <c>cookie</c>.</summary>
    public string? In { get; init; }

    /// <summary>For <c>apiKey</c>: the header or parameter name.</summary>
    public string? Name { get; init; }

    public string? Description { get; init; }
}
