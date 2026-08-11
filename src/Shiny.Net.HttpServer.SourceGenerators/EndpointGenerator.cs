using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Shiny.Net.HttpServer.SourceGenerators;

/// <summary>
/// Turns <c>[Route]</c> classes into route registrations and parameter binders.
/// <para>
/// The generated code is deliberately boring: explicit constructor calls, explicit
/// <c>TryParse</c>, explicit JSON metadata. Every decision that a reflection-based framework would
/// make at startup is made here instead, which is what lets the whole thing survive
/// <c>PublishAot</c> — and means a route that cannot bind fails your build rather than your
/// deployment.
/// </para>
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class EndpointGenerator : IIncrementalGenerator
{
    const string RouteAttributeName = "Shiny.Net.HttpServer.RouteAttribute";
    const string HttpMethodAttributeName = "Shiny.Net.HttpServer.HttpMethodAttribute";
    const string NonEndpointAttributeName = "Shiny.Net.HttpServer.NonEndpointAttribute";
    const string JsonSerializableAttributeName = "System.Text.Json.Serialization.JsonSerializableAttribute";

    const string FromRoute = "Shiny.Net.HttpServer.FromRouteAttribute";
    const string FromQuery = "Shiny.Net.HttpServer.FromQueryAttribute";
    const string FromHeader = "Shiny.Net.HttpServer.FromHeaderAttribute";
    const string FromBody = "Shiny.Net.HttpServer.FromBodyAttribute";
    const string FromServices = "Shiny.Net.HttpServer.FromServicesAttribute";

    const string AuthorizeAttributeName = "Shiny.Net.HttpServer.AuthorizeAttribute";
    const string AllowAnonymousAttributeName = "Shiny.Net.HttpServer.AllowAnonymousAttribute";

    const string EnableCorsAttributeName = "Shiny.Net.HttpServer.EnableCorsAttribute";
    const string DisableCorsAttributeName = "Shiny.Net.HttpServer.DisableCorsAttribute";
    const string EnableRateLimitingAttributeName = "Shiny.Net.HttpServer.EnableRateLimitingAttribute";
    const string DisableRateLimitingAttributeName = "Shiny.Net.HttpServer.DisableRateLimitingAttribute";
    const string RequireIpFilterAttributeName = "Shiny.Net.HttpServer.RequireIpFilterAttribute";
    const string AllowAnyIpAttributeName = "Shiny.Net.HttpServer.AllowAnyIpAttribute";

    const string ProducesAttributeName = "Shiny.Net.HttpServer.ProducesAttribute";
    const string ApiTagsAttributeName = "Shiny.Net.HttpServer.ApiTagsAttribute";
    const string ApiExcludeAttributeName = "Shiny.Net.HttpServer.ApiExcludeAttribute";
    const string HttpEndpointInterfaceName = "Shiny.Net.HttpServer.IHttpEndpoint";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var assemblyName = context.CompilationProvider
            .Select(static (compilation, _) => Naming.ToIdentifier(compilation.AssemblyName ?? "Generated"));

        var endpointClasses = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                RouteAttributeName,
                static (node, _) => node is ClassDeclarationSyntax or RecordDeclarationSyntax,
                static (ctx, token) => BuildClass(ctx, token)
            );

        // The single-endpoint form: verb on the class, one handler method, no [Route] needed.
        // Discovered by interface rather than attribute, because implementing IHttpEndpoint is the
        // declaration — asking for an attribute as well would be ceremony for its own sake.
        var minimalEndpoints = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is ClassDeclarationSyntax { BaseList: not null }
                    or RecordDeclarationSyntax { BaseList: not null },
                static (ctx, token) => BuildMinimalEndpoint(ctx, token)
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

        // Diagnostics are reported from their own output so a class that fails validation still
        // lets every other class in the assembly generate.
        context.RegisterSourceOutput(endpointClasses, static (spc, result) => Report(spc, result));
        context.RegisterSourceOutput(minimalEndpoints, static (spc, result) => Report(spc, result));

        var validControllers = endpointClasses
            .Where(static r => r.Value is not null)
            .Select(static (r, _) => r.Value!);

        var validMinimal = minimalEndpoints
            .Where(static r => r.Value is not null)
            .Select(static (r, _) => r.Value!);

        context.RegisterSourceOutput(
            validControllers.Combine(assemblyName),
            static (spc, pair) => EmitOne(spc, pair.Left, pair.Right)
        );

        context.RegisterSourceOutput(
            validMinimal.Combine(assemblyName),
            static (spc, pair) => EmitOne(spc, pair.Left, pair.Right)
        );

        // Controllers and minimal endpoints land in the same assembly-level registration, so
        // MapMyAppEndpoints() covers both and duplicate-route detection sees them together.
        var allClasses = validControllers.Collect().Combine(validMinimal.Collect());

        context.RegisterSourceOutput(
            allClasses.Combine(assemblyName),
            static (spc, pair) =>
            {
                var models = pair.Left.Left.AddRange(pair.Left.Right);
                if (models.Length == 0)
                    return;

                foreach (var diagnostic in FindDuplicateRoutes(models))
                    spc.ReportDiagnostic(diagnostic.ToDiagnostic());

                spc.AddSource($"Endpoints.{pair.Right}.g.cs", EndpointEmitter.EmitAssembly(models, pair.Right));
            }
        );

        context.RegisterSourceOutput(
            allClasses.Combine(jsonContexts.Collect()),
            static (spc, pair) =>
            {
                var models = pair.Left.Left.AddRange(pair.Left.Right);
                var contexts = pair.Right;

                foreach (var diagnostic in FindMissingJsonMetadata(models, contexts))
                    spc.ReportDiagnostic(diagnostic.ToDiagnostic());

                if (contexts.Length > 0)
                    spc.AddSource("ShinyNetHttpServerJsonRegistration.g.cs", EndpointEmitter.EmitJsonRegistration(contexts));
            }
        );
    }

    static void Report(SourceProductionContext context, Result<EndpointClassModel> result)
    {
        foreach (var diagnostic in result.Diagnostics)
            context.ReportDiagnostic(diagnostic.ToDiagnostic());
    }

    static void EmitOne(SourceProductionContext context, EndpointClassModel model, string assembly)
        => context.AddSource($"Endpoints.{model.SafeName}.g.cs", EndpointEmitter.EmitClass(model, assembly));

    /// <summary>
    /// Builds the model for a class implementing <c>IHttpEndpoint</c>: the verb sits on the class
    /// and there is exactly one handler, so everything downstream — binding, results, authorization,
    /// OpenAPI — is the controller path with the grouping removed.
    /// </summary>
    static Result<EndpointClassModel>? BuildMinimalEndpoint(
        GeneratorSyntaxContext context,
        System.Threading.CancellationToken token
    )
    {
        if (context.SemanticModel.GetDeclaredSymbol(context.Node, token) is not INamedTypeSymbol type)
            return null;

        if (!type.AllInterfaces.Any(i => i.ToDisplayString() == HttpEndpointInterfaceName))
            return null;

        if (type.IsStatic || type.IsAbstract || type.IsGenericType ||
            type.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal))
            return Result<EndpointClassModel>.Fail(
                DiagnosticInfo.Create(Diagnostics.EndpointNotAccessible, type, "Type", type.Name)
            );

        var verbs = type.GetAttributes()
            .Where(a => VerbFor(a.AttributeClass) is not null)
            .ToArray();

        if (verbs.Length == 0)
            return Result<EndpointClassModel>.Fail(
                DiagnosticInfo.Create(Diagnostics.MinimalEndpointVerb, type, type.Name)
            );

        var handlers = type.GetMembers()
            .OfType<IMethodSymbol>()
            .Where(m => m is { MethodKind: MethodKind.Ordinary, IsStatic: false, IsGenericMethod: false }
                && m.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal
                && m.Name is "Handle" or "HandleAsync")
            .ToArray();

        if (handlers.Length != 1)
            return Result<EndpointClassModel>.Fail(DiagnosticInfo.Create(
                Diagnostics.MinimalEndpointHandler,
                type,
                type.Name,
                handlers.Length == 0 ? "has no handler" : $"has {handlers.Length} of them"
            ));

        var handler = handlers[0];
        var prefix = type.FindAttribute(RouteAttributeName)?.GetConstructorString(0) ?? string.Empty;

        var diagnostics = ImmutableArray.CreateBuilder<DiagnosticInfo>();
        var methods = ImmutableArray.CreateBuilder<EndpointMethodModel>();

        foreach (var verb in verbs)
        {
            var method = BuildMethod(type, handler, verb, prefix, diagnostics);
            if (method is null)
                continue;

            // The class is the endpoint, so its doc comment describes it — fall back to that when
            // the handler has none of its own.
            methods.Add(method.Summary is { Length: > 0 }
                ? method
                : method with { Summary = DocComments.Summary(type) });
        }

        if (methods.Count == 0)
            return new Result<EndpointClassModel>(null, new EquatableArray<DiagnosticInfo>(diagnostics.ToImmutable()));

        var constructor = SelectConstructor(type);
        var model = new EndpointClassModel(
            type.ToFullyQualified(),
            type.Name,
            Naming.ToIdentifier(type.ToDisplayString()),
            (constructor?.Parameters ?? ImmutableArray<IParameterSymbol>.Empty)
                .Select(p => p.Type.ToFullyQualified())
                .ToEquatableArray(),
            methods.ToImmutable().ToEquatableArray()
        );

        return Result<EndpointClassModel>.Ok(model, diagnostics.ToImmutable());
    }

    // ---- Symbol -> model ----

    static Result<EndpointClassModel> BuildClass(GeneratorAttributeSyntaxContext context, System.Threading.CancellationToken token)
    {
        if (context.TargetSymbol is not INamedTypeSymbol type)
            return Result<EndpointClassModel>.Fail();

        if (type.IsStatic || type.IsAbstract || type.IsGenericType ||
            type.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal))
            return Result<EndpointClassModel>.Fail(
                DiagnosticInfo.Create(Diagnostics.EndpointNotAccessible, type, "Type", type.Name)
            );

        var prefix = context.Attributes[0].GetConstructorString(0) ?? string.Empty;
        var diagnostics = ImmutableArray.CreateBuilder<DiagnosticInfo>();
        var methods = ImmutableArray.CreateBuilder<EndpointMethodModel>();

        foreach (var member in type.GetMembers().OfType<IMethodSymbol>())
        {
            token.ThrowIfCancellationRequested();

            if (member.MethodKind != MethodKind.Ordinary || member.FindAttribute(NonEndpointAttributeName) is not null)
                continue;

            var verbs = member.GetAttributes()
                .Where(a => VerbFor(a.AttributeClass) is not null)
                .ToArray();

            if (verbs.Length == 0)
                continue;

            if (member.IsStatic || member.IsGenericMethod ||
                member.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal))
            {
                diagnostics.Add(DiagnosticInfo.Create(Diagnostics.EndpointNotAccessible, member, "Method", member.Name));
                continue;
            }

            foreach (var verb in verbs)
            {
                var method = BuildMethod(type, member, verb, prefix, diagnostics);
                if (method is not null)
                    methods.Add(method);
            }
        }

        if (methods.Count == 0)
            return new Result<EndpointClassModel>(null, new EquatableArray<DiagnosticInfo>(diagnostics.ToImmutable()));

        var constructor = SelectConstructor(type);
        var model = new EndpointClassModel(
            type.ToFullyQualified(),
            type.Name,
            Naming.ToIdentifier(type.ToDisplayString()),
            (constructor?.Parameters ?? ImmutableArray<IParameterSymbol>.Empty)
                .Select(p => p.Type.ToFullyQualified())
                .ToEquatableArray(),
            methods.ToImmutable().ToEquatableArray()
        );

        return Result<EndpointClassModel>.Ok(model, diagnostics.ToImmutable());
    }

    static EndpointMethodModel? BuildMethod(
        INamedTypeSymbol type,
        IMethodSymbol method,
        AttributeData verb,
        string classPrefix,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics
    )
    {
        // [HttpMethod("PUT", "/x")] carries the verb first; [Put("/x")] carries only the template.
        var isBaseAttribute = VerbFor(verb.AttributeClass) is "";
        var httpMethod = (isBaseAttribute ? verb.GetConstructorString(0) : VerbFor(verb.AttributeClass))
            ?? HttpVerbs.Get;
        var methodTemplate = (isBaseAttribute ? verb.GetConstructorString(1) : verb.GetConstructorString(0))
            ?? string.Empty;

        var combined = RouteTemplateInfo.Combine(classPrefix, methodTemplate);
        var template = RouteTemplateInfo.TryParse(combined, out var error);
        if (template is null)
        {
            diagnostics.Add(DiagnosticInfo.Create(Diagnostics.InvalidRouteTemplate, method, combined, error));
            return null;
        }

        var parameters = ImmutableArray.CreateBuilder<ParameterModel>();
        var bodyCount = 0;
        var bound = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var parameter in method.Parameters)
        {
            var model = BuildParameter(method, parameter, httpMethod, template, bodyCount > 0, diagnostics);
            if (model is null)
                return null;

            if (model.Source == BindingSource.Body)
                bodyCount++;

            if (model.Source == BindingSource.Route)
                bound.Add(model.BindingKey);

            parameters.Add(model);
        }

        if (bodyCount > 1)
        {
            diagnostics.Add(DiagnosticInfo.Create(Diagnostics.MultipleBodyParameters, method, method.Name));
            return null;
        }

        foreach (var tokenName in template.ParameterNames)
        {
            if (!bound.Contains(tokenName))
                diagnostics.Add(DiagnosticInfo.Create(
                    Diagnostics.UnusedRouteToken,
                    method,
                    template.Template,
                    tokenName,
                    method.Name
                ));
        }

        var returnType = method.ReturnType.UnwrapAwaitable(out var isAwaitable);
        var payload = ResponsePayload.None;
        string? payloadType = null;

        if (returnType is not null && returnType.SpecialType != SpecialType.System_Void)
        {
            if (returnType.ImplementsResult())
            {
                payload = ResponsePayload.Result;
            }
            else if (returnType.SpecialType == SpecialType.System_String)
            {
                payload = ResponsePayload.String;
            }
            else if (returnType.TypeKind is TypeKind.Class or TypeKind.Struct or TypeKind.Interface or TypeKind.Array
                     && returnType.SpecialType != SpecialType.System_Object)
            {
                payload = ResponsePayload.Json;
                payloadType = returnType.ToFullyQualified();
            }
            else
            {
                diagnostics.Add(DiagnosticInfo.Create(
                    Diagnostics.UnsupportedReturnType,
                    method,
                    method.Name,
                    method.ReturnType.ToFriendly()
                ));
                return null;
            }
        }

        return new EndpointMethodModel(
            httpMethod,
            template.Template,
            method.Name,
            parameters.ToImmutable().ToEquatableArray(),
            isAwaitable,
            payload,
            payloadType,
            DocComments.Summary(method),
            TagsFor(type, method),
            type.FindAttribute(ApiExcludeAttributeName) is not null
                || method.FindAttribute(ApiExcludeAttributeName) is not null,
            ResponsesFor(method, payload, payloadType),
            AuthorizationFor(type, method),
            PoliciesFor(type, method)
        );
    }

    /// <summary>
    /// Reads the CORS, rate limit and IP filter attributes off the class and the method.
    /// <para>
    /// The method wins outright where it says anything, and a <c>Disable</c> anywhere wins over an
    /// <c>Enable</c> — an explicit opt-out is never something to second-guess.
    /// </para>
    /// </summary>
    static EndpointPolicyModel PoliciesFor(INamedTypeSymbol type, IMethodSymbol method)
    {
        var corsDisabled = HasAttribute(DisableCorsAttributeName);
        var rateLimitDisabled = HasAttribute(DisableRateLimitingAttributeName);
        var ipFilterDisabled = HasAttribute(AllowAnyIpAttributeName);

        var model = new EndpointPolicyModel(
            corsDisabled ? null : PolicyName(EnableCorsAttributeName),
            corsDisabled,
            rateLimitDisabled ? null : PolicyName(EnableRateLimitingAttributeName),
            rateLimitDisabled,
            ipFilterDisabled ? null : PolicyName(RequireIpFilterAttributeName),
            ipFilterDisabled
        );

        return model.HasCors || model.HasRateLimit || model.HasIpFilter ? model : EndpointPolicyModel.None;

        bool HasAttribute(string name)
            => method.FindAttribute(name) is not null || type.FindAttribute(name) is not null;

        string? PolicyName(string name)
        {
            var attribute = method.FindAttribute(name) ?? type.FindAttribute(name);
            if (attribute is null)
                return null;

            var policy = attribute.GetConstructorString(0) ?? attribute.GetNamedString("Policy");

            return string.IsNullOrWhiteSpace(policy) ? null : policy;
        }
    }

    /// <summary>
    /// Combines class-level and method-level authorization.
    /// <para>
    /// A method's <c>[Authorize]</c> adds to the class's rather than replacing it, so narrowing an
    /// endpoint is additive and cannot accidentally widen it. <c>[AllowAnonymous]</c> anywhere wins
    /// outright — an explicit opt-out should never be second-guessed by a convention.
    /// </para>
    /// </summary>
    static AuthorizationModel AuthorizationFor(INamedTypeSymbol type, IMethodSymbol method)
    {
        if (method.FindAttribute(AllowAnonymousAttributeName) is not null ||
            type.FindAttribute(AllowAnonymousAttributeName) is not null)
            return new AuthorizationModel(false, true, EquatableArray<string>.Empty, EquatableArray<string>.Empty);

        var attributes = type.GetAttributes()
            .Concat(method.GetAttributes())
            .Where(a => a.AttributeClass?.ToDisplayString() == AuthorizeAttributeName)
            .ToArray();

        if (attributes.Length == 0)
            return AuthorizationModel.None;

        var policies = new List<string>();
        var roles = new List<string>();

        foreach (var attribute in attributes)
        {
            var policy = attribute.ConstructorArguments.Length > 0
                ? attribute.ConstructorArguments[0].Value as string
                : attribute.GetNamedString("Policy");

            policy ??= attribute.GetNamedString("Policy");

            if (!string.IsNullOrWhiteSpace(policy) && !policies.Contains(policy!))
                policies.Add(policy!);

            if (attribute.GetNamedString("Roles") is { Length: > 0 } roleList)
            {
                foreach (var role in roleList.Split(','))
                {
                    var trimmed = role.Trim();
                    if (trimmed.Length > 0 && !roles.Contains(trimmed))
                        roles.Add(trimmed);
                }
            }
        }

        return new AuthorizationModel(true, false, policies.ToEquatableArray(), roles.ToEquatableArray());
    }

    /// <summary>
    /// Tags come from the method, then the class, then the class name. Grouping by controller is
    /// what every OpenAPI UI expects, so it is the default rather than something to configure.
    /// </summary>
    static EquatableArray<string> TagsFor(INamedTypeSymbol type, IMethodSymbol method)
    {
        var attribute = method.FindAttribute(ApiTagsAttributeName) ?? type.FindAttribute(ApiTagsAttributeName);

        if (attribute is null || attribute.ConstructorArguments.Length == 0)
            return new[] { type.Name }.ToEquatableArray();

        var tags = attribute.ConstructorArguments[0].Values
            .Select(v => v.Value as string)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!)
            .ToArray();

        return tags.Length == 0 ? new[] { type.Name }.ToEquatableArray() : tags.ToEquatableArray();
    }

    /// <summary>
    /// Declared responses win. Failing that, a return type the generator can see becomes a 200 —
    /// but a method returning IActionResult has deliberately hidden its status codes, so that case
    /// gets nothing here and relies on [Produces].
    /// </summary>
    static EquatableArray<ApiResponseModel> ResponsesFor(
        IMethodSymbol method,
        ResponsePayload payload,
        string? payloadType
    )
    {
        var declared = method.GetAttributes()
            .Where(a => a.AttributeClass?.ToDisplayString() == ProducesAttributeName)
            .Select(a => new ApiResponseModel(
                a.ConstructorArguments.Length > 0 && a.ConstructorArguments[0].Value is int status ? status : 200,
                a.ConstructorArguments.Length > 1 && a.ConstructorArguments[1].Value is ITypeSymbol type
                    ? type.ToFullyQualified()
                    : null,
                a.GetNamedString("Description"),
                a.GetNamedString("ContentType") ?? "application/json"
            ))
            .ToArray();

        if (declared.Length > 0)
            return declared.ToEquatableArray();

        return payload switch
        {
            ResponsePayload.Json when payloadType is not null =>
                new[] { new ApiResponseModel(200, payloadType, null, "application/json") }.ToEquatableArray(),

            ResponsePayload.String =>
                new[] { new ApiResponseModel(200, "string", null, "text/plain") }.ToEquatableArray(),

            _ => new[] { new ApiResponseModel(200, null, null, "application/json") }.ToEquatableArray()
        };
    }

    static ParameterModel? BuildParameter(
        IMethodSymbol method,
        IParameterSymbol parameter,
        string httpMethod,
        RouteTemplateInfo template,
        bool bodyAlreadyTaken,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics
    )
    {
        var parameterType = parameter.Type;
        var fq = parameterType.ToFullyQualified();
        var friendly = parameterType.ToFriendly();

        // Ambient values first: these are never "bound", they are just handed over.
        if (parameterType.IsType(TypeAnalysis.HttpContextType))
            return Ambient(BindingSource.HttpContext);

        if (parameterType.IsType(TypeAnalysis.HttpRequestType))
            return Ambient(BindingSource.HttpRequest);

        if (parameterType.IsType(TypeAnalysis.HttpResponseType))
            return Ambient(BindingSource.HttpResponse);

        if (parameterType.IsType(TypeAnalysis.CancellationTokenType))
            return Ambient(BindingSource.CancellationToken);

        if (parameter.FindAttribute(FromServices) is not null)
            return Ambient(BindingSource.Services);

        var scalar = parameterType.ClassifyScalar(out var element);
        var elementFq = element?.ToFullyQualified();
        var defaultLiteral = FormatDefault(parameter);
        var allowsNull = parameterType.AllowsNull();

        if (parameter.FindAttribute(FromBody) is not null)
            return Bound(BindingSource.Body, parameter.Name, ScalarKind.None);

        if (parameter.FindAttribute(FromRoute) is { } fromRoute)
        {
            var key = fromRoute.GetNamedString("Name") ?? parameter.Name;
            if (!template.HasParameter(key))
            {
                diagnostics.Add(DiagnosticInfo.Create(
                    Diagnostics.RouteParameterNotInTemplate,
                    parameter,
                    key,
                    template.Template
                ));
                return null;
            }
            return RequireScalar(BindingSource.Route, key);
        }

        if (parameter.FindAttribute(FromQuery) is { } fromQuery)
            return RequireScalar(BindingSource.Query, fromQuery.GetNamedString("Name") ?? parameter.Name);

        if (parameter.FindAttribute(FromHeader) is { } fromHeader)
            return RequireScalar(BindingSource.Header, fromHeader.GetNamedString("Name") ?? parameter.Name);

        // No attribute: infer. A name in the template wins, then anything a string can become goes
        // to the query, then a body if the verb has one, and finally the container.
        if (template.HasParameter(parameter.Name) && scalar != ScalarKind.None)
            return Bound(BindingSource.Route, parameter.Name, scalar);

        if (scalar != ScalarKind.None)
            return Bound(BindingSource.Query, parameter.Name, scalar);

        if (HttpVerbs.CanHaveBody(httpMethod) && !bodyAlreadyTaken)
            return Bound(BindingSource.Body, parameter.Name, ScalarKind.None);

        return Ambient(BindingSource.Services);

        ParameterModel Ambient(BindingSource source) => new(
            parameter.Name,
            fq,
            friendly,
            source,
            parameter.Name,
            ScalarKind.None,
            null,
            null,
            AllowsNull: parameterType.AllowsNull()
        );

        ParameterModel Bound(BindingSource source, string key, ScalarKind kind) => new(
            parameter.Name,
            fq,
            friendly,
            source,
            key,
            kind,
            elementFq,
            defaultLiteral,
            allowsNull
        );

        ParameterModel? RequireScalar(BindingSource source, string key)
        {
            if (scalar == ScalarKind.None)
            {
                diagnostics.Add(DiagnosticInfo.Create(
                    Diagnostics.UnbindableParameter,
                    parameter,
                    parameter.Name,
                    friendly
                ));
                return null;
            }
            return Bound(source, key, scalar);
        }
    }

    // ---- Cross-class validation ----

    static IEnumerable<DiagnosticInfo> FindDuplicateRoutes(ImmutableArray<EndpointClassModel> models)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var model in models)
        {
            foreach (var method in model.Methods)
            {
                var key = method.HttpMethod + " " + method.RouteTemplate;
                if (!seen.Add(key))
                    yield return new DiagnosticInfo(
                        Diagnostics.DuplicateRoute,
                        null,
                        new[] { method.HttpMethod, method.RouteTemplate }.ToEquatableArray()
                    );
            }
        }
    }

    static IEnumerable<DiagnosticInfo> FindMissingJsonMetadata(
        ImmutableArray<EndpointClassModel> models,
        ImmutableArray<JsonContextModel> contexts
    )
    {
        var covered = new HashSet<string>(
            contexts.SelectMany(c => c.SerializableTypes),
            StringComparer.Ordinal
        );

        var reported = new HashSet<string>(StringComparer.Ordinal);

        foreach (var model in models)
        {
            foreach (var method in model.Methods)
            {
                foreach (var type in JsonTypesOf(method))
                {
                    if (covered.Contains(type) || !reported.Add(type))
                        continue;

                    yield return new DiagnosticInfo(
                        Diagnostics.MissingJsonMetadata,
                        null,
                        new[] { type, type }.ToEquatableArray()
                    );
                }
            }
        }
    }

    static IEnumerable<string> JsonTypesOf(EndpointMethodModel method)
    {
        if (method.Payload == ResponsePayload.Json && method.PayloadTypeFullyQualified is { } payload)
            yield return payload;

        foreach (var parameter in method.Parameters)
        {
            if (parameter.Source == BindingSource.Body)
                yield return parameter.TypeFullyQualified;
        }
    }

    static JsonContextModel? BuildJsonContext(GeneratorAttributeSyntaxContext context)
    {
        if (context.TargetSymbol is not INamedTypeSymbol type)
            return null;

        if (type.BaseType?.ToDisplayString() != "System.Text.Json.Serialization.JsonSerializerContext")
            return null;

        if (type.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal))
            return null;

        var types = context.Attributes
            .Select(a => a.ConstructorArguments.FirstOrDefault().Value as ITypeSymbol)
            .Where(t => t is not null)
            .Select(t => t!.ToFullyQualified())
            .Distinct(StringComparer.Ordinal)
            .ToEquatableArray();

        return new JsonContextModel(type.ToFullyQualified(), types);
    }

    // ---- Small helpers ----

    static IMethodSymbol? SelectConstructor(INamedTypeSymbol type) => type.InstanceConstructors
        .Where(c => c.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal)
        .OrderByDescending(c => c.Parameters.Length)
        .FirstOrDefault();

    /// <summary>
    /// The verb an attribute maps to, <c>""</c> for the open-ended <c>[HttpMethod]</c> base (whose
    /// verb is its first argument), or null when the attribute is not a verb attribute at all.
    /// </summary>
    static string? VerbFor(INamedTypeSymbol? attributeClass) => attributeClass?.ToDisplayString() switch
    {
        "Shiny.Net.HttpServer.GetAttribute" => HttpVerbs.Get,
        "Shiny.Net.HttpServer.PostAttribute" => HttpVerbs.Post,
        "Shiny.Net.HttpServer.PutAttribute" => HttpVerbs.Put,
        "Shiny.Net.HttpServer.DeleteAttribute" => HttpVerbs.Delete,
        "Shiny.Net.HttpServer.PatchAttribute" => HttpVerbs.Patch,
        HttpMethodAttributeName => "",
        _ => null
    };

    static string? FormatDefault(IParameterSymbol parameter)
    {
        if (!parameter.HasExplicitDefaultValue)
            return parameter.Type.AllowsNull() ? "default" : null;

        var value = parameter.ExplicitDefaultValue;
        if (value is null)
            return "default";

        var underlying = parameter.Type.NullableUnderlying() ?? parameter.Type;
        if (underlying.TypeKind == TypeKind.Enum)
            return $"({underlying.ToFullyQualified()})({value})";

        return Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatPrimitive(value, quoteStrings: true, useHexadecimalNumbers: false);
    }
}

/// <summary>Turns arbitrary names into things that are legal, readable C# identifiers.</summary>
static class Naming
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
