using System.Diagnostics;
using System.Diagnostics.Metrics;
using Shiny.Net.HttpServer.Telemetry;

namespace Shiny.Net.HttpServer.Switchboard.Internal;

/// <summary>
/// Switchboard instruments, on the server's own meter (<see cref="HttpServerTelemetry.MeterName"/>),
/// and one span per call on its activity source. Every instrument carries a <c>switchboard</c> tag —
/// the switchboard type's name.
/// <list type="bullet">
/// <item><c>shiny.switchboard.lines.active</c> — up/down counter of open lines (attached or held for resume).</item>
/// <item><c>shiny.switchboard.lines.opened</c> — counter of new lines.</item>
/// <item><c>shiny.switchboard.lines.closed</c> — counter of closed lines, tagged <c>reason</c>.</item>
/// <item><c>shiny.switchboard.lines.resumed</c> — counter of resumes, tagged <c>missed</c>.</item>
/// <item><c>shiny.switchboard.lines.overflowed</c> — counter of streams cut for falling behind.</item>
/// <item><c>shiny.switchboard.invocation.duration</c> — histogram, seconds per call, tagged <c>method</c> and <c>outcome</c>.</item>
/// <item><c>shiny.switchboard.client_results</c> — counter of server → client calls awaiting a result, tagged <c>outcome</c>.</item>
/// </list>
/// </summary>
static class SwitchboardMetrics
{
    static readonly UpDownCounter<long> Active = HttpServerTelemetry.Meter.CreateUpDownCounter<long>(
        "shiny.switchboard.lines.active", unit: "{line}", description: "Open switchboard lines.");

    static readonly Counter<long> Opened = HttpServerTelemetry.Meter.CreateCounter<long>(
        "shiny.switchboard.lines.opened", unit: "{line}", description: "Switchboard lines opened.");

    static readonly Counter<long> Closed = HttpServerTelemetry.Meter.CreateCounter<long>(
        "shiny.switchboard.lines.closed", unit: "{line}", description: "Switchboard lines closed, by reason.");

    static readonly Counter<long> Resumed = HttpServerTelemetry.Meter.CreateCounter<long>(
        "shiny.switchboard.lines.resumed", unit: "{line}", description: "Switchboard lines resumed after a drop.");

    static readonly Counter<long> Overflowed = HttpServerTelemetry.Meter.CreateCounter<long>(
        "shiny.switchboard.lines.overflowed", unit: "{line}", description: "Switchboard streams cut for falling behind.");

    static readonly Histogram<double> Duration = HttpServerTelemetry.Meter.CreateHistogram(
        "shiny.switchboard.invocation.duration",
        unit: "s",
        description: "Time to run a switchboard call.",
        advice: new InstrumentAdvice<double>
        {
            HistogramBucketBoundaries = [0.001, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30]
        }
    );

    static readonly Counter<long> ClientResults = HttpServerTelemetry.Meter.CreateCounter<long>(
        "shiny.switchboard.client_results", unit: "{call}", description: "Server-to-client calls awaiting a result, by outcome.");

    public static void LineOpened(string board)
    {
        var tag = new KeyValuePair<string, object?>("switchboard", board);
        Opened.Add(1, tag);
        Active.Add(1, tag);
    }

    public static void LineClosed(string board, DisconnectReason reason)
    {
        Closed.Add(1, new("switchboard", board), new("reason", reason.ToString()));
        Active.Add(-1, new KeyValuePair<string, object?>("switchboard", board));
    }

    public static void LineResumed(string board, bool missed)
        => Resumed.Add(1, new("switchboard", board), new("missed", missed));

    public static void LineOverflowed(string board)
        => Overflowed.Add(1, new KeyValuePair<string, object?>("switchboard", board));

    public static void Invocation(string board, string method, string outcome, double seconds)
    {
        if (Duration.Enabled)
            Duration.Record(seconds, new("switchboard", board), new("method", method), new("outcome", outcome));
    }

    public static void ClientResult(string board, string outcome)
        => ClientResults.Add(1, new("switchboard", board), new("outcome", outcome));

    public static Activity? StartInvocation(string board, string method, string lineId)
    {
        var activity = HttpServerTelemetry.ActivitySource.StartActivity($"switchboard {board}.{method}");

        activity?.SetTag("switchboard", board);
        activity?.SetTag("switchboard.method", method);
        activity?.SetTag("switchboard.line", lineId);

        return activity;
    }
}
