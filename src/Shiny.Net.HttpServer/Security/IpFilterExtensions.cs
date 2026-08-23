using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Shiny.Net.HttpServer.Security;

/// <summary>
/// Registering and installing the IP filter.
/// <para>
/// A container is optional here. <see cref="HttpServerIpFilterExtensions.UseIpFilter(HttpServer, Action{IpFilterPolicyBuilder})"/>
/// takes the whole policy inline, which is what a tier-0 embedded server wants; the DI form exists
/// for apps with several named policies to hand out per endpoint.
/// </para>
/// </summary>
public static class IpFilterServiceCollectionExtensions
{
    /// <summary>Registers the IP filter's options and policies.</summary>
    public static ShinyHttpServerBuilder AddIpFilter(this ShinyHttpServerBuilder builder, Action<IpFilterOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton(_ =>
        {
            var options = new IpFilterOptions();
            configure?.Invoke(options);
            return options;
        });

        return builder;
    }
}

/// <summary>Putting the IP filter in the pipeline.</summary>
public static class HttpServerIpFilterExtensions
{
    /// <summary>
    /// Filters every request against <see cref="IpFilterOptions.DefaultPolicy"/> and whatever
    /// individual endpoints asked for with <see cref="RequireIpFilter"/>.
    /// </summary>
    public static HttpServer UseIpFilter(this HttpServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return server.UseIpFilter(policy: null);
    }

    /// <summary>
    /// Filters every request against the named policy, which overrides
    /// <see cref="IpFilterOptions.DefaultPolicy"/> for endpoints that do not name one themselves.
    /// </summary>
    public static HttpServer UseIpFilter(this HttpServer server, string policyName)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrEmpty(policyName);

        var options = Options(server);
        return server.UseIpFilter(options.GetPolicy(policyName));
    }

    /// <summary>
    /// Filters every request against a policy declared right here — no container, no registration.
    /// <code>
    /// app.UseIpFilter(p => p.AllowLoopback().AllowPrivateNetworks());
    /// </code>
    /// </summary>
    public static HttpServer UseIpFilter(this HttpServer server, Action<IpFilterPolicyBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(configure);

        return server.UseIpFilter(IpFilterPolicy.Create(configure));
    }

    /// <summary>Filters every request against an already-built policy.</summary>
    public static HttpServer UseIpFilter(this HttpServer server, IpFilterPolicy? policy)
    {
        ArgumentNullException.ThrowIfNull(server);

        // A filter with nothing to enforce is almost certainly a mistake — and a silent one, since
        // it fails open. Say so at composition time rather than letting it look installed.
        var options = server.Services?.GetService<IpFilterOptions>()
            ?? (policy is not null
                ? new IpFilterOptions()
                : throw new InvalidOperationException(
                    "UseIpFilter has no policy to apply. Either pass one inline — " +
                    "app.UseIpFilter(p => p.AllowLoopback()) — or register them with " +
                    "builder.AddIpFilter(o => ...)."
                ));

        var logger = server.Services
            ?.GetService<ILoggerFactory>()
            ?.CreateLogger<IpFilterMiddleware>();

        return server.Use(new IpFilterMiddleware(server.Router, options, policy, logger));
    }

    /// <summary>
    /// Applies a named policy to the most recently mapped route.
    /// <code>
    /// app.MapGet("/admin/keys", Handler).RequireIpFilter("admin");
    /// </code>
    /// </summary>
    public static HttpServer RequireIpFilter(this HttpServer server, string policyName)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrEmpty(policyName);

        LastEndpointMetadata(server).PolicyName = policyName;
        return server;
    }

    /// <summary>
    /// Exempts the most recently mapped route from the filter, including from the default policy —
    /// a health check a load balancer has to reach, say.
    /// </summary>
    public static HttpServer AllowAnyIp(this HttpServer server)
    {
        ArgumentNullException.ThrowIfNull(server);

        LastEndpointMetadata(server).Disabled = true;
        return server;
    }

    static IpFilterOptions Options(HttpServer server)
        => server.Services?.GetService<IpFilterOptions>()
            ?? throw new InvalidOperationException(
                "Named IP filter policies live in IpFilterOptions. Register them with " +
                "builder.AddIpFilter(o => o.AddPolicy(\"name\", p => ...)), or pass a policy " +
                "inline with app.UseIpFilter(p => ...)."
            );

    static IpFilterMetadata LastEndpointMetadata(HttpServer server)
    {
        if (server.Router.Endpoints.Count == 0)
            throw new InvalidOperationException(
                "RequireIpFilter applies to the most recently mapped route, and no route has been mapped yet."
            );

        var endpoint = server.Router.Endpoints[^1];
        var metadata = endpoint.GetMetadata<IpFilterMetadata>();

        if (metadata is null)
        {
            metadata = new IpFilterMetadata();
            endpoint.WithMetadata(metadata);
        }

        return metadata;
    }
}
