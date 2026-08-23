namespace Shiny.Net.HttpServer.Telemetry;

/// <summary>What the telemetry middleware records, and how much of it.</summary>
public sealed class TelemetryOptions
{
    /// <summary>
    /// Records <c>http.server.request.duration</c> and <c>http.server.active_requests</c> on
    /// <see cref="HttpServerTelemetry.Meter"/>.
    /// </summary>
    public bool Metrics { get; set; } = true;

    /// <summary>
    /// Starts one span per request on <see cref="HttpServerTelemetry.ActivitySource"/>, continuing
    /// the caller's trace when the request carried a <c>traceparent</c>.
    /// </summary>
    public bool Tracing { get; set; } = true;

    /// <summary>
    /// Trusts the inbound <c>traceparent</c>/<c>tracestate</c> headers and continues that trace.
    /// <para>
    /// On by default because the usual caller is the app's own client. Turn it off for a server
    /// exposed to the internet through a tunnel: a caller who chooses the trace id can graft its
    /// spans onto someone else's trace, which is a nuisance in a dashboard and a data leak in a
    /// shared backend.
    /// </para>
    /// </summary>
    public bool ContinueIncomingTrace { get; set; } = true;

    /// <summary>Writes the span's <c>traceparent</c> onto the response, so a client can log the id it hit.</summary>
    public bool EmitResponseTraceHeader { get; set; }

    /// <summary>
    /// Attaches the exception to the span as an event, message and stack trace included. Off by
    /// default for the same reason <see cref="HttpServerOptions.HideExceptionDetails"/> is on.
    /// </summary>
    public bool RecordExceptionDetails { get; set; }

    /// <summary>
    /// Adds <c>url.path</c> and <c>url.query</c> to the span. Off by default: a path routinely
    /// carries identifiers and a query string routinely carries secrets, and spans are shipped
    /// somewhere else by definition.
    /// </summary>
    public bool RecordUrl { get; set; }

    /// <summary>
    /// Decides whether a request is measured at all. Returning false skips both the span and the
    /// metric — the way to keep a health check that a monitor polls every second out of the data.
    /// </summary>
    public Func<HttpContext, bool>? ShouldRecord { get; set; }

    /// <summary>Adds anything else worth carrying — a tenant, a device id — to the request's span.</summary>
    public Action<System.Diagnostics.Activity, HttpContext>? EnrichSpan { get; set; }
}
