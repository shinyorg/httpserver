using Microsoft.CodeAnalysis;

namespace Shiny.Net.HttpServer.SourceGenerators;

/// <summary>
/// Everything the switchboard generator refuses to do. A method a client cannot call correctly
/// should be a red squiggle, not a 400 the first time someone tries.
/// </summary>
static class SwitchboardDiagnostics
{
    const string Category = "Shiny.Net.HttpServer.Switchboard";

    public static readonly DiagnosticDescriptor BoardNotSupported = new(
        "SWB001",
        "Switchboard cannot be dispatched",
        "Switchboard '{0}' cannot have a generated dispatcher: {1}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public static readonly DiagnosticDescriptor NoConstructor = new(
        "SWB002",
        "Switchboard has no usable constructor",
        "Switchboard '{0}' needs a public or internal constructor for the generated factory to call",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public static readonly DiagnosticDescriptor DuplicateMethodName = new(
        "SWB003",
        "Duplicate switchboard method name",
        "Switchboard '{0}' already has a method called '{1}'. Clients call methods by name, so overloads are not possible — rename one with [LineMethod(\"…\")] or hide it with [NotALineMethod].",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public static readonly DiagnosticDescriptor UnsupportedParameter = new(
        "SWB004",
        "Switchboard method parameter is not supported",
        "Parameter '{0}' of '{1}' cannot be bound: {2}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public static readonly DiagnosticDescriptor UnsupportedReturn = new(
        "SWB005",
        "Switchboard method return type is not supported",
        "'{0}' returns '{1}': {2}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public static readonly DiagnosticDescriptor GenericMethod = new(
        "SWB006",
        "Switchboard methods cannot be generic",
        "'{0}' is generic. A client sends JSON, not type arguments — make it non-generic or mark it [NotALineMethod].",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public static readonly DiagnosticDescriptor UnsupportedClientMember = new(
        "SWB008",
        "Client interface member is not supported",
        "'{0}' cannot be called on a client: {1}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true
    );

    public static readonly DiagnosticDescriptor ContractNotCallable = new(
        "SWB009",
        "Contract method is not callable by clients",
        "'{0}' implements '{1}', but clients cannot call it as '{2}': {3}",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true
    );

    public static readonly DiagnosticDescriptor ContractTypeNotExported = new(
        "SWB010",
        "Type cannot be exported with the contract",
        "'{0}' is used by the contract of '{1}' but cannot be exported with it: {2}. The exported file refers to it by name, so the client must reference it some other way.",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true
    );

    public static readonly DiagnosticDescriptor AllowAnonymousHasNoEffect = new(
        "SWB007",
        "[AllowAnonymous] on a switchboard method has no effect",
        "'{0}' is marked [AllowAnonymous], but its switchboard requires authorization and every call goes through the switchboard's routes. The class requirement still applies.",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true
    );
}
