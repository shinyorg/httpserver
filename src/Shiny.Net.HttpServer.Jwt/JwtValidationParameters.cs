namespace Shiny.Net.HttpServer.Jwt;

/// <summary>
/// What a token has to satisfy to be accepted.
/// <para>
/// The defaults are the strict ones. Every "validate X" flag here defaults to true, and turning one
/// off is a decision someone has to type — the opposite arrangement is how tokens end up being
/// accepted from the wrong issuer for a year before anyone notices.
/// </para>
/// </summary>
public sealed class JwtValidationParameters
{
    /// <summary>
    /// Keys the signature is checked against. More than one supports rotation: a token signed with
    /// the previous key keeps validating until it expires.
    /// </summary>
    public IList<JwtSigningKey> SigningKeys { get; } = [];

    /// <summary>Issuers to accept. Required unless <see cref="ValidateIssuer"/> is turned off.</summary>
    public IList<string> ValidIssuers { get; } = [];

    /// <summary>Audiences to accept. Required unless <see cref="ValidateAudience"/> is turned off.</summary>
    public IList<string> ValidAudiences { get; } = [];

    public bool ValidateSignature { get; set; } = true;

    public bool ValidateIssuer { get; set; } = true;

    public bool ValidateAudience { get; set; } = true;

    public bool ValidateLifetime { get; set; } = true;

    /// <summary>Requires <c>exp</c> to be present. A token that never expires is rarely intended.</summary>
    public bool RequireExpiration { get; set; } = true;

    /// <summary>
    /// Tolerance for clock differences between the issuer and this server. Two minutes is the
    /// customary allowance; larger values extend the life of a revoked token.
    /// </summary>
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>The identity's authentication type, which is what makes <c>IsAuthenticated</c> true.</summary>
    public string AuthenticationType { get; set; } = "Bearer";

    /// <summary>Claim type used for <c>ClaimsIdentity.Name</c>.</summary>
    public string NameClaimType { get; set; } = JwtClaimNames.Name;

    /// <summary>Claim type used for <c>IsInRole</c>.</summary>
    public string RoleClaimType { get; set; } = JwtClaimNames.Role;

    /// <summary>Adds a key.</summary>
    public JwtValidationParameters AddSigningKey(JwtSigningKey key)
    {
        ArgumentNullException.ThrowIfNull(key);

        this.SigningKeys.Add(key);
        return this;
    }
}
