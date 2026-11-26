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
    /// <summary>
    /// The cookie the browser round-trips. When the cookie is <c>Secure</c> it is issued and read as
    /// <c>__Host-</c> + this name (see <see cref="UseHostPrefix"/>); on plain HTTP — a LAN server — the name
    /// is used as is, since browsers refuse a <c>__Host-</c> cookie without <c>Secure</c>.
    /// </summary>
    public string CookieName { get; set; } = "shiny.antiforgery";

    /// <summary>
    /// Prefixes the cookie name with <c>__Host-</c> whenever the cookie is <c>Secure</c>. On by default: the
    /// prefix makes the browser refuse the cookie from a sibling subdomain or a plain-HTTP page, which is
    /// how an attacker on a neighbouring host would otherwise plant a cookie of their own and pair it with
    /// a token they hold. Script that wants the request token should use the one
    /// <see cref="IAntiforgery.GetTokens"/> returns rather than reading the cookie by name.
    /// </summary>
    public bool UseHostPrefix { get; set; } = true;

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

    /// <summary>
    /// Always marks the cookie <c>Secure</c>, whatever <see cref="SecurePolicy"/> says. Off by default; the
    /// policy already makes it secure on HTTPS.
    /// </summary>
    public bool SecureCookie { get; set; }

    /// <summary>
    /// When the cookie is <c>Secure</c>. <see cref="CookieSecurePolicy.SameAsRequest"/> by default — secure
    /// on HTTPS, plain on a cleartext LAN server — so an HTTPS deployment no longer issues a cookie a
    /// network attacker could read or overwrite over HTTP.
    /// </summary>
    public CookieSecurePolicy SecurePolicy { get; set; } = CookieSecurePolicy.SameAsRequest;

    /// <summary>
    /// How long a request token stays valid after it was issued. Zero means for as long as the cookie
    /// lasts. The issue time travels inside the signed token, so a token is good for the full lifetime
    /// from the moment it was handed out.
    /// </summary>
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromHours(8);

    /// <summary>Whether the cookie for this request is <c>Secure</c>.</summary>
    internal bool IsSecure(HttpContext context) => this.SecureCookie || this.SecurePolicy switch
    {
        CookieSecurePolicy.Always => true,
        CookieSecurePolicy.Never => false,
        _ => context.Request.IsHttps
    };

    /// <summary>The cookie name for this request — <c>__Host-</c> prefixed when it is secure.</summary>
    internal string CookieNameFor(HttpContext context)
        => this.UseHostPrefix && this.IsSecure(context) && !this.CookieName.StartsWith("__Host-", StringComparison.Ordinal)
            ? "__Host-" + this.CookieName
            : this.CookieName;
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
/// The request token also carries the moment it was issued and is bound to the signed-in user, both
/// inside the signature: it expires <see cref="AntiforgeryOptions.Lifetime"/> after it was handed out, and
/// a token issued to one user (or to an anonymous visitor) does not validate for another — so a token
/// obtained while signed out cannot be replayed against a victim's authenticated session. Fetch a fresh
/// token after signing in.
/// </para>
/// <para>
/// Everything here is in-box crypto: <see cref="RandomNumberGenerator"/> and
/// <see cref="HMACSHA256"/>, no key derivation library and no data protection stack.
/// </para>
/// </summary>
public sealed class Antiforgery(AntiforgeryOptions options) : IAntiforgery
{
    const int TokenBytes = 32;

    // Tokens stamped by a clock a little ahead of this one are still accepted.
    static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

    readonly AntiforgeryOptions options = options ?? throw new ArgumentNullException(nameof(options));
    readonly byte[] key = options.Key ?? RandomNumberGenerator.GetBytes(64);

    public AntiforgeryTokenSet GetTokens(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var cookieName = this.options.CookieNameFor(context);
        var cookieToken = context.Request.Cookies[cookieName];

        if (cookieToken is not { Length: > 0 } || !TryDecode(cookieToken, out _))
        {
            cookieToken = Base64Url.Encode(RandomNumberGenerator.GetBytes(TokenBytes));

            context.Response.Cookies.Append(cookieName, cookieToken, new CookieOptions
            {
                // Deliberately readable by script: a page fetches the pair and echoes the request
                // token back in a header. The security comes from the signature, not from secrecy
                // of the cookie — and an HttpOnly cookie a single-page app cannot read is a cookie
                // it cannot send back.
                HttpOnly = false,
                Secure = this.options.IsSecure(context),
                SameSite = SameSiteMode.Strict,
                Path = "/"
            });
        }

        return new AntiforgeryTokenSet(cookieToken, this.Sign(cookieToken, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), UserBinding(context)));
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

        if (context.Request.Cookies[this.options.CookieNameFor(context)] is not { Length: > 0 } cookieToken)
            return false;

        // issued.signature — the issue time is read from the token, then checked by recomputing the
        // signature over it, so it cannot be edited to extend a token's life.
        var dot = requestToken.IndexOf('.');
        if (dot <= 0 || !long.TryParse(requestToken.AsSpan(0, dot), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var issued))
            return false;

        var expected = this.Sign(cookieToken, issued, UserBinding(context));

        // Fixed-time comparison. A token check that leaks its progress through the string is a
        // token check an attacker can walk one byte at a time.
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(requestToken), Encoding.ASCII.GetBytes(expected)))
            return false;

        if (this.options.Lifetime <= TimeSpan.Zero)
            return true;

        var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(Math.Min(issued, DateTimeOffset.MaxValue.ToUnixTimeSeconds()));
        return age <= this.options.Lifetime && age >= -ClockSkew;
    }

    /// <summary>
    /// The request token: the issue time, and an HMAC over the cookie value, that time and the user it was
    /// issued to. Signing the time rather than a fixed window means a token lives its whole lifetime from
    /// the moment it is handed out — a bucketed stamp died at the next bucket boundary, which with a short
    /// lifetime could be a second after it was issued.
    /// </summary>
    string Sign(string cookieToken, long issued, string user)
    {
        var stamp = issued.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var payload = Encoding.UTF8.GetBytes(cookieToken + "." + stamp + "." + user);
        var signature = HMACSHA256.HashData(this.key, payload);

        return stamp + "." + Base64Url.Encode(signature);
    }

    /// <summary>Who the token is for: the signed-in user's stable id, or nobody for an anonymous visitor.</summary>
    static string UserBinding(HttpContext context)
    {
        var user = context.User;

        if (user.Identity?.IsAuthenticated != true)
            return string.Empty;

        return user.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? user.FindFirst("sub")?.Value
            ?? user.Identity.Name
            ?? string.Empty;
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
