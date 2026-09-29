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
/// Turns <c>[SwitchboardClient&lt;TBoard, TClient&gt;]</c> on a partial class into a typed client:
/// <c>TBoard</c>'s methods become calls on the line, <c>TClient</c>'s become events (or a handler
/// property, where the server waits for an answer), all wired through <c>SwitchboardLine.Handle</c>.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class SwitchboardClientGenerator : IIncrementalGenerator
{
    const string AttributePrefix = "Shiny.Net.HttpServer.Switchboard.Client.SwitchboardClientAttribute<";
    const string Client = "global::Shiny.Net.HttpServer.Switchboard.Client";
    const string Task = "global::System.Threading.Tasks.Task";

    static readonly SymbolDisplayFormat Fq = SymbolDisplayFormat.FullyQualifiedFormat
        .AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    static readonly DiagnosticDescriptor ClassNotSupported = new(
        "SWBC001", "Typed switchboard client cannot be generated", "'{0}' cannot be a typed switchboard client: {1}",
        "Shiny.Net.HttpServer.Switchboard", DiagnosticSeverity.Error, isEnabledByDefault: true);

    static readonly DiagnosticDescriptor MemberNotSupported = new(
        "SWBC002", "Contract member is not supported", "'{0}' cannot be part of a typed switchboard client: {1}",
        "Shiny.Net.HttpServer.Switchboard", DiagnosticSeverity.Error, isEnabledByDefault: true);

    static readonly DiagnosticDescriptor NameCollision = new(
        "SWBC003", "Contract names collide", "'{0}' is used by more than one member of the generated client ({1})",
        "Shiny.Net.HttpServer.Switchboard", DiagnosticSeverity.Error, isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var clients = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is ClassDeclarationSyntax { AttributeLists.Count: > 0 },
                static (ctx, token) => Build(ctx, token)
            )
            .Where(static r => r is not null)
            .Select(static (r, _) => r!);

        context.RegisterSourceOutput(clients, static (spc, result) =>
        {
            foreach (var diagnostic in result.Diagnostics)
                spc.ReportDiagnostic(diagnostic.ToDiagnostic());

            if (result.Value is { } model)
                spc.AddSource($"{model.HintName}.SwitchboardClient.g.cs", model.Source);
        });
    }

    static Result<ClientModel>? Build(GeneratorSyntaxContext context, CancellationToken token)
    {
        var syntax = (ClassDeclarationSyntax)context.Node;
        if (context.SemanticModel.GetDeclaredSymbol(syntax, token) is not INamedTypeSymbol type)
            return null;

        var attribute = type.GetAttributes().FirstOrDefault(a =>
            a.AttributeClass is { IsGenericType: true } c
            && c.OriginalDefinition.ToDisplayString().StartsWith(AttributePrefix, StringComparison.Ordinal));

        if (attribute is null)
            return null;

        // A partial class declared in several parts is built from the part that carries the attribute.
        if (attribute.ApplicationSyntaxReference?.SyntaxTree != syntax.SyntaxTree
            || !syntax.Span.Contains(attribute.ApplicationSyntaxReference.Span))
            return null;

        var display = type.ToDisplayString();

        if (!syntax.Modifiers.Any(SyntaxKind.PartialKeyword))
            return Result<ClientModel>.Fail(DiagnosticInfo.Create(ClassNotSupported, type, display, "it must be declared partial"));

        if (type.ContainingType is not null || type.IsGenericType)
            return Result<ClientModel>.Fail(DiagnosticInfo.Create(ClassNotSupported, type, display, "it must be a non-generic, top-level class"));

        var arguments = attribute.AttributeClass!.TypeArguments;
        var board = arguments[0] as INamedTypeSymbol;
        var client = arguments.Length > 1 ? arguments[1] as INamedTypeSymbol : null;

        if (board is not { TypeKind: TypeKind.Interface } || (client is not null && client.TypeKind != TypeKind.Interface))
            return Result<ClientModel>.Fail(DiagnosticInfo.Create(ClassNotSupported, type, display, "the board and client types must both be interfaces"));

        var diagnostics = ImmutableArray.CreateBuilder<DiagnosticInfo>();
        var names = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Line"] = "the line property",
            ["Register"] = "the Register method",
            ["DisposeAsync"] = "DisposeAsync"
        };

        bool Claim(string name, string owner, ISymbol symbol)
        {
            if (names.TryGetValue(name, out var existing))
            {
                diagnostics.Add(DiagnosticInfo.Create(NameCollision, symbol, name, $"{existing} and {owner}"));
                return false;
            }

            names[name] = owner;
            return true;
        }

        var members = new StringBuilder();
        var registrations = new List<string>();

        foreach (var method in Methods(board))
        {
            if (Claim(method.Name, $"the call {method.Name}", method))
                RenderCall(method, members, diagnostics);
        }

        if (client is not null)
        {
            foreach (var method in Methods(client))
            {
                if (Claim(method.Name, $"the handler {method.Name}", method))
                    RenderHandler(method, client, members, registrations, diagnostics);
            }
        }

        foreach (var member in board.GetMembers().Concat(board.AllInterfaces.SelectMany(i => i.GetMembers())).Concat(client?.GetMembers() ?? ImmutableArray<ISymbol>.Empty))
        {
            if (member is not IMethodSymbol && member.IsAbstract)
                diagnostics.Add(DiagnosticInfo.Create(MemberNotSupported, member, member.ToDisplayString(), "a contract interface can only have methods"));
        }

        if (diagnostics.Any(d => d.Descriptor.DefaultSeverity == DiagnosticSeverity.Error))
            return Result<ClientModel>.Fail([.. diagnostics]);

        var code = new StringBuilder();
        code.Append("// <auto-generated/>\n#nullable enable\n#pragma warning disable CS1591\n\n");

        var ns = type.ContainingNamespace.IsGlobalNamespace ? null : type.ContainingNamespace.ToDisplayString();
        var indent = ns is null ? "" : "    ";
        if (ns is not null)
            code.Append($"namespace {ns}\n{{\n");

        var boardName = board.ToDisplayString(Fq);
        var clientName = client?.ToDisplayString(Fq);

        code.Append($"{indent}partial class {type.Name} : {boardName}, global::System.IAsyncDisposable\n{indent}{{\n");
        code.Append($"{indent}    readonly global::System.IDisposable[] registrations;\n");
        if (clientName is not null)
            code.Append($"{indent}    {clientName}? receiver;\n");
        code.Append('\n');
        code.Append($"{indent}    public {type.Name}({Client}.SwitchboardLine line)\n{indent}    {{\n");
        code.Append($"{indent}        this.Line = line ?? throw new global::System.ArgumentNullException(nameof(line));\n");
        code.Append($"{indent}        this.registrations = new global::System.IDisposable[]\n{indent}        {{\n");
        foreach (var registration in registrations)
            code.Append($"{indent}            {registration},\n");
        code.Append($"{indent}        }};\n{indent}    }}\n\n");
        code.Append($"{indent}    /// <summary>The line underneath: state, events, <c>StartAsync</c> and <c>StopAsync</c>.</summary>\n");
        code.Append($"{indent}    public {Client}.SwitchboardLine Line {{ get; }}\n");

        foreach (var line in members.ToString().Split('\n'))
        {
            if (line.Length > 0)
                code.Append(indent).Append(line).Append('\n');
            else
                code.Append('\n');
        }

        if (clientName is not null)
        {
            code.Append($"\n{indent}    /// <summary>Sends every call from the server to <paramref name=\"receiver\"/>, alongside any events. Dispose the result to stop.</summary>\n");
            code.Append($"{indent}    public global::System.IDisposable Register({clientName} receiver)\n{indent}    {{\n");
            code.Append($"{indent}        this.receiver = receiver ?? throw new global::System.ArgumentNullException(nameof(receiver));\n");
            code.Append($"{indent}        return new Unregistration(this, receiver);\n{indent}    }}\n\n");
            code.Append($"{indent}    sealed class Unregistration({type.Name} owner, {clientName} receiver) : global::System.IDisposable\n{indent}    {{\n");
            code.Append($"{indent}        public void Dispose()\n{indent}        {{\n");
            code.Append($"{indent}            if (global::System.Object.ReferenceEquals(owner.receiver, receiver))\n");
            code.Append($"{indent}                owner.receiver = null;\n");
            code.Append($"{indent}        }}\n{indent}    }}\n");
        }

        code.Append($"\n{indent}    /// <summary>Removes the handlers and closes the line.</summary>\n");
        code.Append($"{indent}    public async global::System.Threading.Tasks.ValueTask DisposeAsync()\n{indent}    {{\n");
        code.Append($"{indent}        foreach (var registration in this.registrations)\n");
        code.Append($"{indent}            registration.Dispose();\n\n");
        code.Append($"{indent}        await this.Line.DisposeAsync().ConfigureAwait(false);\n");
        code.Append($"{indent}    }}\n");
        code.Append($"{indent}}}\n");

        if (ns is not null)
            code.Append("}\n");

        return Result<ClientModel>.Ok(new ClientModel(type.ToDisplayString().Replace('<', '_').Replace('>', '_'), code.ToString()), diagnostics.ToImmutable());
    }

    static IEnumerable<IMethodSymbol> Methods(INamedTypeSymbol iface)
        => new[] { iface }.Concat(iface.AllInterfaces)
            .SelectMany(i => i.GetMembers().OfType<IMethodSymbol>())
            .Where(m => m.MethodKind == MethodKind.Ordinary && m.IsAbstract && !m.IsStatic);

    static bool IsToken(IParameterSymbol p) => p.Type.ToDisplayString() == "System.Threading.CancellationToken";

    static void RenderCall(IMethodSymbol method, StringBuilder members, ImmutableArray<DiagnosticInfo>.Builder diagnostics)
    {
        if (method.IsGenericMethod || method.Parameters.Any(p => p.RefKind != RefKind.None))
        {
            diagnostics.Add(DiagnosticInfo.Create(MemberNotSupported, method, method.ToDisplayString(), "calls cannot be generic or take ref/out/in parameters"));
            return;
        }

        var parameters = string.Join(", ", method.Parameters.Select(Parameter));
        var args = Arguments(method);
        var token = method.Parameters.FirstOrDefault(IsToken) is { } t ? Escape(t.Name) : "default";
        var name = SymbolDisplay.FormatLiteral(method.Name, quote: true);
        var returns = method.ReturnType.ToDisplayString(Fq);

        string? body = null;
        if (method.ReturnType is INamedTypeSymbol named)
        {
            switch (named.ConstructedFrom.ToDisplayString())
            {
                case "System.Threading.Tasks.Task":
                    body = $"this.Line.InvokeCoreAsync({name}, {args}, {token})";
                    break;
                case "System.Threading.Tasks.Task<TResult>":
                    body = $"this.Line.InvokeCoreAsync<{named.TypeArguments[0].ToDisplayString(Fq)}>({name}, {args}, {token})";
                    break;
                case "System.Threading.Tasks.ValueTask":
                    body = $"new global::System.Threading.Tasks.ValueTask(this.Line.InvokeCoreAsync({name}, {args}, {token}))";
                    break;
                case "System.Threading.Tasks.ValueTask<TResult>":
                    body = $"new {returns}(this.Line.InvokeCoreAsync<{named.TypeArguments[0].ToDisplayString(Fq)}>({name}, {args}, {token}))";
                    break;
                case "System.Collections.Generic.IAsyncEnumerable<T>":
                    body = $"this.Line.StreamCoreAsync<{named.TypeArguments[0].ToDisplayString(Fq)}>({name}, {args}, {token})";
                    break;
            }
        }

        if (body is null)
        {
            diagnostics.Add(DiagnosticInfo.Create(MemberNotSupported, method, method.ToDisplayString(), "a call must return Task, Task<T>, ValueTask, ValueTask<T> or IAsyncEnumerable<T>"));
            return;
        }

        members.Append($"\n    /// <summary>Calls <c>{Xml(method.Name)}</c> on the switchboard.</summary>\n");
        members.Append($"    public {returns} {Escape(method.Name)}({parameters})\n        => {body};\n");
    }

    static void RenderHandler(IMethodSymbol method, INamedTypeSymbol client, StringBuilder members, List<string> registrations, ImmutableArray<DiagnosticInfo>.Builder diagnostics)
    {
        if (method.IsGenericMethod || method.Parameters.Any(p => p.RefKind != RefKind.None))
        {
            diagnostics.Add(DiagnosticInfo.Create(MemberNotSupported, method, method.ToDisplayString(), "handlers cannot be generic or take ref/out/in parameters"));
            return;
        }

        var returnType = method.ReturnType as INamedTypeSymbol;
        var definition = returnType?.ConstructedFrom.ToDisplayString();
        var isResult = definition == "System.Threading.Tasks.Task<TResult>";

        if (definition != "System.Threading.Tasks.Task" && !isResult)
        {
            diagnostics.Add(DiagnosticInfo.Create(MemberNotSupported, method, method.ToDisplayString(), "a client method must return Task, or Task<T> when the server waits for an answer"));
            return;
        }

        var data = method.Parameters.Where(p => !IsToken(p)).ToList();
        var delegateTypes = data.Select(p => p.Type.ToDisplayString(Fq)).ToList();
        var name = Escape(method.Name);
        var literal = SymbolDisplay.FormatLiteral(method.Name, quote: true);
        var handlerName = "Handle_" + method.Name;

        // Reads each data argument by position; a CancellationToken parameter gets none.
        var reads = new StringBuilder();
        var index = 0;
        var callArguments = new List<string>();
        var delegateArguments = new List<string>();
        foreach (var parameter in method.Parameters)
        {
            if (IsToken(parameter))
            {
                callArguments.Add("global::System.Threading.CancellationToken.None");
                continue;
            }

            var variable = "a" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            reads.Append($"        var {variable} = args.Get<{parameter.Type.ToDisplayString(Fq)}>({index});\n");
            callArguments.Add(variable);
            delegateArguments.Add(variable);
            index++;
        }

        var clientCall = $"receiver.{name}({string.Join(", ", callArguments)})";
        var delegateCall = string.Join(", ", delegateArguments);

        if (!isResult)
        {
            var eventType = delegateTypes.Count == 0
                ? $"global::System.Func<{Task}>"
                : $"global::System.Func<{string.Join(", ", delegateTypes)}, {Task}>";

            members.Append($"\n    /// <summary>Raised when the switchboard calls <c>{Xml(method.Name)}</c>. Handlers run in turn, each awaited.</summary>\n");
            members.Append($"    public event {eventType}? {name};\n\n");
            members.Append($"    async {Task} {handlerName}({Client}.LineArguments args)\n    {{\n");
            members.Append(reads);
            members.Append($"        if (this.{name} is {{ }} handlers)\n        {{\n");
            members.Append($"            foreach (var handler in handlers.GetInvocationList())\n");
            members.Append($"                await (({eventType})handler)({delegateCall}).ConfigureAwait(false);\n        }}\n\n");
            members.Append($"        if (this.receiver is {{ }} receiver)\n");
            members.Append($"            await {clientCall}.ConfigureAwait(false);\n    }}\n");

            registrations.Add($"line.Handle({literal}, this.{handlerName})");
            return;
        }

        var resultType = returnType!.TypeArguments[0].ToDisplayString(Fq);
        var propertyType = delegateTypes.Count == 0
            ? $"global::System.Func<{Task}<{resultType}>>"
            : $"global::System.Func<{string.Join(", ", delegateTypes)}, {Task}<{resultType}>>";

        members.Append($"\n    /// <summary>\n    /// Answers the switchboard when it calls <c>{Xml(method.Name)}</c> and waits for the result. One answer per\n    /// call, so one handler: a property, not an event. <c>Register</c> takes precedence when both are set.\n    /// </summary>\n");
        members.Append($"    public {propertyType}? {name} {{ get; set; }}\n\n");
        members.Append($"    {Task}<{resultType}> {handlerName}({Client}.LineArguments args)\n    {{\n");
        members.Append(reads);
        members.Append($"        if (this.receiver is {{ }} receiver)\n            return {clientCall};\n\n");
        members.Append($"        if (this.{name} is {{ }} handler)\n            return handler({delegateCall});\n\n");
        members.Append($"        throw new {Client}.SwitchboardException(\"No handler is set for '{method.Name}'.\");\n    }}\n");

        registrations.Add($"line.Handle<{resultType}>({literal}, this.{handlerName})");
    }

    static string Parameter(IParameterSymbol parameter)
    {
        var rendered = $"{parameter.Type.ToDisplayString(Fq)} {Escape(parameter.Name)}";
        if (!parameter.HasExplicitDefaultValue)
            return rendered;

        return parameter.ExplicitDefaultValue is null
            ? rendered + " = default"
            : $"{rendered} = ({parameter.Type.ToDisplayString(Fq)})({SymbolDisplay.FormatPrimitive(parameter.ExplicitDefaultValue, quoteStrings: true, useHexadecimalNumbers: false)})";
    }

    static string Arguments(IMethodSymbol method)
    {
        var values = method.Parameters.Where(p => !IsToken(p)).Select(p => Escape(p.Name)).ToList();
        return values.Count == 0
            ? "global::System.Array.Empty<object?>()"
            : $"new object?[] {{ {string.Join(", ", values)} }}";
    }

    static string Escape(string name)
        => SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ? "@" + name : name;

    static string Xml(string value) => value.Replace("<", "{").Replace(">", "}");
}

sealed record ClientModel(string HintName, string Source);
