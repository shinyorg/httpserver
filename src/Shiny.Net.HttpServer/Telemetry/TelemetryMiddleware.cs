using System.Diagnostics;

namespace Shiny.Net.HttpServer.Telemetry;

/// <summary>
/// One span and one duration measurement per request.
/// <para>
/// Outermost in the pipeline, because everything it measures is time the client waited — including
/// the time rate limiting, authentication and compression spend. The route is read <em>after</em>
/// the pipeline unwinds, which is the only point at which the router has chosen one.
/// </para>
/// <code>
/// app.UseTelemetry();
/// </code>
/// </summary>
public sealed class TelemetryMiddleware(TelemetryOptions options) : IHttpMiddleware
{
    readonly TelemetryOptions options = options ?? throw new ArgumentNullException(nameof(options));

    public async ValueTask InvokeAsync(HttpContext context, RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        if (this.options.ShouldRecord is { } predicate && !predicate(context))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var activity = this.options.Tracing ? this.StartActivity(context) : null;
        var tags = this.options.Metrics ? HttpServerMetrics.RequestStarted(context) : default;
        var timestamp = Stopwatch.GetTimestamp();

        try
        {
            await next(context).ConfigureAwait(false);
            this.Complete(context, activity, tags, timestamp, exception: null);
        }
        catch (Exception ex)
        {
            // Recorded and rethrown: this middleware observes, it does not handle. The exception
            // handler chain below it is what turns this into a response.
            this.Complete(context, activity, tags, timestamp, ex);
            throw;
        }
        finally
        {
            activity?.Dispose();
        }
    }

    Activity? StartActivity(HttpContext context)
    {
        var parent = this.options.ContinueIncomingTrace ? ParentOf(context.Request) : default;

        // Named for the method alone at this point. The route is not known until the router has
        // run, and a span whose name is the raw path would give the backend one name per id.
        var activity = HttpServerTelemetry.ActivitySource.StartActivity(
            HttpServerMetrics.MethodTag(context.Request.Method),
            ActivityKind.Server,
            parent
        );

        if (activity is null)
            return null;

        activity.SetTag("http.request.method", HttpServerMetrics.MethodTag(context.Request.Method));
        activity.SetTag("url.scheme", context.Request.Scheme);
        activity.SetTag("network.protocol.version", context.Request.Protocol);
        activity.SetTag("server.address", context.Request.Host);
        activity.SetTag("client.address", context.Connection.RemoteIpAddress?.ToString());

        if (this.options.RecordUrl)
        {
            activity.SetTag("url.path", context.Request.Path);
            activity.SetTag("url.query", context.Request.QueryString);
        }

        if (this.options.EmitResponseTraceHeader)
        {
            var traceparent = activity.Id;
            if (traceparent is not null)
                context.Response.OnStarting(() =>
                {
                    context.Response.Headers.Set("traceparent", traceparent);
                    return default;
                });
        }

        this.options.EnrichSpan?.Invoke(activity, context);

        return activity;
    }

    static ActivityContext ParentOf(HttpRequest request)
    {
        var traceparent = request.Headers.GetFirst("traceparent");

        return traceparent is not null
            && ActivityContext.TryParse(traceparent, request.Headers.GetFirst("tracestate"), isRemote: true, out var parsed)
                ? parsed
                : default;
    }

    void Complete(HttpContext context, Activity? activity, in TagList tags, long timestamp, Exception? exception)
    {
        if (this.options.Metrics)
            HttpServerMetrics.RequestStopped(context, tags, Stopwatch.GetElapsedTime(timestamp).TotalSeconds, exception);

        if (activity is null)
            return;

        var status = context.Response.StatusCode;
        activity.SetTag("http.response.status_code", status);

        if (context.Endpoint is Routing.RouteEndpoint route)
        {
            activity.SetTag("http.route", route.Template.RawText);

            // Renamed now that the route is known: "GET /users/{id}" is one span name for every
            // user, which is the whole point of recording the template rather than the path.
            activity.DisplayName = $"{HttpServerMetrics.MethodTag(context.Request.Method)} {route.Template.RawText}";
        }

        if (exception is not null)
        {
            activity.SetStatus(ActivityStatusCode.Error, exception.Message);
            activity.SetTag("error.type", exception.GetType().FullName);

            if (this.options.RecordExceptionDetails)
                activity.AddException(exception);
        }
        else if (status >= 500)
        {
            // 4xx is the client's fault and stays Unset, exactly as the convention says: a wall of
            // red 404s from a scanner tells you nothing about the server's health.
            activity.SetStatus(ActivityStatusCode.Error);
            activity.SetTag("error.type", status.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}
