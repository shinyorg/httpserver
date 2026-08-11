using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Shiny.Net.HttpServer.SourceGenerators;

/// <summary>
/// Publishes Shiny.Mediator handlers as HTTP endpoints.
/// <para>
/// The ASP.NET integration binds a contract with <c>[AsParameters]</c> and <c>[FromBody]</c>, which
/// is reflection over the delegate's parameters and is marked <c>RequiresDynamicCode</c> for exactly
/// that reason. Nothing here may do that, so the binding is written out instead: every member of a
/// contract gets an explicit read and an explicit parse, decided at compile time. A contract that
/// cannot be bound is a build error rather than a 500.
/// </para>
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class MediatorEndpointGenerator : IIncrementalGenerator
{
    const string GroupAttributeName = "Shiny.Net.HttpServer.Mediator.MediatorHttpGroupAttribute";
    const string AttributeNamespace = "Shiny.Net.HttpServer.Mediator.";
    const string JsonSerializableAttributeName = "System.Text.Json.Serialization.JsonSerializableAttribute";

    const string RequestHandler = "Shiny.Mediator.IRequestHandler<TRequest, TResult>";
    const string CommandHandler = "Shiny.Mediator.ICommandHandler<TCommand>";
    const string StreamHandler = "Shiny.Mediator.IStreamRequestHandler<TRequest, TResult>";

    static readonly Dictionary<string, string> VerbsByAttribute = new(StringComparer.Ordinal)
    {
        ["MediatorHttpGetAttribute"] = "GET",
        ["MediatorHttpPostAttribute"] = "POST",
        ["MediatorHttpPutAttribute"] = "PUT",
        ["MediatorHttpPatchAttribute"] = "PATCH",
        ["MediatorHttpDeleteAttribute"] = "DELETE"
    };

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var assemblyName = context.CompilationProvider
            .Select(static (compilation, _) => MediatorNaming.ToIdentifier(compilation.AssemblyName ?? "Generated"));

        // One provider over attributed methods rather than five ForAttributeWithMetadataName
        // registrations, because a single Handle method can carry several verbs and they have to be
        // read together to keep their order stable.
        var methods = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is MethodDeclarationSyntax { AttributeLists.Count: > 0 },
                static (ctx, token) => BuildMethod(ctx, token)
            )
            .Where(static r => r is not null)
            .Select(static (r, _) => r!);

        var jsonContexts = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                JsonSerializableAttributeName,
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, _) => BuildJsonContext(ctx)
            )
            .Where(static c => c is not null)
            .Select(static (c, _) => c!);

        context.RegisterSourceOutput(methods, static (spc, result) =>
        {
            foreach (var diagnostic in result.Diagnostics)
                spc.ReportDiagnostic(diagnostic.ToDiagnostic());
        });

        var valid = methods
            .Where(static r => r.Value is not null)
            .Select(static (r, _) => r.Value!);

        context.RegisterSourceOutput(
            valid.Collect().Combine(assemblyName).Combine(jsonContexts.Collect()),
            static (spc, pair) => Emit(spc, pair.Left.Left, pair.Left.Right, pair.Right)
        );
    }

    static void Emit(
        SourceProductionContext context,
        ImmutableArray<MediatorHandlerModel> handlers,
        string assembly,
        ImmutableArray<JsonContextModel> jsonContexts
    )
    {
        if (handlers.Length == 0)
            return;

        // A handler class with two Handle methods arrives as two models; they are one registration.
        var merged = handlers
            .GroupBy(h => h.FullyQualifiedName, StringComparer.Ordinal)
            .Select(group => new MediatorHandlerModel(
                group.Key,
                group.First().DisplayName,
                group.First().SafeName,
                group.SelectMany(h => h.Endpoints).ToEquatableArray()
            ))
            .OrderBy(h => h.FullyQualifiedName, StringComparer.Ordinal)
            .ToList();

        foreach (var diagnostic in FindDuplicateRoutes(merged))
            context.ReportDiagnostic(diagnostic.ToDiagnostic());

        foreach (var diagnostic in FindMissingJsonMetadata(merged, jsonContexts))
            context.ReportDiagnostic(diagnostic.ToDiagnostic());

        context.AddSource(
            $"MediatorEndpoints.{assembly}.g.cs",
            MediatorEndpointEmitter.Emit(merged, assembly)
        );

        // The endpoint generator emits an identical registration when it is also referenced, and
        // JsonTypeInfoRegistry.Register ignores a context it already holds — so both being present
        // is harmless, and a project using only this package still gets its metadata registered.
        if (jsonContexts.Length > 0)
        {
            context.AddSource(
                "ShinyNetHttpServerMediatorJsonRegistration.g.cs",
                MediatorEndpointEmitter.EmitJsonRegistration(jsonContexts)
            );
        }
    }

    static Result<MediatorHandlerModel>? BuildMethod(GeneratorSyntaxContext context, CancellationToken token)
    {
        var syntax = (MethodDeclarationSyntax)context.Node;

        if (context.SemanticModel.GetDeclaredSymbol(syntax, token) is not IMethodSymbol method)
            return null;

        var attributes = method
            .GetAttributes()
            .Where(a => a.AttributeClass is { } c
                && c.ToDisplayString().StartsWith(AttributeNamespace, StringComparison.Ordinal)
                && VerbsByAttribute.ContainsKey(c.Name))
            .ToList();

        if (attributes.Count == 0)
            return null;

        var handler = method.ContainingType;
        if (handler is null)
            return null;

        var diagnostics = ImmutableArray.CreateBuilder<DiagnosticInfo>();

        if (handler.IsGenericType || handler.DeclaredAccessibility is Accessibility.Private or Accessibility.Protected)
        {
            diagnostics.Add(DiagnosticInfo.Create(
                MediatorDiagnostics.HandlerNotAccessible,
                handler,
                handler.Name
            ));

            return Result<MediatorHandlerModel>.Fail(diagnostics.ToArray());
        }

        // The mediator's shape is read off the method rather than the interface list: a handler can
        // implement several, and only this method's signature says which one this endpoint is.
        var kind = ClassifyMethod(method, out var resultType);
        if (kind is null || method.Parameters.Length == 0)
        {
            diagnostics.Add(DiagnosticInfo.Create(MediatorDiagnostics.NotAHandler, method, handler.Name));
            return Result<MediatorHandlerModel>.Fail(diagnostics.ToArray());
        }

        if (!ImplementsHandler(handler, kind.Value))
        {
            diagnostics.Add(DiagnosticInfo.Create(MediatorDiagnostics.NotAHandler, method, handler.Name));
            return Result<MediatorHandlerModel>.Fail(diagnostics.ToArray());
        }

        var contract = method.Parameters[0].Type;
        var group = handler.FindAttribute(GroupAttributeName);
        var prefix = group?.GetConstructorString(0) ?? string.Empty;

        var endpoints = ImmutableArray.CreateBuilder<MediatorEndpointModel>();

        foreach (var attribute in attributes)
        {
            var endpoint = BuildEndpoint(
                handler,
                method,
                contract,
                kind.Value,
                resultType,
                attribute,
                group,
                prefix,
                diagnostics
            );

            if (endpoint is not null)
                endpoints.Add(endpoint);
        }

        if (endpoints.Count == 0)
            return Result<MediatorHandlerModel>.Fail(diagnostics.ToArray());

        var model = new MediatorHandlerModel(
            handler.ToFullyQualified(),
            handler.Name,
            MediatorNaming.ToIdentifier(handler.ToDisplayString()),
            endpoints.ToEquatable()
        );

        return Result<MediatorHandlerModel>.Ok(model, diagnostics.ToImmutable());
    }

    static MediatorEndpointModel? BuildEndpoint(
        INamedTypeSymbol handler,
        IMethodSymbol method,
        ITypeSymbol contract,
        MediatorKind kind,
        ITypeSymbol? resultType,
        AttributeData attribute,
        AttributeData? group,
        string prefix,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics
    )
    {
        var verb = VerbsByAttribute[attribute.AttributeClass!.Name];
        var template = attribute.GetConstructorString(0) ?? string.Empty;
        var combined = RouteTemplateInfo.Combine(prefix, template);

        var route = RouteTemplateInfo.TryParse(combined, out var error);
        if (route is null)
        {
            diagnostics.Add(DiagnosticInfo.Create(
                MediatorDiagnostics.InvalidRouteTemplate,
                method,
                combined,
                error
            ));

            return null;
        }

        if (kind == MediatorKind.Stream && verb != "GET")
        {
            diagnostics.Add(DiagnosticInfo.Create(
                MediatorDiagnostics.StreamVerb,
                method,
                contract.ToFriendly(),
                verb
            ));

            return null;
        }

        var bindsFromBody = verb is "POST" or "PUT" or "PATCH";
        var named = contract as INamedTypeSymbol;

        var members = bindsFromBody
            ? EquatableArray<MediatorMemberModel>.Empty
            : BuildMembers(contract, named, route, method, diagnostics);

        var overrides = bindsFromBody
            ? BuildRouteOverrides(contract, named, route, method, diagnostics)
            : EquatableArray<MediatorMemberModel>.Empty;

        if (!bindsFromBody && members.Count == 0 && route.ParameterNames.Count > 0)
        {
            foreach (var name in route.ParameterNames)
                diagnostics.Add(DiagnosticInfo.Create(
                    MediatorDiagnostics.UnusedRouteToken,
                    method,
                    combined,
                    name,
                    contract.ToFriendly()
                ));
        }

        return new MediatorEndpointModel(
            verb,
            route.Template,
            kind,
            handler.Name,
            contract.ToFullyQualified(),
            contract.ToFriendly(),
            resultType?.ToFullyQualified(),
            bindsFromBody,
            overrides,
            named?.IsRecord ?? false,
            members,
            attribute.GetNamedString("OperationId") ?? $"{handler.Name}_{contract.ToFriendly()}",
            attribute.GetNamedString("Summary") ?? group?.GetNamedString("Summary"),
            attribute.GetNamedString("Description") ?? group?.GetNamedString("Description"),
            MergeTags(group, attribute),
            GetNamedBool(attribute, "ExcludeFromDescription") || GetNamedBool(group, "ExcludeFromDescription"),
            attribute.GetNamedString("EventName"),
            GetNamedInt(attribute, "SuccessStatusCode") ?? 204,
            BuildAuthorization(group, attribute),
            BuildPolicies(group, attribute)
        );
    }

    /// <summary>
    /// Works out how a contract is built and where each member's value comes from.
    /// <para>
    /// The constructor with the most parameters wins, which is what picks a record's primary
    /// constructor. Anything settable that the constructor did not cover is assigned afterwards in
    /// an object initializer, so a plain class with properties works too.
    /// </para>
    /// </summary>
    static EquatableArray<MediatorMemberModel> BuildMembers(
        ITypeSymbol contract,
        INamedTypeSymbol? named,
        RouteTemplateInfo route,
        IMethodSymbol method,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics
    )
    {
        if (named is null)
            return EquatableArray<MediatorMemberModel>.Empty;

        var constructors = named.InstanceConstructors
            .Where(c => c.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal)
            .OrderByDescending(c => c.Parameters.Length)
            .ToList();

        if (constructors.Count == 0)
        {
            diagnostics.Add(DiagnosticInfo.Create(
                MediatorDiagnostics.UnconstructableContract,
                method,
                contract.ToFriendly()
            ));

            return EquatableArray<MediatorMemberModel>.Empty;
        }

        var primary = constructors[0];
        var members = ImmutableArray.CreateBuilder<MediatorMemberModel>();
        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var parameter in primary.Parameters)
        {
            covered.Add(parameter.Name);

            var member = BuildMember(
                contract,
                parameter.Name,
                parameter.Type,
                route,
                isConstructorParameter: true,
                DefaultLiteralOf(parameter),
                method,
                diagnostics
            );

            if (member is not null)
                members.Add(member);
        }

        foreach (var property in named.GetMembers().OfType<IPropertySymbol>())
        {
            if (property.IsStatic || property.IsIndexer || covered.Contains(property.Name))
                continue;

            if (property.DeclaredAccessibility != Accessibility.Public || property.SetMethod is null)
                continue;

            if (property.SetMethod.DeclaredAccessibility != Accessibility.Public)
                continue;

            var member = BuildMember(
                contract,
                property.Name,
                property.Type,
                route,
                isConstructorParameter: false,
                property.Type.AllowsNull() ? "default" : null,
                method,
                diagnostics
            );

            if (member is not null)
                members.Add(member);
        }

        return members.ToEquatable();
    }

    /// <summary>
    /// The route tokens that a body-bound contract still needs applying, so <c>PUT /widgets/{id}</c>
    /// puts the id from the URL onto the contract that came up in the body.
    /// </summary>
    static EquatableArray<MediatorMemberModel> BuildRouteOverrides(
        ITypeSymbol contract,
        INamedTypeSymbol? named,
        RouteTemplateInfo route,
        IMethodSymbol method,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics
    )
    {
        if (named is null || route.ParameterNames.Count == 0)
            return EquatableArray<MediatorMemberModel>.Empty;

        var members = ImmutableArray.CreateBuilder<MediatorMemberModel>();

        foreach (var token in route.ParameterNames)
        {
            var property = named
                .GetMembers()
                .OfType<IPropertySymbol>()
                .FirstOrDefault(p => !p.IsStatic
                    && !p.IsIndexer
                    && p.DeclaredAccessibility == Accessibility.Public
                    && string.Equals(p.Name, token, StringComparison.OrdinalIgnoreCase));

            if (property is null)
            {
                diagnostics.Add(DiagnosticInfo.Create(
                    MediatorDiagnostics.UnusedRouteToken,
                    method,
                    route.Template,
                    token,
                    contract.ToFriendly()
                ));

                continue;
            }

            // A record can be rebuilt with `with`. Anything else needs a real setter, and an
            // init-only property on a non-record cannot be reached once the body has been read.
            var assignable = named.IsRecord
                || (property.SetMethod is { IsInitOnly: false, DeclaredAccessibility: Accessibility.Public });

            if (!assignable)
            {
                diagnostics.Add(DiagnosticInfo.Create(
                    MediatorDiagnostics.RouteTokenNotApplied,
                    method,
                    route.Template,
                    token,
                    contract.ToFriendly()
                ));

                continue;
            }

            var member = BuildMember(
                contract,
                property.Name,
                property.Type,
                route,
                isConstructorParameter: false,
                defaultLiteral: null,
                method,
                diagnostics
            );

            if (member is not null)
                members.Add(member);
        }

        return members.ToEquatable();
    }

    static MediatorMemberModel? BuildMember(
        ITypeSymbol contract,
        string name,
        ITypeSymbol type,
        RouteTemplateInfo route,
        bool isConstructorParameter,
        string? defaultLiteral,
        IMethodSymbol method,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics
    )
    {
        var kind = type.ClassifyScalar(out var element);

        if (kind == ScalarKind.None)
        {
            diagnostics.Add(DiagnosticInfo.Create(
                MediatorDiagnostics.UnbindableMember,
                method,
                contract.ToFriendly(),
                name,
                type.ToFriendly()
            ));

            return null;
        }

        var fromRoute = route.HasParameter(name);

        return new MediatorMemberModel(
            name,
            type.ToFullyQualified(),
            type.ToFriendly(),
            fromRoute ? BindingSource.Route : BindingSource.Query,
            LowerFirst(name),
            kind,
            element?.ToFullyQualified(),
            defaultLiteral,
            isConstructorParameter
        );
    }

    static MediatorKind? ClassifyMethod(IMethodSymbol method, out ITypeSymbol? resultType)
    {
        resultType = null;

        if (method.Name != "Handle")
            return null;

        var returnType = method.ReturnType;

        if (returnType is INamedTypeSymbol named)
        {
            var definition = named.ConstructedFrom.ToDisplayString();

            if (definition == "System.Threading.Tasks.Task")
                return MediatorKind.Command;

            if (definition == "System.Threading.Tasks.Task<TResult>")
            {
                resultType = named.TypeArguments[0];
                return MediatorKind.Request;
            }

            if (definition == "System.Collections.Generic.IAsyncEnumerable<T>")
            {
                resultType = named.TypeArguments[0];
                return MediatorKind.Stream;
            }
        }

        return null;
    }

    static bool ImplementsHandler(INamedTypeSymbol handler, MediatorKind kind)
    {
        var wanted = kind switch
        {
            MediatorKind.Request => RequestHandler,
            MediatorKind.Command => CommandHandler,
            _ => StreamHandler
        };

        return handler.AllInterfaces.Any(i => i.OriginalDefinition.ToDisplayString() == wanted);
    }

    static AuthorizationModel BuildAuthorization(AttributeData? group, AttributeData attribute)
    {
        if (GetNamedBool(attribute, "AllowAnonymous"))
            return new AuthorizationModel(false, true, EquatableArray<string>.Empty, EquatableArray<string>.Empty);

        var required = GetNamedBool(attribute, "RequiresAuthorization") || GetNamedBool(group, "RequiresAuthorization");

        var policies = GetNamedStrings(attribute, "AuthorizationPolicies")
            .Concat(GetNamedStrings(group, "AuthorizationPolicies"))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var roles = GetNamedStrings(attribute, "Roles")
            .Concat(GetNamedStrings(group, "Roles"))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        // A group that says AllowAnonymous is overridden by an endpoint that asks for authorization,
        // which is the direction that fails safe.
        if (!required && GetNamedBool(group, "AllowAnonymous"))
            return new AuthorizationModel(false, true, EquatableArray<string>.Empty, EquatableArray<string>.Empty);

        if (!required && policies.Length == 0 && roles.Length == 0)
            return AuthorizationModel.None;

        return new AuthorizationModel(true, false, policies.ToEquatableArray(), roles.ToEquatableArray());
    }

    static EndpointPolicyModel BuildPolicies(AttributeData? group, AttributeData attribute) => new(
        attribute.GetNamedString("CorsPolicy") ?? group?.GetNamedString("CorsPolicy"),
        GetNamedBool(attribute, "DisableCors") || GetNamedBool(group, "DisableCors"),
        attribute.GetNamedString("RateLimitingPolicy") ?? group?.GetNamedString("RateLimitingPolicy"),
        GetNamedBool(attribute, "DisableRateLimiting") || GetNamedBool(group, "DisableRateLimiting"),
        attribute.GetNamedString("IpFilterPolicy") ?? group?.GetNamedString("IpFilterPolicy"),
        GetNamedBool(attribute, "AllowAnyIp") || GetNamedBool(group, "AllowAnyIp")
    );

    static EquatableArray<string> MergeTags(AttributeData? group, AttributeData attribute)
        => GetNamedStrings(group, "Tags")
            .Concat(GetNamedStrings(attribute, "Tags"))
            .Distinct(StringComparer.Ordinal)
            .ToArray()
            .ToEquatableArray();

    static bool GetNamedBool(AttributeData? attribute, string name)
        => attribute?.NamedArguments.FirstOrDefault(a => a.Key == name).Value.Value is bool value && value;

    static int? GetNamedInt(AttributeData? attribute, string name)
        => attribute?.NamedArguments.FirstOrDefault(a => a.Key == name).Value.Value is int value ? value : null;

    static IEnumerable<string> GetNamedStrings(AttributeData? attribute, string name)
    {
        if (attribute is null)
            return Array.Empty<string>();

        var argument = attribute.NamedArguments.FirstOrDefault(a => a.Key == name).Value;

        if (argument.Kind != TypedConstantKind.Array || argument.Values.IsDefaultOrEmpty)
            return Array.Empty<string>();

        return argument.Values
            .Select(v => v.Value as string)
            .Where(v => !string.IsNullOrEmpty(v))
            .Select(v => v!)
            .ToArray();
    }

    /// <summary>
    /// The literal a missing value falls back to, or null when the member is required.
    /// <para>
    /// A nullable member is optional even without an explicit default — the same rule the endpoint
    /// generator applies to a handler parameter, so <c>?search=</c> behaves identically whichever
    /// tier declared it.
    /// </para>
    /// </summary>
    static string? DefaultLiteralOf(IParameterSymbol parameter)
    {
        if (!parameter.HasExplicitDefaultValue)
            return parameter.Type.AllowsNull() ? "default" : null;

        var value = parameter.ExplicitDefaultValue;

        return value switch
        {
            null => "default",
            string text => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"",
            bool flag => flag ? "true" : "false",
            char c => "'" + c + "'",
            _ => System.Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)
        };
    }

    static string LowerFirst(string value)
        => value.Length == 0 ? value : char.ToLowerInvariant(value[0]) + value.Substring(1);

    static JsonContextModel? BuildJsonContext(GeneratorAttributeSyntaxContext context)
    {
        if (context.TargetSymbol is not INamedTypeSymbol type)
            return null;

        if (type.BaseType?.ToDisplayString() != "System.Text.Json.Serialization.JsonSerializerContext")
            return null;

        var types = type
            .GetAttributes()
            .Where(a => a.AttributeClass?.ToDisplayString() == JsonSerializableAttributeName)
            .Select(a => a.ConstructorArguments.Length > 0 ? a.ConstructorArguments[0].Value as ITypeSymbol : null)
            .Where(t => t is not null)
            .Select(t => t!.ToFullyQualified())
            .ToArray();

        return new JsonContextModel(type.ToFullyQualified(), types.ToEquatableArray());
    }

    static IEnumerable<DiagnosticInfo> FindDuplicateRoutes(IReadOnlyList<MediatorHandlerModel> handlers)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var handler in handlers)
        {
            foreach (var endpoint in handler.Endpoints)
            {
                var key = endpoint.HttpMethod + " " + endpoint.RouteTemplate;

                if (!seen.Add(key))
                    yield return new DiagnosticInfo(
                        MediatorDiagnostics.DuplicateRoute,
                        null,
                        new[] { endpoint.HttpMethod, endpoint.RouteTemplate }.ToEquatableArray()
                    );
            }
        }
    }

    /// <summary>
    /// Warns about a contract or result that will be serialized but has no compiled metadata.
    /// <para>
    /// Only checked when the assembly declares at least one context. A project with none has either
    /// registered its metadata somewhere this generator cannot see, or is about to find out at
    /// runtime — and warning on every type in that case would be noise rather than help.
    /// </para>
    /// </summary>
    static IEnumerable<DiagnosticInfo> FindMissingJsonMetadata(
        IReadOnlyList<MediatorHandlerModel> handlers,
        ImmutableArray<JsonContextModel> contexts
    )
    {
        if (contexts.Length == 0)
            yield break;

        var known = new HashSet<string>(
            contexts.SelectMany(c => c.SerializableTypes),
            StringComparer.Ordinal
        );

        var reported = new HashSet<string>(StringComparer.Ordinal);

        foreach (var handler in handlers)
        {
            foreach (var endpoint in handler.Endpoints)
            {
                if (endpoint.BindsFromBody)
                    foreach (var diagnostic in Check(endpoint.ContractFullyQualified))
                        yield return diagnostic;

                if (endpoint.ResultFullyQualified is { } result)
                    foreach (var diagnostic in Check(result))
                        yield return diagnostic;
            }
        }

        IEnumerable<DiagnosticInfo> Check(string type)
        {
            if (known.Contains(type) || !reported.Add(type))
                yield break;

            var display = type.StartsWith("global::", StringComparison.Ordinal)
                ? type.Substring("global::".Length)
                : type;

            yield return new DiagnosticInfo(
                MediatorDiagnostics.MissingJsonMetadata,
                null,
                new[] { display, display }.ToEquatableArray()
            );
        }
    }
}

static class MediatorNaming
{
    public static string ToIdentifier(string value)
    {
        var builder = new StringBuilder(value.Length);
        var startOfWord = true;

        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(startOfWord ? char.ToUpperInvariant(c) : c);
                startOfWord = false;
            }
            else
            {
                startOfWord = true;
            }
        }

        if (builder.Length == 0)
            return "Generated";

        if (char.IsDigit(builder[0]))
            builder.Insert(0, '_');

        return builder.ToString();
    }
}

static class MediatorEquatableExtensions
{
    public static EquatableArray<T> ToEquatable<T>(this ImmutableArray<T>.Builder builder)
        where T : IEquatable<T>
        => new(builder.ToImmutable());
}
