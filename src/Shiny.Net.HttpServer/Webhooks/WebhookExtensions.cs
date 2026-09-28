using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shiny.Net.HttpServer.Routing;

namespace Shiny.Net.HttpServer.Webhooks;

/// <summary>Registering named webhook verifiers and shared webhook settings.</summary>
public static class WebhookServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="WebhookOptions"/>. Only needed for named verifiers, or for settings every
    /// webhook endpoint shares — <c>MapWebhook</c> with a verifier inline works without it.
    /// </summary>
    public static ShinyHttpServerBuilder AddWebhooks(this ShinyHttpServerBuilder builder, Action<WebhookOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        OptionsRegistration.Configure<WebhookOptions>(builder.Services, configure);
        return builder;
    }
}

/// <summary>Mapping webhook receivers and putting verification in the pipeline.</summary>
public static class HttpServerWebhookExtensions
{
    /// <summary>
    /// Maps a <c>POST</c> webhook receiver that only runs for deliveries whose signature verifies.
    /// <code>
    /// app.MapWebhook("/hooks/github", WebhookSignature.GitHub(secret), async (WebhookContext ctx) =>
    /// {
    ///     if (ctx.EventType == "push")
    ///         await builds.QueueAsync(ctx.ReadFromJson(AppJson.Default.PushEvent)!);
    /// });
    /// </code>
    /// <para>
    /// The body is buffered (up to <see cref="WebhookOptions.MaxBodySize"/>), verified over its exact
    /// bytes, and handed to the handler as <see cref="WebhookContext.Body"/>. A delivery that fails is
    /// answered 401 with a problem body that does not say which check failed; the reason is logged.
    /// A handler that writes nothing answers 200, which is all any sender wants.
    /// </para>
    /// <para>
    /// Verification is part of the route itself rather than a separate middleware, so there is no
    /// ordering to get wrong and nothing to forget to install.
    /// </para>
    /// </summary>
    /// <param name="server">The server to map the route on.</param>
    /// <param name="pattern">The route template; the route answers <c>POST</c> only.</param>
    /// <param name="verifier">Checks the sender's signature, e.g. <see cref="WebhookSignature.GitHub"/>.</param>
    /// <param name="handler">Runs only for a verified delivery.</param>
    /// <param name="configure">Adjusts the shared <see cref="WebhookOptions"/> for this endpoint alone — a larger body limit, duplicate suppression.</param>
    public static RouteEndpointBuilder MapWebhook(
        this HttpServer server,
        string pattern,
        IWebhookVerifier verifier,
        Func<WebhookContext, Task> handler,
        Action<WebhookOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(handler);

        return server.MapWebhookCore(pattern, verifier, async ctx => await handler(ctx).ConfigureAwait(false), configure);
    }

    /// <summary><see cref="MapWebhook(HttpServer, string, IWebhookVerifier, Func{WebhookContext, Task}, Action{WebhookOptions}?)"/> for a handler that returns a result.</summary>
    public static RouteEndpointBuilder MapWebhook(
        this HttpServer server,
        string pattern,
        IWebhookVerifier verifier,
        Func<WebhookContext, Task<IResult>> handler,
        Action<WebhookOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(handler);

        return server.MapWebhookCore(pattern, verifier, Execute(handler), configure);
    }

    /// <summary>Maps a webhook receiver against a verifier registered by name with <c>AddWebhooks</c>.</summary>
    public static RouteEndpointBuilder MapWebhook(
        this HttpServer server,
        string pattern,
        string verifierName,
        Func<WebhookContext, Task> handler,
        Action<WebhookOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrEmpty(verifierName);

        return server.MapWebhook(pattern, SharedOptions(server.Services).GetVerifier(verifierName), handler, configure);
    }

    /// <summary>Maps a webhook receiver against a named verifier, for a handler that returns a result.</summary>
    public static RouteEndpointBuilder MapWebhook(
        this HttpServer server,
        string pattern,
        string verifierName,
        Func<WebhookContext, Task<IResult>> handler,
        Action<WebhookOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrEmpty(verifierName);

        return server.MapWebhook(pattern, SharedOptions(server.Services).GetVerifier(verifierName), handler, configure);
    }

    /// <summary>Maps a webhook receiver in a module or route group.</summary>
    public static RouteEndpointBuilder MapWebhook(
        this IEndpointRouteBuilder routes,
        string pattern,
        IWebhookVerifier verifier,
        Func<WebhookContext, Task> handler,
        Action<WebhookOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(handler);

        var (options, logger) = Prepare(routes.Services, configure);
        var builder = routes.Map(HttpMethods.Post, pattern, CreateDelegate(verifier, options, logger, async ctx => await handler(ctx).ConfigureAwait(false)));

        return builder.WithMetadata(new WebhookMetadata { Verifier = verifier });
    }

    /// <summary>Maps a webhook receiver in a module or route group, for a handler that returns a result.</summary>
    public static RouteEndpointBuilder MapWebhook(
        this IEndpointRouteBuilder routes,
        string pattern,
        IWebhookVerifier verifier,
        Func<WebhookContext, Task<IResult>> handler,
        Action<WebhookOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(handler);

        var (options, logger) = Prepare(routes.Services, configure);
        var builder = routes.Map(HttpMethods.Post, pattern, CreateDelegate(verifier, options, logger, Execute(handler)));

        return builder.WithMetadata(new WebhookMetadata { Verifier = verifier });
    }

    /// <summary>
    /// Verifies deliveries to every endpoint marked with <c>[RequireWebhookSignature]</c> or
    /// <c>.RequireWebhookSignature(…)</c>. Runs after routing; other endpoints are untouched.
    /// <para>
    /// Put it after anything that must see every request unaltered and before nothing in particular —
    /// it only replaces the request body with a buffered copy of itself.
    /// </para>
    /// </summary>
    public static HttpServer UseWebhookVerification(this HttpServer server)
    {
        ArgumentNullException.ThrowIfNull(server);

        var logger = server.Services?.GetService<ILoggerFactory>()?.CreateLogger<WebhookVerificationMiddleware>();
        return server.UseAfterRouting(new WebhookVerificationMiddleware(SharedOptions(server.Services), logger));
    }

    /// <summary>
    /// Requires a verified signature on this route, from a verifier registered by name. Enforced by
    /// <see cref="UseWebhookVerification"/>.
    /// </summary>
    public static RouteEndpointBuilder RequireWebhookSignature(this RouteEndpointBuilder builder, string verifierName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(verifierName);

        return builder.WithMetadata(new WebhookMetadata { VerifierName = verifierName });
    }

    /// <summary>
    /// Requires a verified signature on this route, from the given verifier. Enforced by
    /// <see cref="UseWebhookVerification"/>.
    /// </summary>
    public static RouteEndpointBuilder RequireWebhookSignature(this RouteEndpointBuilder builder, IWebhookVerifier verifier)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(verifier);

        return builder.WithMetadata(new WebhookMetadata { Verifier = verifier });
    }

    /// <summary>
    /// Requires a verified signature on the most recently mapped route.
    /// <code>
    /// app.MapPost("/hooks/stripe", Handler).RequireWebhookSignature("stripe");
    /// </code>
    /// </summary>
    public static HttpServer RequireWebhookSignature(this HttpServer server, string verifierName)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrEmpty(verifierName);

        LastEndpoint(server).WithMetadata(new WebhookMetadata { VerifierName = verifierName });
        return server;
    }

    /// <summary>Requires a verified signature, from the given verifier, on the most recently mapped route.</summary>
    public static HttpServer RequireWebhookSignature(this HttpServer server, IWebhookVerifier verifier)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(verifier);

        LastEndpoint(server).WithMetadata(new WebhookMetadata { Verifier = verifier });
        return server;
    }

    static RouteEndpointBuilder MapWebhookCore(
        this HttpServer server,
        string pattern,
        IWebhookVerifier verifier,
        Func<WebhookContext, ValueTask> handler,
        Action<WebhookOptions>? configure
    )
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(verifier);

        var (options, logger) = Prepare(server.Services, configure);

        // The metadata describes the route (and stops UseWebhookVerification from doing the work a
        // second time); the delegate is what enforces it.
        var endpoint = server.MapRoute(
            HttpMethods.Post,
            pattern,
            CreateDelegate(verifier, options, logger, handler),
            new WebhookMetadata { Verifier = verifier }
        );

        return new RouteEndpointBuilder(endpoint);
    }

    static RequestDelegate CreateDelegate(
        IWebhookVerifier verifier,
        WebhookOptions options,
        ILogger logger,
        Func<WebhookContext, ValueTask> handler
    )
    {
        ArgumentNullException.ThrowIfNull(verifier);

        return ctx => WebhookPipeline.RunAsync(ctx, verifier, options, logger, c => handler(c.GetWebhook()));
    }

    static Func<WebhookContext, ValueTask> Execute(Func<WebhookContext, Task<IResult>> handler)
        => async webhook =>
        {
            var result = await handler(webhook).ConfigureAwait(false);
            if (result is not null)
                await result.ExecuteAsync(webhook.HttpContext).ConfigureAwait(false);
        };

    static (WebhookOptions Options, ILogger Logger) Prepare(IServiceProvider? services, Action<WebhookOptions>? configure)
    {
        var options = SharedOptions(services);

        if (configure is not null)
        {
            options = options.CloneSettings();
            configure(options);
        }

        ILogger logger = services?.GetService<ILoggerFactory>()?.CreateLogger<WebhookVerificationMiddleware>()
            ?? (ILogger)NullLogger<WebhookVerificationMiddleware>.Instance;

        return (options, logger);
    }

    static WebhookOptions SharedOptions(IServiceProvider? services)
        => services?.GetService<WebhookOptions>() ?? new WebhookOptions();

    static RouteEndpoint LastEndpoint(HttpServer server)
    {
        if (server.Router.Endpoints.Count == 0)
            throw new InvalidOperationException(
                "RequireWebhookSignature applies to the most recently mapped route, and no route has been mapped yet."
            );

        return server.Router.Endpoints[^1];
    }
}
