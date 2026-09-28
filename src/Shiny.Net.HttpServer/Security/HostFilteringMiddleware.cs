using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Shiny.Net.HttpServer.Security;

/// <summary>
/// Refuses requests addressed to a name the server does not answer to, with a 400.
/// <para>
/// The same check on every protocol: HTTP/1.1's <c>Host</c> header, and HTTP/2's and HTTP/3's
/// <c>:authority</c>, which the server publishes as <see cref="HttpRequest.Host"/>. When the two
/// differ — a <c>Host</c> header alongside <c>:authority</c>, or <c>X-Forwarded-Host</c> rewriting
/// <see cref="HttpRequest.Host"/> under <see cref="HttpServerOptions.UseForwardedHeaders"/> — both
/// have to pass. A rebinding page is same-origin as far as its browser knows, so it can set
/// <c>X-Forwarded-Host</c> to anything it likes; checking only the rewritten value would let it name
/// an allowed host and walk in.
/// </para>
/// <para>
/// Belongs first in the pipeline, ahead of the IP filter and routing: a request aimed at the wrong
/// name should not learn which routes exist.
/// </para>
/// </summary>
public sealed class HostFilteringMiddleware : IHttpMiddleware
{
    readonly HostFilteringOptions options;
    readonly HostMatcher matcher;
    readonly ILogger logger;

    public HostFilteringMiddleware(HostFilteringOptions options, ILogger<HostFilteringMiddleware>? logger = null)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.matcher = new HostMatcher(options);
        this.logger = logger ?? NullLogger<HostFilteringMiddleware>.Instance;
    }

    public async ValueTask InvokeAsync(HttpContext context, RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        if (this.matcher.AllowsEverything || (this.options.AllowTunneledConnections && context.Connection.IsTunneled))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var host = context.Request.Host;
        var header = context.Request.Headers.GetFirst(HeaderNames.Host);

        string? refused = null;
        if (!this.matcher.IsAllowed(host))
            refused = host ?? string.Empty;
        else if (header is not null && !string.Equals(header, host, StringComparison.Ordinal) && !this.matcher.IsAllowed(header))
            refused = header;

        if (refused is null)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        this.logger.LogWarning(
            "Refused {Method} {Path}: host '{Host}' is not allowed",
            context.Request.Method,
            context.Request.Path,
            refused
        );

        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status400BadRequest,
            Detail = this.options.IncludeFailureMessage
                ? (refused.Length == 0
                    ? "This server does not accept requests without a host."
                    : $"This server does not answer to the host '{refused}'.")
                : null
        };
        ProblemDetailsDefaults.ApplyDefaults(problem, context, StatusCodes.Status400BadRequest);

        await ProblemDetailsWriter.WriteResponseAsync(context, problem, context.RequestAborted).ConfigureAwait(false);
    }
}

/// <summary>Registering and installing host filtering.</summary>
public static class HostFilteringExtensions
{
    /// <summary>
    /// Registers <see cref="HostFilteringOptions"/>. Every call contributes, so a library can add
    /// its own names alongside the app's.
    /// </summary>
    public static ShinyHttpServerBuilder AddHostFiltering(
        this ShinyHttpServerBuilder builder,
        Action<HostFilteringOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(builder);

        OptionsRegistration.Configure<HostFilteringOptions>(builder.Services, configure);
        return builder;
    }

    /// <summary>
    /// Refuses requests addressed to a host the server does not answer to. Put it first.
    /// <para>
    /// Works with or without <see cref="AddHostFiltering"/>: the registered options are used when
    /// there are some, and <paramref name="configure"/> adjusts them (or fresh defaults) in place.
    /// With nothing configured at all it still admits only loopback names, IP literals and
    /// tunnelled requests — which is the DNS-rebinding protection a LAN server needs.
    /// </para>
    /// <code>
    /// app.UseHostFiltering(o => o.AllowedHosts.Add("printer.local"));
    /// </code>
    /// </summary>
    public static HttpServer UseHostFiltering(this HttpServer server, Action<HostFilteringOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(server);

        var options = server.Services?.GetService<HostFilteringOptions>() ?? new HostFilteringOptions();
        configure?.Invoke(options);

        var logger = server.Services
            ?.GetService<ILoggerFactory>()
            ?.CreateLogger<HostFilteringMiddleware>();

        return server.Use(new HostFilteringMiddleware(options, logger));
    }
}
