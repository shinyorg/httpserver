namespace Shiny.Net.HttpServer.Security;

/// <summary>
/// The authorization rules for the whole app, configured once at registration.
/// <code>
/// builder.Services.AddAuthorization(o =>
/// {
///     o.AddPolicy("admin", p => p.RequireRole("admin"));
///     o.AddPolicy("owner", p => p.RequireAssertion(ctx =>
///         ctx.User.FindFirst("sub")?.Value == ctx.HttpContext.Request.RouteValues["userId"]));
/// });
/// </code>
/// </summary>
public sealed class AuthorizationOptions
{
    readonly Dictionary<string, AuthorizationPolicy> policies = new(StringComparer.Ordinal);

    /// <summary>
    /// Applied by a bare <c>[Authorize]</c>. Requires an authenticated caller unless replaced.
    /// </summary>
    public AuthorizationPolicy DefaultPolicy { get; set; }
        = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();

    /// <summary>
    /// Applied to endpoints with no <c>[Authorize]</c> at all. Null (the default) leaves them open.
    /// <para>
    /// Setting this flips the app to deny-by-default, which is the safer posture for anything
    /// reachable from a tunnel — a route added later is then protected until someone says otherwise
    /// with <c>[AllowAnonymous]</c>.
    /// </para>
    /// </summary>
    public AuthorizationPolicy? FallbackPolicy { get; set; }

    public IReadOnlyDictionary<string, AuthorizationPolicy> Policies => this.policies;

    public AuthorizationOptions AddPolicy(string name, AuthorizationPolicy policy)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(policy);

        this.policies[name] = policy;
        return this;
    }

    public AuthorizationOptions AddPolicy(string name, Action<AuthorizationPolicyBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new AuthorizationPolicyBuilder();
        configure(builder);

        return this.AddPolicy(name, builder.Build());
    }

    /// <summary>Replaces <see cref="DefaultPolicy"/>.</summary>
    public AuthorizationOptions SetDefaultPolicy(Action<AuthorizationPolicyBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new AuthorizationPolicyBuilder();
        configure(builder);
        this.DefaultPolicy = builder.Build();

        return this;
    }

    /// <summary>Sets <see cref="FallbackPolicy"/>, making the app deny-by-default.</summary>
    public AuthorizationOptions SetFallbackPolicy(Action<AuthorizationPolicyBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new AuthorizationPolicyBuilder();
        configure(builder);
        this.FallbackPolicy = builder.Build();

        return this;
    }

    /// <summary>
    /// Resolves the policy an endpoint's metadata asked for, throwing if it names one that was never
    /// registered — at the first request rather than silently letting everyone through.
    /// </summary>
    public AuthorizationPolicy GetPolicy(string name)
        => this.policies.TryGetValue(name, out var policy)
            ? policy
            : throw new InvalidOperationException(
                $"No authorization policy named '{name}' is registered. " +
                $"Add it with services.AddAuthorization(o => o.AddPolicy(\"{name}\", p => ...))."
            );
}
