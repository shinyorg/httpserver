using System.Security.Cryptography;
using System.Text;

namespace Shiny.Net.HttpServer.Security;

/// <summary>The pair of tokens a page needs: one in a cookie, one to send back.</summary>
/// <param name="CookieToken">Written to the antiforgery cookie. The browser returns it automatically.</param>
/// <param name="RequestToken">Put in a header or a hidden form field. Only same-origin script can read it.</param>
public readonly record struct AntiforgeryTokenSet(string CookieToken, string RequestToken);

/// <summary>How antiforgery tokens are issued, carried and checked.</summary>
public sealed class AntiforgeryOptions
{
    /// <summary>The cookie the browser round-trips. Prefixed <c>__Host-</c> style naming is not used because the server is often plain HTTP on a LAN.</summary>
    public string CookieName { get; set; } = "shiny.antiforgery";

    /// <summary>The header a client sends the request token in.</summary>
    public string HeaderName { get; set; } = "X-CSRF-TOKEN";

    /// <summary>The form field name, for a page that posts a form rather than calling fetch.</summary>
    public string FormFieldName { get; set; } = "__RequestVerificationToken";

    /// <summary>
    /// The key the request token is signed with.
    /// <para>
    /// Generated at startup when left null, which is usually right for an embedded server — but it
    /// means tokens issued before a restart stop validating, and it means two servers cannot check
    /// each other's. Set it explicitly for either.
    /// </para>
    /// </summary>
    public byte[]? Key { get; set; }

    /// <summary>Marks the cookie <c>Secure</c>. Off by default because a LAN server is usually cleartext.</summary>
    public bool SecureCookie { get; set; }

    /// <summary>How long a token stays valid. Zero means for as long as the cookie lasts.</summary>
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromHours(8);
}

/// <summary>Issues and validates antiforgery tokens.</summary>
public interface IAntiforgery
{
    /// <summary>
    /// Returns the token pair for this request, writing the cookie if it is not already there.
    /// Call it when rendering the page or handing a client its bootstrap data.
    /// </summary>
    AntiforgeryTokenSet GetTokens(HttpContext context);

    /// <summary>Checks the request token against the cookie. Reads the token from the configured header.</summary>
    bool Validate(HttpContext context);

    /// <summary>
    /// Checks a token the caller extracted itself — the hidden field of a form the handler has
    /// already read, which is the one place this middleware cannot reach without consuming the body.
    /// </summary>
    bool ValidateToken(HttpContext context, string? requestToken);
}

/// <summary>
/// Signed double-submit tokens.
/// <para>
/// The cookie carries a random value; the request token is that value plus an HMAC of it. An
/// attacker's page can cause the cookie to be sent — that is what CSRF is — but cannot read it, and
/// cannot forge the HMAC, so it cannot produce a matching request token.
/// </para>
/// <para>
/// Everything here is in-box crypto: <see cref="RandomNumberGenerator"/> and
/// <see cref="HMACSHA256"/>, no key derivation library and no data protection stack.
/// </para>
/// </summary>
public sealed class Antiforgery(AntiforgeryOptions options) : IAntiforgery
{
    const int TokenBytes = 32;

    readonly AntiforgeryOptions options = options ?? throw new ArgumentNullException(nameof(options));
    readonly byte[] key = options.Key ?? RandomNumberGenerator.GetBytes(64);

    public AntiforgeryTokenSet GetTokens(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var cookieToken = context.Request.Cookies[this.options.CookieName];

        if (cookieToken is not { Length: > 0 } || !TryDecode(cookieToken, out _))
        {
            cookieToken = Base64Url.Encode(RandomNumberGenerator.GetBytes(TokenBytes));

            context.Response.Cookies.Append(this.options.CookieName, cookieToken, new CookieOptions
            {
                // Deliberately readable by script: a page fetches the pair and echoes the request
                // token back in a header. The security comes from the signature, not from secrecy
                // of the cookie — and an HttpOnly cookie a single-page app cannot read is a cookie
                // it cannot send back.
                HttpOnly = false,
                Secure = this.options.SecureCookie,
                SameSite = SameSiteMode.Strict,
                Path = "/"
            });
        }

        return new AntiforgeryTokenSet(cookieToken, this.Sign(cookieToken));
    }

    public bool Validate(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return this.ValidateToken(context, context.Request.Headers.GetFirst(this.options.HeaderName));
    }

    public bool ValidateToken(HttpContext context, string? requestToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (requestToken is not { Length: > 0 })
            return false;

        if (context.Request.Cookies[this.options.CookieName] is not { Length: > 0 } cookieToken)
            return false;

        var expected = this.Sign(cookieToken);

        // Fixed-time comparison. A token check that leaks its progress through the string is a
        // token check an attacker can walk one byte at a time.
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(requestToken),
            Encoding.ASCII.GetBytes(expected)
        );
    }

    /// <summary>
    /// The request token: the cookie value, a timestamp, and an HMAC over both. The timestamp is
    /// inside the signature, so an expired token cannot be refreshed by editing it.
    /// </summary>
    string Sign(string cookieToken)
    {
        var stamp = this.options.Lifetime > TimeSpan.Zero
            ? (DateTimeOffset.UtcNow.ToUnixTimeSeconds() / (long)Math.Max(1, this.options.Lifetime.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "0";

        var payload = Encoding.UTF8.GetBytes(cookieToken + "." + stamp);
        var signature = HMACSHA256.HashData(this.key, payload);

        return stamp + "." + Base64Url.Encode(signature);
    }

    static bool TryDecode(string value, out byte[] bytes)
    {
        try
        {
            bytes = Base64Url.Decode(value);
            return bytes.Length == TokenBytes;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }

    /// <summary>Base64url, because a cookie value may not contain '+', '/' or '=' without quoting.</summary>
    static class Base64Url
    {
        public static string Encode(ReadOnlySpan<byte> bytes)
            => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        public static byte[] Decode(string value)
        {
            var padded = value.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - padded.Length % 4) % 4);

            return Convert.FromBase64String(padded);
        }
    }
}
