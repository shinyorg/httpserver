namespace Shiny.Net.HttpServer.Cors;

/// <summary>
/// The CORS policies for the whole app.
/// <code>
/// builder.Services.AddCors(o =>
/// {
///     o.AddDefaultPolicy(p => p.WithOrigins("https://app.example.com").AllowAnyHeader().AllowAnyMethod());
///     o.AddPolicy("public", p => p.AllowAnyOrigin().WithMethods("GET"));
/// });
///
/// app.UseCors();
/// app.MapGet("/status", Handler).RequireCors("public");
/// </code>
/// </summary>
public sealed class CorsOptions
{
    readonly Dictionary<string, CorsPolicy> policies = new(StringComparer.Ordinal);

    /// <summary>
    /// Applied to any cross-origin request that does not name a policy. Null leaves CORS off except
    /// for endpoints that asked for it.
    /// </summary>
    public CorsPolicy? DefaultPolicy { get; set; }

    public IReadOnlyDictionary<string, CorsPolicy> Policies => this.policies;

    public CorsOptions AddPolicy(string name, CorsPolicy policy)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(policy);

        this.policies[name] = policy;
        return this;
    }

    public CorsOptions AddPolicy(string name, Action<CorsPolicyBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        return this.AddPolicy(name, CorsPolicy.Create(configure));
    }

    public CorsOptions AddDefaultPolicy(CorsPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        this.DefaultPolicy = policy;
        return this;
    }

    public CorsOptions AddDefaultPolicy(Action<CorsPolicyBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        return this.AddDefaultPolicy(CorsPolicy.Create(configure));
    }

    /// <summary>
    /// Resolves a named policy, throwing when it was never registered. Loudly, because the
    /// alternative is a browser error message that says nothing about which policy was missing.
    /// </summary>
    public CorsPolicy GetPolicy(string name)
        => this.policies.TryGetValue(name, out var policy)
            ? policy
            : throw new InvalidOperationException(
                $"No CORS policy named '{name}' is registered. " +
                $"Add it with services.AddCors(o => o.AddPolicy(\"{name}\", p => ...))."
            );
}

/// <summary>What an endpoint asks of CORS, attached to it as metadata.</summary>
public sealed class CorsMetadata
{
    /// <summary>The named policy to apply, or null for <see cref="CorsOptions.DefaultPolicy"/>.</summary>
    public string? PolicyName { get; set; }

    /// <summary>True when the endpoint opted out, including out of the default policy.</summary>
    public bool Disabled { get; set; }
}
