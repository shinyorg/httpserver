using System.Collections.Immutable;
using System.Linq;
using System.Text;

namespace Shiny.Net.HttpServer.SourceGenerators;

/// <summary>Canonical verb strings, so the generator and the emitted code never drift apart.</summary>
static class HttpVerbs
{
    public const string Get = "GET";
    public const string Post = "POST";
    public const string Put = "PUT";
    public const string Delete = "DELETE";
    public const string Patch = "PATCH";

    public static bool CanHaveBody(string method)
        => method is Post or Put or Patch or Delete;
}

/// <summary>
/// Writes the generated C#. Everything emitted here is fully qualified with <c>global::</c> and
/// uses no <c>using</c> directives, because generated code lands in whatever namespace and aliasing
/// situation the user's file happens to have and must not care.
/// </summary>
static class EndpointEmitter
{
    const string Binder = "global::Shiny.Net.HttpServer.Endpoints.EndpointBinder";
    const string Services = "global::Shiny.Net.HttpServer.Endpoints.EndpointServices";
    const string Descriptor = "global::Shiny.Net.HttpServer.Endpoints.EndpointDescriptor";
    const string Operation = "global::Shiny.Net.HttpServer.OpenApi.ApiOperation";
    const string Location = "global::Shiny.Net.HttpServer.OpenApi.ApiParameterLocation";
    const string Authorization = "global::Shiny.Net.HttpServer.Security.AuthorizationMetadata";
    const string CorsMetadata = "global::Shiny.Net.HttpServer.Cors.CorsMetadata";
    const string RateLimitMetadata = "global::Shiny.Net.HttpServer.RateLimiting.RateLimitMetadata";
    const string IpFilterMetadata = "global::Shiny.Net.HttpServer.Security.IpFilterMetadata";
    const string Context = "global::Shiny.Net.HttpServer.HttpContext";
    const string Server = "global::Shiny.Net.HttpServer.HttpServer";
    const string Results = "global::Shiny.Net.HttpServer.Results";

    public static string EmitClass(EndpointClassModel model, string assembly)
    {
        var writer = new CodeWriter();
        WriteHeader(writer);

        writer.Line("namespace Shiny.Net.HttpServer");
        writer.OpenBrace();

        writer.Line($"public static partial class {assembly}EndpointExtensions");
        writer.OpenBrace();

        writer.Line($"/// <summary>Registers the {model.Methods.Count} endpoint(s) declared on <see cref=\"{XmlRef(model.FullyQualifiedName)}\"/>.</summary>");
        writer.Line($"public static {Server} Map{model.DisplayName}(this {Server} server)");
        writer.OpenBrace();
        writer.Line("global::System.ArgumentNullException.ThrowIfNull(server);");
        writer.Blank();

        foreach (var method in model.Methods)
            WriteRegistration(writer, model, method);

        writer.Line("return server;");
        writer.CloseBrace();
        writer.Blank();

        WriteFactory(writer, model);

        writer.CloseBrace();
        writer.CloseBrace();

        return writer.ToString();
    }

    static void WriteRegistration(CodeWriter writer, EndpointClassModel model, EndpointMethodModel method)
    {
        writer.Line($"server.Map(");
        writer.Indent();
        writer.Line($"\"{method.HttpMethod}\",");
        writer.Line($"\"{method.RouteTemplate}\",");
        writer.Line($"static async ({Context} __ctx) =>");
        writer.OpenBrace();

        foreach (var parameter in method.Parameters)
            WriteParameterBinding(writer, model, method, parameter);

        WriteInvocation(writer, model, method);

        writer.CloseBraceWith(",");
        writer.Line($"new {Descriptor}(\"{model.DisplayName}\", \"{method.MethodName}\"),");

        // Each of these fits on one line, so they are collected first and emitted with the commas
        // the argument list needs — the alternative is every writer below knowing what follows it.
        var policies = PolicyMetadata(method.Policies);

        WriteApiOperation(
            writer,
            model,
            method,
            method.Authorization.HasValue || policies.Count > 0 ? "," : string.Empty
        );

        if (method.Authorization.HasValue)
            WriteAuthorization(writer, method.Authorization, policies.Count > 0 ? "," : string.Empty);

        for (var i = 0; i < policies.Count; i++)
            writer.Line(policies[i] + (i < policies.Count - 1 ? "," : string.Empty));

        writer.Outdent();
        writer.Line(");");
        writer.Blank();
    }

    /// <summary>
    /// Emits what the endpoint requires as metadata rather than as a check inside the handler.
    /// <para>
    /// The authorization middleware reads it after routing and before invocation, so a denied
    /// request never reaches the endpoint class at all — its constructor does not run, and neither
    /// do its dependencies.
    /// </para>
    /// </summary>
    /// <summary>
    /// Emits the CORS, rate limit and IP filter policies an endpoint named, as metadata each
    /// module's middleware reads for itself.
    /// </summary>
    static List<string> PolicyMetadata(EndpointPolicyModel policies)
    {
        var metadata = new List<string>();

        if (policies.HasCors)
            metadata.Add($"new {CorsMetadata} {{ {Assignments(policies.CorsPolicy, policies.CorsDisabled)} }}");

        if (policies.HasRateLimit)
            metadata.Add($"new {RateLimitMetadata} {{ {Assignments(policies.RateLimitPolicy, policies.RateLimitDisabled)} }}");

        if (policies.HasIpFilter)
            metadata.Add($"new {IpFilterMetadata} {{ {Assignments(policies.IpFilterPolicy, policies.IpFilterDisabled)} }}");

        return metadata;

        static string Assignments(string? policy, bool disabled)
            => disabled ? "Disabled = true" : $"PolicyName = {Literal(policy!)}";
    }

    static void WriteAuthorization(CodeWriter writer, AuthorizationModel authorization, string suffix)
    {
        writer.Line($"new {Authorization}");
        writer.OpenBrace();

        if (authorization.AllowAnonymous)
        {
            writer.Line("AllowAnonymous = true");
            writer.CloseBraceWith(suffix);
            return;
        }

        writer.Line("Required = true,");

        if (authorization.Policies.Count > 0)
            writer.Line($"Policies = {{ {string.Join(", ", authorization.Policies.Select(Literal))} }},");

        if (authorization.Roles.Count > 0)
            writer.Line($"Roles = {{ {string.Join(", ", authorization.Roles.Select(Literal))} }},");

        writer.CloseBraceWith(suffix);
    }

    /// <summary>
    /// Emits the OpenAPI description alongside the route.
    /// <para>
    /// Nothing here is discovered at runtime, because nothing needs to be: the generator already
    /// worked out every parameter's source and type in order to write the binder, and the return
    /// type in order to write the response. The document is a second use of the same analysis, so
    /// it cannot drift from what the endpoint actually does.
    /// </para>
    /// </summary>
    static void WriteApiOperation(CodeWriter writer, EndpointClassModel model, EndpointMethodModel method, string suffix)
    {
        writer.Line($"new {Operation}");
        writer.OpenBrace();

        if (method.ApiExcluded)
        {
            writer.Line("Exclude = true");
            writer.CloseBraceWith(suffix);
            return;
        }

        if (method.Summary is { Length: > 0 } summary)
            writer.Line($"Summary = {Literal(summary)},");

        writer.Line($"OperationId = \"{model.DisplayName}_{method.MethodName}\",");

        if (method.Tags.Count > 0)
            writer.Line($"Tags = {{ {string.Join(", ", method.Tags.Select(Literal))} }},");

        var documented = method.Parameters
            .Where(p => p.Source is BindingSource.Route or BindingSource.Query or BindingSource.Header)
            .ToArray();

        if (documented.Length > 0)
        {
            writer.Line("Parameters =");
            writer.OpenBrace();
            foreach (var parameter in documented)
            {
                // "Route" is this server's word for it; OpenAPI calls the same thing "path".
                var location = parameter.Source == BindingSource.Route ? "Path" : parameter.Source.ToString();

                writer.Line(
                    $"new() {{ Name = \"{parameter.BindingKey}\", In = {Location}.{location}, " +
                    $"Type = typeof({DocumentedType(parameter)}), Required = {(IsRequired(parameter) ? "true" : "false")} }},"
                );
            }
            writer.CloseBraceWith(",");
        }

        if (method.Parameters.FirstOrDefault(p => p.Source == BindingSource.Body) is { } body)
            writer.Line($"RequestBody = new() {{ Type = typeof({body.TypeFullyQualified}) }},");

        writer.Line("Responses =");
        writer.OpenBrace();
        foreach (var response in method.Responses)
        {
            var parts = new List<string> { $"StatusCode = {response.StatusCode}" };

            if (response.TypeFullyQualified is { } type)
                parts.Add($"Type = typeof({type})");

            if (response.Description is { Length: > 0 } description)
                parts.Add($"Description = {Literal(description)}");

            parts.Add($"ContentType = \"{response.ContentType}\"");
            writer.Line($"new() {{ {string.Join(", ", parts)} }},");
        }
        writer.CloseBrace();

        writer.CloseBraceWith(suffix);
    }

    /// <summary>
    /// The type a parameter is documented as. A nullable or array binding is described by what it
    /// actually carries, not by the wrapper the handler happens to receive.
    /// </summary>
    static string DocumentedType(ParameterModel parameter) => parameter.ScalarKind switch
    {
        ScalarKind.NullableParsable or ScalarKind.NullableEnum => parameter.ElementTypeFullyQualified
            ?? parameter.TypeFullyQualified,
        _ => parameter.TypeFullyQualified
    };

    static bool IsRequired(ParameterModel parameter)
        => parameter.DefaultLiteral is null
        && parameter.ScalarKind is not (ScalarKind.NullableParsable or ScalarKind.NullableEnum
            or ScalarKind.StringArray or ScalarKind.ParsableArray);

    /// <summary>Escapes a string for emission as a C# literal.</summary>
    static string Literal(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');

        foreach (var c in value)
        {
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\r': builder.Append("\\r"); break;
                case '\n': builder.Append("\\n"); break;
                default: builder.Append(c); break;
            }
        }

        builder.Append('"');
        return builder.ToString();
    }

    static void WriteParameterBinding(
        CodeWriter writer,
        EndpointClassModel model,
        EndpointMethodModel method,
        ParameterModel parameter
    )
    {
        var name = parameter.Name;
        var type = parameter.TypeFullyQualified;

        switch (parameter.Source)
        {
            case BindingSource.HttpContext:
                writer.Line($"var {name} = __ctx;");
                return;

            case BindingSource.HttpRequest:
                writer.Line($"var {name} = __ctx.Request;");
                return;

            case BindingSource.HttpResponse:
                writer.Line($"var {name} = __ctx.Response;");
                return;

            case BindingSource.CancellationToken:
                writer.Line($"var {name} = __ctx.RequestAborted;");
                return;

            case BindingSource.Services:
                writer.Line(
                    $"var {name} = {Services}.GetRequired<{type}>(__ctx.RequestServices, " +
                    $"\"{model.DisplayName}.{method.MethodName}\", \"{name}\");"
                );
                return;

            case BindingSource.Body:
                writer.Line($"var (__ok_{name}, __body_{name}) = await {Binder}");
                writer.Indent();
                writer.Line($".TryReadJsonBodyAsync<{type}>(__ctx)");
                writer.Line(".ConfigureAwait(false);");
                writer.Outdent();
                writer.Line($"if (!__ok_{name})");
                writer.OpenBrace();
                WriteBindFailure(writer, parameter);
                writer.Line("return;");
                writer.CloseBrace();
                writer.Line($"var {name} = __body_{name}!;");
                writer.Blank();
                return;
        }

        // Route, query and header all reduce to "here is a string (or several), make it a T".
        var raw = parameter.Source switch
        {
            BindingSource.Route => $"__ctx.Request.RouteValues[\"{parameter.BindingKey}\"]",
            BindingSource.Query => $"__ctx.Request.Query.GetFirst(\"{parameter.BindingKey}\")",
            _ => $"__ctx.Request.Headers.GetFirst(\"{parameter.BindingKey}\")"
        };

        var multi = parameter.Source switch
        {
            BindingSource.Query => $"__ctx.Request.Query[\"{parameter.BindingKey}\"]",
            _ => $"__ctx.Request.Headers[\"{parameter.BindingKey}\"]"
        };

        switch (parameter.ScalarKind)
        {
            case ScalarKind.StringArray:
                writer.Line($"var {name} = {Binder}.BindStringArray({multi});");
                return;

            case ScalarKind.ParsableArray:
                writer.Line($"if (!{Binder}.TryBindArray<{parameter.ElementTypeFullyQualified}>({multi}, out var {name}))");
                writer.OpenBrace();
                WriteBindFailure(writer, parameter);
                writer.Line("return;");
                writer.CloseBrace();
                writer.Blank();
                return;

            case ScalarKind.NullableParsable:
                writer.Line($"if (!{Binder}.TryBindNullable<{parameter.ElementTypeFullyQualified}>({raw}, out var {name}))");
                writer.OpenBrace();
                WriteBindFailure(writer, parameter);
                writer.Line("return;");
                writer.CloseBrace();
                writer.Blank();
                return;

            case ScalarKind.NullableEnum:
                writer.Line($"if (!{Binder}.TryBindNullableEnum<{parameter.ElementTypeFullyQualified}>({raw}, out var {name}))");
                writer.OpenBrace();
                WriteBindFailure(writer, parameter);
                writer.Line("return;");
                writer.CloseBrace();
                writer.Blank();
                return;

            case ScalarKind.String:
                writer.Line($"var __raw_{name} = {raw};");
                if (parameter.DefaultLiteral is { } stringDefault)
                {
                    // "default" here means the parameter is optional and null is a real answer, so
                    // the declaration carries the nullable annotation rather than coalescing to it.
                    writer.Line(stringDefault == "default"
                        ? $"{type}? {name} = __raw_{name};"
                        : $"var {name} = __raw_{name} ?? {stringDefault};");
                }
                else
                {
                    writer.Line($"if (__raw_{name} is null)");
                    writer.OpenBrace();
                    WriteBindFailure(writer, parameter);
                    writer.Line("return;");
                    writer.CloseBrace();
                    writer.Line($"var {name} = __raw_{name};");
                }
                writer.Blank();
                return;

            default:
                // Parsable and Enum: a missing value is only an error when there is no default to
                // fall back to, which is what keeps "?page=" optional without a nullable type.
                var bind = parameter.ScalarKind == ScalarKind.Enum ? "TryBindEnum" : "TryBind";
                writer.Line($"var __raw_{name} = {raw};");
                writer.Line($"{parameter.TypeFullyQualified} {name};");
                writer.Line($"if (__raw_{name} is null)");
                writer.OpenBrace();
                if (parameter.DefaultLiteral is { } scalarDefault)
                {
                    writer.Line($"{name} = {scalarDefault};");
                }
                else
                {
                    WriteBindFailure(writer, parameter);
                    writer.Line("return;");
                }
                writer.CloseBrace();
                writer.Line($"else if (!{Binder}.{bind}<{parameter.TypeFullyQualified}>(__raw_{name}, out {name}))");
                writer.OpenBrace();
                WriteBindFailure(writer, parameter);
                writer.Line("return;");
                writer.CloseBrace();
                writer.Blank();
                return;
        }
    }

    static void WriteBindFailure(CodeWriter writer, ParameterModel parameter)
    {
        var source = parameter.Source switch
        {
            BindingSource.Route => "Route",
            BindingSource.Query => "Query",
            BindingSource.Header => "Header",
            _ => "Body"
        };

        writer.Line($"await {Binder}");
        writer.Indent();
        writer.Line(
            $".BindFailedAsync(__ctx, \"{parameter.BindingKey}\", {Binder}.Source.{source}, " +
            $"\"{parameter.TypeDisplay}\")"
        );
        writer.Line(".ConfigureAwait(false);");
        writer.Outdent();
    }

    static void WriteInvocation(CodeWriter writer, EndpointClassModel model, EndpointMethodModel method)
    {
        writer.Line($"var __endpoint = Create{model.SafeName}(__ctx.RequestServices);");

        var arguments = string.Join(", ", method.Parameters.Select(p => p.Name));
        var call = $"__endpoint.{method.MethodName}({arguments})";
        var invocation = method.IsAwaitable ? $"await {call}.ConfigureAwait(false)" : call;

        switch (method.Payload)
        {
            case ResponsePayload.None:
                writer.Line($"{invocation};");
                writer.Line($"await {Binder}.CompleteAsync(__ctx).ConfigureAwait(false);");
                break;

            case ResponsePayload.Result:
                writer.Line($"var __result = {invocation};");
                writer.Line($"await {Binder}.ExecuteAsync(__ctx, __result).ConfigureAwait(false);");
                break;

            case ResponsePayload.String:
                writer.Line($"var __result = {invocation};");
                writer.Line("await __ctx.Response");
                writer.Indent();
                writer.Line(".WriteTextAsync(__result ?? string.Empty, cancellationToken: __ctx.RequestAborted)");
                writer.Line(".ConfigureAwait(false);");
                writer.Outdent();
                break;

            default:
                writer.Line($"var __result = {invocation};");
                writer.Line($"await {Results}.Ok<{method.PayloadTypeFullyQualified}>(__result)");
                writer.Indent();
                writer.Line(".ExecuteAsync(__ctx)");
                writer.Line(".ConfigureAwait(false);");
                writer.Outdent();
                break;
        }
    }

    static void WriteFactory(CodeWriter writer, EndpointClassModel model)
    {
        writer.Line(
            $"/// <summary>Resolves <see cref=\"{XmlRef(model.FullyQualifiedName)}\"/> from the request scope, " +
            "or constructs it directly when it was never registered.</summary>"
        );
        writer.Line($"static {model.FullyQualifiedName} Create{model.SafeName}(global::System.IServiceProvider __services)");
        writer.OpenBrace();
        writer.Line($"var __existing = {Services}.Get<{model.FullyQualifiedName}>(__services);");
        writer.Line("if (__existing is not null)");
        writer.Indent();
        writer.Line("return __existing;");
        writer.Outdent();
        writer.Blank();

        if (model.ConstructorParameterTypes.Count == 0)
        {
            writer.Line($"return new {model.FullyQualifiedName}();");
        }
        else
        {
            writer.Line($"return new {model.FullyQualifiedName}(");
            writer.Indent();
            var index = 0;
            foreach (var type in model.ConstructorParameterTypes)
            {
                var comma = ++index == model.ConstructorParameterTypes.Count ? string.Empty : ",";
                writer.Line(
                    $"{Services}.GetRequired<{type}>(__services, \"{model.DisplayName}\", \"ctor\"){comma}"
                );
            }
            writer.Outdent();
            writer.Line(");");
        }

        writer.CloseBrace();
    }

    public static string EmitAssembly(ImmutableArray<EndpointClassModel> models, string assembly)
    {
        var writer = new CodeWriter();
        WriteHeader(writer);

        writer.Line("namespace Shiny.Net.HttpServer");
        writer.OpenBrace();

        writer.Line($"public static partial class {assembly}EndpointExtensions");
        writer.OpenBrace();

        writer.Line($"/// <summary>Registers every endpoint class declared in this assembly ({models.Length} of them).</summary>");
        writer.Line($"public static {Server} Map{assembly}Endpoints(this {Server} server)");
        writer.OpenBrace();
        writer.Line("global::System.ArgumentNullException.ThrowIfNull(server);");
        writer.Blank();
        foreach (var model in models)
            writer.Line($"server.Map{model.DisplayName}();");
        writer.Blank();
        writer.Line("return server;");
        writer.CloseBrace();
        writer.Blank();

        writer.Line(
            "/// <summary>Registers every endpoint class as scoped, so they can also be injected elsewhere. " +
            "Optional — endpoints are constructed from the request scope with or without this.</summary>"
        );
        writer.Line(
            "public static global::Microsoft.Extensions.DependencyInjection.IServiceCollection " +
            $"Add{assembly}Endpoints(this global::Microsoft.Extensions.DependencyInjection.IServiceCollection services)"
        );
        writer.OpenBrace();
        writer.Line("global::System.ArgumentNullException.ThrowIfNull(services);");
        writer.Blank();
        foreach (var model in models)
        {
            writer.Line(
                "global::Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions" +
                $".AddScoped<{model.FullyQualifiedName}>(services);"
            );
        }
        writer.Blank();
        writer.Line("return services;");
        writer.CloseBrace();

        writer.CloseBrace();
        writer.CloseBrace();

        return writer.ToString();
    }

    /// <summary>
    /// Registers the app's own <c>JsonSerializerContext</c>s with the runtime registry.
    /// <para>
    /// The contexts themselves are written by the System.Text.Json generator, which this generator
    /// cannot see the output of — generators never see each other's work. It does not need to: the
    /// user's <c>partial class</c> declaration is enough to know the type's name, and
    /// <c>.Default</c> resolves at compile time once both generators' output is compiled together.
    /// </para>
    /// </summary>
    public static string EmitJsonRegistration(ImmutableArray<JsonContextModel> contexts)
    {
        var writer = new CodeWriter();
        WriteHeader(writer);

        writer.Line("namespace Shiny.Net.HttpServer.Generated");
        writer.OpenBrace();
        writer.Line("internal static class ShinyNetHttpServerJsonRegistration");
        writer.OpenBrace();
        writer.Line("[global::System.Runtime.CompilerServices.ModuleInitializer]");
        writer.Line("internal static void Initialize()");
        writer.OpenBrace();

        foreach (var context in contexts.Select(c => c.FullyQualifiedName).Distinct().OrderBy(n => n, System.StringComparer.Ordinal))
            writer.Line($"global::Shiny.Net.HttpServer.JsonTypeInfoRegistry.Register({context}.Default);");

        writer.CloseBrace();
        writer.CloseBrace();
        writer.CloseBrace();

        return writer.ToString();
    }

    static void WriteHeader(CodeWriter writer)
    {
        writer.Line("// <auto-generated/>");
        writer.Line("#nullable enable");
        writer.Line("#pragma warning disable CS1591");
        writer.Blank();
    }

    static string XmlRef(string fullyQualified)
        => fullyQualified.StartsWith("global::") ? fullyQualified.Substring("global::".Length) : fullyQualified;
}

/// <summary>A minimal indentation-aware writer. Generated code that is readable gets debugged.</summary>
sealed class CodeWriter
{
    readonly StringBuilder builder = new();
    int depth;

    public void Indent() => this.depth++;

    public void Outdent() => this.depth = this.depth > 0 ? this.depth - 1 : 0;

    public void Blank() => this.builder.AppendLine();

    public void Line(string text)
    {
        this.builder.Append(' ', this.depth * 4);
        this.builder.AppendLine(text);
    }

    public void OpenBrace()
    {
        this.Line("{");
        this.Indent();
    }

    public void CloseBrace()
    {
        this.Outdent();
        this.Line("}");
    }

    public void CloseBraceWith(string suffix)
    {
        this.Outdent();
        this.Line("}" + suffix);
    }

    public override string ToString() => this.builder.ToString();
}
