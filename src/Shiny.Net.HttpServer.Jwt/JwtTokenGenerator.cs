using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Shiny.Net.HttpServer.Jwt;

/// <summary>
/// Creates signed JWTs.
/// <para>
/// The JSON is written with <see cref="Utf8JsonWriter"/> rather than serialized from a model, so
/// there is no metadata to register, nothing to trim, and the claim names on the wire are exactly
/// the ones written here.
/// </para>
/// </summary>
public sealed class JwtTokenGenerator(JwtSigningKey signingKey, TimeProvider? timeProvider = null)
{
    readonly TimeProvider time = timeProvider ?? TimeProvider.System;

    /// <summary>The key tokens are signed with.</summary>
    public JwtSigningKey SigningKey { get; } = signingKey;

    /// <summary>Creates and signs a token.</summary>
    public string Create(JwtTokenDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (!this.SigningKey.CanSign)
            throw new InvalidOperationException(
                $"The configured {this.SigningKey.Algorithm} key is public-only and cannot sign tokens."
            );

        var issuedAt = descriptor.IssuedAt ?? this.time.GetUtcNow();
        var expires = descriptor.ExpiresAt ?? issuedAt + descriptor.Lifetime;

        var header = WriteJson(writer =>
        {
            writer.WriteString("alg", this.SigningKey.Algorithm.ToString());
            writer.WriteString("typ", "JWT");

            if (this.SigningKey.KeyId is { Length: > 0 } keyId)
                writer.WriteString("kid", keyId);
        });

        var payload = WriteJson(writer =>
        {
            if (descriptor.Issuer is { Length: > 0 } issuer)
                writer.WriteString(JwtClaimNames.Issuer, issuer);

            if (descriptor.Subject is { Length: > 0 } subject)
                writer.WriteString(JwtClaimNames.Subject, subject);

            // One audience is a string, several are an array — RFC 7519 allows both, and emitting a
            // single-element array trips up validators that only expect the string form.
            if (descriptor.Audiences.Count == 1)
            {
                writer.WriteString(JwtClaimNames.Audience, descriptor.Audiences[0]);
            }
            else if (descriptor.Audiences.Count > 1)
            {
                writer.WriteStartArray(JwtClaimNames.Audience);
                foreach (var audience in descriptor.Audiences)
                    writer.WriteStringValue(audience);
                writer.WriteEndArray();
            }

            writer.WriteNumber(JwtClaimNames.Expiration, expires.ToUnixTimeSeconds());
            writer.WriteNumber(JwtClaimNames.NotBefore, (descriptor.NotBefore ?? issuedAt).ToUnixTimeSeconds());
            writer.WriteNumber(JwtClaimNames.IssuedAt, issuedAt.ToUnixTimeSeconds());

            if (descriptor.TokenId is { Length: > 0 } tokenId)
                writer.WriteString(JwtClaimNames.TokenId, tokenId);

            WriteClaims(writer, descriptor);
        });

        var signingInput = $"{Base64Url.Encode(header)}.{Base64Url.Encode(payload)}";
        var signature = this.SigningKey.Sign(Encoding.ASCII.GetBytes(signingInput));

        return $"{signingInput}.{Base64Url.Encode(signature)}";
    }

    /// <summary>
    /// Groups claims by type so a repeated type becomes one JSON array rather than duplicate keys —
    /// which is legal JSON but means different things to different parsers.
    /// </summary>
    static void WriteClaims(Utf8JsonWriter writer, JwtTokenDescriptor descriptor)
    {
        if (descriptor.Claims.Count == 0)
            return;

        var reserved = new HashSet<string>(StringComparer.Ordinal)
        {
            JwtClaimNames.Issuer,
            JwtClaimNames.Subject,
            JwtClaimNames.Audience,
            JwtClaimNames.Expiration,
            JwtClaimNames.NotBefore,
            JwtClaimNames.IssuedAt,
            JwtClaimNames.TokenId
        };

        foreach (var group in descriptor.Claims.GroupBy(c => c.Type, StringComparer.Ordinal))
        {
            if (reserved.Contains(group.Key))
                throw new InvalidOperationException(
                    $"'{group.Key}' is a registered claim; set it through the matching property on " +
                    $"{nameof(JwtTokenDescriptor)} rather than as a custom claim."
                );

            var values = group.ToArray();
            if (values.Length == 1)
            {
                WriteClaimValue(writer, group.Key, values[0]);
                continue;
            }

            writer.WriteStartArray(group.Key);
            foreach (var claim in values)
                WriteClaimValueOnly(writer, claim);

            writer.WriteEndArray();
        }
    }

    static void WriteClaimValue(Utf8JsonWriter writer, string name, System.Security.Claims.Claim claim)
    {
        switch (claim.ValueType)
        {
            case System.Security.Claims.ClaimValueTypes.Integer:
            case System.Security.Claims.ClaimValueTypes.Integer32:
            case System.Security.Claims.ClaimValueTypes.Integer64:
                if (long.TryParse(claim.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                {
                    writer.WriteNumber(name, number);
                    return;
                }
                break;

            case System.Security.Claims.ClaimValueTypes.Boolean:
                if (bool.TryParse(claim.Value, out var flag))
                {
                    writer.WriteBoolean(name, flag);
                    return;
                }
                break;
        }

        writer.WriteString(name, claim.Value);
    }

    static void WriteClaimValueOnly(Utf8JsonWriter writer, System.Security.Claims.Claim claim)
        => writer.WriteStringValue(claim.Value);

    static byte[] WriteJson(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>(256);

        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            // Compact, and without escaping every non-ASCII character in a display name.
            Indented = false,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }
}
