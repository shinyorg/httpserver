using System.Security.Claims;

namespace Shiny.Net.HttpServer.Security;

/// <summary>What the caller has to satisfy. Everything a policy contains must pass.</summary>
public interface IAuthorizationRequirement
{
    /// <summary>How this requirement reads in a denial reason, e.g. <c>role 'admin'</c>.</summary>
    string Describe();

    ValueTask<bool> IsSatisfiedAsync(AuthorizationContext context);
}

/// <summary>What a requirement gets to look at.</summary>
public sealed class AuthorizationContext(HttpContext httpContext, ClaimsPrincipal user)
{
    public HttpContext HttpContext { get; } = httpContext;

    public ClaimsPrincipal User { get; } = user;
}

/// <summary>
/// A named set of requirements. Built through <see cref="AuthorizationPolicyBuilder"/> and looked up
/// by the name an <c>[Authorize(Policy = "...")]</c> asked for.
/// </summary>
public sealed class AuthorizationPolicy(IReadOnlyList<IAuthorizationRequirement> requirements)
{
    public IReadOnlyList<IAuthorizationRequirement> Requirements { get; } = requirements;

    /// <summary>
    /// Evaluates every requirement. Returns null on success, or the first failure's description —
    /// which becomes a log line, never a response body: telling an anonymous caller exactly which
    /// claim they are missing is a free hint for someone probing.
    /// </summary>
    public async ValueTask<string?> EvaluateAsync(AuthorizationContext context)
    {
        foreach (var requirement in this.Requirements)
        {
            if (!await requirement.IsSatisfiedAsync(context).ConfigureAwait(false))
                return requirement.Describe();
        }

        return null;
    }
}

/// <summary>Assembles a policy.</summary>
public sealed class AuthorizationPolicyBuilder
{
    readonly List<IAuthorizationRequirement> requirements = [];

    /// <summary>Requires an authenticated caller. Implied by every other requirement.</summary>
    public AuthorizationPolicyBuilder RequireAuthenticatedUser()
    {
        this.requirements.Add(new AuthenticatedUserRequirement());
        return this;
    }

    /// <summary>Requires any one of the given roles.</summary>
    public AuthorizationPolicyBuilder RequireRole(params string[] roles)
    {
        ArgumentNullException.ThrowIfNull(roles);

        this.requirements.Add(new RoleRequirement(roles));
        return this;
    }

    /// <summary>Requires a claim, optionally with one of the given values.</summary>
    public AuthorizationPolicyBuilder RequireClaim(string claimType, params string[] allowedValues)
    {
        ArgumentException.ThrowIfNullOrEmpty(claimType);

        this.requirements.Add(new ClaimRequirement(claimType, allowedValues));
        return this;
    }

    /// <summary>Requires an arbitrary predicate — the escape hatch for anything above.</summary>
    public AuthorizationPolicyBuilder RequireAssertion(
        Func<AuthorizationContext, ValueTask<bool>> assertion,
        string? description = null
    )
    {
        ArgumentNullException.ThrowIfNull(assertion);

        this.requirements.Add(new AssertionRequirement(assertion, description));
        return this;
    }

    /// <summary>Synchronous <see cref="RequireAssertion(Func{AuthorizationContext, ValueTask{bool}}, string?)"/>.</summary>
    public AuthorizationPolicyBuilder RequireAssertion(
        Func<AuthorizationContext, bool> assertion,
        string? description = null
    )
    {
        ArgumentNullException.ThrowIfNull(assertion);
        return this.RequireAssertion(ctx => new ValueTask<bool>(assertion(ctx)), description);
    }

    public AuthorizationPolicyBuilder AddRequirement(IAuthorizationRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(requirement);

        this.requirements.Add(requirement);
        return this;
    }

    public AuthorizationPolicy Build()
    {
        // A policy with nothing in it would authorize anonymous callers, which is never what
        // someone writing AddPolicy meant.
        if (this.requirements.Count == 0)
            this.RequireAuthenticatedUser();

        return new AuthorizationPolicy([.. this.requirements]);
    }
}

sealed class AuthenticatedUserRequirement : IAuthorizationRequirement
{
    public string Describe() => "an authenticated user";

    public ValueTask<bool> IsSatisfiedAsync(AuthorizationContext context)
        => new(context.User.Identity?.IsAuthenticated == true);
}

sealed class RoleRequirement(string[] roles) : IAuthorizationRequirement
{
    public string Describe() => roles.Length == 1
        ? $"role '{roles[0]}'"
        : $"one of the roles {string.Join(", ", roles.Select(r => $"'{r}'"))}";

    public ValueTask<bool> IsSatisfiedAsync(AuthorizationContext context)
    {
        if (context.User.Identity?.IsAuthenticated != true)
            return new ValueTask<bool>(false);

        foreach (var role in roles)
        {
            if (context.User.IsInRole(role))
                return new ValueTask<bool>(true);
        }

        return new ValueTask<bool>(false);
    }
}

sealed class ClaimRequirement(string claimType, string[] allowedValues) : IAuthorizationRequirement
{
    public string Describe() => allowedValues.Length == 0
        ? $"claim '{claimType}'"
        : $"claim '{claimType}' with one of {string.Join(", ", allowedValues.Select(v => $"'{v}'"))}";

    public ValueTask<bool> IsSatisfiedAsync(AuthorizationContext context)
    {
        if (context.User.Identity?.IsAuthenticated != true)
            return new ValueTask<bool>(false);

        foreach (var claim in context.User.FindAll(claimType))
        {
            if (allowedValues.Length == 0 || allowedValues.Contains(claim.Value, StringComparer.Ordinal))
                return new ValueTask<bool>(true);
        }

        return new ValueTask<bool>(false);
    }
}

sealed class AssertionRequirement(
    Func<AuthorizationContext, ValueTask<bool>> assertion,
    string? description
) : IAuthorizationRequirement
{
    public string Describe() => description ?? "a custom authorization rule";

    public ValueTask<bool> IsSatisfiedAsync(AuthorizationContext context) => assertion(context);
}
