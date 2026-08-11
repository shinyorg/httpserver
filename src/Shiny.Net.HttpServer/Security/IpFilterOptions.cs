using System.Net;

namespace Shiny.Net.HttpServer.Security;

/// <summary>
/// The address rules for the whole app.
/// <code>
/// builder.Services.AddIpFilter(o =>
/// {
///     o.DefaultPolicy = IpFilterPolicy.Create(p => p.AllowLoopback().AllowPrivateNetworks());
///     o.AddPolicy("admin", p => p.Allow("10.1.0.0/24"));
/// });
///
/// app.UseIpFilter();
/// app.OnGet("/admin/keys", Handler).RequireIpFilter("admin");
/// </code>
/// </summary>
public sealed class IpFilterOptions
{
    readonly Dictionary<string, IpFilterPolicy> policies = new(StringComparer.Ordinal);

    /// <summary>
    /// Applied to every request that does not name a policy of its own. Null (the default) means the
    /// filter only does anything for endpoints that asked for it.
    /// </summary>
    public IpFilterPolicy? DefaultPolicy { get; set; }

    /// <summary>
    /// What a blocked caller gets. 403 by default: the request was understood and refused, and no
    /// credential is going to change that.
    /// </summary>
    public int RejectionStatusCode { get; set; } = StatusCodes.Status403Forbidden;

    /// <summary>
    /// Called instead of the plain rejection response, for an app that wants to write a body, emit a
    /// metric, or answer 404 to avoid confirming the endpoint exists. Whatever it writes is the
    /// response; the middleware writes nothing else.
    /// </summary>
    public Func<HttpContext, IPAddress?, ValueTask>? OnRejected { get; set; }

    public IReadOnlyDictionary<string, IpFilterPolicy> Policies => this.policies;

    public IpFilterOptions AddPolicy(string name, IpFilterPolicy policy)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(policy);

        this.policies[name] = policy;
        return this;
    }

    public IpFilterOptions AddPolicy(string name, Action<IpFilterPolicyBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new IpFilterPolicyBuilder();
        configure(builder);

        return this.AddPolicy(name, builder.Build());
    }

    /// <summary>Sets <see cref="DefaultPolicy"/> from a builder.</summary>
    public IpFilterOptions SetDefaultPolicy(Action<IpFilterPolicyBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new IpFilterPolicyBuilder();
        configure(builder);
        this.DefaultPolicy = builder.Build();

        return this;
    }

    /// <summary>
    /// Resolves a named policy, throwing when it was never registered — at the first request that
    /// names it, rather than silently letting everyone through.
    /// </summary>
    public IpFilterPolicy GetPolicy(string name)
        => this.policies.TryGetValue(name, out var policy)
            ? policy
            : throw new InvalidOperationException(
                $"No IP filter policy named '{name}' is registered. " +
                $"Add it with services.AddIpFilter(o => o.AddPolicy(\"{name}\", p => ...))."
            );
}

/// <summary>
/// What an endpoint asks of the IP filter, attached to it as metadata.
/// </summary>
public sealed class IpFilterMetadata
{
    /// <summary>The named policy to apply, or null to use <see cref="IpFilterOptions.DefaultPolicy"/>.</summary>
    public string? PolicyName { get; set; }

    /// <summary>True when the endpoint opted out entirely, including out of the default policy.</summary>
    public bool Disabled { get; set; }
}
