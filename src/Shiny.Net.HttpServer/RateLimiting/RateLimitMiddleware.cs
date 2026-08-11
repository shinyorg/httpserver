using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shiny.Net.HttpServer.Internal;
using Shiny.Net.HttpServer.Routing;

namespace Shiny.Net.HttpServer.RateLimiting;

/// <summary>
/// Turns away requests that are arriving too fast, before they cost anything.
/// <para>
/// Ahead of routing, deliberately: a limiter that only covered mapped routes would leave a flood of
/// 404s costing full price, and 404s are exactly what a scanner produces. Endpoint-specific policies
/// still work — the middleware asks the router which endpoint the request would have reached.
/// </para>
/// <para>
/// The lease is held for the whole request and released when it completes, which is what makes
/// <see cref="ConcurrencyRateLimitPolicy"/> mean anything. The window and bucket limiters do not
/// care when the lease goes away.
/// </para>
/// </summary>
public sealed class RateLimitMiddleware(
    Router router,
    RateLimitOptions options,
    RateLimitPolicy? globalPolicy = null,
    ILogger<RateLimitMiddleware>? logger = null
) : IHttpMiddleware
{
    readonly ILogger logger = logger ?? NullLogger<RateLimitMiddleware>.Instance;

    public async ValueTask InvokeAsync(HttpContext context, RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);

        var policy = this.PolicyFor(context);
        if (policy is null)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        // A null partition key is the partitioner saying this request is none of its business.
        var partitionKey = policy.Partitioner(context);
        if (partitionKey is null)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        using var lease = policy.Acquire(partitionKey);

        if (!lease.IsAcquired)
        {
            await this.RejectAsync(context, lease, partitionKey).ConfigureAwait(false);
            return;
        }

        if (options.IncludeRateLimitHeaders)
            AddLimitHeaders(context.Response, lease);

        await next(context).ConfigureAwait(false);
    }

    async ValueTask RejectAsync(HttpContext context, RateLimitLease lease, string partitionKey)
    {
        this.logger.LogInformation(
            "Rate limited {Method} {Path} for partition {Partition}",
            context.Request.Method,
            context.Request.Path,
            partitionKey
        );

        if (options.OnRejected is { } onRejected)
        {
            await onRejected(context, lease).ConfigureAwait(false);
            return;
        }

        var response = context.Response;
        response.StatusCode = options.RejectionStatusCode;

        if (options.IncludeRateLimitHeaders)
            AddLimitHeaders(response, lease);

        if (options.IncludeRetryAfterHeader && lease.RetryAfter is { } retryAfter)
        {
            // Seconds, rounded up: rounding down tells a client to come back while it would still
            // be refused, which produces exactly the retry storm the header exists to prevent.
            var seconds = Math.Max(1, (long)Math.Ceiling(retryAfter.TotalSeconds));
            response.Headers.Set(HeaderNames.RetryAfter, seconds.ToString(CultureInfo.InvariantCulture));
        }

        response.ContentLength = 0;
        await response.StartAsync(context.RequestAborted).ConfigureAwait(false);
    }

    static void AddLimitHeaders(HttpResponse response, RateLimitLease lease)
    {
        if (lease.Limit is { } limit)
            response.Headers.Set("X-RateLimit-Limit", limit.ToString(CultureInfo.InvariantCulture));

        if (lease.Remaining is { } remaining)
            response.Headers.Set("X-RateLimit-Remaining", remaining.ToString(CultureInfo.InvariantCulture));
    }

    RateLimitPolicy? PolicyFor(HttpContext context)
    {
        var metadata = EndpointResolver.Resolve(router, context)?.GetMetadata<RateLimitMetadata>();

        if (metadata is { Disabled: true })
            return null;

        if (metadata?.PolicyName is { } name)
            return options.GetPolicy(name);

        return globalPolicy ?? options.GlobalPolicy;
    }
}
