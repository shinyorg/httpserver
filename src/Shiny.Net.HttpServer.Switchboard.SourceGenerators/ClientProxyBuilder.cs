using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Shiny.Net.HttpServer.SourceGenerators;

/// <summary>
/// Builds the implementation of a <c>Switchboard&lt;TClient&gt;</c>'s client interface: each method
/// becomes a named call on an <c>IClientProxy</c>. SignalR emits this with <c>Reflection.Emit</c> at
/// run time; here it is ordinary code, written once at compile time.
/// </summary>
static class ClientProxyBuilder
{
    const string TypedBaseName = "Shiny.Net.HttpServer.Switchboard.Switchboard<TClient>";
    const string Runtime = "global::Shiny.Net.HttpServer.Switchboard";

    static readonly SymbolDisplayFormat Fq = SymbolDisplayFormat.FullyQualifiedFormat;

    public static INamedTypeSymbol? FindClientInterface(INamedTypeSymbol board)
    {
        for (var current = board.BaseType; current is not null; current = current.BaseType)
        {
            if (current.OriginalDefinition.ToDisplayString() == TypedBaseName)
                return current.TypeArguments[0] as INamedTypeSymbol;
        }

        return null;
    }

    public static ClientProxyModel? Build(INamedTypeSymbol client, ImmutableArray<DiagnosticInfo>.Builder diagnostics)
    {
        var display = client.ToDisplayString();

        if (client.TypeKind != TypeKind.Interface)
        {
            diagnostics.Add(DiagnosticInfo.Create(SwitchboardDiagnostics.UnsupportedClientMember, client, display, "the client type of a Switchboard<TClient> must be an interface"));
            return null;
        }

        if (client.IsGenericType)
        {
            diagnostics.Add(DiagnosticInfo.Create(SwitchboardDiagnostics.UnsupportedClientMember, client, display, "generic client interfaces are not supported"));
            return null;
        }

        var members = ImmutableArray.CreateBuilder<string>();
        var failed = false;

        foreach (var iface in new[] { client }.Concat(client.AllInterfaces))
        {
            var ifaceName = iface.ToDisplayString(Fq);

            foreach (var member in iface.GetMembers())
            {
                if (member.IsStatic || !member.IsAbstract)
                    continue;

                if (member is not IMethodSymbol { MethodKind: MethodKind.Ordinary } method)
                {
                    diagnostics.Add(DiagnosticInfo.Create(SwitchboardDiagnostics.UnsupportedClientMember, member, member.ToDisplayString(), "a client interface can only have methods"));
                    failed = true;
                    continue;
                }

                if (Render(method, ifaceName, diagnostics) is { } rendered)
                    members.Add(rendered);
                else
                    failed = true;
            }
        }

        return failed ? null : new ClientProxyModel(client.ToDisplayString(Fq), members.ToImmutable().ToEquatableArray());
    }

    static string? Render(IMethodSymbol method, string ifaceName, ImmutableArray<DiagnosticInfo>.Builder diagnostics)
    {
        var display = method.ToDisplayString();

        if (method.IsGenericMethod)
        {
            diagnostics.Add(DiagnosticInfo.Create(SwitchboardDiagnostics.UnsupportedClientMember, method, display, "a client method cannot be generic"));
            return null;
        }

        if (method.Parameters.Any(p => p.RefKind != RefKind.None))
        {
            diagnostics.Add(DiagnosticInfo.Create(SwitchboardDiagnostics.UnsupportedClientMember, method, display, "ref, out and in parameters cannot be sent to a client"));
            return null;
        }

        var returnType = method.ReturnType as INamedTypeSymbol;
        var definition = returnType?.ConstructedFrom.ToDisplayString();
        var isTask = definition == "System.Threading.Tasks.Task";
        var isTaskOfT = definition == "System.Threading.Tasks.Task<TResult>";

        if (!isTask && !isTaskOfT)
        {
            diagnostics.Add(DiagnosticInfo.Create(SwitchboardDiagnostics.UnsupportedClientMember, method, display, "a client method must return Task, or Task<T> to wait for the client's answer"));
            return null;
        }

        var parameters = string.Join(", ", method.Parameters.Select(p => $"{p.Type.ToDisplayString(Fq)} {Escape(p.Name)}"));
        var isToken = (IParameterSymbol p) => p.Type.ToDisplayString() == "System.Threading.CancellationToken";
        var args = string.Join(", ", method.Parameters.Where(p => !isToken(p)).Select(p => Escape(p.Name)));
        var token = method.Parameters.FirstOrDefault(isToken) is { } t ? Escape(t.Name) : "default";
        var name = SymbolDisplay.FormatLiteral(method.Name, quote: true);
        var array = args.Length == 0 ? "global::System.Array.Empty<object>()" : $"new object[] {{ {args} }}";

        return isTask
            ? $"global::System.Threading.Tasks.Task {ifaceName}.{Escape(method.Name)}({parameters}) => proxy.SendCoreAsync({name}, {array}, {token});"
            : $"{returnType!.ToDisplayString(Fq)} {ifaceName}.{Escape(method.Name)}({parameters}) => {Runtime}.SwitchboardClientResults.InvokeAsync<{returnType.TypeArguments[0].ToDisplayString(Fq)}>(proxy, {name}, {array}, {token});";
    }

    public static string Escape(string name)
        => SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ? "@" + name : name;
}

sealed record ClientProxyModel(string InterfaceName, EquatableArray<string> Members);
