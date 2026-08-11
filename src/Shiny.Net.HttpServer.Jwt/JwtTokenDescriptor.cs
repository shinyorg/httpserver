using System.Security.Claims;

namespace Shiny.Net.HttpServer.Jwt;

/// <summary>
/// What goes into a token. Registered claims (<c>iss</c>, <c>sub</c>, <c>aud</c>, <c>exp</c>,
/// <c>nbf</c>, <c>iat</c>, <c>jti</c>) have properties; anything else goes in <see cref="Claims"/>.
/// </summary>
public sealed class JwtTokenDescriptor
{
    /// <summary>Who issued the token — <c>iss</c>.</summary>
    public string? Issuer { get; set; }

    /// <summary>Who the token is for — <c>aud</c>. Several audiences are written as an array.</summary>
    public IList<string> Audiences { get; } = [];

    /// <summary>Who the token is about — <c>sub</c>.</summary>
    public string? Subject { get; set; }

    /// <summary>How long the token stays valid. Ignored when <see cref="ExpiresAt"/> is set.</summary>
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Absolute expiry, overriding <see cref="Lifetime"/>.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>When the token becomes valid — <c>nbf</c>. Defaults to the issue time.</summary>
    public DateTimeOffset? NotBefore { get; set; }

    /// <summary>When the token was issued — <c>iat</c>. Defaults to now.</summary>
    public DateTimeOffset? IssuedAt { get; set; }

    /// <summary>Unique token id — <c>jti</c>. Set one if you intend to revoke individual tokens.</summary>
    public string? TokenId { get; set; }

    /// <summary>
    /// Everything else. Repeating a type produces a JSON array, which is how roles usually travel.
    /// </summary>
    public IList<Claim> Claims { get; } = [];

    /// <summary>Adds a claim.</summary>
    public JwtTokenDescriptor AddClaim(string type, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(type);
        ArgumentNullException.ThrowIfNull(value);

        this.Claims.Add(new Claim(type, value));
        return this;
    }

    /// <summary>Adds one <c>role</c> claim per role.</summary>
    public JwtTokenDescriptor AddRoles(params string[] roles)
    {
        ArgumentNullException.ThrowIfNull(roles);

        foreach (var role in roles)
            this.Claims.Add(new Claim(JwtClaimNames.Role, role));

        return this;
    }

    public JwtTokenDescriptor AddAudience(string audience)
    {
        ArgumentException.ThrowIfNullOrEmpty(audience);

        this.Audiences.Add(audience);
        return this;
    }
}

/// <summary>
/// The claim names this library reads and writes.
/// <para>
/// Short JWT names, not the long <c>schemas.xmlsoap.org/...</c> URIs .NET historically mapped them
/// to. A token written here and read by any other JWT library says the same thing, and
/// <c>ClaimsPrincipal.IsInRole</c> is wired to <see cref="Role"/> explicitly rather than relying on
/// a mapping table.
/// </para>
/// </summary>
public static class JwtClaimNames
{
    public const string Issuer = "iss";
    public const string Subject = "sub";
    public const string Audience = "aud";
    public const string Expiration = "exp";
    public const string NotBefore = "nbf";
    public const string IssuedAt = "iat";
    public const string TokenId = "jti";

    /// <summary>Roles. Matches the OIDC and Microsoft.IdentityModel short name.</summary>
    public const string Role = "role";

    /// <summary>Display name, used for <c>ClaimsIdentity.Name</c> when present.</summary>
    public const string Name = "name";
}
