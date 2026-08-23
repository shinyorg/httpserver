using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Shiny.Net.HttpServer.Telemetry;

/// <summary>Registering telemetry options.</summary>
public static class TelemetryServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="TelemetryOptions"/>.
    /// <code>
    /// builder.Services.AddHttpServerTelemetry(o => o.RecordUrl = true);
    /// </code>
    /// </summary>
    public static ShinyHttpServerBuilder AddHttpServerTelemetry(
        this ShinyHttpServerBuilder builder,
        Action<TelemetryOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton(_ =>
        {
            var options = new TelemetryOptions();
            configure?.Invoke(options);

            return options;
        });

        return builder;
    }
}

/// <summary>Putting telemetry in the pipeline.</summary>
public static class HttpServerTelemetryExtensions
{
    /// <summary>
    /// Records a span and a duration measurement for every request, and reports the server's
    /// connection count.
    /// <para>
    /// Register it first. Anything above it is time the client waited that nothing measured.
    /// </para>
    /// </summary>
    public static HttpServer UseTelemetry(this HttpServer server, Action<TelemetryOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(server);

        var options = server.Services?.GetService<TelemetryOptions>() ?? new TelemetryOptions();
        configure?.Invoke(options);

        return server.UseTelemetry(options);
    }

    /// <summary>Records telemetry using options built elsewhere.</summary>
    public static HttpServer UseTelemetry(this HttpServer server, TelemetryOptions options)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(options);

        if (options.Metrics)
            HttpServerMetrics.Track(server);

        return server.Use(new TelemetryMiddleware(options));
    }
}
