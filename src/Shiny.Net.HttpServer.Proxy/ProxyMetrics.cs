using System.Diagnostics;
using System.Diagnostics.Metrics;
using Shiny.Net.HttpServer.Telemetry;

namespace Shiny.Net.HttpServer.Proxy;

/// <summary>
/// The proxy's instruments, on the server's own meter (<see cref="HttpServerTelemetry.MeterName"/>).
/// <list type="bullet">
/// <item><c>shiny.proxy.upstream.request.duration</c> — histogram, seconds from sending the request
/// upstream to its response headers (or to the failure), tagged <c>server.address</c> (the
/// destination's host:port), <c>http.request.method</c>, <c>http.response.status_code</c> when there
/// was a response, and <c>error.type</c> when there was not.</item>
/// <item><c>shiny.proxy.upstream.errors</c> — counter of forwards that did not end in a relayed
/// response, tagged <c>error.type</c> (the <see cref="ProxyError"/> name) and <c>server.address</c>
/// when a destination had been chosen.</item>
/// </list>
/// </summary>
static class ProxyMetrics
{
    public const string DurationName = "shiny.proxy.upstream.request.duration";
    public const string ErrorsName = "shiny.proxy.upstream.errors";

    static readonly Histogram<double> Duration = HttpServerTelemetry.Meter.CreateHistogram(
        DurationName,
        unit: "s",
        description: "Time from forwarding a request upstream to its response headers.",
        advice: new InstrumentAdvice<double>
        {
            HistogramBucketBoundaries = [0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10, 30, 60]
        }
    );

    static readonly Counter<long> Errors = HttpServerTelemetry.Meter.CreateCounter<long>(
        ErrorsName,
        unit: "{error}",
        description: "Proxied requests that failed, by kind."
    );

    public static void RecordDuration(string method, string destination, double seconds, int? statusCode, ProxyError error)
    {
        if (!Duration.Enabled)
            return;

        var tags = new TagList
        {
            { "http.request.method", HttpMethods.IsKnown(method) ? method : "_OTHER" },
            { "server.address", destination }
        };

        if (statusCode is { } status)
            tags.Add("http.response.status_code", status);

        if (error != ProxyError.None)
            tags.Add("error.type", Name(error));

        Duration.Record(seconds, tags);
    }

    public static void RecordError(ProxyError error, string? destination)
    {
        if (error == ProxyError.None || !Errors.Enabled)
            return;

        var tags = new TagList { { "error.type", Name(error) } };
        if (destination is not null)
            tags.Add("server.address", destination);

        Errors.Add(1, tags);
    }

    static string Name(ProxyError error) => error switch
    {
        ProxyError.Request => nameof(ProxyError.Request),
        ProxyError.RequestTimeout => nameof(ProxyError.RequestTimeout),
        ProxyError.RequestCanceled => nameof(ProxyError.RequestCanceled),
        ProxyError.ResponseBody => nameof(ProxyError.ResponseBody),
        ProxyError.Upgrade => nameof(ProxyError.Upgrade),
        ProxyError.NoAvailableDestination => nameof(ProxyError.NoAvailableDestination),
        _ => "other"
    };
}
