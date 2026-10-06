using Shiny.Net.HttpServer.Npm.Internal;

namespace Shiny.Net.HttpServer.Npm;

/// <summary>
/// The routes one <c>MapNpmRegistry</c> call registered, so a policy is stated once for the whole
/// registry - or for the half that reads (<c>GET</c>/<c>HEAD</c>) or the half that writes.
/// </summary>
public sealed class NpmRegistryMountBuilder
{
    readonly List<RouteEndpointBuilder> reads;
    readonly List<RouteEndpointBuilder> writes;

    internal NpmRegistryMountBuilder(List<RouteEndpointBuilder> reads, List<RouteEndpointBuilder> writes)
    {
        this.reads = reads;
        this.writes = writes;
    }

    public IReadOnlyList<RouteEndpointBuilder> Routes => [.. this.reads, .. this.writes];

    /// <summary>Install, metadata, search, whoami.</summary>
    public IReadOnlyList<RouteEndpointBuilder> ReadRoutes => this.reads;

    /// <summary>Publish, unpublish, deprecate, dist-tag and owner changes - and <c>npm login</c>, which is a <c>PUT</c>.</summary>
    public IReadOnlyList<RouteEndpointBuilder> WriteRoutes => this.writes;

    public NpmRegistryMountBuilder RequireAuthorization(params string[] policies)
        => this.ForEach(route => route.RequireAuthorization(policies));

    public NpmRegistryMountBuilder AllowAnonymous()
        => this.ForEach(route => route.AllowAnonymous());

    public NpmRegistryMountBuilder RequireRateLimiting(string policyName)
        => this.ForEach(route => route.RequireRateLimiting(policyName));

    public NpmRegistryMountBuilder RequireIpFilter(string policyName)
        => this.ForEach(route => route.RequireIpFilter(policyName));

    /// <summary>Omits the registry from the OpenAPI document. On by default.</summary>
    public NpmRegistryMountBuilder ExcludeFromDescription()
        => this.ForEach(route => route.ExcludeFromDescription());

    public NpmRegistryMountBuilder WithMetadata(object metadata)
        => this.ForEach(route => route.WithMetadata(metadata));

    public NpmRegistryMountBuilder ForEach(Action<RouteEndpointBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        foreach (var route in this.reads.Concat(this.writes))
            configure(route);

        return this;
    }

    public NpmRegistryMountBuilder ForReads(Action<RouteEndpointBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        foreach (var route in this.reads)
            configure(route);

        return this;
    }

    public NpmRegistryMountBuilder ForWrites(Action<RouteEndpointBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        foreach (var route in this.writes)
            configure(route);

        return this;
    }
}

/// <summary>
/// A private npm registry - the API npm, pnpm and Yarn use - mapped onto the server.
/// <code>
/// app.MapNpmRegistry("/npm", o =>
/// {
///     o.Store = new DiskNpmPackageStore(Path.Combine(dataDir, "npm"));
///     o.Tokens[builder.Configuration["Npm:Token"]!] = "ci";
/// });
///
/// // .npmrc
/// // @acme:registry=http://host:5000/npm/
/// // //host:5000/npm/:_authToken=${NPM_TOKEN}
/// </code>
/// </summary>
public static class NpmRegistryExtensions
{
    public static NpmRegistryMountBuilder MapNpmRegistry(this HttpServer server, string prefix, Action<NpmRegistryOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new NpmRegistryOptions();
        configure(options);

        return server.MapNpmRegistry(prefix, options);
    }

    public static NpmRegistryMountBuilder MapNpmRegistry(this HttpServer server, string prefix, NpmRegistryOptions options)
    {
        ArgumentNullException.ThrowIfNull(server);

        NpmRegistryMountBuilder? builder = null;
        server.MapGroup(NormalizePrefix(prefix), group => builder = Map(group, options));

        return builder!;
    }

    public static NpmRegistryMountBuilder MapNpmRegistry(this IEndpointRouteBuilder endpoints, string prefix, NpmRegistryOptions options)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        return Map(endpoints.MapGroup(NormalizePrefix(prefix)), options);
    }

    static NpmRegistryMountBuilder Map(IEndpointRouteBuilder group, NpmRegistryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.MaxTarballSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), $"{nameof(NpmRegistryOptions.MaxTarballSize)} must be positive.");

        // A registry anyone can publish to is a supply-chain incident waiting for a name.
        if (!options.ReadOnly && options.Tokens.Count == 0 && options.ValidateTokenAsync is null)
            throw new InvalidOperationException(
                $"An npm registry that accepts publishes needs {nameof(NpmRegistryOptions.Tokens)} or {nameof(NpmRegistryOptions.ValidateTokenAsync)}. " +
                $"Set {nameof(NpmRegistryOptions.ReadOnly)} for one nobody publishes to."
            );

        var basePath = group.Prefix.Trim('/') is { Length: > 0 } trimmed ? "/" + trimmed : string.Empty;
        var handler = new NpmRegistryHandler(options, options.ResolveStore(), basePath);
        RequestDelegate handle = handler.HandleAsync;

        var reads = new List<RouteEndpointBuilder>
        {
            group.Map(HttpMethods.Get, "/{*path}", handle)
        };

        var writes = new List<RouteEndpointBuilder>
        {
            group.Map(HttpMethods.Put, "/{*path}", handle),
            group.Map(HttpMethods.Post, "/{*path}", handle),
            group.Map(HttpMethods.Delete, "/{*path}", handle)
        };

        var builder = new NpmRegistryMountBuilder(reads, writes);
        builder.ExcludeFromDescription();

        return builder;
    }

    static string NormalizePrefix(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);

        var trimmed = prefix.Trim().Trim('/');
        return trimmed.Length == 0 ? "/" : "/" + trimmed;
    }
}
