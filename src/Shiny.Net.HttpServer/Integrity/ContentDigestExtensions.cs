using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shiny.Net.HttpServer.Routing;

namespace Shiny.Net.HttpServer.Integrity;

/// <summary>Registering RFC 9530 digests.</summary>
public static class ContentDigestServiceCollectionExtensions
{
    /// <summary>
    /// Registers the digest options.
    /// <code>
    /// builder.AddContentDigest(o => o.ResponseDigest = DigestEmission.Always);
    /// </code>
    /// </summary>
    public static ShinyHttpServerBuilder AddContentDigest(
        this ShinyHttpServerBuilder builder,
        Action<ContentDigestOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(builder);

        OptionsRegistration.Configure<ContentDigestOptions>(builder.Services, configure);

        return builder;
    }
}

/// <summary>Putting digests in the pipeline, and setting them per endpoint.</summary>
public static class HttpServerContentDigestExtensions
{
    /// <summary>
    /// Verifies request digests and adds response digests.
    /// <para>
    /// Register it <b>before</b> <c>UseRequestDecompression</c> and <c>UseResponseCompression</c>: the
    /// digest covers the bytes on the wire, so it has to see them compressed.
    /// </para>
    /// </summary>
    public static HttpServer UseContentDigest(this HttpServer server, Action<ContentDigestOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(server);

        var options = server.Services?.GetService<ContentDigestOptions>() ?? new ContentDigestOptions();
        configure?.Invoke(options);

        return server.UseContentDigest(options);
    }

    /// <summary>Verifies and adds digests using options built elsewhere.</summary>
    public static HttpServer UseContentDigest(this HttpServer server, ContentDigestOptions options)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(options);

        if (!DigestFields.IsSupported(options.DefaultAlgorithm))
            throw new ArgumentException(
                $"'{options.DefaultAlgorithm}' is not a supported digest algorithm. Use '{DigestFields.Sha256}' or '{DigestFields.Sha512}'.",
                nameof(options)
            );

        var logger = server.Services
            ?.GetService<ILoggerFactory>()
            ?.CreateLogger<ContentDigestMiddleware>();

        return server.Use(new ContentDigestMiddleware(server.Router, options, logger));
    }

    /// <summary>
    /// Requires requests to every route the preceding call mapped to carry a <c>Content-Digest</c> that
    /// verifies — an upload that must not arrive truncated.
    /// </summary>
    public static HttpServer RequireContentDigest(this HttpServer server)
    {
        ArgumentNullException.ThrowIfNull(server);

        foreach (var endpoint in LastMappedConventions.LastMapped(server, nameof(RequireContentDigest)))
            new RouteEndpointBuilder(endpoint).RequireContentDigest();

        return server;
    }

    /// <summary>Gives the responses of every route the preceding call mapped a <c>Content-Digest</c> whether or not the client asked.</summary>
    public static HttpServer WithContentDigest(this HttpServer server)
    {
        ArgumentNullException.ThrowIfNull(server);

        foreach (var endpoint in LastMappedConventions.LastMapped(server, nameof(WithContentDigest)))
            new RouteEndpointBuilder(endpoint).WithContentDigest();

        return server;
    }

    /// <summary>Exempts every route the preceding call mapped from digests in both directions.</summary>
    public static HttpServer DisableContentDigest(this HttpServer server)
    {
        ArgumentNullException.ThrowIfNull(server);

        foreach (var endpoint in LastMappedConventions.LastMapped(server, nameof(DisableContentDigest)))
            new RouteEndpointBuilder(endpoint).DisableContentDigest();

        return server;
    }

    /// <summary>Requires this route's requests to carry a <c>Content-Digest</c> that verifies.</summary>
    public static RouteEndpointBuilder RequireContentDigest(this RouteEndpointBuilder route)
    {
        ArgumentNullException.ThrowIfNull(route);

        var metadata = GetOrAdd(route);
        metadata.RequireRequestDigest = true;
        metadata.Disabled = false;

        return route;
    }

    /// <summary>Gives this route's responses a <c>Content-Digest</c> whether or not the client asked.</summary>
    public static RouteEndpointBuilder WithContentDigest(this RouteEndpointBuilder route)
    {
        ArgumentNullException.ThrowIfNull(route);

        var metadata = GetOrAdd(route);
        metadata.ResponseDigest = DigestEmission.Always;
        metadata.Disabled = false;

        return route;
    }

    /// <summary>Exempts this route from digests in both directions, including from a group's convention.</summary>
    public static RouteEndpointBuilder DisableContentDigest(this RouteEndpointBuilder route)
    {
        ArgumentNullException.ThrowIfNull(route);

        GetOrAdd(route).Disabled = true;
        return route;
    }

    /// <summary>Requires a verified <c>Content-Digest</c> on every route mapped through the returned builder.</summary>
    public static IEndpointRouteBuilder RequireContentDigest(this IEndpointRouteBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        return new ConventionEndpointRouteBuilder(group, static route => route.RequireContentDigest());
    }

    /// <summary>Gives every route mapped through the returned builder a response <c>Content-Digest</c>.</summary>
    public static IEndpointRouteBuilder WithContentDigest(this IEndpointRouteBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        return new ConventionEndpointRouteBuilder(group, static route => route.WithContentDigest());
    }

    static ContentDigestMetadata GetOrAdd(RouteEndpointBuilder route)
    {
        if (route.Endpoint.GetMetadata<ContentDigestMetadata>() is { } existing)
            return existing;

        var created = new ContentDigestMetadata();
        route.WithMetadata(created);

        return created;
    }
}
