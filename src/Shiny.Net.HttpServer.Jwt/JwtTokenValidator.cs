using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

namespace Shiny.Net.HttpServer.Jwt;

/// <summary>The outcome of validating a token.</summary>
public readonly struct JwtValidationResult
{
    JwtValidationResult(ClaimsPrincipal? principal, string? error)
    {
        this.Principal = principal;
        this.Error = error;
    }

    public ClaimsPrincipal? Principal { get; }

    /// <summary>Why the token was rejected. For logs and <c>WWW-Authenticate</c>, not for the body.</summary>
    public string? Error { get; }

    [MemberNotNullWhen(true, nameof(Principal))]
    public bool IsValid => this.Principal is not null;

    public static JwtValidationResult Success(ClaimsPrincipal principal) => new(principal, null);

    public static JwtValidationResult Fail(string error) => new(null, error);
}

/// <summary>
/// Validates JWTs.
/// <para>
/// The order of checks is the point. The signature is verified <em>before</em> anything in the
/// payload is believed, and the algorithm comes from the configured key rather than from the
/// token's own header — which is what closes the two classic JWT holes: <c>alg: none</c>, and
/// handing an RSA public key to an HMAC verifier as if it were a shared secret.
/// </para>
/// </summary>
public sealed class JwtTokenValidator(JwtValidationParameters parameters, TimeProvider? timeProvider = null)
{
    readonly TimeProvider time = timeProvider ?? TimeProvider.System;

    public JwtValidationParameters Parameters { get; } = parameters;

    public JwtValidationResult Validate(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return JwtValidationResult.Fail("The token is empty.");

        // Exactly three parts. Five means JWE, which this library does not implement and must not
        // pretend to — silently treating it as a JWS would skip decryption entirely.
        var first = token.IndexOf('.');
        if (first <= 0)
            return JwtValidationResult.Fail("The token is not a JWS.");

        var second = token.IndexOf('.', first + 1);
        if (second <= first + 1 || token.IndexOf('.', second + 1) >= 0)
            return JwtValidationResult.Fail("The token is not a JWS.");

        var headerPart = token.AsSpan(0, first);
        var payloadPart = token.AsSpan(first + 1, second - first - 1);
        var signaturePart = token.AsSpan(second + 1);

        if (!Base64Url.TryDecode(headerPart, out var headerBytes) ||
            !Base64Url.TryDecode(payloadPart, out var payloadBytes) ||
            !Base64Url.TryDecode(signaturePart, out var signature))
            return JwtValidationResult.Fail("The token is not valid base64url.");

        if (!TryReadHeader(headerBytes, out var algorithm, out var keyId))
            return JwtValidationResult.Fail("The token header is malformed.");

        if (this.Parameters.ValidateSignature)
        {
            var signingInput = Encoding.ASCII.GetBytes(token[..second]);
            if (!this.VerifySignature(signingInput, signature, algorithm, keyId, out var signatureError))
                return JwtValidationResult.Fail(signatureError);
        }

        return this.ReadPayload(payloadBytes);
    }

    /// <summary>
    /// Verifies against every key that matches the token's <c>kid</c> and whose own algorithm the
    /// header claims. A header naming an algorithm no configured key implements is rejected without
    /// any key being tried.
    /// </summary>
    bool VerifySignature(
        byte[] signingInput,
        byte[] signature,
        string? headerAlgorithm,
        string? keyId,
        out string error
    )
    {
        if (this.Parameters.SigningKeys.Count == 0)
        {
            error = "No signing key is configured.";
            return false;
        }

        if (headerAlgorithm is null)
        {
            error = "The token header does not name an algorithm.";
            return false;
        }

        // "none" is a legal JWS algorithm and an unconditional forgery. It never reaches a key.
        if (headerAlgorithm.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            error = "Unsigned tokens are not accepted.";
            return false;
        }

        if (!Enum.TryParse<JwtAlgorithm>(headerAlgorithm, ignoreCase: false, out var algorithm))
        {
            error = $"Algorithm '{headerAlgorithm}' is not supported.";
            return false;
        }

        var tried = false;

        foreach (var key in this.Parameters.SigningKeys)
        {
            // The key decides the algorithm. A token asking for HS256 can only ever be checked
            // against a key that is itself HS256.
            if (key.Algorithm != algorithm)
                continue;

            if (keyId is not null && key.KeyId is not null && !string.Equals(key.KeyId, keyId, StringComparison.Ordinal))
                continue;

            tried = true;

            try
            {
                if (key.Verify(signingInput, signature))
                {
                    error = string.Empty;
                    return true;
                }
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                // A malformed signature for this key type. Try the next key.
            }
        }

        error = tried ? "The token signature is invalid." : $"No configured key can verify {algorithm} tokens.";
        return false;
    }

    static bool TryReadHeader(byte[] header, out string? algorithm, out string? keyId)
    {
        algorithm = null;
        keyId = null;

        try
        {
            var reader = new Utf8JsonReader(header);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return false;

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var isAlg = reader.ValueTextEquals("alg"u8);
                var isKid = reader.ValueTextEquals("kid"u8);

                if (!reader.Read())
                    return false;

                if (isAlg && reader.TokenType == JsonTokenType.String)
                    algorithm = reader.GetString();
                else if (isKid && reader.TokenType == JsonTokenType.String)
                    keyId = reader.GetString();
                else
                    reader.Skip();
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    JwtValidationResult ReadPayload(byte[] payload)
    {
        var claims = new List<Claim>();
        string? issuer = null;
        var audiences = new List<string>();
        long? expiration = null;
        long? notBefore = null;

        try
        {
            var reader = new Utf8JsonReader(payload);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return JwtValidationResult.Fail("The token payload is malformed.");

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var name = reader.GetString()!;

                if (!reader.Read())
                    return JwtValidationResult.Fail("The token payload is malformed.");

                switch (name)
                {
                    case JwtClaimNames.Issuer:
                        issuer = reader.GetString();
                        break;

                    case JwtClaimNames.Audience:
                        if (reader.TokenType == JsonTokenType.StartArray)
                        {
                            while (reader.Read() && reader.TokenType == JsonTokenType.String)
                                audiences.Add(reader.GetString()!);
                        }
                        else if (reader.TokenType == JsonTokenType.String)
                        {
                            audiences.Add(reader.GetString()!);
                        }
                        break;

                    case JwtClaimNames.Expiration:
                        expiration = reader.TokenType == JsonTokenType.Number ? reader.GetInt64() : null;
                        break;

                    case JwtClaimNames.NotBefore:
                        notBefore = reader.TokenType == JsonTokenType.Number ? reader.GetInt64() : null;
                        break;

                    default:
                        ReadClaim(ref reader, name, claims);
                        break;
                }
            }
        }
        catch (JsonException)
        {
            return JwtValidationResult.Fail("The token payload is malformed.");
        }
        catch (InvalidOperationException)
        {
            return JwtValidationResult.Fail("The token payload is malformed.");
        }

        if (this.Parameters.ValidateIssuer)
        {
            if (this.Parameters.ValidIssuers.Count == 0)
                return JwtValidationResult.Fail("Issuer validation is on but no valid issuer is configured.");

            if (issuer is null || !this.Parameters.ValidIssuers.Contains(issuer, StringComparer.Ordinal))
                return JwtValidationResult.Fail("The token issuer is not accepted.");
        }

        if (this.Parameters.ValidateAudience)
        {
            if (this.Parameters.ValidAudiences.Count == 0)
                return JwtValidationResult.Fail("Audience validation is on but no valid audience is configured.");

            if (!audiences.Any(a => this.Parameters.ValidAudiences.Contains(a, StringComparer.Ordinal)))
                return JwtValidationResult.Fail("The token audience is not accepted.");
        }

        if (this.Parameters.ValidateLifetime)
        {
            var now = this.time.GetUtcNow();
            var skew = this.Parameters.ClockSkew;

            if (expiration is null && this.Parameters.RequireExpiration)
                return JwtValidationResult.Fail("The token does not expire.");

            if (expiration is { } exp && DateTimeOffset.FromUnixTimeSeconds(exp) + skew < now)
                return JwtValidationResult.Fail("The token has expired.");

            if (notBefore is { } nbf && DateTimeOffset.FromUnixTimeSeconds(nbf) - skew > now)
                return JwtValidationResult.Fail("The token is not valid yet.");
        }

        // Rebuild the registered claims onto the identity so handlers can read them like any other.
        if (issuer is not null)
            claims.Add(new Claim(JwtClaimNames.Issuer, issuer));

        foreach (var audience in audiences)
            claims.Add(new Claim(JwtClaimNames.Audience, audience));

        if (expiration is { } expiresAt)
            claims.Add(new Claim(JwtClaimNames.Expiration, expiresAt.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        var identity = new ClaimsIdentity(
            claims,
            this.Parameters.AuthenticationType,
            this.Parameters.NameClaimType,
            this.Parameters.RoleClaimType
        );

        return JwtValidationResult.Success(new ClaimsPrincipal(identity));
    }

    static void ReadClaim(ref Utf8JsonReader reader, string name, List<Claim> claims)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                claims.Add(new Claim(name, reader.GetString()!));
                return;

            case JsonTokenType.Number:
                claims.Add(new Claim(name, reader.GetRawValueAsString(), ClaimValueTypes.Integer64));
                return;

            case JsonTokenType.True:
            case JsonTokenType.False:
                claims.Add(new Claim(name, reader.TokenType == JsonTokenType.True ? "true" : "false", ClaimValueTypes.Boolean));
                return;

            case JsonTokenType.StartArray:
                // One claim per element, which is how roles arrive and what IsInRole expects.
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType == JsonTokenType.String)
                        claims.Add(new Claim(name, reader.GetString()!));
                    else if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                        reader.Skip();
                }
                return;

            default:
                reader.Skip();
                return;
        }
    }
}

static class Utf8JsonReaderExtensions
{
    /// <summary>Reads a JSON number as its literal text, so a claim keeps whatever precision it had.</summary>
    public static string GetRawValueAsString(this ref Utf8JsonReader reader)
        => reader.HasValueSequence
            ? Encoding.UTF8.GetString(System.Buffers.BuffersExtensions.ToArray(reader.ValueSequence))
            : Encoding.UTF8.GetString(reader.ValueSpan);
}
