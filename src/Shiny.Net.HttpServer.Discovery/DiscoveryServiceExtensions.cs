using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shiny.Net.Discovery;

namespace Shiny.Net.HttpServer.Discovery;

/// <summary>Wiring mDNS discovery to a server.</summary>
public static class DiscoveryServiceCollectionExtensions
{
    /// <summary>
    /// Advertises the server on the local link for as long as the host runs.
    /// <code>
    /// builder.Options.Address = IPAddress.Any;
    /// builder.AddHttpServerAdvertisement(o =>
    /// {
    ///     o.ServiceType = "_myapp._tcp";
    ///     o.TxtRecords["role"] = "controller";
    /// });
    /// </code>
    /// <para>
    /// Hosted services start in registration order, and an advertisement for a port that is not
    /// bound yet is an advertisement for nothing. (It recovers
    /// either way — the advertiser watches the server's state — but the first announcement is the
    /// one peers are listening for.)
    /// </para>
    /// <para>
    /// Platform notes that decide whether this works at all: iOS and Mac Catalyst need
    /// <c>NSLocalNetworkUsageDescription</c> in Info.plist and every service type you browse for
    /// listed in <c>NSBonjourServices</c>; Android needs <c>INTERNET</c> and
    /// <c>ACCESS_NETWORK_STATE</c>. Publishing goes through NSNetService and NsdManager, so no
    /// multicast entitlement is required.
    /// </para>
    /// </summary>
    public static ShinyHttpServerBuilder AddHttpServerAdvertisement(
        this ShinyHttpServerBuilder builder,
        Action<HttpServerAdvertisementOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddMdns();

        builder.Services.TryAddSingleton(_ =>
        {
            var options = new HttpServerAdvertisementOptions();
            configure?.Invoke(options);

            return options;
        });

        builder.Services.TryAddSingleton<IHttpServerAdvertiser>(sp => new HttpServerAdvertiser(
            sp.GetRequiredService<IMdnsManager>(),
            sp.GetRequiredService<HttpServer>(),
            sp.GetRequiredService<HttpServerAdvertisementOptions>(),
            sp.GetService<ILoggerFactory>()?.CreateLogger<HttpServerAdvertiser>()
        ));

        builder.Services.AddHostedService<HttpServerAdvertisementHostedService>();

        return builder;
    }

    /// <summary>
    /// Registers <see cref="IHttpServerLocator"/> for finding servers other devices advertise.
    /// <para>
    /// The one registration here that is <em>not</em> on <see cref="ShinyHttpServerBuilder"/>, and
    /// deliberately: an app that only looks for other devices has no server of its own to hang it
    /// off. Everything else in this library is registered through the builder.
    /// </para>
    /// </summary>
    public static IServiceCollection AddHttpServerLocator(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddMdns();
        services.TryAddSingleton<IHttpServerLocator>(sp => new HttpServerLocator(sp.GetRequiredService<IMdnsManager>()));

        return services;
    }
}

/// <summary>Publishes the advertisement for the lifetime of the host.</summary>
sealed class HttpServerAdvertisementHostedService(IHttpServerAdvertiser advertiser) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => advertiser.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => advertiser.StopAsync(cancellationToken);
}

/// <summary>Advertising a server that was built without a host.</summary>
public static class HttpServerAdvertisementExtensions
{
    /// <summary>
    /// Advertises a running server, with no container involved.
    /// <code>
    /// await server.StartAsync();
    /// await using var advertisement = await server.AdvertiseAsync(mdns, o => o.ServiceType = "_myapp._tcp");
    /// </code>
    /// </summary>
    public static async Task<IHttpServerAdvertiser> AdvertiseAsync(
        this HttpServer server,
        IMdnsManager mdns,
        Action<HttpServerAdvertisementOptions>? configure = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(mdns);

        var options = new HttpServerAdvertisementOptions();
        configure?.Invoke(options);

        var advertiser = new HttpServerAdvertiser(mdns, server, options);
        await advertiser.StartAsync(cancellationToken).ConfigureAwait(false);

        return advertiser;
    }
}
