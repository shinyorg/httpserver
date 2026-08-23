using System.Buffers;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Shiny.Net.HttpServer.HealthChecks;

/// <summary>How one mapped health endpoint answers.</summary>
public sealed class HealthEndpointOptions
{
    /// <summary>
    /// Which registrations this endpoint runs. Null runs all of them; the usual reason to set it is
    /// to split liveness from readiness by tag.
    /// </summary>
    public Func<HealthCheckRegistration, bool>? Predicate { get; set; }

    /// <summary>
    /// The status code each verdict answers with. Degraded is a 200 by default — it means "serving,
    /// with a caveat", and a load balancer that pulls the instance out over it turns a caveat into
    /// an outage.
    /// </summary>
    public IDictionary<HealthStatus, int> StatusCodes { get; } = new Dictionary<HealthStatus, int>
    {
        [HealthStatus.Healthy] = Shiny.Net.HttpServer.StatusCodes.Status200OK,
        [HealthStatus.Degraded] = Shiny.Net.HttpServer.StatusCodes.Status200OK,
        [HealthStatus.Unhealthy] = Shiny.Net.HttpServer.StatusCodes.Status503ServiceUnavailable
    };

    /// <summary>
    /// Writes the per-check entries, not just the overall status.
    /// <para>
    /// On by default because the endpoint is normally private. A health endpoint published through
    /// a tunnel is a free inventory of the app's dependencies and their failure modes — turn this
    /// off there, or put the endpoint behind authorization.
    /// </para>
    /// </summary>
    public bool IncludeDetails { get; set; } = true;

    /// <summary>Replaces the response body entirely. The status code is still taken from <see cref="StatusCodes"/>.</summary>
    public Func<HttpContext, HealthReport, ValueTask>? ResponseWriter { get; set; }
}

/// <summary>Mapping a health endpoint.</summary>
public static class HealthCheckEndpointExtensions
{
    /// <summary>
    /// Maps a health endpoint.
    /// <code>
    /// app.MapHealthChecks("/health");
    /// app.MapHealthChecks("/health/ready", o => o.Predicate = r => r.Tags.Contains("ready"));
    /// </code>
    /// <para>
    /// Answers <c>Cache-Control: no-store</c>, because a cached health check is a health check that
    /// tells you about a moment that has passed.
    /// </para>
    /// </summary>
    public static HttpServer MapHealthChecks(
        this HttpServer server,
        string pattern = "/health",
        Action<HealthEndpointOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(server);

        var service = server.Services?.GetService<HealthCheckService>()
            ?? throw new InvalidOperationException(
                "MapHealthChecks needs the health check service. Register it with " +
                "services.AddHealthChecks() before building the server."
            );

        var options = new HealthEndpointOptions();
        configure?.Invoke(options);

        return server.MapGet(pattern, async ctx =>
        {
            var report = await service.CheckHealthAsync(options.Predicate, ctx.RequestAborted).ConfigureAwait(false);

            ctx.Response.StatusCode = options.StatusCodes.TryGetValue(report.Status, out var status)
                ? status
                : Shiny.Net.HttpServer.StatusCodes.Status200OK;

            ctx.Response.Headers.Set(HeaderNames.CacheControl, "no-store, no-cache");

            if (options.ResponseWriter is { } writer)
            {
                await writer(ctx, report).ConfigureAwait(false);
                return;
            }

            await WriteJsonAsync(ctx, report, options.IncludeDetails).ConfigureAwait(false);
        });
    }

    /// <summary>Maps a health endpoint that runs only the checks carrying <paramref name="tag"/>.</summary>
    public static HttpServer MapHealthChecks(this HttpServer server, string pattern, string tag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        return server.MapHealthChecks(pattern, o => o.Predicate = r => r.Tags.Contains(tag));
    }

    /// <summary>
    /// Writes the report by hand rather than through a serializer.
    /// <para>
    /// The shape is fixed and known here, so there is nothing for a <c>JsonSerializerContext</c> to
    /// describe — and asking an app to declare one for the framework's own diagnostics type would
    /// be a trim-safety tax with no upside.
    /// </para>
    /// </summary>
    static async ValueTask WriteJsonAsync(HttpContext context, HealthReport report, bool includeDetails)
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("status", report.Status.ToString());
            writer.WriteNumber("totalDurationMs", Math.Round(report.TotalDuration.TotalMilliseconds, 3));

            if (includeDetails)
            {
                writer.WriteStartObject("entries");

                foreach (var entry in report.Entries)
                {
                    writer.WriteStartObject(entry.Name);
                    writer.WriteString("status", entry.Status.ToString());
                    writer.WriteNumber("durationMs", Math.Round(entry.Duration.TotalMilliseconds, 3));

                    if (entry.Description is { } description)
                        writer.WriteString("description", description);

                    if (entry.Tags.Count > 0)
                    {
                        writer.WriteStartArray("tags");
                        foreach (var tag in entry.Tags)
                            writer.WriteStringValue(tag);
                        writer.WriteEndArray();
                    }

                    if (entry.Data is { Count: > 0 } data)
                    {
                        writer.WriteStartObject("data");
                        foreach (var pair in data)
                            writer.WriteString(pair.Key, pair.Value);
                        writer.WriteEndObject();
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        await context.Response
            .WriteBytesAsync(buffer.WrittenMemory, "application/json; charset=utf-8", context.RequestAborted)
            .ConfigureAwait(false);
    }
}
