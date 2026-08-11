using Microsoft.CodeAnalysis;

namespace Shiny.Net.HttpServer.SourceGenerators;

/// <summary>
/// Everything the generator can refuse to do, and why. These fire at build time, which is the whole
/// point of tier 3 — a route that cannot bind should be a red squiggle, not a 500 in production.
/// </summary>
static class Diagnostics
{
    const string Category = "Shiny.Net.HttpServer";

    public static readonly DiagnosticDescriptor InvalidRouteTemplate = new(
        "SWS001",
        "Invalid route template",
        "Route template '{0}' is invalid: {1}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public static readonly DiagnosticDescriptor UnbindableParameter = new(
        "SWS002",
        "Parameter cannot be bound",
        "Parameter '{0}' of type '{1}' cannot be bound. Give it a [FromServices] attribute if it is a dependency, implement IParsable<T> so it can be read from the route or query, or accept it as a JSON body with [FromBody].",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public static readonly DiagnosticDescriptor UnsupportedReturnType = new(
        "SWS003",
        "Unsupported endpoint return type",
        "Endpoint '{0}' returns '{1}', which cannot be turned into a response. Return IResult, IActionResult, a serializable value, a string, or void.",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public static readonly DiagnosticDescriptor EndpointNotAccessible = new(
        "SWS004",
        "Endpoint is not reachable from generated code",
        "{0} '{1}' must be public or internal and not static, abstract, or generic for an endpoint to be generated for it",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public static readonly DiagnosticDescriptor DuplicateRoute = new(
        "SWS005",
        "Duplicate route",
        "'{0} {1}' is declared more than once in this assembly; the second registration would throw at startup",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public static readonly DiagnosticDescriptor MissingJsonMetadata = new(
        "SWS006",
        "No JSON metadata for an endpoint type",
        "'{0}' crosses an endpoint boundary as JSON but no JsonSerializerContext in this assembly declares [JsonSerializable(typeof({1}))]. Serialization will fail at runtime; add it to a context so the metadata is compiled in.",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true
    );

    public static readonly DiagnosticDescriptor MultipleBodyParameters = new(
        "SWS007",
        "More than one body parameter",
        "Endpoint '{0}' binds more than one parameter from the request body. A request has only one body.",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public static readonly DiagnosticDescriptor RouteParameterNotInTemplate = new(
        "SWS008",
        "Route parameter is not in the template",
        "Parameter '{0}' binds from the route but '{1}' has no '{{{0}}}' token",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public static readonly DiagnosticDescriptor MinimalEndpointHandler = new(
        "SWS010",
        "Minimal endpoint handler is missing or ambiguous",
        "'{0}' implements IHttpEndpoint but {1}. A minimal endpoint needs exactly one public instance method named Handle or HandleAsync.",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public static readonly DiagnosticDescriptor MinimalEndpointVerb = new(
        "SWS011",
        "Minimal endpoint has no verb attribute",
        "'{0}' implements IHttpEndpoint but carries no [Get]/[Post]/[Put]/[Delete]/[Patch] attribute on the class",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public static readonly DiagnosticDescriptor UnusedRouteToken = new(
        "SWS009",
        "Route token is never bound",
        "Route template '{0}' captures '{{{1}}}' but endpoint '{2}' has no parameter to receive it",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true
    );
}
