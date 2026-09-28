using System.Security.Cryptography;
using System.Text;

namespace Shiny.Net.HttpServer.Integrity;

/// <summary>
/// The digest fields of RFC 9530 — computing, formatting and parsing them — for code that wants to
/// set one itself rather than leave it to <see cref="ContentDigestMiddleware"/>.
/// <code>
/// ctx.Response.Headers.Set(DigestFields.ReprDigest, await DigestFields.ComputeAsync(file, DigestFields.Sha256));
/// </code>
/// <para>
/// Only <c>sha-256</c> and <c>sha-512</c>, the two algorithms the RFC registers as active. The others
/// in the registry (<c>md5</c>, <c>sha</c>, the checksums) are deprecated there for good reason: a
/// digest an attacker can collide is an integrity check in name only.
/// </para>
/// </summary>
public static class DigestFields
{
    /// <summary>A digest of the message content, as it went over the wire (after any content coding).</summary>
    public const string ContentDigest = "Content-Digest";

    /// <summary>
    /// A digest of the whole selected representation — the same as <see cref="ContentDigest"/>
    /// for a full 200, and the one a ranged download checks once every part has arrived.
    /// </summary>
    public const string ReprDigest = "Repr-Digest";

    /// <summary>Asks the other side to send <see cref="ContentDigest"/>, with a preference per algorithm.</summary>
    public const string WantContentDigest = "Want-Content-Digest";

    /// <summary>Asks the other side to send <see cref="ReprDigest"/>.</summary>
    public const string WantReprDigest = "Want-Repr-Digest";

    public const string Sha256 = "sha-256";

    public const string Sha512 = "sha-512";

    /// <summary>True for an algorithm this server can compute and verify.</summary>
    public static bool IsSupported(string algorithm)
        => string.Equals(algorithm, Sha256, StringComparison.OrdinalIgnoreCase)
            || string.Equals(algorithm, Sha512, StringComparison.OrdinalIgnoreCase);

    /// <summary>Hashes <paramref name="content"/> and formats it as a field value: <c>sha-256=:…:</c>.</summary>
    public static string Compute(ReadOnlySpan<byte> content, string algorithm = Sha256)
        => Format(algorithm, Hash(content, algorithm));

    /// <summary>Hashes a stream to its end and formats it as a field value.</summary>
    public static async ValueTask<string> ComputeAsync(Stream content, string algorithm = Sha256, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        using var hash = CreateHash(algorithm);
        var buffer = new byte[16 * 1024];

        int read;
        while ((read = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            hash.AppendData(buffer, 0, read);

        return Format(algorithm, hash.GetHashAndReset());
    }

    /// <summary>One dictionary member: the algorithm, and the digest as a structured-field byte sequence.</summary>
    public static string Format(string algorithm, ReadOnlySpan<byte> digest)
    {
        ArgumentException.ThrowIfNullOrEmpty(algorithm);
        return $"{algorithm.ToLowerInvariant()}=:{Convert.ToBase64String(digest)}:";
    }

    /// <summary>
    /// Parses a <c>Content-Digest</c> or <c>Repr-Digest</c> value — a structured-field dictionary of
    /// algorithm to byte sequence. Algorithms this server does not know are returned too; what to do
    /// about them is the caller's decision.
    /// </summary>
    public static bool TryParse(string? value, out IReadOnlyList<KeyValuePair<string, byte[]>> digests)
    {
        var parsed = new List<KeyValuePair<string, byte[]>>();
        digests = parsed;

        if (!TrySplit(value, out var members))
            return false;

        foreach (var (key, item) in members)
        {
            if (item.Length < 2 || item[0] != ':' || item[^1] != ':')
                return false;

            var base64 = item[1..^1];
            var bytes = new byte[base64.Length];

            if (!Convert.TryFromBase64String(base64, bytes, out var written))
                return false;

            parsed.Add(new KeyValuePair<string, byte[]>(key, bytes[..written]));
        }

        return parsed.Count > 0;
    }

    /// <summary>
    /// Parses a <c>Want-Content-Digest</c> or <c>Want-Repr-Digest</c> value — algorithm to a
    /// preference from 0 (not acceptable) to 10 (most preferred) — and returns the supported
    /// algorithm the sender prefers most, or null when none is acceptable.
    /// </summary>
    public static string? SelectPreferred(string? value)
    {
        if (!TrySplit(value, out var members))
            return null;

        string? best = null;
        var bestWeight = 0;

        foreach (var (key, item) in members)
        {
            if (!int.TryParse(item, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var weight))
                continue;

            // Strictly greater, so on a tie the first one listed wins — the order the sender wrote
            // them in is the only other preference it expressed.
            if (weight > bestWeight && weight <= 10 && IsSupported(key))
            {
                best = key;
                bestWeight = weight;
            }
        }

        return best;
    }

    /// <summary>The <c>Want-Content-Digest</c> value this server answers with: what it can verify.</summary>
    internal static string WantValue(string preferred)
    {
        var other = string.Equals(preferred, Sha512, StringComparison.OrdinalIgnoreCase) ? Sha256 : Sha512;
        return $"{preferred.ToLowerInvariant()}=10, {other}=5";
    }

    internal static IncrementalHash CreateHash(string algorithm)
        => string.Equals(algorithm, Sha512, StringComparison.OrdinalIgnoreCase)
            ? IncrementalHash.CreateHash(HashAlgorithmName.SHA512)
            : string.Equals(algorithm, Sha256, StringComparison.OrdinalIgnoreCase)
                ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
                : throw new NotSupportedException($"Digest algorithm '{algorithm}' is not supported. Use '{Sha256}' or '{Sha512}'.");

    static byte[] Hash(ReadOnlySpan<byte> content, string algorithm)
        => string.Equals(algorithm, Sha512, StringComparison.OrdinalIgnoreCase)
            ? SHA512.HashData(content)
            : string.Equals(algorithm, Sha256, StringComparison.OrdinalIgnoreCase)
                ? SHA256.HashData(content)
                : throw new NotSupportedException($"Digest algorithm '{algorithm}' is not supported. Use '{Sha256}' or '{Sha512}'.");

    /// <summary>
    /// Splits a structured-field dictionary into its members, dropping parameters.
    /// <para>
    /// Splitting on commas is safe for the two value shapes these fields use: a byte sequence is
    /// base64 between colons and an integer is digits, and neither can contain a comma or a
    /// semicolon. A general structured-field parser would buy nothing here.
    /// </para>
    /// </summary>
    static bool TrySplit(string? value, out List<(string Key, string Value)> members)
    {
        members = [];

        if (string.IsNullOrWhiteSpace(value))
            return false;

        foreach (var raw in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var member = raw;
            var parameters = member.IndexOf(';');
            if (parameters >= 0)
                member = member[..parameters];

            var equals = member.IndexOf('=');
            if (equals <= 0)
                return false;

            var key = member[..equals].Trim().ToLowerInvariant();
            if (!IsKey(key))
                return false;

            members.Add((key, member[(equals + 1)..].Trim()));
        }

        return members.Count > 0;
    }

    static bool IsKey(string key)
    {
        if (key.Length == 0 || !(char.IsAsciiLetterLower(key[0]) || key[0] == '*'))
            return false;

        foreach (var c in key)
        {
            if (!(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '_' or '-' or '.' or '*'))
                return false;
        }

        return true;
    }

    internal static string Join(Microsoft.Extensions.Primitives.StringValues values)
    {
        if (values.Count <= 1)
            return values.ToString();

        var builder = new StringBuilder();
        foreach (var value in values)
        {
            if (builder.Length > 0)
                builder.Append(", ");

            builder.Append(value);
        }

        return builder.ToString();
    }
}
