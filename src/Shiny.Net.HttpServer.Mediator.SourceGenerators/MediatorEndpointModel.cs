using System;

namespace Shiny.Net.HttpServer.SourceGenerators;

/// <summary>Which mediator shape a handler implements, which decides how the result is written.</summary>
enum MediatorKind
{
    /// <summary><c>IRequestHandler&lt;TRequest, TResult&gt;</c> — one result, written as JSON.</summary>
    Request,

    /// <summary><c>ICommandHandler&lt;TCommand&gt;</c> — no result, answered with a status code.</summary>
    Command,

    /// <summary><c>IStreamRequestHandler&lt;TRequest, TResult&gt;</c> — many results, written as SSE.</summary>
    Stream
}

/// <summary>
/// How one member of a contract gets its value.
/// <para>
/// <see cref="IsConstructorParameter"/> decides where it lands in the emitted construction: a
/// positional record is built by calling its constructor, and anything left over is assigned in an
/// object initializer.
/// </para>
/// </summary>
sealed record MediatorMemberModel(
    string MemberName,
    string TypeFullyQualified,
    string TypeDisplay,
    BindingSource Source,
    string BindingKey,
    ScalarKind ScalarKind,
    string? ElementTypeFullyQualified,
    string? DefaultLiteral,
    bool IsConstructorParameter
) : IEquatable<MediatorMemberModel>;

/// <summary>One published endpoint: a contract, a verb, a route, and how to fill the contract in.</summary>
sealed record MediatorEndpointModel(
    string HttpMethod,
    string RouteTemplate,
    MediatorKind Kind,
    string HandlerDisplayName,
    string ContractFullyQualified,
    string ContractDisplay,
    string? ResultFullyQualified,

    /// <summary>True for POST/PUT/PATCH, where the contract is deserialized from the request body.</summary>
    bool BindsFromBody,

    /// <summary>
    /// Route tokens applied over a body-bound contract, e.g. the <c>{id}</c> in
    /// <c>PUT /widgets/{id}</c>. Empty for everything else.
    /// </summary>
    EquatableArray<MediatorMemberModel> RouteOverrides,

    /// <summary>True when <see cref="RouteOverrides"/> are applied with a <c>with</c> expression.</summary>
    bool ContractIsRecord,

    /// <summary>Members bound from the route and query string. Empty when body-bound.</summary>
    EquatableArray<MediatorMemberModel> Members,

    string? OperationId,
    string? Summary,
    string? Description,
    EquatableArray<string> Tags,
    bool ApiExcluded,
    string? EventName,
    int SuccessStatusCode,
    AuthorizationModel Authorization,
    EndpointPolicyModel Policies
) : IEquatable<MediatorEndpointModel>;

/// <summary>Every endpoint published by one handler class.</summary>
sealed record MediatorHandlerModel(
    string FullyQualifiedName,
    string DisplayName,
    string SafeName,
    EquatableArray<MediatorEndpointModel> Endpoints
) : IEquatable<MediatorHandlerModel>;
