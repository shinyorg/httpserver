using System.Globalization;

namespace Shiny.Net.HttpServer.Negotiation;

/// <summary>One entry from an <c>Accept</c> header: a media range and how much the client wants it.</summary>
/// <param name="Type">The type part, or <c>*</c>.</param>
/// <param name="SubType">The subtype part, or <c>*</c>.</param>
/// <param name="Quality">Preference from 0 to 1. Zero is a refusal, not a weak preference.</param>
public readonly record struct MediaTypeRange(string Type, string SubType, double Quality)
{
    /// <summary>
    /// How specific this range is: an exact type beats <c>type/*</c>, which beats <c>*/*</c>.
    /// <para>
    /// RFC 9110 settles ties by specificity, which is what stops a browser's trailing <c>*/*;q=0.8</c>
    /// from being treated as an equal preference to the <c>text/html</c> it actually asked for.
    /// </para>
    /// </summary>
    public int Specificity => (this.Type == "*" ? 0 : 2) + (this.SubType == "*" ? 0 : 1);

    /// <summary>Whether a concrete media type falls inside this range.</summary>
    public bool Matches(string mediaType)
    {
        var slash = mediaType.IndexOf('/');
        if (slash <= 0)
            return false;

        var type = mediaType.AsSpan(0, slash);
        var subType = mediaType.AsSpan(slash + 1);

        // Parameters on the offered type ("application/json; charset=utf-8") are not part of the
        // match — the client asked for a format, not an encoding.
        var semicolon = subType.IndexOf(';');
        if (semicolon >= 0)
            subType = subType[..semicolon].TrimEnd();

        if (this.Type != "*" && !type.Equals(this.Type, StringComparison.OrdinalIgnoreCase))
            return false;

        return this.SubType == "*" || subType.Equals(this.SubType, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Parses an <c>Accept</c> header, best first.
    /// <para>
    /// An absent or empty header means the client will take anything, which is reported as a single
    /// <c>*/*</c> — the same thing it means, without every caller having to special-case null.
    /// </para>
    /// </summary>
    public static IReadOnlyList<MediaTypeRange> ParseAccept(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
            return [new MediaTypeRange("*", "*", 1)];

        var ranges = new List<MediaTypeRange>();

        foreach (var part in header.Split(','))
        {
            var entry = part.AsSpan().Trim();
            if (entry.IsEmpty)
                continue;

            var quality = 1d;
            var mediaType = entry;

            var semicolon = entry.IndexOf(';');
            if (semicolon >= 0)
            {
                mediaType = entry[..semicolon].Trim();

                // Only q matters here. Other parameters (level, charset) are accepted and ignored.
                foreach (var parameterRange in entry[(semicolon + 1)..].ToString().Split(';'))
                {
                    var parameter = parameterRange.AsSpan().Trim();

                    if (parameter.StartsWith("q=", StringComparison.OrdinalIgnoreCase)
                        && double.TryParse(parameter[2..], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                        quality = Math.Clamp(parsed, 0, 1);
                }
            }

            var slash = mediaType.IndexOf('/');
            if (slash <= 0)
                continue;

            ranges.Add(new MediaTypeRange(
                mediaType[..slash].Trim().ToString(),
                mediaType[(slash + 1)..].Trim().ToString(),
                quality
            ));
        }

        if (ranges.Count == 0)
            return [new MediaTypeRange("*", "*", 1)];

        return [.. ranges.OrderByDescending(r => r.Quality).ThenByDescending(r => r.Specificity)];
    }
}
