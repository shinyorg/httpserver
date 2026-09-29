using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Shiny.Net.HttpServer.SourceGenerators;

/// <summary>
/// Writes the dispatcher for every switchboard: a factory that does constructor injection, one
/// handler per public method that binds its JSON arguments with registered metadata and writes its
/// result, and a module initializer that registers the lot. SignalR does this with reflection over
/// the hub type at startup; nothing here may, so it is decided at compile time instead.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class SwitchboardGenerator : IIncrementalGenerator
{
    const string BaseTypeName = "Shiny.Net.HttpServer.Switchboard.Switchboard";
    const string CallerContextName = "Shiny.Net.HttpServer.Switchboard.CallerContext";
    const string LineMethodAttributeName = "Shiny.Net.HttpServer.Switchboard.LineMethodAttribute";
    const string NotALineMethodAttributeName = "Shiny.Net.HttpServer.Switchboard.NotALineMethodAttribute";
    const string AuthorizeAttributeName = "Shiny.Net.HttpServer.AuthorizeAttribute";
    const string AllowAnonymousAttributeName = "Shiny.Net.HttpServer.AllowAnonymousAttribute";
    const string FromServicesAttributeName = "Shiny.Net.HttpServer.FromServicesAttribute";
    const string ExportContractAttributeName = "Shiny.Net.HttpServer.Switchboard.ExportContractAttribute";

    const string Runtime = "global::Shiny.Net.HttpServer.Switchboard";
    const string Services = "global::Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions";

    static readonly SymbolDisplayFormat Fq = SymbolDisplayFormat.FullyQualifiedFormat;

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var assemblyName = context.CompilationProvider
            .Select(static (compilation, _) => ToIdentifier(compilation.AssemblyName ?? "Generated"));

        var boards = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is ClassDeclarationSyntax { BaseList: not null },
                static (ctx, token) => Build(ctx, token)
            )
            .Where(static r => r is not null)
            .Select(static (r, _) => r!);

        context.RegisterSourceOutput(boards, static (spc, result) =>
        {
            foreach (var diagnostic in result.Diagnostics)
                spc.ReportDiagnostic(diagnostic.ToDiagnostic());
        });

        var valid = boards
            .Where(static r => r.Value is not null)
            .Select(static (r, _) => r.Value!);

        context.RegisterSourceOutput(
            valid.Collect().Combine(assemblyName),
            static (spc, pair) => Emit(spc, pair.Left, pair.Right)
        );
    }

    // ---- model ----

    static Result<BoardModel>? Build(GeneratorSyntaxContext context, CancellationToken token)
    {
        var syntax = (ClassDeclarationSyntax)context.Node;

        if (context.SemanticModel.GetDeclaredSymbol(syntax, token) is not INamedTypeSymbol type)
            return null;

        if (type.IsAbstract || type.IsStatic || !DerivesFromSwitchboard(type))
            return null;

        var display = type.ToDisplayString();

        if (type.IsGenericType || ContainingTypes(type).Any(t => t.IsGenericType))
            return Result<BoardModel>.Fail(DiagnosticInfo.Create(SwitchboardDiagnostics.BoardNotSupported, type, display, "generic switchboards are not supported"));

        if (!IsAccessible(type))
            return Result<BoardModel>.Fail(DiagnosticInfo.Create(SwitchboardDiagnostics.BoardNotSupported, type, display, "it must be public or internal, and so must any type it is nested in"));

        var diagnostics = ImmutableArray.CreateBuilder<DiagnosticInfo>();

        var factory = BuildFactory(type);
        if (factory is null)
            return Result<BoardModel>.Fail(DiagnosticInfo.Create(SwitchboardDiagnostics.NoConstructor, type, display));

        var classMetadata = BuildMetadata(type);
        var classRequiresAuth = type.GetAttributes().Any(a => IsAttribute(a, AuthorizeAttributeName));

        var methods = new List<MethodModel>();
        var lineMethods = new List<(IMethodSymbol Method, string Name)>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var overridden = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);

        for (var current = type; current is not null && !IsSwitchboardBase(current); current = current.BaseType)
        {
            foreach (var method in current.GetMembers().OfType<IMethodSymbol>())
            {
                token.ThrowIfCancellationRequested();

                if (method.MethodKind != MethodKind.Ordinary || method.IsStatic || method.DeclaredAccessibility != Accessibility.Public)
                    continue;

                // Only the most-derived override of a virtual is a method; the base versions it
                // replaced are not separately callable.
                if (overridden.Contains(method))
                    continue;

                for (var o = method.OverriddenMethod; o is not null; o = o.OverriddenMethod)
                    overridden.Add(o);

                if (IsInheritedInfrastructure(method))
                    continue;

                if (method.GetAttributes().Any(a => IsAttribute(a, NotALineMethodAttributeName)))
                    continue;

                if (method.Name is "Dispose" or "DisposeAsync" && method.Parameters.Length == 0)
                    continue;

                if (method.IsGenericMethod)
                {
                    diagnostics.Add(DiagnosticInfo.Create(SwitchboardDiagnostics.GenericMethod, method, method.ToDisplayString()));
                    continue;
                }

                var name = method.GetAttributes()
                    .FirstOrDefault(a => IsAttribute(a, LineMethodAttributeName))
                    ?.ConstructorArguments.FirstOrDefault().Value as string
                    ?? method.Name;

                if (!names.Add(name))
                {
                    diagnostics.Add(DiagnosticInfo.Create(SwitchboardDiagnostics.DuplicateMethodName, method, display, name));
                    continue;
                }

                if (classRequiresAuth && method.GetAttributes().Any(a => IsAttribute(a, AllowAnonymousAttributeName)))
                    diagnostics.Add(DiagnosticInfo.Create(SwitchboardDiagnostics.AllowAnonymousHasNoEffect, method, method.ToDisplayString()));

                if (BuildMethod(type, method, name, diagnostics) is { } model)
                {
                    methods.Add(model);
                    lineMethods.Add((method, name));
                }
            }
        }

        CheckContracts(type, diagnostics);

        var client = ClientProxyBuilder.FindClientInterface(type);
        var proxy = client is null ? null : ClientProxyBuilder.Build(client, diagnostics);

        var export = type.GetAttributes().FirstOrDefault(a => IsAttribute(a, ExportContractAttributeName)) is { } exportAttribute
            ? ContractExporter.Export(type, exportAttribute, lineMethods, client, context.SemanticModel.Compilation, diagnostics)
            : null;

        var board = new BoardModel(
            type.ToDisplayString(Fq),
            factory,
            classMetadata,
            methods.ToEquatableArray(),
            proxy,
            export
        );

        return Result<BoardModel>.Ok(board, diagnostics.ToImmutable());
    }

    /// <summary>
    /// A switchboard that implements a contract interface is saying "clients can call these". The
    /// compiler already proves each method exists; this catches the ways one can exist and still not
    /// be callable under the contract's name.
    /// </summary>
    static void CheckContracts(INamedTypeSymbol type, ImmutableArray<DiagnosticInfo>.Builder diagnostics)
    {
        foreach (var contract in type.AllInterfaces)
        {
            if (contract.ContainingNamespace.ToDisplayString() is var ns && (ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal)))
                continue;

            foreach (var member in contract.GetMembers().OfType<IMethodSymbol>())
            {
                if (type.FindImplementationForInterfaceMember(member) is not IMethodSymbol implementation)
                    continue;

                string? reason = null;
                if (implementation.MethodKind == MethodKind.ExplicitInterfaceImplementation)
                    reason = "it is implemented explicitly, so it is not a public method";
                else if (implementation.GetAttributes().Any(a => IsAttribute(a, NotALineMethodAttributeName)))
                    reason = "it is marked [NotALineMethod]";
                else if (implementation.GetAttributes().FirstOrDefault(a => IsAttribute(a, LineMethodAttributeName))?.ConstructorArguments.FirstOrDefault().Value is string renamed
                    && renamed != member.Name)
                    reason = $"[LineMethod] publishes it as '{renamed}'";

                if (reason is not null)
                    diagnostics.Add(DiagnosticInfo.Create(SwitchboardDiagnostics.ContractNotCallable, implementation, implementation.ToDisplayString(), contract.ToDisplayString(), member.Name, reason));
            }
        }
    }

    static MethodModel? BuildMethod(INamedTypeSymbol board, IMethodSymbol method, string name, ImmutableArray<DiagnosticInfo>.Builder diagnostics)
    {
        var display = method.ToDisplayString();
        var arguments = new List<string>();
        var body = new StringBuilder();
        var required = 0;
        var total = 0;
        var failed = false;

        foreach (var parameter in method.Parameters)
        {
            var parameterType = parameter.Type;
            var typeName = parameterType.ToDisplayString(Fq);

            if (parameter.RefKind != RefKind.None)
            {
                diagnostics.Add(DiagnosticInfo.Create(SwitchboardDiagnostics.UnsupportedParameter, parameter, parameter.Name, display, "ref, out and in parameters cannot come from a client"));
                failed = true;
                continue;
            }

            if (parameterType.ToDisplayString() == "System.Threading.CancellationToken")
            {
                arguments.Add("invocation.Aborted");
                continue;
            }

            if (parameterType.ToDisplayString() == CallerContextName)
            {
                arguments.Add("invocation.Caller");
                continue;
            }

            if (parameter.GetAttributes().Any(a => IsAttribute(a, FromServicesAttributeName)))
            {
                arguments.Add($"{Services}.GetRequiredService<{typeName}>(invocation.Services)");
                continue;
            }

            if (parameterType.IsRefLikeType || parameterType.TypeKind == TypeKind.Pointer)
            {
                diagnostics.Add(DiagnosticInfo.Create(SwitchboardDiagnostics.UnsupportedParameter, parameter, parameter.Name, display, "ref structs and pointers cannot be deserialized"));
                failed = true;
                continue;
            }

            if (IsAsyncEnumerable(parameterType, out _))
            {
                diagnostics.Add(DiagnosticInfo.Create(SwitchboardDiagnostics.UnsupportedParameter, parameter, parameter.Name, display, "streaming from the client is not supported yet"));
                failed = true;
                continue;
            }

            var variable = "a" + total.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var index = total.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var literalName = SymbolDisplay.FormatLiteral(parameter.Name, quote: true);

            if (parameter.HasExplicitDefaultValue)
            {
                body.Append($"        var {variable} = invocation.GetArgument<{typeName}>({index}, {literalName}, {DefaultValue(parameter)});\n");
            }
            else
            {
                body.Append($"        var {variable} = invocation.GetArgument<{typeName}>({index}, {literalName});\n");
                required = total + 1;
            }

            arguments.Add(variable);
            total++;
        }

        var call = $"target.{EscapeIdentifier(method.Name)}({string.Join(", ", arguments)})";
        var returns = method.ReturnType;

        if (method.ReturnsByRef || method.ReturnsByRefReadonly)
        {
            diagnostics.Add(DiagnosticInfo.Create(SwitchboardDiagnostics.UnsupportedReturn, method, display, returns.ToDisplayString(), "ref returns cannot be sent to a client"));
            return null;
        }

        if (method.ReturnsVoid)
        {
            body.Append($"        {call};\n");
            body.Append("        await invocation.CompleteAsync();\n");
        }
        else if (IsAsyncEnumerable(returns, out var itemType))
        {
            body.Append($"        await invocation.StreamAsync<{itemType!.ToDisplayString(Fq)}>({call});\n");
        }
        else if (UnwrapAwaitable(returns, out var awaitedType))
        {
            if (awaitedType is null)
            {
                body.Append($"        await {call};\n");
                body.Append("        await invocation.CompleteAsync();\n");
            }
            else if (IsAsyncEnumerable(awaitedType, out _))
            {
                diagnostics.Add(DiagnosticInfo.Create(SwitchboardDiagnostics.UnsupportedReturn, method, display, returns.ToDisplayString(), "return the IAsyncEnumerable<T> itself — an async iterator — rather than a task of one"));
                return null;
            }
            else
            {
                body.Append($"        var result = await {call};\n");
                body.Append($"        await invocation.ReturnAsync<{awaitedType.ToDisplayString(Fq)}>(result);\n");
            }
        }
        else
        {
            body.Append($"        var result = {call};\n");
            body.Append($"        await invocation.ReturnAsync<{returns.ToDisplayString(Fq)}>(result);\n");
        }

        if (failed)
            return null;

        return new MethodModel(name, required, total, BuildMetadata(method), body.ToString());
    }

    /// <summary>
    /// Picks the constructor with the most parameters — the one DI would pick — and resolves each
    /// from the scope. An optional or nullable parameter tolerates a missing registration.
    /// </summary>
    static string? BuildFactory(INamedTypeSymbol type)
    {
        var constructor = type.InstanceConstructors
            .Where(c => c.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal)
            .OrderByDescending(c => c.Parameters.Length)
            .FirstOrDefault();

        if (constructor is null)
            return null;

        var arguments = constructor.Parameters.Select(p =>
        {
            var typeName = p.Type.ToDisplayString(Fq);

            if (p.Type.ToDisplayString() == "System.IServiceProvider")
                return "services";

            var optional = p.HasExplicitDefaultValue
                || p.NullableAnnotation == NullableAnnotation.Annotated
                || p.Type.NullableAnnotation == NullableAnnotation.Annotated;

            return optional
                ? $"{Services}.GetService<{typeName}>(services)"
                : $"{Services}.GetRequiredService<{typeName}>(services)";
        });

        return $"new {type.ToDisplayString(Fq)}({string.Join(", ", arguments)})";
    }

    static EquatableArray<string> BuildMetadata(ISymbol symbol)
    {
        var metadata = new List<string>();

        foreach (var attribute in symbol.GetAttributes())
        {
            if (IsAttribute(attribute, AuthorizeAttributeName))
            {
                var assignments = new List<string>();

                if (attribute.ConstructorArguments.FirstOrDefault().Value is string policy)
                    assignments.Add($"Policy = {SymbolDisplay.FormatLiteral(policy, quote: true)}");

                foreach (var named in attribute.NamedArguments)
                {
                    if (named.Key is "Policy" or "Roles" && named.Value.Value is string value)
                        assignments.Add($"{named.Key} = {SymbolDisplay.FormatLiteral(value, quote: true)}");
                }

                metadata.Add($"new global::{AuthorizeAttributeName} {{ {string.Join(", ", assignments)} }}");
            }
            else if (IsAttribute(attribute, AllowAnonymousAttributeName))
            {
                metadata.Add($"new global::{AllowAnonymousAttributeName}()");
            }
        }

        return metadata.ToEquatableArray();
    }

    static string DefaultValue(IParameterSymbol parameter)
    {
        var typeName = parameter.Type.ToDisplayString(Fq);

        return parameter.ExplicitDefaultValue is null
            ? $"default({typeName})"
            : $"({typeName})({SymbolDisplay.FormatPrimitive(parameter.ExplicitDefaultValue, quoteStrings: true, useHexadecimalNumbers: false)})";
    }

    // ---- symbol questions ----

    static bool IsSwitchboardBase(INamedTypeSymbol type) => type.ToDisplayString() == BaseTypeName;

    static bool DerivesFromSwitchboard(INamedTypeSymbol type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (IsSwitchboardBase(current))
                return true;
        }

        return false;
    }

    /// <summary>Overrides of <c>Switchboard</c>'s lifecycle callbacks and of <c>object</c>'s members are not client-callable.</summary>
    static bool IsInheritedInfrastructure(IMethodSymbol method)
    {
        var root = method;
        while (root.OverriddenMethod is { } next)
            root = next;

        if (!method.IsOverride)
            return false;

        return root.ContainingType.SpecialType == SpecialType.System_Object
            || IsSwitchboardBase(root.ContainingType);
    }

    static bool IsAttribute(AttributeData attribute, string metadataName)
        => attribute.AttributeClass?.ToDisplayString() == metadataName;

    static bool IsAsyncEnumerable(ITypeSymbol type, out ITypeSymbol? itemType)
    {
        if (type is INamedTypeSymbol { IsGenericType: true } named
            && named.OriginalDefinition.ToDisplayString() == "System.Collections.Generic.IAsyncEnumerable<T>")
        {
            itemType = named.TypeArguments[0];
            return true;
        }

        itemType = null;
        return false;
    }

    /// <summary>True for <c>Task</c>/<c>ValueTask</c>, with <paramref name="awaited"/> the result type or null.</summary>
    static bool UnwrapAwaitable(ITypeSymbol type, out ITypeSymbol? awaited)
    {
        awaited = null;

        if (type is not INamedTypeSymbol named)
            return false;

        switch (named.ConstructedFrom.ToDisplayString())
        {
            case "System.Threading.Tasks.Task":
            case "System.Threading.Tasks.ValueTask":
                return true;

            case "System.Threading.Tasks.Task<TResult>":
            case "System.Threading.Tasks.ValueTask<TResult>":
                awaited = named.TypeArguments[0];
                return true;

            default:
                return false;
        }
    }

    static IEnumerable<INamedTypeSymbol> ContainingTypes(INamedTypeSymbol type)
    {
        for (var current = type.ContainingType; current is not null; current = current.ContainingType)
            yield return current;
    }

    static bool IsAccessible(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? current = type; current is not null; current = current.ContainingType)
        {
            if (current.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal))
                return false;
        }

        return true;
    }

    static string EscapeIdentifier(string name)
        => SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ? "@" + name : name;

    static string ToIdentifier(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
            builder.Append(char.IsLetterOrDigit(c) ? c : '_');

        return builder.ToString();
    }

    // ---- emit ----

    static void Emit(SourceProductionContext context, ImmutableArray<BoardModel> boards, string assembly)
    {
        if (boards.Length == 0)
            return;

        // A partial class with a base list on more than one part arrives more than once.
        var distinct = boards
            .GroupBy(b => b.TypeName, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(b => b.TypeName, StringComparer.Ordinal)
            .ToList();

        var code = new StringBuilder();
        code.Append("// <auto-generated/>\n");
        code.Append("#nullable disable\n");
        code.Append("#pragma warning disable CS1591, CS0618, CA2255\n\n");
        code.Append("namespace Shiny.Net.HttpServer.Switchboard.Generated\n{\n");
        code.Append("    [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]\n");
        code.Append($"    internal static class SwitchboardRegistration_{assembly}\n    {{\n");
        code.Append("        [global::System.Runtime.CompilerServices.ModuleInitializer]\n");
        code.Append("        internal static void Initialize()\n        {\n");

        for (var i = 0; i < distinct.Count; i++)
            code.Append($"            {Runtime}.SwitchboardRegistry.Register(Board{i}());\n");

        // One implementation per client interface, however many switchboards share it.
        var proxies = distinct
            .Select(b => b.ClientProxy)
            .Where(p => p is not null)
            .GroupBy(p => p!.InterfaceName, StringComparer.Ordinal)
            .Select(g => g.First()!)
            .ToList();

        for (var i = 0; i < proxies.Count; i++)
            code.Append($"            {Runtime}.SwitchboardClientProxies.Register<{proxies[i].InterfaceName}>(static proxy => new ClientProxy{i}(proxy));\n");

        code.Append("        }\n");

        for (var i = 0; i < distinct.Count; i++)
            EmitBoard(code, distinct[i], i);

        for (var i = 0; i < proxies.Count; i++)
        {
            code.Append('\n');
            code.Append($"        sealed class ClientProxy{i}({Runtime}.IClientProxy proxy) : {proxies[i].InterfaceName}\n");
            code.Append("        {\n");
            foreach (var member in proxies[i].Members)
                code.Append("            ").Append(member).Append('\n');
            code.Append("        }\n");
        }

        code.Append("    }\n}\n");

        context.AddSource($"Switchboards.{assembly}.g.cs", code.ToString());

        foreach (var contract in distinct.Select(b => b.Contract).Where(c => c is not null))
            context.AddSource(contract!.HintName, contract.Source);
    }

    static void EmitBoard(StringBuilder code, BoardModel board, int index)
    {
        code.Append('\n');
        code.Append($"        // {board.TypeName}\n");
        code.Append($"        static {Runtime}.SwitchboardDescriptor Board{index}() => new {Runtime}.SwitchboardDescriptor(\n");
        code.Append($"            typeof({board.TypeName}),\n");
        code.Append($"            static services => {board.Factory},\n");
        code.Append($"            {ObjectArray(board.Metadata)},\n");
        code.Append($"            new {Runtime}.SwitchboardMethod[]\n            {{\n");

        for (var m = 0; m < board.Methods.Count; m++)
        {
            var method = board.Methods[m];
            code.Append($"                new {Runtime}.SwitchboardMethod({SymbolDisplay.FormatLiteral(method.Name, quote: true)}, {method.Required}, {method.Total}, {ObjectArray(method.Metadata)}, Board{index}_Method{m}),\n");
        }

        code.Append("            }\n        );\n");

        for (var m = 0; m < board.Methods.Count; m++)
        {
            var method = board.Methods[m];

            code.Append('\n');
            code.Append($"        static async global::System.Threading.Tasks.ValueTask Board{index}_Method{m}({Runtime}.Switchboard board, {Runtime}.SwitchboardInvocation invocation)\n");
            code.Append("        {\n");
            code.Append($"            var target = ({board.TypeName})board;\n");

            foreach (var line in method.Body.Split('\n'))
            {
                if (line.Length > 0)
                    code.Append("    ").Append(line).Append('\n');
            }

            code.Append("        }\n");
        }
    }

    static string ObjectArray(EquatableArray<string> items)
        => items.Count == 0
            ? "global::System.Array.Empty<object>()"
            : $"new object[] {{ {string.Join(", ", items)} }}";
}

sealed record BoardModel(
    string TypeName,
    string Factory,
    EquatableArray<string> Metadata,
    EquatableArray<MethodModel> Methods,
    ClientProxyModel? ClientProxy,
    ContractModel? Contract
);

sealed record MethodModel(string Name, int Required, int Total, EquatableArray<string> Metadata, string Body);
