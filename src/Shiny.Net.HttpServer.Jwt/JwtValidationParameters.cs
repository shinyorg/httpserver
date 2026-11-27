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
    // Snapshot lists, because these are read by every request and changed - a key rotated in, an
    // issuer added - while the server is serving. See SnapshotList for what a plain list did.
    readonly SnapshotList<JwtSigningKey> signingKeys = new();
    readonly SnapshotList<string> validIssuers = new();
    readonly SnapshotList<string> validAudiences = new();

    /// <summary>
    /// Keys the signature is checked against. More than one supports rotation: a token signed with
    /// the previous key keeps validating until it expires.
    /// <para>
    /// Safe to change while requests are being validated: each validation works from the keys as
    /// they were when it started.
    /// </para>
    /// </summary>
    public IList<JwtSigningKey> SigningKeys => this.signingKeys;

    /// <summary>Issuers to accept. Required unless <see cref="ValidateIssuer"/> is turned off. Safe to change while serving.</summary>
    public IList<string> ValidIssuers => this.validIssuers;

    /// <summary>Audiences to accept. Required unless <see cref="ValidateAudience"/> is turned off. Safe to change while serving.</summary>
    public IList<string> ValidAudiences => this.validAudiences;

    internal JwtSigningKey[] SigningKeySnapshot => this.signingKeys.Snapshot;

    internal string[] ValidIssuerSnapshot => this.validIssuers.Snapshot;

    internal string[] ValidAudienceSnapshot => this.validAudiences.Snapshot;

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
