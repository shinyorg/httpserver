using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shiny.Net.HttpServer.Internal;
using Shiny.Net.HttpServer.Routing;

namespace Shiny.Net.HttpServer.Security;

/// <summary>
/// Turns callers away by address, before anything else looks at their request.
/// <para>
/// It runs ahead of routing deliberately. An address that is not welcome should not reach the
/// router, the fallback handler, or the endpoint's constructor — and a whitelist that only covered
/// mapped routes would leave every 404 answering to the whole internet, which is enough to
/// fingerprint a server.
/// </para>
/// </summary>
public sealed class IpFilterMiddleware(
    Router router,
    IpFilterOptions options,
    IpFilterPolicy? globalPolicy = null,
    ILogger<IpFilterMiddleware>? logger = null
) : IHttpMiddleware
{
    readonly ILogger logger = logger ?? NullLogger<IpFilterMiddleware>.Instance;

    public async ValueTask InvokeAsync(HttpContext context, RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);

        var policy = this.PolicyFor(context);
        if (policy is null)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var address = context.Connection.RemoteIpAddress;
        if (policy.IsAllowed(address))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        this.logger.LogWarning(
            "Blocked {Method} {Path} from {Address}",
            context.Request.Method,
            context.Request.Path,
            address?.ToString() ?? "an unknown address"
        );

        if (options.OnRejected is { } onRejected)
        {
            await onRejected(context, address).ConfigureAwait(false);
            return;
        }

        context.Response.StatusCode = options.RejectionStatusCode;
        context.Response.ContentLength = 0;
        await context.Response.StartAsync(context.RequestAborted).ConfigureAwait(false);
    }

    IpFilterPolicy? PolicyFor(HttpContext context)
    {
        var metadata = EndpointResolver.Resolve(router, context)?.GetMetadata<IpFilterMetadata>();

        if (metadata is { Disabled: true })
            return null;

        if (metadata?.PolicyName is { } name)
            return options.GetPolicy(name);

        return globalPolicy ?? options.DefaultPolicy;
    }
}
