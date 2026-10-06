using Shiny.Net.HttpServer.NuGet.Internal;

namespace Shiny.Net.HttpServer.NuGet;

/// <summary>
/// The routes one <c>MapNuGetFeed</c> call registered, so a policy can be stated once for the
/// whole feed - or only for the half that reads, or the half that writes.
/// <code>
/// app.MapNuGetFeed("/nuget", o => { ... })
///    .ForReads(r => r.RequireAuthorization())   // a private feed: Basic credentials in nuget.config
///    .ForWrites(r => r.RequireRateLimiting("push"));
/// </code>
/// </summary>
public sealed class NuGetFeedMountBuilder
{
    readonly List<RouteEndpointBuilder> reads;
    readonly List<RouteEndpointBuilder> writes;

    internal NuGetFeedMountBuilder(List<RouteEndpointBuilder> reads, List<RouteEndpointBuilder> writes, string serviceIndexPath)
    {
        this.reads = reads;
        this.writes = writes;
        this.ServiceIndexPath = serviceIndexPath;
    }

    /// <summary>
    /// The path of the service index, <c>{prefix}/v3/index.json</c> - the URL a client is given as
    /// the package source; it finds every other route from there.
    /// </summary>
    public string ServiceIndexPath { get; }

    /// <summary>Every route this feed registered.</summary>
    public IReadOnlyList<RouteEndpointBuilder> Routes => [.. this.reads, .. this.writes];

    /// <summary>Service index, package content, registrations, search and autocomplete.</summary>
    public IReadOnlyList<RouteEndpointBuilder> ReadRoutes => this.reads;

    /// <summary>Push, delete and relist. Empty for a <see cref="NuGetFeedOptions.ReadOnly"/> feed.</summary>
    public IReadOnlyList<RouteEndpointBuilder> WriteRoutes => this.writes;

    /// <summary>Requires authorization on every route, optionally against named policies.</summary>
    public NuGetFeedMountBuilder RequireAuthorization(params string[] policies)
        => this.ForEach(route => route.RequireAuthorization(policies));

    /// <summary>Exempts every route from authorization, including from a fallback policy.</summary>
    public NuGetFeedMountBuilder AllowAnonymous()
        => this.ForEach(route => route.AllowAnonymous());

    /// <summary>Applies a named rate limit policy to every route.</summary>
    public NuGetFeedMountBuilder RequireRateLimiting(string policyName)
        => this.ForEach(route => route.RequireRateLimiting(policyName));

    /// <summary>Applies a named IP filter policy to every route.</summary>
    public NuGetFeedMountBuilder RequireIpFilter(string policyName)
        => this.ForEach(route => route.RequireIpFilter(policyName));

    /// <summary>Omits the feed from the OpenAPI document. On by default.</summary>
    public NuGetFeedMountBuilder ExcludeFromDescription()
        => this.ForEach(route => route.ExcludeFromDescription());

    /// <summary>Attaches arbitrary metadata to every route.</summary>
    public NuGetFeedMountBuilder WithMetadata(object metadata)
        => this.ForEach(route => route.WithMetadata(metadata));

    /// <summary>Runs <paramref name="configure"/> against every route.</summary>
    public NuGetFeedMountBuilder ForEach(Action<RouteEndpointBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        foreach (var route in this.reads.Concat(this.writes))
            configure(route);

        return this;
    }

    /// <summary>Runs <paramref name="configure"/> against every read route.</summary>
    public NuGetFeedMountBuilder ForReads(Action<RouteEndpointBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        foreach (var route in this.reads)
            configure(route);

        return this;
    }

    /// <summary>Runs <paramref name="configure"/> against every write route.</summary>
    public NuGetFeedMountBuilder ForWrites(Action<RouteEndpointBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        foreach (var route in this.writes)
            configure(route);

        return this;
    }
}

/// <summary>
/// A private NuGet feed (https://learn.microsoft.com/nuget/api/overview) - the V3 protocol that
/// <c>dotnet restore</c>, <c>dotnet nuget push</c>, Visual Studio and Rider speak, with nothing to
/// install on the client side beyond a package source.
/// <code>
/// app.MapNuGetFeed("/nuget", o =>
/// {
///     o.Store = new DiskNuGetPackageStore(Path.Combine(dataDir, "packages"));
///     o.ApiKeys.Add(builder.Configuration["NuGet:ApiKey"]!);
/// });
///
/// // dotnet nuget add source http://host:5000/nuget/v3/index.json -n mine
/// // dotnet nuget push My.Package.1.0.0.nupkg -s mine -k {key}
/// </code>
/// </summary>
public static class NuGetFeedExtensions
{
    /// <summary>Maps a NuGet feed at <paramref name="prefix"/>.</summary>
    public static NuGetFeedMountBuilder MapNuGetFeed(this HttpServer server, string prefix, Action<NuGetFeedOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new NuGetFeedOptions();
        configure(options);

        return server.MapNuGetFeed(prefix, options);
    }

    /// <summary>Maps a NuGet feed from options built elsewhere.</summary>
    public static NuGetFeedMountBuilder MapNuGetFeed(this HttpServer server, string prefix, NuGetFeedOptions options)
    {
        ArgumentNullException.ThrowIfNull(server);

        NuGetFeedMountBuilder? builder = null;
        server.MapGroup(NormalizePrefix(prefix), group => builder = Map(group, options));

        return builder!;
    }

    /// <summary>Maps a NuGet feed onto an existing route builder, such as a group.</summary>
    public static NuGetFeedMountBuilder MapNuGetFeed(this IEndpointRouteBuilder endpoints, string prefix, NuGetFeedOptions options)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        return Map(endpoints.MapGroup(NormalizePrefix(prefix)), options);
    }

    static NuGetFeedMountBuilder Map(IEndpointRouteBuilder group, NuGetFeedOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.MaxPackageSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), $"{nameof(NuGetFeedOptions.MaxPackageSize)} must be positive.");

        // A feed that takes pushes from anyone is almost never what was meant, and the failure is
        // silent until someone else's package turns up in a build.
        if (!options.ReadOnly && options.RequireApiKey && options.ApiKeys.Count == 0 && options.ValidateApiKeyAsync is null)
            throw new InvalidOperationException(
                $"A NuGet feed that accepts pushes needs {nameof(NuGetFeedOptions.ApiKeys)} or {nameof(NuGetFeedOptions.ValidateApiKeyAsync)}. " +
                $"Set {nameof(NuGetFeedOptions.ReadOnly)} for a feed nobody pushes to, or {nameof(NuGetFeedOptions.RequireApiKey)} = false " +
                "when the write routes are protected by authorization instead."
            );

        // Taken from the group rather than the argument, so a feed nested in another group hands
        // out URLs that include the outer prefix.
        var basePath = group.Prefix.Trim('/') is { Length: > 0 } trimmed ? "/" + trimmed : string.Empty;
        var handler = new NuGetFeedHandler(options, options.ResolveStore(), basePath);

        var reads = new List<RouteEndpointBuilder>
        {
            group.Map(HttpMethods.Get, NuGetFeedHandler.ServiceIndexPath, handler.Guard(handler.ServiceIndexAsync)),
            group.Map(HttpMethods.Get, NuGetFeedHandler.PackageBasePath + "/{id}/index.json", handler.Guard(handler.VersionListAsync)),
            group.Map(HttpMethods.Get, NuGetFeedHandler.PackageBasePath + "/{id}/{version}/{file}", handler.Guard(handler.DownloadAsync)),
            group.Map(HttpMethods.Get, NuGetFeedHandler.RegistrationPath + "/{id}/{leaf}", handler.Guard(handler.RegistrationAsync)),
            group.Map(HttpMethods.Get, NuGetFeedHandler.SearchPath, handler.Guard(handler.SearchAsync)),
            group.Map(HttpMethods.Get, NuGetFeedHandler.AutocompletePath, handler.Guard(handler.AutocompleteAsync))
        };

        var writes = options.ReadOnly
            ? new List<RouteEndpointBuilder>()
            : new List<RouteEndpointBuilder>
            {
                group.Map(HttpMethods.Put, NuGetFeedHandler.PublishPath, handler.Guard(handler.PushAsync)),
                group.Map(HttpMethods.Delete, NuGetFeedHandler.PublishPath + "/{id}/{version}", handler.Guard(handler.DeleteAsync)),
                group.Map(HttpMethods.Post, NuGetFeedHandler.PublishPath + "/{id}/{version}", handler.Guard(handler.RelistAsync))
            };

        var builder = new NuGetFeedMountBuilder(reads, writes, basePath + NuGetFeedHandler.ServiceIndexPath);

        // A protocol documented by NuGet rather than by an OpenAPI schema no NuGet client would read.
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
