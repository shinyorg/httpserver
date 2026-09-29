using System.Diagnostics;
using System.Diagnostics.Metrics;
using Shiny.Net.HttpServer.Telemetry;

namespace Shiny.Net.HttpServer.Acme.Internal;

/// <summary>
/// ACME's instruments, on the server's own meter (<see cref="HttpServerTelemetry.MeterName"/>), so an
/// exporter already subscribed to the server needs nothing more to alert on a certificate before it
/// lapses.
/// <list type="bullet">
/// <item><c>shiny.acme.certificate.issuances</c> — counter of orders, tagged <c>acme.certificate.name</c>,
/// <c>acme.issuance.result</c> (<c>success</c> / <c>failure</c>) and, on failure, <c>error.type</c>
/// (the CA's problem type, or the exception type).</item>
/// <item><c>shiny.acme.certificate.expiry</c> — observable gauge of days until the certificate being
/// served expires, per <c>acme.certificate.name</c>. Absent for a name with no certificate yet.</item>
/// </list>
/// </summary>
static class AcmeMetrics
{
    public const string IssuancesName = "shiny.acme.certificate.issuances";
    public const string ExpiryName = "shiny.acme.certificate.expiry";

    static readonly Counter<long> Issuances = HttpServerTelemetry.Meter.CreateCounter<long>(
        IssuancesName,
        unit: "{issuance}",
        description: "ACME certificate orders, by outcome."
    );

    static readonly List<WeakReference<AcmeCertificateManager>> Managers = [];

    static AcmeMetrics()
        => HttpServerTelemetry.Meter.CreateObservableGauge(
            ExpiryName,
            Observe,
            unit: "d",
            description: "Days until the ACME certificate being served expires."
        );

    public static void Track(AcmeCertificateManager manager)
    {
        lock (Managers)
            Managers.Add(new WeakReference<AcmeCertificateManager>(manager));
    }

    public static void Untrack(AcmeCertificateManager manager)
    {
        lock (Managers)
        {
            for (var i = Managers.Count - 1; i >= 0; i--)
            {
                if (!Managers[i].TryGetTarget(out var existing) || ReferenceEquals(existing, manager))
                    Managers.RemoveAt(i);
            }
        }
    }

    public static void IssuanceSucceeded(AcmeCertificateManager manager)
        => Issuances.Add(1, new TagList
        {
            { "acme.certificate.name", manager.MetricName },
            { "acme.issuance.result", "success" }
        });

    public static void IssuanceFailed(AcmeCertificateManager manager, Exception exception)
        => Issuances.Add(1, new TagList
        {
            { "acme.certificate.name", manager.MetricName },
            { "acme.issuance.result", "failure" },
            { "error.type", (exception as AcmeException)?.ProblemType ?? exception.GetType().FullName }
        });

    static IEnumerable<Measurement<double>> Observe()
    {
        var measurements = new List<Measurement<double>>();
        var now = DateTimeOffset.UtcNow;

        lock (Managers)
        {
            for (var i = Managers.Count - 1; i >= 0; i--)
            {
                if (!Managers[i].TryGetTarget(out var manager))
                {
                    Managers.RemoveAt(i);
                    continue;
                }

                if (manager.CurrentNotAfter is { } notAfter)
                    measurements.Add(new Measurement<double>(
                        (notAfter - now).TotalDays,
                        new KeyValuePair<string, object?>("acme.certificate.name", manager.MetricName)
                    ));
            }
        }

        return measurements;
    }
}
