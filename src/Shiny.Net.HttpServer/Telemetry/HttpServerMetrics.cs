using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Shiny.Net.HttpServer.Telemetry;

/// <summary>
/// The server's instruments, named and shaped by the OpenTelemetry HTTP semantic conventions so a
/// dashboard built for ASP.NET Core reads this server without being told about it.
/// <para>
/// Static because instruments are per-meter, not per-server: two servers in one process (loopback
/// and LAN, say) report to the same histogram and are told apart by their attributes.
/// </para>
/// </summary>
public static class HttpServerMetrics
{
    // Buckets from the semantic convention's recommendation. The short end is finer than a
    // datacentre default because on-device request latency is measured in single milliseconds.
    static readonly InstrumentAdvice<double> DurationAdvice = new()
    {
        HistogramBucketBoundaries = [0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10]
    };

    static readonly Histogram<double> RequestDuration = HttpServerTelemetry.Meter.CreateHistogram(
        "http.server.request.duration",
        unit: "s",
        description: "Duration of HTTP server requests.",
        advice: DurationAdvice
    );

    static readonly UpDownCounter<long> ActiveRequests = HttpServerTelemetry.Meter.CreateUpDownCounter<long>(
        "http.server.active_requests",
        unit: "{request}",
        description: "Number of active HTTP server requests."
    );

    static readonly List<WeakReference<HttpServer>> Servers = [];

    static HttpServerMetrics()
        => HttpServerTelemetry.Meter.CreateObservableUpDownCounter(
            "http.server.active_connections",
            ObserveConnections,
            unit: "{connection}",
            description: "Number of connections the server is currently serving."
        );

    /// <summary>
    /// Includes a server in <c>http.server.active_connections</c>. Held weakly, so a server that is
    /// disposed and dropped stops being reported without anything having to unregister it.
    /// </summary>
    public static void Track(HttpServer server)
    {
        ArgumentNullException.ThrowIfNull(server);

        lock (Servers)
        {
            foreach (var entry in Servers)
            {
                if (entry.TryGetTarget(out var existing) && ReferenceEquals(existing, server))
                    return;
            }

            Servers.Add(new WeakReference<HttpServer>(server));
        }
    }

    static IEnumerable<Measurement<long>> ObserveConnections()
    {
        var measurements = new List<Measurement<long>>();

        lock (Servers)
        {
            for (var i = Servers.Count - 1; i >= 0; i--)
            {
                if (!Servers[i].TryGetTarget(out var server))
                {
                    Servers.RemoveAt(i);
                    continue;
                }

                measurements.Add(new Measurement<long>(
                    server.ActiveConnections,
                    new KeyValuePair<string, object?>("server.address", server.ListenUrl ?? "(unbound)")
                ));
            }
        }

        return measurements;
    }

    /// <summary>Reports a request that has begun. Returns the tags to hand back to <see cref="RequestStopped"/>.</summary>
    internal static TagList RequestStarted(HttpContext context)
    {
        var tags = new TagList
        {
            { "http.request.method", MethodTag(context.Request.Method) },
            { "url.scheme", context.Request.Scheme }
        };

        ActiveRequests.Add(1, tags);
        return tags;
    }

    /// <summary>
    /// Reports a finished request. <paramref name="startTags"/> must be exactly what
    /// <see cref="RequestStarted"/> returned, because an up-down counter that is incremented and
    /// decremented under different tags never returns to zero.
    /// </summary>
    internal static void RequestStopped(
        HttpContext context,
        in TagList startTags,
        double elapsedSeconds,
        Exception? exception
    )
    {
        ActiveRequests.Add(-1, startTags);

        var tags = startTags;
        tags.Add("http.response.status_code", context.Response.StatusCode);
        tags.Add("network.protocol.version", ProtocolTag(context.Request.Protocol));

        if (context.Endpoint is Routing.RouteEndpoint route)
            tags.Add("http.route", route.Template.RawText);

        if (ErrorType(context, exception) is { } error)
            tags.Add("error.type", error);

        RequestDuration.Record(elapsedSeconds, tags);
    }

    /// <summary>
    /// Only methods the server actually recognises become attribute values. An unknown one is
    /// reported as <c>_OTHER</c> — the convention exists because the method is caller-controlled,
    /// and a metric with an unbounded attribute is a memory leak in the collector.
    /// </summary>
    internal static string MethodTag(string method) => HttpMethods.IsKnown(method) ? method : "_OTHER";

    static string ProtocolTag(string protocol) => protocol switch
    {
        HttpProtocols.Http11 => "1.1",
        HttpProtocols.Http10 => "1.0",
        HttpProtocols.Http2 => "2",
        HttpProtocols.Http3 => "3",
        _ => protocol
    };

    static string? ErrorType(HttpContext context, Exception? exception)
    {
        if (exception is not null)
            return exception.GetType().FullName;

        return context.Response.StatusCode >= 500
            ? context.Response.StatusCode.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : null;
    }
}
