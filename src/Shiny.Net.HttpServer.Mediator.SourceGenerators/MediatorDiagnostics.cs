using Microsoft.CodeAnalysis;

namespace Shiny.Net.HttpServer.SourceGenerators;

/// <summary>
/// Everything the mediator generator can refuse to do, and why.
/// <para>
/// The SWM prefix keeps these separate from the endpoint generator's SWS codes, so a project using
/// both can suppress one family without silencing the other.
/// </para>
/// </summary>
static class MediatorDiagnostics
{
    const string Category = "Shiny.Net.HttpServer.Mediator";

    public static readonly DiagnosticDescriptor InvalidRouteTemplate = new(
        "SWM001",
        "Invalid route template",
        "Route template '{0}' is invalid: {1}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public static readonly DiagnosticDescriptor NotAHandler = new(
        "SWM002",
        "Attribute is not on a mediator handler",
        "'{0}' carries a mediator HTTP attribute but does not implement IRequestHandler<,>, ICommandHandler<> or IStreamRequestHandler<,>",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public static readonly DiagnosticDescriptor UnbindableMember = new(
        "SWM003",
        "Contract member cannot be bound from the route or query string",
        "'{0}.{1}' is of type '{2}', which cannot be read from a route value or query string. Move this endpoint to POST/PUT so the contract is read from the JSON body, or give the member a type that implements IParsable<T>.",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public static readonly DiagnosticDescriptor UnconstructableContract = new(
        "SWM004",
        "Contract cannot be constructed by generated code",
        "'{0}' has no public constructor the generator can call. A contract bound from the route or query needs a public constructor whose parameters are all bindable, or a public parameterless constructor with settable properties.",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public static readonly DiagnosticDescriptor DuplicateRoute = new(
        "SWM005",
        "Duplicate route",
        "'{0} {1}' is declared more than once in this assembly; the second registration would throw at startup",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public static readonly DiagnosticDescriptor MissingJsonMetadata = new(
        "SWM006",
        "No JSON metadata for a mediator contract",
        "'{0}' crosses an endpoint boundary as JSON but no JsonSerializerContext in this assembly declares [JsonSerializable(typeof({1}))]. Serialization will fail at runtime; add it to a context so the metadata is compiled in.",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true
    );

    public static readonly DiagnosticDescriptor StreamVerb = new(
        "SWM007",
        "Stream request published on an unsupported verb",
        "'{0}' is a stream request published as {1}. A Server-Sent Events response is a GET; use [MediatorHttpGet] instead.",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public static readonly DiagnosticDescriptor RouteTokenNotApplied = new(
        "SWM008",
        "Route token cannot be applied to the contract",
        "Route template '{0}' captures '{{{1}}}' and '{2}' has a matching member, but the contract is read from the request body and the member cannot be assigned afterwards. Make '{2}' a record, give the member a setter, or drop the token from the template.",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true
    );

    public static readonly DiagnosticDescriptor UnusedRouteToken = new(
        "SWM009",
        "Route token is never bound",
        "Route template '{0}' captures '{{{1}}}' but contract '{2}' has no member to receive it",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true
    );

    public static readonly DiagnosticDescriptor HandlerNotAccessible = new(
        "SWM010",
        "Handler is not reachable from generated code",
        "'{0}' must be public or internal and not generic for mediator endpoints to be generated for it",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );
}
