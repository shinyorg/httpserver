using System.Globalization;
using Shiny.Net.HttpServer.Internal;
using Shiny.Net.HttpServer.Routing;

namespace Shiny.Net.HttpServer.Cors;

/// <summary>
/// Answers preflights and stamps cross-origin responses with the headers a browser needs.
/// <para>
/// It runs before routing, for a reason specific to CORS: a preflight is an <c>OPTIONS</c> request
/// to a path that only answers <c>GET</c> or <c>POST</c>, so leaving it to the router would produce
/// a 405 with no CORS headers and the real request would never be sent. Instead the preflight is
/// answered here, after asking the router which endpoint the <c>Access-Control-Request-Method</c>
/// would actually have reached — so a per-endpoint policy still applies to it.
/// </para>
/// <para>
/// A request with no <c>Origin</c> header is not a CORS request and costs nothing: one dictionary
/// lookup, then straight through.
/// </para>
/// </summary>
public sealed class CorsMiddleware(Router router, CorsOptions options, CorsPolicy? globalPolicy = null) : IHttpMiddleware
{
    public ValueTask InvokeAsync(HttpContext context, RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);

        var origin = context.Request.Headers.GetFirst(HeaderNames.Origin);
        if (string.IsNullOrEmpty(origin))
            return next(context);

        var requestedMethod = context.Request.Headers.GetFirst(HeaderNames.AccessControlRequestMethod);
        var isPreflight = HttpMethods.IsOptions(context.Request.Method) && !string.IsNullOrEmpty(requestedMethod);

        var policy = this.PolicyFor(context, isPreflight ? requestedMethod : null);

        if (isPreflight)
            return this.HandlePreflightAsync(context, policy, origin, requestedMethod!);

        if (policy is not null)
        {
            // Registered rather than set here and now: a handler that sets Vary itself would
            // otherwise overwrite ours, and a middleware between us and it that short-circuits
            // would produce a response with no CORS headers at all. OnStarting runs last, just
            // before the head goes to the wire.
            var applying = policy;
            context.Response.OnStarting(() =>
            {
                ApplyResponseHeaders(context, applying, origin);
                return ValueTask.CompletedTask;
            });
        }

        return next(context);
    }

    /// <summary>
    /// A preflight is answered here and never forwarded. It carries no body and no credentials the
    /// application could act on, and letting it reach a handler would mean every handler had to know
    /// about CORS.
    /// </summary>
    async ValueTask HandlePreflightAsync(HttpContext context, CorsPolicy? policy, string origin, string requestedMethod)
    {
        var response = context.Response;
        response.StatusCode = StatusCodes.Status204NoContent;
        response.ContentLength = 0;

        if (policy is null)
        {
            await response.StartAsync(context.RequestAborted).ConfigureAwait(false);
            return;
        }

        if (policy.VariesByOrigin)
            AppendVaryOrigin(response);

        var requestedHeaders = context.Request.Headers.GetFirst(HeaderNames.AccessControlRequestHeaders);

        // Anything short of a complete "yes" gets a bare 204 with no CORS headers, which is how a
        // browser is told no. Emitting a partial answer would be worse than emitting none: it would
        // let through exactly the parts nobody vetted.
        var approved = policy.IsOriginAllowed(origin)
            && policy.IsMethodAllowed(requestedMethod)
            && policy.AreHeadersAllowed(requestedHeaders);

        if (!approved)
        {
            await response.StartAsync(context.RequestAborted).ConfigureAwait(false);
            return;
        }

        SetAllowOrigin(response, policy, origin);

        // The requested method is echoed rather than the whole configured list: the browser only
        // asked about one, and listing the others tells a stranger more than they need to know.
        response.Headers.Set(
            HeaderNames.AccessControlAllowMethods,
            policy.AllowAnyMethod ? requestedMethod.ToUpperInvariant() : string.Join(", ", policy.Methods)
        );

        if (!string.IsNullOrEmpty(requestedHeaders))
            response.Headers.Set(HeaderNames.AccessControlAllowHeaders, requestedHeaders);
        else if (!policy.AllowAnyHeader && policy.Headers.Count > 0)
            response.Headers.Set(HeaderNames.AccessControlAllowHeaders, string.Join(", ", policy.Headers));

        if (policy.AllowCredentials)
            response.Headers.Set(HeaderNames.AccessControlAllowCredentials, "true");

        if (policy.PreflightMaxAge is { } maxAge)
            response.Headers.Set(
                HeaderNames.AccessControlMaxAge,
                ((long)maxAge.TotalSeconds).ToString(CultureInfo.InvariantCulture)
            );

        await response.StartAsync(context.RequestAborted).ConfigureAwait(false);
    }

    static void ApplyResponseHeaders(HttpContext context, CorsPolicy policy, string origin)
    {
        var response = context.Response;

        // Vary goes on whether or not this origin was allowed: the answer still depends on which
        // origin asked, and a cache that does not know that will hand one site another's response.
        if (policy.VariesByOrigin)
            AppendVaryOrigin(response);

        if (!policy.IsOriginAllowed(origin))
            return;

        SetAllowOrigin(response, policy, origin);

        if (policy.AllowCredentials)
            response.Headers.Set(HeaderNames.AccessControlAllowCredentials, "true");

        if (policy.ExposedHeaders.Count > 0)
            response.Headers.Set(HeaderNames.AccessControlExposeHeaders, string.Join(", ", policy.ExposedHeaders));
    }

    static void SetAllowOrigin(HttpResponse response, CorsPolicy policy, string origin)
        => response.Headers.Set(
            HeaderNames.AccessControlAllowOrigin,
            // "*" only when the policy really does not care who is asking. The moment credentials
            // or a named origin are involved the actual origin has to be echoed back.
            policy.AllowAnyOrigin && !policy.AllowCredentials ? "*" : origin
        );

    static void AppendVaryOrigin(HttpResponse response)
    {
        var existing = response.Headers.GetFirst(HeaderNames.Vary);

        if (string.IsNullOrEmpty(existing))
            response.Headers.Set(HeaderNames.Vary, HeaderNames.Origin);
        else if (!existing.Contains(HeaderNames.Origin, StringComparison.OrdinalIgnoreCase))
            response.Headers.Set(HeaderNames.Vary, $"{existing}, {HeaderNames.Origin}");
    }

    CorsPolicy? PolicyFor(HttpContext context, string? preflightMethod)
    {
        var metadata = EndpointResolver
            .Resolve(router, context, preflightMethod?.ToUpperInvariant())
            ?.GetMetadata<CorsMetadata>();

        if (metadata is { Disabled: true })
            return null;

        if (metadata?.PolicyName is { } name)
            return options.GetPolicy(name);

        return globalPolicy ?? options.DefaultPolicy;
    }
}
