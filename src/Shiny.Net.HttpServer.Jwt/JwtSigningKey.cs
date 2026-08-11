using System.Security.Cryptography;
using System.Text;

namespace Shiny.Net.HttpServer.Jwt;

/// <summary>The JWS algorithms this library supports.</summary>
public enum JwtAlgorithm
{
    HS256,
    HS384,
    HS512,
    RS256,
    RS384,
    RS512,
    ES256,
    ES384,
    ES512
}

/// <summary>
/// A key that can sign, verify, or both.
/// <para>
/// Deliberately a closed set backed by <c>System.Security.Cryptography</c> rather than a pluggable
/// <c>SecurityKey</c> hierarchy. Everything here — HMAC, RSA-PKCS1, ECDSA — is in the box, so the
/// whole JWT story adds no package dependency and nothing for the trimmer to chase. It also means
/// <c>alg</c> is chosen by the key, not read from the token, which is the single most important
/// thing to get right about JWT validation.
/// </para>
/// </summary>
public abstract class JwtSigningKey : IDisposable
{
    /// <summary>The algorithm this key implements, written into the token header.</summary>
    public abstract JwtAlgorithm Algorithm { get; }

    /// <summary>Optional <c>kid</c>, so a validator holding several keys can pick the right one.</summary>
    public string? KeyId { get; set; }

    /// <summary>True when this key can produce signatures, not just check them.</summary>
    public abstract bool CanSign { get; }

    public abstract byte[] Sign(ReadOnlySpan<byte> data);

    public abstract bool Verify(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature);

    /// <summary>
    /// An HMAC key from a shared secret. The secret must be at least as long as the hash it feeds —
    /// 32 bytes for HS256 — because a shorter one silently weakens the signature to its own length.
    /// </summary>
    public static JwtSigningKey FromSecret(byte[] secret, JwtAlgorithm algorithm = JwtAlgorithm.HS256)
    {
        ArgumentNullException.ThrowIfNull(secret);

        var minimum = algorithm switch
        {
            JwtAlgorithm.HS256 => 32,
            JwtAlgorithm.HS384 => 48,
            JwtAlgorithm.HS512 => 64,
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm), algorithm, "Not an HMAC algorithm.")
        };

        if (secret.Length < minimum)
            throw new ArgumentException(
                $"{algorithm} needs a secret of at least {minimum} bytes; this one is {secret.Length}.",
                nameof(secret)
            );

        return new HmacSigningKey(secret, algorithm);
    }

    /// <summary>UTF-8 convenience over <see cref="FromSecret(byte[], JwtAlgorithm)"/>.</summary>
    public static JwtSigningKey FromSecret(string secret, JwtAlgorithm algorithm = JwtAlgorithm.HS256)
        => FromSecret(Encoding.UTF8.GetBytes(secret), algorithm);

    /// <summary>Generates a fresh random HMAC secret of the right length for the algorithm.</summary>
    public static JwtSigningKey CreateSecret(JwtAlgorithm algorithm = JwtAlgorithm.HS256)
    {
        var length = algorithm switch
        {
            JwtAlgorithm.HS256 => 32,
            JwtAlgorithm.HS384 => 48,
            JwtAlgorithm.HS512 => 64,
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm), algorithm, "Not an HMAC algorithm.")
        };

        return new HmacSigningKey(RandomNumberGenerator.GetBytes(length), algorithm);
    }

    /// <summary>An RSA key. A public-only key verifies; a private key also signs.</summary>
    public static JwtSigningKey FromRsa(RSA rsa, JwtAlgorithm algorithm = JwtAlgorithm.RS256, bool ownsKey = false)
    {
        ArgumentNullException.ThrowIfNull(rsa);

        if (algorithm is not (JwtAlgorithm.RS256 or JwtAlgorithm.RS384 or JwtAlgorithm.RS512))
            throw new ArgumentOutOfRangeException(nameof(algorithm), algorithm, "Not an RSA algorithm.");

        return new RsaSigningKey(rsa, algorithm, ownsKey);
    }

    /// <summary>An ECDSA key. A public-only key verifies; a private key also signs.</summary>
    public static JwtSigningKey FromEcdsa(ECDsa ecdsa, JwtAlgorithm algorithm = JwtAlgorithm.ES256, bool ownsKey = false)
    {
        ArgumentNullException.ThrowIfNull(ecdsa);

        if (algorithm is not (JwtAlgorithm.ES256 or JwtAlgorithm.ES384 or JwtAlgorithm.ES512))
            throw new ArgumentOutOfRangeException(nameof(algorithm), algorithm, "Not an ECDSA algorithm.");

        return new EcdsaSigningKey(ecdsa, algorithm, ownsKey);
    }

    internal static HashAlgorithmName HashFor(JwtAlgorithm algorithm) => algorithm switch
    {
        JwtAlgorithm.HS256 or JwtAlgorithm.RS256 or JwtAlgorithm.ES256 => HashAlgorithmName.SHA256,
        JwtAlgorithm.HS384 or JwtAlgorithm.RS384 or JwtAlgorithm.ES384 => HashAlgorithmName.SHA384,
        _ => HashAlgorithmName.SHA512
    };

    public virtual void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}

sealed class HmacSigningKey(byte[] secret, JwtAlgorithm algorithm) : JwtSigningKey
{
    public override JwtAlgorithm Algorithm => algorithm;

    public override bool CanSign => true;

    /// <summary>The raw secret, for callers that need to persist or share it.</summary>
    public byte[] Secret { get; } = secret;

    public override byte[] Sign(ReadOnlySpan<byte> data) => algorithm switch
    {
        JwtAlgorithm.HS256 => HMACSHA256.HashData(this.Secret, data),
        JwtAlgorithm.HS384 => HMACSHA384.HashData(this.Secret, data),
        _ => HMACSHA512.HashData(this.Secret, data)
    };

    public override bool Verify(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
        // Fixed-time, because comparing signatures byte by byte with an early exit leaks how much
        // of a forged signature was correct — which is enough to forge the rest.
        => CryptographicOperations.FixedTimeEquals(this.Sign(data), signature);
}

sealed class RsaSigningKey(RSA rsa, JwtAlgorithm algorithm, bool ownsKey) : JwtSigningKey
{
    public override JwtAlgorithm Algorithm => algorithm;

    public override bool CanSign => TryGetPrivate(rsa);

    public override byte[] Sign(ReadOnlySpan<byte> data)
        => rsa.SignData(data.ToArray(), HashFor(algorithm), RSASignaturePadding.Pkcs1);

    public override bool Verify(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
        => rsa.VerifyData(data, signature, HashFor(algorithm), RSASignaturePadding.Pkcs1);

    static bool TryGetPrivate(RSA key)
    {
        try
        {
            key.ExportParameters(includePrivateParameters: true);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    public override void Dispose()
    {
        if (ownsKey)
            rsa.Dispose();

        base.Dispose();
    }
}

sealed class EcdsaSigningKey(ECDsa ecdsa, JwtAlgorithm algorithm, bool ownsKey) : JwtSigningKey
{
    public override JwtAlgorithm Algorithm => algorithm;

    public override bool CanSign => TryGetPrivate(ecdsa);

    // IeeeP1363FixedFieldConcatenation, not DER: JWS defines the ECDSA signature as the raw r||s
    // pair. A DER-encoded signature here is the classic reason an ES256 token is rejected by every
    // other library.
    public override byte[] Sign(ReadOnlySpan<byte> data)
        => ecdsa.SignData(data.ToArray(), HashFor(algorithm), DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    public override bool Verify(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
        => ecdsa.VerifyData(data, signature, HashFor(algorithm), DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    static bool TryGetPrivate(ECDsa key)
    {
        try
        {
            key.ExportParameters(includePrivateParameters: true);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    public override void Dispose()
    {
        if (ownsKey)
            ecdsa.Dispose();

        base.Dispose();
    }
}
