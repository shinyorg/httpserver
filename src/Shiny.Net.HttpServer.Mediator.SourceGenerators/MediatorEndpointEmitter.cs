using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;

namespace Shiny.Net.HttpServer.SourceGenerators;

/// <summary>
/// Writes the generated registrations. Everything is fully qualified with <c>global::</c> and uses
/// no <c>using</c> directives, because generated code lands in whatever namespace and aliasing
/// situation the user's file happens to have and must not care.
/// </summary>
static class MediatorEndpointEmitter
{
    const string Server = "global::Shiny.Net.HttpServer.HttpServer";
    const string Context = "global::Shiny.Net.HttpServer.HttpContext";
    const string Binder = "global::Shiny.Net.HttpServer.Endpoints.EndpointBinder";
    const string Descriptor = "global::Shiny.Net.HttpServer.Endpoints.EndpointDescriptor";
    const string Dispatch = "global::Shiny.Net.HttpServer.Mediator.MediatorDispatch";
    const string Operation = "global::Shiny.Net.HttpServer.OpenApi.ApiOperation";
    const string Location = "global::Shiny.Net.HttpServer.OpenApi.ApiParameterLocation";
    const string Authorization = "global::Shiny.Net.HttpServer.Security.AuthorizationMetadata";
    const string CorsMetadata = "global::Shiny.Net.HttpServer.Cors.CorsMetadata";
    const string RateLimitMetadata = "global::Shiny.Net.HttpServer.RateLimiting.RateLimitMetadata";
    const string IpFilterMetadata = "global::Shiny.Net.HttpServer.Security.IpFilterMetadata";

    public static string Emit(IReadOnlyList<MediatorHandlerModel> handlers, string assembly)
    {
        var writer = new CodeWriter();
        WriteHeader(writer);

        writer.Line("namespace Shiny.Net.HttpServer");
        writer.OpenBrace();

        writer.Line($"public static partial class {assembly}MediatorEndpointExtensions");
        writer.OpenBrace();

        var total = handlers.Sum(h => h.Endpoints.Count);

        writer.Line($"/// <summary>Registers the {total} mediator endpoint(s) declared in this assembly.</summary>");
        writer.Line($"public static {Server} MapGeneratedMediatorEndpoints(this {Server} server)");
        writer.OpenBrace();
        writer.Line("global::System.ArgumentNullException.ThrowIfNull(server);");
        writer.Blank();

        foreach (var handler in handlers)
            writer.Line($"server.Map{handler.DisplayName}MediatorEndpoints();");

        writer.Blank();
        writer.Line("return server;");
        writer.CloseBrace();

        foreach (var handler in handlers)
        {
            writer.Blank();
            WriteHandler(writer, handler);
        }

        writer.CloseBrace();
        writer.CloseBrace();

        return writer.ToString();
    }

    static void WriteHandler(CodeWriter writer, MediatorHandlerModel handler)
    {
        writer.Line($"/// <summary>Registers the {handler.Endpoints.Count} endpoint(s) published by <see cref=\"{XmlRef(handler.FullyQualifiedName)}\"/>.</summary>");
        writer.Line($"public static {Server} Map{handler.DisplayName}MediatorEndpoints(this {Server} server)");
        writer.OpenBrace();
        writer.Line("global::System.ArgumentNullException.ThrowIfNull(server);");
        writer.Blank();

        foreach (var endpoint in handler.Endpoints)
            WriteRegistration(writer, handler, endpoint);

        writer.Line("return server;");
        writer.CloseBrace();
    }

    static void WriteRegistration(CodeWriter writer, MediatorHandlerModel handler, MediatorEndpointModel endpoint)
    {
        writer.Line("server.Map(");
        writer.Indent();
        writer.Line($"\"{endpoint.HttpMethod}\",");
        writer.Line($"\"{endpoint.RouteTemplate}\",");
        writer.Line($"static async ({Context} __ctx) =>");
        writer.OpenBrace();

        if (endpoint.BindsFromBody)
            WriteBodyBinding(writer, endpoint);
        else
            WriteMemberBinding(writer, endpoint);

        WriteDispatch(writer, endpoint);

        writer.CloseBraceWith(",");
        writer.Line($"new {Descriptor}(\"{handler.DisplayName}\", \"{endpoint.ContractDisplay}\"),");

        var policies = PolicyMetadata(endpoint.Policies);

        WriteApiOperation(
            writer,
            endpoint,
            endpoint.Authorization.HasValue || policies.Count > 0 ? "," : string.Empty
        );

        if (endpoint.Authorization.HasValue)
            WriteAuthorization(writer, endpoint.Authorization, policies.Count > 0 ? "," : string.Empty);

        for (var i = 0; i < policies.Count; i++)
            writer.Line(policies[i] + (i < policies.Count - 1 ? "," : string.Empty));

        writer.Outdent();
        writer.Line(");");
        writer.Blank();
    }

    /// <summary>
    /// Reads the whole contract out of the request body, then lays any route tokens over the top.
    /// <para>
    /// The tokens go on afterwards rather than being merged into the JSON, because the URL is the
    /// authority on which resource is being addressed — a body that disagrees with the path it was
    /// sent to should not win.
    /// </para>
    /// </summary>
    static void WriteBodyBinding(CodeWriter writer, MediatorEndpointModel endpoint)
    {
        writer.Line($"var (__ok, __body) = await {Binder}");
        writer.Indent();
        writer.Line($".TryReadJsonBodyAsync<{endpoint.ContractFullyQualified}>(__ctx)");
        writer.Line(".ConfigureAwait(false);");
        writer.Outdent();

        writer.Line("if (!__ok || __body is null)");
        writer.OpenBrace();
        writer.Line($"await {Binder}");
        writer.Indent();
        writer.Line($".BindFailedAsync(__ctx, \"body\", {Binder}.Source.Body, \"{endpoint.ContractDisplay}\")");
        writer.Line(".ConfigureAwait(false);");
        writer.Outdent();
        writer.Line("return;");
        writer.CloseBrace();
        writer.Blank();

        writer.Line("var __request = __body;");
        writer.Blank();

        if (endpoint.RouteOverrides.Count == 0)
            return;

        foreach (var member in endpoint.RouteOverrides)
            WriteMemberRead(writer, member);

        if (endpoint.ContractIsRecord)
        {
            var assignments = string.Join(
                ", ",
                endpoint.RouteOverrides.Select(m => $"{m.MemberName} = __m_{m.MemberName}")
            );

            writer.Line($"__request = __request with {{ {assignments} }};");
        }
        else
        {
            foreach (var member in endpoint.RouteOverrides)
                writer.Line($"__request.{member.MemberName} = __m_{member.MemberName};");
        }

        writer.Blank();
    }

    static void WriteMemberBinding(CodeWriter writer, MediatorEndpointModel endpoint)
    {
        foreach (var member in endpoint.Members)
            WriteMemberRead(writer, member);

        var constructorArguments = endpoint.Members
            .Where(m => m.IsConstructorParameter)
            .Select(m => $"__m_{m.MemberName}")
            .ToArray();

        var initializers = endpoint.Members
            .Where(m => !m.IsConstructorParameter)
            .Select(m => $"{m.MemberName} = __m_{m.MemberName}")
            .ToArray();

        var construction = $"new {endpoint.ContractFullyQualified}({string.Join(", ", constructorArguments)})";

        if (initializers.Length > 0)
            construction += $" {{ {string.Join(", ", initializers)} }}";

        writer.Line($"var __request = {construction};");
        writer.Blank();
    }

    /// <summary>
    /// Reads one member from the route or query string into <c>__m_{name}</c>.
    /// <para>
    /// The same shape the endpoint generator emits for a handler parameter — the two have to agree,
    /// because a 400 for a bad <c>?page=x</c> should read the same whichever tier produced it.
    /// </para>
    /// </summary>
    static void WriteMemberRead(CodeWriter writer, MediatorMemberModel member)
    {
        var name = "__m_" + member.MemberName;

        var raw = member.Source == BindingSource.Route
            ? $"__ctx.Request.RouteValues[\"{member.BindingKey}\"]"
            : $"__ctx.Request.Query.GetFirst(\"{member.BindingKey}\")";

        var multi = $"__ctx.Request.Query[\"{member.BindingKey}\"]";

        switch (member.ScalarKind)
        {
            case ScalarKind.StringArray:
                writer.Line($"var {name} = {Binder}.BindStringArray({multi});");
                writer.Blank();
                return;

            case ScalarKind.ParsableArray:
                writer.Line($"if (!{Binder}.TryBindArray<{member.ElementTypeFullyQualified}>({multi}, out var {name}))");
                writer.OpenBrace();
                WriteBindFailure(writer, member);
                writer.Line("return;");
                writer.CloseBrace();
                writer.Blank();
                return;

            case ScalarKind.NullableParsable:
                writer.Line($"if (!{Binder}.TryBindNullable<{member.ElementTypeFullyQualified}>({raw}, out var {name}))");
                writer.OpenBrace();
                WriteBindFailure(writer, member);
                writer.Line("return;");
                writer.CloseBrace();
                writer.Blank();
                return;

            case ScalarKind.NullableEnum:
                writer.Line($"if (!{Binder}.TryBindNullableEnum<{member.ElementTypeFullyQualified}>({raw}, out var {name}))");
                writer.OpenBrace();
                WriteBindFailure(writer, member);
                writer.Line("return;");
                writer.CloseBrace();
                writer.Blank();
                return;

            case ScalarKind.String:
                writer.Line($"var __raw_{member.MemberName} = {raw};");
                if (member.DefaultLiteral is { } stringDefault)
                {
                    writer.Line(stringDefault == "default"
                        ? $"{member.TypeFullyQualified}? {name} = __raw_{member.MemberName};"
                        : $"var {name} = __raw_{member.MemberName} ?? {stringDefault};");
                }
                else
                {
                    writer.Line($"if (__raw_{member.MemberName} is null)");
                    writer.OpenBrace();
                    WriteBindFailure(writer, member);
                    writer.Line("return;");
                    writer.CloseBrace();
                    writer.Line($"var {name} = __raw_{member.MemberName};");
                }
                writer.Blank();
                return;

            default:
                var bind = member.ScalarKind == ScalarKind.Enum ? "TryBindEnum" : "TryBind";
                writer.Line($"var __raw_{member.MemberName} = {raw};");
                writer.Line($"{member.TypeFullyQualified} {name};");
                writer.Line($"if (__raw_{member.MemberName} is null)");
                writer.OpenBrace();
                if (member.DefaultLiteral is { } scalarDefault)
                {
                    writer.Line($"{name} = {scalarDefault};");
                }
                else
                {
                    WriteBindFailure(writer, member);
                    writer.Line("return;");
                }
                writer.CloseBrace();
                writer.Line($"else if (!{Binder}.{bind}<{member.TypeFullyQualified}>(__raw_{member.MemberName}, out {name}))");
                writer.OpenBrace();
                WriteBindFailure(writer, member);
                writer.Line("return;");
                writer.CloseBrace();
                writer.Blank();
                return;
        }
    }

    static void WriteBindFailure(CodeWriter writer, MediatorMemberModel member)
    {
        var source = member.Source == BindingSource.Route ? "Route" : "Query";

        writer.Line($"await {Binder}");
        writer.Indent();
        writer.Line($".BindFailedAsync(__ctx, \"{member.BindingKey}\", {Binder}.Source.{source}, \"{member.TypeDisplay}\")");
        writer.Line(".ConfigureAwait(false);");
        writer.Outdent();
    }

    static void WriteDispatch(CodeWriter writer, MediatorEndpointModel endpoint)
    {
        switch (endpoint.Kind)
        {
            case MediatorKind.Request:
                writer.Line($"await {Dispatch}");
                writer.Indent();
                writer.Line($".RequestAsync<{endpoint.ResultFullyQualified}>(__ctx, __request)");
                writer.Line(".ConfigureAwait(false);");
                writer.Outdent();
                return;

            case MediatorKind.Command:
                writer.Line($"await {Dispatch}");
                writer.Indent();
                writer.Line($".SendAsync(__ctx, __request, {endpoint.SuccessStatusCode})");
                writer.Line(".ConfigureAwait(false);");
                writer.Outdent();
                return;

            default:
                var eventName = endpoint.EventName is { Length: > 0 } name ? Literal(name) : "null";

                writer.Line($"await {Dispatch}");
                writer.Indent();
                writer.Line($".StreamAsync<{endpoint.ResultFullyQualified}>(__ctx, __request, {eventName})");
                writer.Line(".ConfigureAwait(false);");
                writer.Outdent();
                return;
        }
    }

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

    static void WriteApiOperation(CodeWriter writer, MediatorEndpointModel endpoint, string suffix)
    {
        writer.Line($"new {Operation}");
        writer.OpenBrace();

        if (endpoint.ApiExcluded)
        {
            writer.Line("Exclude = true");
            writer.CloseBraceWith(suffix);
            return;
        }

        if (endpoint.Summary is { Length: > 0 } summary)
            writer.Line($"Summary = {Literal(summary)},");

        if (endpoint.Description is { Length: > 0 } description)
            writer.Line($"Description = {Literal(description)},");

        writer.Line($"OperationId = {Literal(endpoint.OperationId ?? endpoint.ContractDisplay)},");

        if (endpoint.Tags.Count > 0)
            writer.Line($"Tags = {{ {string.Join(", ", endpoint.Tags.Select(Literal))} }},");

        if (endpoint.Members.Count > 0)
        {
            writer.Line("Parameters =");
            writer.OpenBrace();
            foreach (var member in endpoint.Members)
            {
                var location = member.Source == BindingSource.Route ? "Path" : "Query";

                writer.Line(
                    $"new() {{ Name = \"{member.BindingKey}\", In = {Location}.{location}, " +
                    $"Type = typeof({DocumentedType(member)}), Required = {(IsRequired(member) ? "true" : "false")} }},"
                );
            }
            writer.CloseBraceWith(",");
        }

        if (endpoint.BindsFromBody)
            writer.Line($"RequestBody = new() {{ Type = typeof({endpoint.ContractFullyQualified}) }},");

        writer.Line("Responses =");
        writer.OpenBrace();

        switch (endpoint.Kind)
        {
            case MediatorKind.Request:
                writer.Line($"new() {{ StatusCode = 200, Type = typeof({endpoint.ResultFullyQualified}), ContentType = \"application/json\" }},");
                break;

            case MediatorKind.Command:
                writer.Line($"new() {{ StatusCode = {endpoint.SuccessStatusCode}, ContentType = \"application/json\" }},");
                break;

            default:
                writer.Line($"new() {{ StatusCode = 200, Type = typeof({endpoint.ResultFullyQualified}), ContentType = \"text/event-stream\" }},");
                break;
        }

        writer.CloseBrace();
        writer.CloseBraceWith(suffix);
    }

    static string DocumentedType(MediatorMemberModel member) => member.ScalarKind switch
    {
        ScalarKind.NullableParsable or ScalarKind.NullableEnum => member.ElementTypeFullyQualified
            ?? member.TypeFullyQualified,
        _ => member.TypeFullyQualified
    };

    static bool IsRequired(MediatorMemberModel member)
        => member.DefaultLiteral is null
        && member.Source == BindingSource.Route
        && member.ScalarKind is not (ScalarKind.NullableParsable or ScalarKind.NullableEnum
            or ScalarKind.StringArray or ScalarKind.ParsableArray);

    public static string EmitJsonRegistration(ImmutableArray<JsonContextModel> contexts)
    {
        var writer = new CodeWriter();
        WriteHeader(writer);

        writer.Line("namespace Shiny.Net.HttpServer.Mediator.Generated");
        writer.OpenBrace();
        writer.Line("internal static class ShinyNetHttpServerMediatorJsonRegistration");
        writer.OpenBrace();
        writer.Line("[global::System.Runtime.CompilerServices.ModuleInitializer]");
        writer.Line("internal static void Initialize()");
        writer.OpenBrace();

        foreach (var context in contexts.Select(c => c.FullyQualifiedName).Distinct().OrderBy(n => n, StringComparer.Ordinal))
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
        => fullyQualified.StartsWith("global::", StringComparison.Ordinal)
            ? fullyQualified.Substring("global::".Length)
            : fullyQualified;

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
}

/// <summary>A tiny indent-aware writer. Its own copy, because the endpoint generator's lives inside
/// that generator's emitter and linking the file would bring the whole emitter with it.</summary>
sealed class CodeWriter
{
    readonly StringBuilder builder = new();
    int depth;

    public void Indent() => this.depth++;

    public void Outdent() => this.depth = Math.Max(0, this.depth - 1);

    public void Blank() => this.builder.AppendLine();

    public void Line(string text)
    {
        if (text.Length > 0)
            this.builder.Append(new string(' ', this.depth * 4));

        this.builder.AppendLine(text);
    }

    public void OpenBrace()
    {
        this.Line("{");
        this.Indent();
    }

    public void CloseBrace() => this.CloseBraceWith(string.Empty);

    public void CloseBraceWith(string suffix)
    {
        this.Outdent();
        this.Line("}" + suffix);
    }

    public override string ToString() => this.builder.ToString();
}
