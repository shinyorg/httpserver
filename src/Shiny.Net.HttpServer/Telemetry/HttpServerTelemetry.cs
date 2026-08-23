using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Shiny.Net.HttpServer.Telemetry;

/// <summary>
/// The names an exporter subscribes to. Both are plain <see cref="System.Diagnostics"/> primitives,
/// so nothing here takes a dependency on OpenTelemetry — a console app that never configures an
/// exporter pays for an inactive <see cref="ActivitySource"/> and an unlistened-to
/// <see cref="Meter"/>, which is close enough to nothing to leave on by default.
/// <code>
/// builder.Services.AddOpenTelemetry()
///     .WithTracing(t => t.AddSource(HttpServerTelemetry.ActivitySourceName))
///     .WithMetrics(m => m.AddMeter(HttpServerTelemetry.MeterName));
/// </code>
/// </summary>
public static class HttpServerTelemetry
{
    public const string ActivitySourceName = "Shiny.Net.HttpServer";

    public const string MeterName = "Shiny.Net.HttpServer";

    /// <summary>The source every server span is started from.</summary>
    public static ActivitySource ActivitySource { get; } = new(ActivitySourceName, ThisAssembly.AssemblyInformationalVersion);

    /// <summary>The meter every server instrument is created on.</summary>
    public static Meter Meter { get; } = new(MeterName, ThisAssembly.AssemblyInformationalVersion);
}
