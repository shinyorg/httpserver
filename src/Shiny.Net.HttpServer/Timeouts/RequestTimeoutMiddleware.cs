using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Shiny.Net.HttpServer.Timeouts;

/// <summary>
/// Cancels a request that has taken too long, and answers rather than leaving the client waiting.
/// <para>
/// Runs after routing, because the timeout is a property of the endpoint — and because the two
/// things worth exempting, an event stream and a file download, are endpoints rather than paths.
/// </para>
/// <para>
/// The server already bounds how long a client may take to send a request. This bounds the other
/// half, which is the half your own code is responsible for.
/// </para>
/// </summary>
public sealed class RequestTimeoutMiddleware(
    RequestTimeoutOptions options,
    ILogger<RequestTimeoutMiddleware>? logger = null
) : IHttpMiddleware
{
    readonly RequestTimeoutOptions options = options ?? throw new ArgumentNullException(nameof(options));
    readonly ILogger logger = logger ?? NullLogger<RequestTimeoutMiddleware>.Instance;

    public async ValueTask InvokeAsync(HttpContext context, RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var policy = this.PolicyFor(context.Endpoint?.GetMetadata<RequestTimeoutMetadata>());
        if (policy is null)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var clientAborted = context.RequestAborted;

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(clientAborted);
        timeoutSource.CancelAfter(policy.Timeout);

        context.RequestAborted = timeoutSource.Token;

        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !clientAborted.IsCancellationRequested)
        {
            // The handler noticed the token and gave up. That is cooperation, not a fault, so it
            // is answered here rather than escaping to the exception handler as a 500.
            await this.TimedOutAsync(context, policy).ConfigureAwait(false);
            return;
        }
        finally
        {
            context.RequestAborted = clientAborted;
        }

        // A handler that returned normally but ignored its token still ran over. Say so, if there
        // is still a response to say it in.
        if (timeoutSource.IsCancellationRequested && !clientAborted.IsCancellationRequested && !context.Response.HasStarted)
            await this.TimedOutAsync(context, policy).ConfigureAwait(false);
    }

    RequestTimeoutPolicy? PolicyFor(RequestTimeoutMetadata? metadata)
    {
        if (metadata is null)
            return this.options.DefaultPolicy;

        if (metadata.Disabled)
            return null;

        if (metadata.Timeout is { } timeout)
            return new RequestTimeoutPolicy(timeout);

        if (metadata.PolicyName is { } name)
            return this.options.GetPolicy(name);

        return this.options.DefaultPolicy;
    }

    async ValueTask TimedOutAsync(HttpContext context, RequestTimeoutPolicy policy)
    {
        this.logger.LogWarning(
            "{Method} {Path} exceeded its {Timeout} timeout",
            context.Request.Method,
            context.Request.Path,
            policy.Timeout
        );

        if (context.Response.HasStarted)
        {
            // Headers are already gone, so there is no status to change and no honest way to
            // finish the body. Cutting the connection is what tells the client not to trust it.
            context.Abort();
            return;
        }

        context.Response.StatusCode = policy.StatusCode;

        if (policy.OnTimeout is { } handler)
        {
            await handler(context).ConfigureAwait(false);
            return;
        }

        context.Response.ContentLength = 0;
        await context.Response.StartAsync(CancellationToken.None).ConfigureAwait(false);
    }
}

/// <summary>Registering request timeout policies.</summary>
public static class RequestTimeoutServiceCollectionExtensions
{
    /// <summary>
    /// Registers the timeout options and any named policies.
    /// <code>
    /// builder.Services.AddRequestTimeouts(o =>
    /// {
    ///     o.DefaultPolicy = new RequestTimeoutPolicy(TimeSpan.FromSeconds(30));
    ///     o.AddPolicy("reports", TimeSpan.FromMinutes(2));
    /// });
    /// </code>
    /// </summary>
    public static ShinyHttpServerBuilder AddRequestTimeouts(
        this ShinyHttpServerBuilder builder,
        Action<RequestTimeoutOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton(_ =>
        {
            var options = new RequestTimeoutOptions();
            configure?.Invoke(options);

            return options;
        });

        return builder;
    }
}

/// <summary>Putting request timeouts in the pipeline.</summary>
public static class HttpServerRequestTimeoutExtensions
{
    /// <summary>Applies the default policy and whatever individual endpoints asked for.</summary>
    public static HttpServer UseRequestTimeouts(this HttpServer server)
    {
        ArgumentNullException.ThrowIfNull(server);

        var options = server.Services?.GetService<RequestTimeoutOptions>()
            ?? throw new InvalidOperationException(
                "UseRequestTimeouts has no policies to apply. Register them with " +
                "builder.AddRequestTimeouts(o => o.DefaultPolicy = new RequestTimeoutPolicy(TimeSpan.FromSeconds(30))), " +
                "or pass a default inline."
            );

        return server.UseRequestTimeouts(options);
    }

    /// <summary>
    /// Applies one default timeout to every endpoint, with no container involved.
    /// <code>
    /// app.UseRequestTimeouts(TimeSpan.FromSeconds(30));
    /// </code>
    /// </summary>
    public static HttpServer UseRequestTimeouts(this HttpServer server, TimeSpan defaultTimeout)
    {
        ArgumentNullException.ThrowIfNull(server);

        var options = server.Services?.GetService<RequestTimeoutOptions>() ?? new RequestTimeoutOptions();
        options.DefaultPolicy = new RequestTimeoutPolicy(defaultTimeout);

        return server.UseRequestTimeouts(options);
    }

    /// <summary>Applies timeouts using options built elsewhere.</summary>
    public static HttpServer UseRequestTimeouts(this HttpServer server, RequestTimeoutOptions options)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(options);

        var logger = server.Services?.GetService<ILoggerFactory>()?.CreateLogger<RequestTimeoutMiddleware>();

        return server.UseAfterRouting(new RequestTimeoutMiddleware(options, logger));
    }

    /// <summary>
    /// Bounds the most recently mapped route.
    /// <code>
    /// app.MapGet("/report", Handler).WithRequestTimeout(TimeSpan.FromSeconds(10));
    /// </code>
    /// </summary>
    public static HttpServer WithRequestTimeout(this HttpServer server, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(server);

        LastEndpointMetadata(server).Timeout = timeout;
        return server;
    }

    /// <summary>Applies a named policy to the most recently mapped route.</summary>
    public static HttpServer WithRequestTimeout(this HttpServer server, string policyName)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrEmpty(policyName);

        LastEndpointMetadata(server).PolicyName = policyName;
        return server;
    }

    /// <summary>Exempts the most recently mapped route, the default policy included.</summary>
    public static HttpServer DisableRequestTimeout(this HttpServer server)
    {
        ArgumentNullException.ThrowIfNull(server);

        LastEndpointMetadata(server).Disabled = true;
        return server;
    }

    static RequestTimeoutMetadata LastEndpointMetadata(HttpServer server)
    {
        if (server.Router.Endpoints.Count == 0)
            throw new InvalidOperationException(
                "WithRequestTimeout applies to the most recently mapped route, and no route has been mapped yet."
            );

        var endpoint = server.Router.Endpoints[^1];
        var metadata = endpoint.GetMetadata<RequestTimeoutMetadata>();

        if (metadata is null)
        {
            metadata = new RequestTimeoutMetadata();
            endpoint.WithMetadata(metadata);
        }

        return metadata;
    }
}
