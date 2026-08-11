using System.Text;
using Microsoft.CodeAnalysis;

namespace Shiny.Net.HttpServer.SourceGenerators;

/// <summary>
/// Pulls the <c>&lt;summary&gt;</c> out of a doc comment so it can become an OpenAPI summary.
/// <para>
/// Reads the structured XML when the compilation produced it and falls back to the raw trivia when
/// it did not — <c>GetDocumentationCommentXml</c> returns nothing unless documentation parsing is
/// on, which it is not in a default build, and an endpoint description that appears only when a
/// csproj flag happens to be set is worse than no feature at all.
/// </para>
/// </summary>
static class DocComments
{
    public static string? Summary(ISymbol symbol)
    {
        var xml = symbol.GetDocumentationCommentXml();
        if (!string.IsNullOrWhiteSpace(xml) && Extract(xml!) is { Length: > 0 } fromXml)
            return fromXml;

        foreach (var reference in symbol.DeclaringSyntaxReferences)
        {
            var trivia = reference.GetSyntax().GetLeadingTrivia().ToFullString();
            if (Extract(trivia) is { Length: > 0 } fromTrivia)
                return fromTrivia;
        }

        return null;
    }

    static string? Extract(string text)
    {
        var start = text.IndexOf("<summary>", StringComparison.Ordinal);
        if (start < 0)
            return null;

        start += "<summary>".Length;

        var end = text.IndexOf("</summary>", start, StringComparison.Ordinal);
        if (end < 0)
            return null;

        return Clean(text.Substring(start, end - start));
    }

    /// <summary>
    /// Strips comment markers and inline tags, then collapses to one line. A summary lands in a
    /// JSON string; newlines and <c>&lt;see cref&gt;</c> markup would only make it harder to read.
    /// </summary>
    static string Clean(string raw)
    {
        var builder = new StringBuilder(raw.Length);
        var insideTag = false;

        foreach (var c in raw)
        {
            switch (c)
            {
                case '<':
                    insideTag = true;
                    break;

                case '>':
                    insideTag = false;
                    break;

                case '/' when !insideTag && builder.Length > 0 && builder[builder.Length - 1] == '/':
                    // Trailing "///" from raw trivia; drop the run entirely.
                    builder.Length--;
                    break;

                default:
                    if (!insideTag)
                        builder.Append(c);
                    break;
            }
        }

        var collapsed = new StringBuilder(builder.Length);
        var pendingSpace = false;

        foreach (var c in builder.ToString())
        {
            if (char.IsWhiteSpace(c) || c == '/')
            {
                pendingSpace = collapsed.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                collapsed.Append(' ');
                pendingSpace = false;
            }

            collapsed.Append(c);
        }

        return collapsed.ToString().Trim();
    }
}
