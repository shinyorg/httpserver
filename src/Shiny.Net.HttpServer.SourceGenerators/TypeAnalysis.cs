using System.Linq;
using Microsoft.CodeAnalysis;

namespace Shiny.Net.HttpServer.SourceGenerators;

/// <summary>Symbol questions the generator keeps asking. Kept in one place so the answers stay consistent.</summary>
static class TypeAnalysis
{
    public const string HttpContextType = "Shiny.Net.HttpServer.HttpContext";
    public const string HttpRequestType = "Shiny.Net.HttpServer.HttpRequest";
    public const string HttpResponseType = "Shiny.Net.HttpServer.HttpResponse";
    public const string ResultInterface = "Shiny.Net.HttpServer.IResult";
    public const string CancellationTokenType = "System.Threading.CancellationToken";

    static readonly SymbolDisplayFormat Fq = SymbolDisplayFormat.FullyQualifiedFormat;

    static readonly SymbolDisplayFormat Friendly = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
    );

    /// <summary>Fully qualified with <c>global::</c> — always safe to emit, never ambiguous.</summary>
    public static string ToFullyQualified(this ITypeSymbol type) => type.ToDisplayString(Fq);

    /// <summary>Short and readable, for diagnostics and the text of a 400.</summary>
    public static string ToFriendly(this ITypeSymbol type) => type.ToDisplayString(Friendly);

    public static bool IsType(this ITypeSymbol type, string metadataName)
        => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat.WithGlobalNamespaceStyle(
            SymbolDisplayGlobalNamespaceStyle.Omitted)) == metadataName;

    public static bool ImplementsResult(this ITypeSymbol type)
        => type.IsType(ResultInterface)
        || type.AllInterfaces.Any(i => i.IsType(ResultInterface));

    /// <summary>
    /// True when the type can parse itself from a string. Covers every BCL primitive plus
    /// <see cref="System.Guid"/>, the date/time types, and any user type that opted in — which is
    /// exactly the set the generated binder can handle without reflection.
    /// </summary>
    public static bool ImplementsParsable(this ITypeSymbol type) => type.AllInterfaces.Any(
        i => i.IsGenericType
            && i.ConstructedFrom.ToDisplayString() == "System.IParsable<TSelf>"
            && SymbolEqualityComparer.Default.Equals(i.TypeArguments[0], type)
    );

    /// <summary>Unwraps <c>T?</c> for value types, returning null for anything else.</summary>
    public static ITypeSymbol? NullableUnderlying(this ITypeSymbol type)
        => type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } named
            ? named.TypeArguments[0]
            : null;

    public static bool AllowsNull(this ITypeSymbol type)
        => type.NullableUnderlying() is not null
        || (type.IsReferenceType && type.NullableAnnotation != NullableAnnotation.NotAnnotated);

    /// <summary>
    /// Classifies a parameter type for the binder. <c>None</c> means "not something a string can
    /// become" — a dependency or a request body, not a route value.
    /// </summary>
    public static ScalarKind ClassifyScalar(this ITypeSymbol type, out ITypeSymbol? elementType)
    {
        elementType = null;

        if (type.SpecialType == SpecialType.System_String)
            return ScalarKind.String;

        if (type is IArrayTypeSymbol array)
        {
            var element = array.ElementType;
            if (element.SpecialType == SpecialType.System_String)
            {
                elementType = element;
                return ScalarKind.StringArray;
            }
            if (element.ImplementsParsable())
            {
                elementType = element;
                return ScalarKind.ParsableArray;
            }
            return ScalarKind.None;
        }

        if (type.NullableUnderlying() is { } underlying)
        {
            elementType = underlying;
            if (underlying.TypeKind == TypeKind.Enum)
                return ScalarKind.NullableEnum;

            return underlying.ImplementsParsable() ? ScalarKind.NullableParsable : ScalarKind.None;
        }

        if (type.TypeKind == TypeKind.Enum)
            return ScalarKind.Enum;

        return type.ImplementsParsable() ? ScalarKind.Parsable : ScalarKind.None;
    }

    /// <summary>
    /// Unwraps <c>Task&lt;T&gt;</c> / <c>ValueTask&lt;T&gt;</c>. <paramref name="isAwaitable"/>
    /// is true for the bare <c>Task</c> and <c>ValueTask</c> too, where there is no T at all.
    /// </summary>
    public static ITypeSymbol? UnwrapAwaitable(this ITypeSymbol type, out bool isAwaitable)
    {
        isAwaitable = false;

        if (type is not INamedTypeSymbol named)
            return type;

        var definition = named.ConstructedFrom.ToDisplayString();
        switch (definition)
        {
            case "System.Threading.Tasks.Task":
            case "System.Threading.Tasks.ValueTask":
                isAwaitable = true;
                return null;

            case "System.Threading.Tasks.Task<TResult>":
            case "System.Threading.Tasks.ValueTask<TResult>":
                isAwaitable = true;
                return named.TypeArguments[0];

            default:
                return type;
        }
    }

    public static AttributeData? FindAttribute(this ISymbol symbol, string metadataName)
        => symbol.GetAttributes().FirstOrDefault(
            a => a.AttributeClass?.ToDisplayString() == metadataName
        );

    /// <summary>Reads a named argument, e.g. <c>[FromQuery(Name = "q")]</c>.</summary>
    public static string? GetNamedString(this AttributeData attribute, string name)
        => attribute.NamedArguments
            .FirstOrDefault(a => a.Key == name)
            .Value.Value as string;

    public static string? GetConstructorString(this AttributeData attribute, int index)
        => attribute.ConstructorArguments.Length > index
            ? attribute.ConstructorArguments[index].Value as string
            : null;

    /// <summary>Reads a constructor argument that is a number, e.g. <c>[RequestTimeout(2000)]</c>.</summary>
    public static int? GetConstructorInt(this AttributeData attribute, int index)
        => attribute.ConstructorArguments.Length > index && attribute.ConstructorArguments[index].Value is int value
            ? value
            : null;

    /// <summary>Reads a named argument that is a number, e.g. <c>[OutputCache(Seconds = 30)]</c>.</summary>
    public static int? GetNamedInt(this AttributeData attribute, string name)
        => attribute.NamedArguments.FirstOrDefault(a => a.Key == name).Value.Value is int value ? value : null;
}
