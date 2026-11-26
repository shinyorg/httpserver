using System.Buffers.Binary;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace Shiny.Net.HttpServer.Security;

/// <summary>A signed-in caller and when that expires.</summary>
public sealed class AuthenticationTicket(ClaimsPrincipal principal, DateTimeOffset issued, DateTimeOffset expires, bool isPersistent)
{
    /// <summary>A persistent ticket — see <see cref="IsPersistent"/>.</summary>
    public AuthenticationTicket(ClaimsPrincipal principal, DateTimeOffset issued, DateTimeOffset expires)
        : this(principal, issued, expires, isPersistent: true)
    {
    }

    public ClaimsPrincipal Principal { get; } = principal ?? throw new ArgumentNullException(nameof(principal));

    public DateTimeOffset IssuedUtc { get; } = issued;

    public DateTimeOffset ExpiresUtc { get; } = expires;

    /// <summary>
    /// True when the cookie carrying the ticket outlives the browser session (it has an <c>Expires</c>).
    /// Recorded in the ticket so a sliding renewal reissues the cookie the way it was first issued — a
    /// "don't remember me" sign-in must not quietly become a 14-day cookie on the first renewal.
    /// </summary>
    public bool IsPersistent { get; } = isPersistent;

    public bool HasExpired(DateTimeOffset now) => now >= this.ExpiresUtc;
}

/// <summary>
/// Serializes a ticket to bytes without reflection.
/// <para>
/// Hand-rolled rather than JSON because a claim is a small, fixed shape and the format is entirely
/// internal — nothing outside this server ever reads it. A length-prefixed binary format also makes
/// the parser total: every field is bounded by the buffer, so a truncated or tampered payload fails
/// as a parse error rather than an exception from somewhere deeper.
/// </para>
/// </summary>
static class TicketSerializer
{
    // Bumped if the layout ever changes, so an unknown layout is rejected rather than misread.
    // 2 added the flags byte (persistence); 1 is still read, as a persistent ticket — which is how
    // every renewal treated it before the flag existed.
    const byte FormatVersion = 2;
    const byte LegacyFormatVersion = 1;
    const byte PersistentFlag = 0x01;

    public static byte[] Serialize(AuthenticationTicket ticket)
    {
        using var buffer = new MemoryStream(256);
        using var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true);

        writer.Write(FormatVersion);
        writer.Write(ticket.IsPersistent ? PersistentFlag : (byte)0);
        writer.Write(ticket.IssuedUtc.ToUnixTimeSeconds());
        writer.Write(ticket.ExpiresUtc.ToUnixTimeSeconds());

        var identities = ticket.Principal.Identities.Where(i => i.IsAuthenticated).ToArray();
        writer.Write(identities.Length);

        foreach (var identity in identities)
        {
            writer.Write(identity.AuthenticationType ?? string.Empty);
            writer.Write(identity.NameClaimType);
            writer.Write(identity.RoleClaimType);

            var claims = identity.Claims.ToArray();
            writer.Write(claims.Length);

            foreach (var claim in claims)
            {
                writer.Write(claim.Type);
                writer.Write(claim.Value);
                writer.Write(claim.ValueType);
                writer.Write(claim.Issuer);
            }
        }

        return buffer.ToArray();
    }

    public static AuthenticationTicket? Deserialize(ReadOnlySpan<byte> payload)
    {
        try
        {
            using var buffer = new MemoryStream(payload.ToArray(), writable: false);
            using var reader = new BinaryReader(buffer, Encoding.UTF8, leaveOpen: true);

            var version = reader.ReadByte();
            if (version is not (FormatVersion or LegacyFormatVersion))
                return null;

            var persistent = version == LegacyFormatVersion || (reader.ReadByte() & PersistentFlag) != 0;
            var issued = DateTimeOffset.FromUnixTimeSeconds(reader.ReadInt64());
            var expires = DateTimeOffset.FromUnixTimeSeconds(reader.ReadInt64());

            var identityCount = reader.ReadInt32();
            if (identityCount is < 0 or > 16)
                return null;

            var principal = new ClaimsPrincipal();

            for (var i = 0; i < identityCount; i++)
            {
                var authenticationType = reader.ReadString();
                var nameClaimType = reader.ReadString();
                var roleClaimType = reader.ReadString();

                var claimCount = reader.ReadInt32();
                if (claimCount is < 0 or > 1024)
                    return null;

                var claims = new List<Claim>(claimCount);

                for (var c = 0; c < claimCount; c++)
                {
                    var type = reader.ReadString();
                    var value = reader.ReadString();
                    var valueType = reader.ReadString();
                    var issuer = reader.ReadString();

                    claims.Add(new Claim(type, value, valueType, issuer));
                }

                principal.AddIdentity(new ClaimsIdentity(
                    claims,
                    authenticationType.Length == 0 ? null : authenticationType,
                    nameClaimType,
                    roleClaimType
                ));
            }

            return new AuthenticationTicket(principal, issued, expires, persistent);
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or ArgumentException or FormatException)
        {
            // A payload that does not parse is a payload that was not ours. Same outcome as a bad
            // signature: no principal, no exception escaping into the request.
            return null;
        }
    }
}

/// <summary>
/// Encrypts and authenticates a ticket so a client can hold it without being able to read or forge
/// it.
/// <para>
/// AES-GCM: the ticket is confidential (a cookie is stored on a machine you do not control and
/// carries claims) and tamper-evident in one pass. Signing alone would leave the claims readable,
/// and encrypt-then-MAC by hand is a well-known way to get this wrong.
/// </para>
/// <para>
/// Keys are identified by a short id, so several can be trusted at once: a rotation adds the new key
/// as primary and keeps the old one for decryption until the last cookie issued under it expires.
/// </para>
/// <para>
/// A ticket is bound to a <b>purpose</b> — the authentication cookie's and the session cookie's are
/// different ones — which is mixed into the authenticated data, so a ticket protected for one purpose
/// does not decrypt for another. Without it, one protector shared by sessions and cookie authentication
/// would accept a session cookie, renamed, as a sign-in.
/// </para>
/// </summary>
public sealed class TicketProtector
{
    // 2 binds the purpose into the AES-GCM associated data. 1 (key id only) is still read, so cookies
    // issued before the upgrade keep working until they expire; nothing is issued as 1 any more.
    const byte FormatVersion = 2;
    const byte LegacyFormatVersion = 1;
    const int KeyIdLength = 4;
    const int NonceLength = 12;   // AES-GCM's required nonce size
    const int TagLength = 16;

    readonly (byte[] Id, byte[] Key) primary;
    readonly List<(byte[] Id, byte[] Key)> all = [];

    /// <param name="primaryKey">32 bytes. Anything else is derived to 32 by SHA-256.</param>
    /// <param name="secondaryKeys">Older keys, still accepted for decryption during a rotation.</param>
    public TicketProtector(ReadOnlySpan<byte> primaryKey, params IEnumerable<byte[]> secondaryKeys)
    {
        this.primary = Derive(primaryKey);
        this.all.Add(this.primary);

        foreach (var key in secondaryKeys)
            this.all.Add(Derive(key));
    }

    /// <summary>
    /// Derives a protector from a passphrase.
    /// <para>
    /// Convenience, not a substitute for a random key: the strength of everything here is the
    /// strength of that string. Use <see cref="CreateKey"/> and store the bytes for anything real.
    /// </para>
    /// </summary>
    public static TicketProtector FromSecret(string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);

        return new TicketProtector(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
    }

    /// <summary>A fresh 32-byte key.</summary>
    public static byte[] CreateKey() => RandomNumberGenerator.GetBytes(32);

    /// <summary>Protects a ticket with no particular purpose. Prefer <see cref="Protect(AuthenticationTicket, string)"/>.</summary>
    public string Protect(AuthenticationTicket ticket) => this.Protect(ticket, string.Empty);

    /// <summary>
    /// Protects a ticket for one purpose — it will only <see cref="Unprotect(string, string)"/> under the
    /// same purpose string.
    /// </summary>
    public string Protect(AuthenticationTicket ticket, string purpose)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentNullException.ThrowIfNull(purpose);

        var plaintext = TicketSerializer.Serialize(ticket);
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagLength];

        using (var aes = new AesGcm(this.primary.Key, TagLength))
            aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData(this.primary.Id, purpose));

        // version | key id | nonce | tag | ciphertext. The key id travels in the clear because the
        // decryptor has to know which key to try, and it identifies nothing on its own.
        var output = new byte[1 + KeyIdLength + NonceLength + TagLength + ciphertext.Length];
        var span = output.AsSpan();

        span[0] = FormatVersion;
        this.primary.Id.CopyTo(span[1..]);
        nonce.CopyTo(span[(1 + KeyIdLength)..]);
        tag.CopyTo(span[(1 + KeyIdLength + NonceLength)..]);
        ciphertext.CopyTo(span[(1 + KeyIdLength + NonceLength + TagLength)..]);

        CryptographicOperations.ZeroMemory(plaintext);

        return Base64Url.Encode(output);
    }

    /// <summary>
    /// Returns null for anything that is not a valid ticket under a key we hold, protected with no
    /// particular purpose.
    /// </summary>
    public AuthenticationTicket? Unprotect(string protectedTicket) => this.Unprotect(protectedTicket, string.Empty);

    /// <summary>
    /// Returns null for anything that is not a valid ticket under a key we hold and protected for
    /// <paramref name="purpose"/>. A ticket from before purposes existed is still accepted, so an upgrade
    /// does not sign everyone out — such tickets age out with their own expiry.
    /// </summary>
    public AuthenticationTicket? Unprotect(string protectedTicket, string purpose)
    {
        ArgumentNullException.ThrowIfNull(purpose);

        if (string.IsNullOrEmpty(protectedTicket))
            return null;

        if (!Base64Url.TryDecode(protectedTicket, out var payload))
            return null;

        if (payload.Length < 1 + KeyIdLength + NonceLength + TagLength || payload[0] is not (FormatVersion or LegacyFormatVersion))
            return null;

        var legacy = payload[0] == LegacyFormatVersion;

        var keyId = payload.AsSpan(1, KeyIdLength);
        var nonce = payload.AsSpan(1 + KeyIdLength, NonceLength);
        var tag = payload.AsSpan(1 + KeyIdLength + NonceLength, TagLength);
        var ciphertext = payload.AsSpan(1 + KeyIdLength + NonceLength + TagLength);

        foreach (var (id, key) in this.all)
        {
            if (!CryptographicOperations.FixedTimeEquals(id, keyId))
                continue;

            var plaintext = new byte[ciphertext.Length];

            try
            {
                using var aes = new AesGcm(key, TagLength);
                aes.Decrypt(nonce, ciphertext, tag, plaintext, legacy ? id : AssociatedData(id, purpose));

                return TicketSerializer.Deserialize(plaintext);
            }
            catch (CryptographicException)
            {
                // Authentication failed: the payload was altered, or it was not produced by this
                // key at all. Either way there is nothing to report to the caller beyond "no".
                return null;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }

        return null;
    }

    /// <summary>
    /// The key id followed by the purpose: authenticated but not encrypted, so a ticket only opens
    /// under the key and the purpose it was sealed with. The purpose never travels in the envelope.
    /// </summary>
    static byte[] AssociatedData(byte[] keyId, string purpose)
    {
        var data = new byte[keyId.Length + Encoding.UTF8.GetByteCount(purpose)];
        keyId.CopyTo(data, 0);
        Encoding.UTF8.GetBytes(purpose, 0, purpose.Length, data, keyId.Length);

        return data;
    }

    /// <summary>
    /// Normalizes a key to 32 bytes and derives a short id for it.
    /// <para>
    /// The id is a truncated hash of the key. It has to be stable across restarts — a random id
    /// would invalidate every cookie on every boot — and it must not weaken the key, which is why
    /// it is a hash rather than a prefix of the key itself.
    /// </para>
    /// </summary>
    static (byte[] Id, byte[] Key) Derive(ReadOnlySpan<byte> material)
    {
        if (material.Length == 0)
            throw new ArgumentException("The key is empty.", nameof(material));

        var key = material.Length == 32 ? material.ToArray() : SHA256.HashData(material);
        var id = SHA256.HashData(key).AsSpan(0, KeyIdLength).ToArray();

        return (id, key);
    }
}

/// <summary>Base64url without padding, as used in cookies and tokens.</summary>
static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes)
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');

    public static bool TryDecode(string value, out byte[] bytes)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');

        // Base64 wants a multiple of four; the padding is dropped on the way out and restored here.
        padded = (padded.Length % 4) switch
        {
            2 => padded + "==",
            3 => padded + "=",
            0 => padded,
            _ => null!
        };

        if (padded is null)
        {
            bytes = [];
            return false;
        }

        try
        {
            bytes = Convert.FromBase64String(padded);
            return true;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }
}
