using System.Globalization;
using System.IO.Compression;

namespace Shiny.Net.HttpServer.Compression;

/// <summary>What gets compressed, and how hard.</summary>
public sealed class ResponseCompressionOptions
{
    /// <summary>
    /// Codings the server can produce, best first. Brotli, gzip and deflate by default; remove one
    /// to stop offering it.
    /// </summary>
    public IList<ICompressionProvider> Providers { get; } =
    [
        new BrotliCompressionProvider(),
        new GzipCompressionProvider(),
        new DeflateCompressionProvider()
    ];

    /// <summary>
    /// Content types worth compressing. Matched exactly, or by prefix for a <c>type/*</c> entry.
    /// <para>
    /// Everything here is text or text-shaped. Images, video, fonts and archives are already
    /// compressed, and running them through gzip spends CPU to make them very slightly larger.
    /// </para>
    /// </summary>
    public ISet<string> MimeTypes { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "text/*",
        "application/json",
        "application/problem+json",
        "application/xml",
        "application/problem+xml",
        "application/javascript",
        "application/manifest+json",
        "application/wasm",
        "image/svg+xml",
        "application/x-ndjson"
    };

    /// <summary>Types never compressed, even when <see cref="MimeTypes"/> would match them.</summary>
    public ISet<string> ExcludedMimeTypes { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Compressing an event stream is legal and usually wrong: the compressor buffers, and a
        // stream whose whole purpose is immediacy arrives in bursts instead.
        "text/event-stream"
    };

    /// <summary>
    /// Responses smaller than this are sent as-is.
    /// <para>
    /// Below roughly a packet's worth there is nothing to win — the compressed form is often
    /// larger, and it is never fewer round trips. Only applies when the length is known up front;
    /// a streamed response of unknown size is compressed regardless.
    /// </para>
    /// </summary>
    public int MinimumBytes { get; set; } = 1024;

    public CompressionLevel Level { get; set; } = CompressionLevel.Fastest;

    /// <summary>
    /// Compresses over HTTPS as well as plain HTTP.
    /// <para>
    /// On by default, because a tunnelled server is HTTPS end to end and switching compression off
    /// there would mean never compressing at all. The reason it is a switch: BREACH infers a secret
    /// by watching compressed response sizes, and it needs a response that contains both a secret
    /// (a CSRF token) and attacker-controlled input. An API returning data has neither; a rendered
    /// HTML page with a hidden token in it might. Turn it off for those.
    /// </para>
    /// </summary>
    public bool EnableForHttps { get; set; } = true;

    /// <summary>Decides whether a response should be compressed, overriding all of the above.</summary>
    public Func<HttpContext, bool>? ShouldCompress { get; set; }

    /// <summary>Whether a content type is on the list.</summary>
    public bool IsCompressibleType(string? contentType)
    {
        if (contentType is not { Length: > 0 })
            return false;

        // "text/html; charset=utf-8" — the parameters are not part of the match.
        var semicolon = contentType.IndexOf(';');
        var type = (semicolon < 0 ? contentType : contentType[..semicolon]).Trim();

        if (Matches(this.ExcludedMimeTypes, type))
            return false;

        return Matches(this.MimeTypes, type);
    }

    static bool Matches(ISet<string> patterns, string contentType)
    {
        if (patterns.Contains(contentType))
            return true;

        var slash = contentType.IndexOf('/');
        if (slash <= 0)
            return false;

        return patterns.Contains(string.Concat(contentType.AsSpan(0, slash + 1), "*"))
            || patterns.Contains("*/*");
    }

    /// <summary>
    /// Picks the best coding for an <c>Accept-Encoding</c> header, or null when the client wants
    /// none of what is on offer.
    /// </summary>
    public ICompressionProvider? SelectProvider(string? acceptEncoding)
    {
        if (acceptEncoding is not { Length: > 0 })
            return null;

        ICompressionProvider? best = null;
        double bestQuality = 0;
        var wildcardQuality = -1d;

        foreach (var range in acceptEncoding.Split(','))
        {
            var entry = range.AsSpan().Trim();
            if (entry.IsEmpty)
                continue;

            var quality = 1d;
            var name = entry;

            var semicolon = entry.IndexOf(';');
            if (semicolon >= 0)
            {
                name = entry[..semicolon].Trim();

                var parameters = entry[(semicolon + 1)..].Trim();
                if (parameters.StartsWith("q=", StringComparison.OrdinalIgnoreCase)
                    && double.TryParse(parameters[2..], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                    quality = parsed;
            }

            // "q=0" is a refusal, not a weak preference.
            if (quality <= 0)
            {
                if (name.SequenceEqual("*"))
                    wildcardQuality = 0;

                continue;
            }

            if (name.SequenceEqual("*"))
            {
                wildcardQuality = Math.Max(wildcardQuality, quality);
                continue;
            }

            foreach (var provider in this.Providers)
            {
                if (!name.Equals(provider.EncodingName, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Higher q wins; equal q falls back to the provider's own preference, which is how
                // "gzip, br" — no stated preference — still picks brotli.
                if (quality > bestQuality || (quality == bestQuality && best is not null && provider.Priority > best.Priority))
                {
                    best = provider;
                    bestQuality = quality;
                }
                else if (best is null)
                {
                    best = provider;
                    bestQuality = quality;
                }
            }
        }

        // A bare "*" accepts anything not otherwise named, so the server's own first choice applies.
        if (best is null && wildcardQuality > 0)
            best = this.Providers.OrderByDescending(p => p.Priority).FirstOrDefault();

        return best;
    }
}
