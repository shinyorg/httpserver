using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Shiny.Net.HttpServer.SourceGenerators;

/// <summary>
/// A diagnostic reduced to values.
/// <para>
/// <see cref="Location"/> holds a <c>SyntaxTree</c>, which holds the whole compilation; caching a
/// <see cref="Diagnostic"/> in a pipeline model would therefore root everything it touched and
/// defeat incremental caching entirely. This carries only the coordinates and rebuilds the real
/// diagnostic at the point it is reported.
/// </para>
/// </summary>
sealed record LocationInfo(string FilePath, TextSpan TextSpan, LinePositionSpan LineSpan)
{
    public Location ToLocation() => Location.Create(this.FilePath, this.TextSpan, this.LineSpan);

    public static LocationInfo? From(SyntaxNode? node)
        => node is null ? null : From(node.GetLocation());

    public static LocationInfo? From(ISymbol? symbol)
        => symbol?.Locations.FirstOrDefault(l => l.IsInSource) is { } location ? From(location) : null;

    static LocationInfo? From(Location location)
    {
        if (location.SourceTree is null)
            return null;

        return new LocationInfo(
            location.SourceTree.FilePath,
            location.SourceSpan,
            location.GetLineSpan().Span
        );
    }
}

sealed record DiagnosticInfo(
    DiagnosticDescriptor Descriptor,
    LocationInfo? Location,
    EquatableArray<string> Arguments
)
{
    public static DiagnosticInfo Create(
        DiagnosticDescriptor descriptor,
        ISymbol? symbol,
        params string[] arguments
    ) => new(descriptor, LocationInfo.From(symbol), arguments.ToEquatableArray());

    public static DiagnosticInfo Create(
        DiagnosticDescriptor descriptor,
        SyntaxNode? node,
        params string[] arguments
    ) => new(descriptor, LocationInfo.From(node), arguments.ToEquatableArray());

    public Diagnostic ToDiagnostic() => Diagnostic.Create(
        this.Descriptor,
        this.Location?.ToLocation(),
        this.Arguments.Cast<object?>().ToArray()
    );
}

/// <summary>A pipeline stage's output: whatever it produced, plus whatever went wrong producing it.</summary>
sealed record Result<T>(T? Value, EquatableArray<DiagnosticInfo> Diagnostics) where T : class
{
    public static Result<T> Fail(params DiagnosticInfo[] diagnostics)
        => new(null, diagnostics.ToEquatableArray());

    public static Result<T> Ok(T value)
        => new(value, EquatableArray<DiagnosticInfo>.Empty);

    public static Result<T> Ok(T value, ImmutableArray<DiagnosticInfo> diagnostics)
        => new(value, new EquatableArray<DiagnosticInfo>(diagnostics));
}
