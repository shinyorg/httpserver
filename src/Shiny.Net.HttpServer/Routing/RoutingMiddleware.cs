using Shiny.Net.HttpServer.Versioning;

namespace Shiny.Net.HttpServer.Routing;

/// <summary>
/// Matches the request against the route table, publishes the captured parameters and the selected
/// endpoint onto the context, and hands off to <paramref name="endpointPipeline"/> — the
/// after-routing middleware wrapped around the endpoint's own delegate.
/// <para>
/// The split matters: authorization and anything else that reads endpoint metadata has to run after
/// selection but before invocation, and this is the only point where both are true.
/// </para>
/// <para>
/// When nothing matches it falls through to <paramref name="fallback"/> rather than answering
/// itself, so an app can serve static files or a SPA index from the same pipeline.
/// </para>
/// </summary>
sealed class RoutingMiddleware(Router router, RequestDelegate fallback, RequestDelegate endpointPipeline)
{
    public async ValueTask InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        var match = router.Match(request.Method, request.Path, request.RouteValues);

        if (match.Endpoint is { } endpoint)
        {
            // A route with API versions — one versioned endpoint, or several sharing the route —
            // is resolved to the endpoint for the requested version before anything reads
            // ctx.Endpoint, so authorization and the rest see the endpoint that will actually run.
            if (ApiVersionSelection.RequiresSelection(match.Candidates))
            {
                var options = router.Versioning ?? ApiVersioningOptions.Default;
                var selection = ApiVersionSelection.Select(options, context, match.Candidates);

                if (selection.Endpoint is null)
                {
                    await ApiVersionSelection.WriteErrorAsync(options, context, match.Candidates, selection).ConfigureAwait(false);
                    return;
                }

                endpoint = selection.Endpoint;

                if (selection.Version is { } version)
                    ApiVersionSelection.SetRequestedVersion(context, version);

                ApiVersionSelection.WriteHeaders(options, context, match.Candidates, selection.Version);
            }

            context.Endpoint = endpoint;
            await endpointPipeline(context).ConfigureAwait(false);
            return;
        }

        if (match.IsMethodNotAllowed)
        {
            // RFC 9110 requires Allow on a 405. Some clients rely on it to retry correctly.
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            context.Response.Headers[HeaderNames.Allow] = match.AllowedMethods;
            context.Response.ContentLength = 0;
            await context.Response.StartAsync(context.RequestAborted).ConfigureAwait(false);
            return;
        }

        await fallback(context).ConfigureAwait(false);
    }
}
