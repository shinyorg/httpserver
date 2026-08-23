using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Shiny.Net.HttpServer.Mobile;

/// <summary>Wiring the server to a mobile app's lifecycle.</summary>
public static class MobileServiceCollectionExtensions
{
    /// <summary>
    /// Follows the app's lifecycle: stop on background, start on resume, rebind when the network
    /// changes.
    /// <code>
    /// builder.Services.AddHttpServer(o => o.Address = IPAddress.Any, autoStart: false);
    /// builder.Services.AddHttpServerLifecycle(o => o.BackgroundMode = BackgroundServerMode.KeepAlive);
    /// </code>
    /// <para>
    /// Needs a Shiny host — <c>UseShiny()</c>, from <c>MauiProgram</c> or from a plain iOS or
    /// Android head — because that is what delivers the platform's lifecycle callbacks. It is not
    /// tied to MAUI beyond that. On a non-mobile target framework this registers nothing and does
    /// nothing, so shared code can call it unconditionally.
    /// </para>
    /// <para>
    /// The manifest side is not optional and is not something this can do for you:
    /// <list type="bullet">
    /// <item>iOS/Mac Catalyst — <c>NSLocalNetworkUsageDescription</c> in Info.plist, or the bind is
    /// refused with no error worth reading. Mac Catalyst also needs the
    /// <c>com.apple.security.network.server</c> entitlement.</item>
    /// <item>Android — <c>INTERNET</c>, plus <c>FOREGROUND_SERVICE</c> and
    /// <c>FOREGROUND_SERVICE_DATA_SYNC</c> for <see cref="BackgroundServerMode.KeepAlive"/>, and
    /// <c>POST_NOTIFICATIONS</c> on API 33+ for the notification that service must show.</item>
    /// </list>
    /// <see cref="LocalNetworkAccess.Check"/> reports which of these are missing at runtime.
    /// </para>
    /// </summary>
    public static ShinyHttpServerBuilder AddHttpServerLifecycle(
        this ShinyHttpServerBuilder builder,
        Action<HttpServerLifecycleOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton(_ =>
        {
            var options = new HttpServerLifecycleOptions();
            configure?.Invoke(options);

            return options;
        });

#if PLATFORM
        // Registered against its interfaces, which is how Shiny's lifecycle executor finds it —
        // IShinyStartupTask to be constructed at startup, IApplicationLifecycle to be told about
        // foreground and background.
        builder.Services.AddSingletonAsImplementedInterfaces<HttpServerLifecycleTask>();
#endif

        return builder;
    }
}
