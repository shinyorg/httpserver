using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Shiny.Net.HttpServer.SourceGenerators;

/// <summary>
/// Reads <c>[ApiVersion]</c>, <c>[MapToApiVersion]</c> and <c>[ApiVersionNeutral]</c> off an endpoint
/// class and method, rejecting at build time any version the runtime would fail to parse at startup.
/// </summary>
static class ApiVersionInfo
{
    const string ApiVersionAttributeName = "Shiny.Net.HttpServer.ApiVersionAttribute";
    const string MapToApiVersionAttributeName = "Shiny.Net.HttpServer.MapToApiVersionAttribute";
    const string ApiVersionNeutralAttributeName = "Shiny.Net.HttpServer.ApiVersionNeutralAttribute";

    /// <summary>
    /// Combines the class's and the method's declarations. The class declares the API's versions; a
    /// method's own <c>[ApiVersion]</c> adds to them and scopes the method to what it names, as does
    /// <c>[MapToApiVersion]</c>. <c>[ApiVersionNeutral]</c> on either wins outright.
    /// </summary>
    public static ApiVersionModel For(
        INamedTypeSymbol type,
        IMethodSymbol method,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics
    )
    {
        if (type.FindAttribute(ApiVersionNeutralAttributeName) is not null ||
            method.FindAttribute(ApiVersionNeutralAttributeName) is not null)
            return new ApiVersionModel(EquatableArray<string>.Empty, EquatableArray<string>.Empty, EquatableArray<string>.Empty, true);

        var supported = new List<string>();
        var deprecated = new List<string>();
        var mapped = new List<string>();

        Declare(type, supported, deprecated, null, diagnostics);
        Declare(method, supported, deprecated, mapped, diagnostics);

        foreach (var attribute in method.GetAttributes().Where(a => a.AttributeClass?.ToDisplayString() == MapToApiVersionAttributeName))
        {
            var text = VersionText(attribute);
            if (!Validate(text, method, diagnostics))
                continue;

            AddUnique(mapped, text!);

            if (!supported.Concat(deprecated).Any(v => ApiVersionModel.Key(v) == ApiVersionModel.Key(text)))
                diagnostics.Add(DiagnosticInfo.Create(Diagnostics.UndeclaredMappedApiVersion, method, method.Name, text!));
        }

        return new ApiVersionModel(
            supported.ToEquatableArray(),
            deprecated.ToEquatableArray(),
            mapped.ToEquatableArray(),
            false
        );
    }

    static void Declare(
        ISymbol symbol,
        List<string> supported,
        List<string> deprecated,
        List<string>? mapped,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics
    )
    {
        foreach (var attribute in symbol.GetAttributes().Where(a => a.AttributeClass?.ToDisplayString() == ApiVersionAttributeName))
        {
            var text = VersionText(attribute);
            if (!Validate(text, symbol, diagnostics))
                continue;

            var isDeprecated = attribute.NamedArguments.Any(a => a.Key == "Deprecated" && a.Value.Value is true);
            AddUnique(isDeprecated ? deprecated : supported, text!);

            // A method-level [ApiVersion] scopes the method to it.
            if (mapped is not null)
                AddUnique(mapped, text!);
        }
    }

    static bool Validate(string? text, ISymbol symbol, ImmutableArray<DiagnosticInfo>.Builder diagnostics)
    {
        if (text is not null && ApiVersionModel.Key(text) is not null)
            return true;

        diagnostics.Add(DiagnosticInfo.Create(Diagnostics.InvalidApiVersion, symbol, text ?? "(null)", symbol.Name));
        return false;
    }

    static void AddUnique(List<string> list, string text)
    {
        var key = ApiVersionModel.Key(text);
        if (!list.Any(v => ApiVersionModel.Key(v) == key))
            list.Add(text);
    }

    static string? VersionText(AttributeData attribute)
    {
        if (attribute.ConstructorArguments.Length == 0)
            return null;

        return attribute.ConstructorArguments[0].Value switch
        {
            string text => text.Trim(),
            double number => number.ToString("0.0###############", CultureInfo.InvariantCulture),
            _ => null
        };
    }
}
