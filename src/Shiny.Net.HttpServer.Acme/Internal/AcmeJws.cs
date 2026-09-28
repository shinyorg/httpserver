using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Shiny.Net.HttpServer.Acme.Internal;

/// <summary>
/// The account key: ECDSA P-256, signing as ES256 (RFC 7518 §3.4).
/// <para>
/// P-256 rather than RSA because every ACME CA accepts it, the signatures are 64 bytes instead of 256,
/// and .NET's <see cref="ECDsa.SignData(byte[], HashAlgorithmName)"/> already produces the IEEE P1363
/// <c>r‖s</c> form JWS requires — no DER unwrapping, which is where hand-rolled JWS usually goes wrong.
/// </para>
/// </summary>
sealed class AcmeAccountKey : IDisposable
{
    readonly ECDsa key;

    AcmeAccountKey(ECDsa key)
    {
        this.key = key;

        var parameters = key.ExportParameters(includePrivateParameters: false);
        var x = Base64Url.EncodeToString(parameters.Q.X);
        var y = Base64Url.EncodeToString(parameters.Q.Y);

        // RFC 7638: the thumbprint input is the required members only, in lexicographic order, with no
        // whitespace. Written by hand because the exact bytes are what get hashed.
        this.Jwk = $$"""{"crv":"P-256","kty":"EC","x":"{{x}}","y":"{{y}}"}""";
        this.Thumbprint = Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(this.Jwk)));
    }

    /// <summary>The public key as a JWK, in RFC 7638 canonical form.</summary>
    public string Jwk { get; }

    /// <summary>The JWK thumbprint — the second half of every key authorization.</summary>
    public string Thumbprint { get; }

    public static AcmeAccountKey Create() => new(ECDsa.Create(ECCurve.NamedCurves.nistP256));

    public static AcmeAccountKey Import(byte[] pkcs8)
    {
        var key = ECDsa.Create();
        try
        {
            key.ImportPkcs8PrivateKey(pkcs8, out _);
            if (key.KeySize != 256)
                throw new AcmeException("The stored ACME account key is not ECDSA P-256.");

            return new AcmeAccountKey(key);
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    public byte[] ExportPkcs8() => this.key.ExportPkcs8PrivateKey();

    public byte[] Sign(ReadOnlySpan<byte> data) => this.key.SignData(data, HashAlgorithmName.SHA256);

    /// <summary><c>token.thumbprint</c> (RFC 8555 §8.1).</summary>
    public string KeyAuthorization(string token) => token + "." + this.Thumbprint;

    public void Dispose() => this.key.Dispose();
}

/// <summary>
/// JWS in the flattened JSON serialization (RFC 7515 §7.2.2), which is the only form ACME accepts.
/// </summary>
static class AcmeJws
{
    /// <summary>
    /// Signs a request. <paramref name="kid"/> null embeds the public key (<c>jwk</c>) — only for
    /// newAccount, and for revocation by certificate key; every other request names the account.
    /// A null <paramref name="payload"/> is POST-as-GET: an empty payload string, not <c>{}</c>.
    /// </summary>
    public static byte[] Sign(AcmeAccountKey key, string url, string? nonce, string? kid, byte[]? payload)
    {
        using var header = new MemoryStream();
        using (var writer = new Utf8JsonWriter(header))
        {
            writer.WriteStartObject();
            writer.WriteString("alg", "ES256");

            if (kid is null)
            {
                writer.WritePropertyName("jwk");
                writer.WriteRawValue(key.Jwk, skipInputValidation: true);
            }
            else
                writer.WriteString("kid", kid);

            if (nonce is not null)
                writer.WriteString("nonce", nonce);

            writer.WriteString("url", url);
            writer.WriteEndObject();
        }

        var protectedPart = Base64Url.EncodeToString(header.ToArray());
        var payloadPart = payload is null ? "" : Base64Url.EncodeToString(payload);
        var signature = key.Sign(Encoding.ASCII.GetBytes(protectedPart + "." + payloadPart));

        return Envelope(protectedPart, payloadPart, Base64Url.EncodeToString(signature));
    }

    /// <summary>
    /// The External Account Binding (RFC 8555 §7.3.4): a JWS over the account's public key, MAC'd with
    /// the CA-issued HMAC key, so the CA can tie the new ACME account to one in its own system.
    /// </summary>
    public static byte[] ExternalAccountBinding(AcmeAccountKey key, AcmeExternalAccountBinding binding, string newAccountUrl)
    {
        if (string.IsNullOrWhiteSpace(binding.KeyId) || string.IsNullOrWhiteSpace(binding.HmacKey))
            throw new AcmeException("External Account Binding needs both a key id and an HMAC key.");

        byte[] hmacKey;
        try
        {
            hmacKey = Base64Url.DecodeFromChars(binding.HmacKey.Trim().TrimEnd('='));
        }
        catch (FormatException ex)
        {
            throw new AcmeException("The External Account Binding HMAC key is not valid base64url.", ex);
        }

        using var header = new MemoryStream();
        using (var writer = new Utf8JsonWriter(header))
        {
            writer.WriteStartObject();
            writer.WriteString("alg", "HS256");
            writer.WriteString("kid", binding.KeyId);
            writer.WriteString("url", newAccountUrl);
            writer.WriteEndObject();
        }

        var protectedPart = Base64Url.EncodeToString(header.ToArray());
        var payloadPart = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(key.Jwk));
        var signature = HMACSHA256.HashData(hmacKey, Encoding.ASCII.GetBytes(protectedPart + "." + payloadPart));

        return Envelope(protectedPart, payloadPart, Base64Url.EncodeToString(signature));
    }

    static byte[] Envelope(string protectedPart, string payloadPart, string signature)
    {
        using var body = new MemoryStream();
        using (var writer = new Utf8JsonWriter(body))
        {
            writer.WriteStartObject();
            writer.WriteString("protected", protectedPart);
            writer.WriteString("payload", payloadPart);
            writer.WriteString("signature", signature);
            writer.WriteEndObject();
        }

        return body.ToArray();
    }

    /// <summary>Builds a JSON payload with a <see cref="Utf8JsonWriter"/> — no serializer, nothing to trim.</summary>
    public static byte[] Json(Action<Utf8JsonWriter> write)
    {
        using var body = new MemoryStream();
        using (var writer = new Utf8JsonWriter(body))
            write(writer);

        return body.ToArray();
    }
}
