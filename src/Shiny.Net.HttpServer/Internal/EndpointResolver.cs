using Shiny.Net.HttpServer.Routing;

namespace Shiny.Net.HttpServer.Internal;

/// <summary>
/// Finds the endpoint a request <em>would</em> select, from middleware that runs before routing.
/// <para>
/// CORS, rate limiting and IP filtering all want two things at once: to see the endpoint's metadata,
/// and to run early enough that a rejected request never reaches the router's 404, the fallback
/// handler, or a preflight's non-existent OPTIONS route. Those are only compatible if the middleware
/// does its own lookup, which is what this is. The cost is one extra walk of an immutable trie —
/// cheap next to parsing the request and creating its DI scope, and paid only by the modules that
/// need it.
/// </para>
/// </summary>
static class EndpointResolver
{
    /// <summary>
    /// Matches without disturbing the request. <paramref name="method"/> overrides the request's own
    /// verb, which is what a CORS preflight needs — the interesting endpoint is the one the
    /// <c>Access-Control-Request-Method</c> names, not the OPTIONS the browser actually sent.
    /// </summary>
    public static Endpoint? Resolve(Router router, HttpContext context, string? method = null)
    {
        var request = context.Request;
        var routeValues = request.RouteValues;

        var match = router.Match(method ?? request.Method, request.Path, routeValues);

        // The routing middleware matches again for real and captures again. Leaving these behind
        // would give the handler a second copy of every route parameter.
        routeValues.Reset();

        return match.Endpoint;
    }
}
