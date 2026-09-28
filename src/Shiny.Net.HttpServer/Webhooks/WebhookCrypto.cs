using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Shiny.Net.HttpServer.Webhooks;

/// <summary>The hash behind an HMAC signature.</summary>
public enum WebhookHmacAlgorithm
{
    /// <summary>
    /// HMAC-SHA1. Still what some older senders use; HMAC does not inherit SHA-1's collision
    /// weakness, but prefer SHA-256 wherever the sender offers it.
    /// </summary>
    Sha1,
    Sha256,
    Sha512
}

/// <summary>How the signature bytes are written into the header.</summary>
public enum WebhookSignatureEncoding
{
    /// <summary>Hexadecimal, either case — GitHub, Stripe, Slack.</summary>
    Hex,

    /// <summary>Standard base64 — Standard Webhooks, Shopify.</summary>
    Base64
}

/// <summary>
/// The arithmetic every built-in verifier shares: an HMAC over one or more segments, a
/// constant-time comparison against a signature as the sender encoded it, and a timestamp check.
/// </summary>
static class WebhookCrypto
{
    static readonly UTF8Encoding Utf8 = new(false, true);

    public static HashAlgorithmName ToHashName(WebhookHmacAlgorithm algorithm) => algorithm switch
    {
        WebhookHmacAlgorithm.Sha1 => HashAlgorithmName.SHA1,
        WebhookHmacAlgorithm.Sha256 => HashAlgorithmName.SHA256,
        WebhookHmacAlgorithm.Sha512 => HashAlgorithmName.SHA512,
        _ => throw new ArgumentOutOfRangeException(nameof(algorithm), algorithm, null)
    };

    /// <summary>
    /// HMAC over <paramref name="prefix"/> followed by <paramref name="body"/>, without copying the
    /// body into a new buffer to put the prefix in front of it.
    /// </summary>
    public static byte[] Compute(HashAlgorithmName algorithm, byte[] key, string? prefix, ReadOnlySpan<byte> body)
    {
        using var hmac = IncrementalHash.CreateHMAC(algorithm, key);

        if (!String.IsNullOrEmpty(prefix))
            hmac.AppendData(Utf8.GetBytes(prefix));

        hmac.AppendData(body);
        return hmac.GetHashAndReset();
    }

    /// <summary>
    /// Decodes a signature as the sender wrote it and compares it with <paramref name="expected"/>
    /// in constant time. Anything that does not decode is simply not a match.
    /// </summary>
    public static bool Matches(ReadOnlySpan<byte> expected, ReadOnlySpan<char> candidate, WebhookSignatureEncoding encoding)
    {
        candidate = candidate.Trim();
        if (candidate.IsEmpty)
            return false;

        // Sized to the expected hash: a candidate that decodes to anything longer cannot match,
        // and FixedTimeEquals only leaks length, which is public anyway.
        Span<byte> decoded = stackalloc byte[128];
        int written;

        if (encoding == WebhookSignatureEncoding.Hex)
        {
            if (candidate.Length % 2 != 0 || candidate.Length / 2 > decoded.Length)
                return false;

            if (Convert.FromHexString(candidate, decoded, out _, out written) != OperationStatus.Done)
                return false;
        }
        else
        {
            if (!Convert.TryFromBase64Chars(candidate, decoded, out written))
                return false;
        }

        return CryptographicOperations.FixedTimeEquals(expected, decoded[..written]);
    }

    /// <summary>
    /// Parses a Unix-seconds timestamp and checks it against the tolerance. A null tolerance turns
    /// the check off — never the default, since a signature with no freshness check is valid
    /// forever and anyone who captured one delivery can replay it.
    /// </summary>
    public static bool TryCheckTimestamp(
        string? raw,
        DateTimeOffset now,
        TimeSpan? tolerance,
        out DateTimeOffset timestamp,
        out string? failure
    )
    {
        timestamp = default;

        if (!long.TryParse(raw, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
            || seconds > DateTimeOffset.MaxValue.ToUnixTimeSeconds())
        {
            failure = "the timestamp is missing or is not a Unix time";
            return false;
        }

        timestamp = DateTimeOffset.FromUnixTimeSeconds(seconds);

        // Both directions: a timestamp from the future is as suspicious as a stale one, and a clock
        // skewed either way is what the tolerance is there to absorb.
        if (tolerance is { } window && (now - timestamp).Duration() > window)
        {
            failure = $"the timestamp {seconds} is outside the {window.TotalSeconds:0}s tolerance";
            return false;
        }

        failure = null;
        return true;
    }

    /// <summary>Validates and converts a list of UTF-8 secrets up front, so a bad one fails at startup.</summary>
    public static byte[][] Utf8Keys(IEnumerable<string> secrets, string provider)
    {
        ArgumentNullException.ThrowIfNull(secrets);

        var keys = secrets
            .Select(x => String.IsNullOrEmpty(x)
                ? throw new ArgumentException($"A {provider} webhook secret cannot be empty.", nameof(secrets))
                : Encoding.UTF8.GetBytes(x))
            .ToArray();

        if (keys.Length == 0)
            throw new ArgumentException($"A {provider} webhook verifier needs at least one secret.", nameof(secrets));

        return keys;
    }

    /// <summary>
    /// Reads the top-level <c>id</c> and <c>type</c> members of a JSON body, for senders that put the
    /// delivery id there rather than in a header. Tolerates anything that is not a JSON object.
    /// </summary>
    public static (string? Id, string? Type) ReadIdAndType(ReadOnlySpan<byte> body)
    {
        string? id = null;
        string? type = null;

        try
        {
            var reader = new Utf8JsonReader(body, new JsonReaderOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });

            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return (null, null);

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var isId = reader.ValueTextEquals("id"u8);
                var isType = !isId && reader.ValueTextEquals("type"u8);

                if (!reader.Read())
                    break;

                if (reader.TokenType == JsonTokenType.String)
                {
                    if (isId)
                        id = reader.GetString();
                    else if (isType)
                        type = reader.GetString();
                }
                else
                {
                    reader.Skip();
                }

                if (id is not null && type is not null)
                    break;
            }
        }
        catch (JsonException)
        {
            // Not JSON, or truncated. The signature already checked out, so the body is what the
            // sender sent; it just has no id to deduplicate on.
        }

        return (id, type);
    }
}
