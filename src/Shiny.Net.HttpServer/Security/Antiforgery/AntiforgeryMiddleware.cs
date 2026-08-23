using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Shiny.Net.HttpServer.Security;

/// <summary>What an endpoint said about antiforgery, attached to it as metadata.</summary>
public sealed class AntiforgeryMetadata
{
    /// <summary>True when the endpoint asked to be checked even though the default would not have.</summary>
    public bool Required { get; set; }

    /// <summary>True when the endpoint opted out.</summary>
    public bool Disabled { get; set; }
}

/// <summary>
/// Rejects unsafe requests that did not present a valid antiforgery token.
/// <para>
/// The check is skipped for a request that carries no cookies at all, and that is the whole
/// distinction worth understanding: CSRF is an attack on <em>ambient</em> credentials — a cookie
/// the browser attaches whether or not the page meant to. A caller holding a bearer token has to
/// attach it deliberately, and an attacker's page cannot. Checking those would cost every API
/// client a token exchange to prevent an attack that cannot happen to them.
/// </para>
/// <para>
/// This matters here more than it looks: this server ships a file browser and a WebDAV UI, and
/// putting either behind cookie authentication without this is a delete button any page on the
/// internet can press.
/// </para>
/// </summary>
public sealed class AntiforgeryMiddleware(IAntiforgery antiforgery) : IHttpMiddleware
{
    readonly IAntiforgery antiforgery = antiforgery ?? throw new ArgumentNullException(nameof(antiforgery));

    public async ValueTask InvokeAsync(HttpContext context, RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var metadata = context.Endpoint?.GetMetadata<AntiforgeryMetadata>();

        if (this.ShouldValidate(context, metadata) && !this.antiforgery.Validate(context))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;

            await context.Response
                .WriteTextAsync("Missing or invalid antiforgery token.", cancellationToken: context.RequestAborted)
                .ConfigureAwait(false);

            return;
        }

        await next(context).ConfigureAwait(false);
    }

    bool ShouldValidate(HttpContext context, AntiforgeryMetadata? metadata)
    {
        if (metadata is { Disabled: true })
            return false;

        if (metadata is { Required: true })
            return true;

        // GET, HEAD, OPTIONS and TRACE are supposed to change nothing, and a token requirement on
        // them would break every link into the app.
        if (!IsUnsafe(context.Request.Method))
            return false;

        return context.Request.Cookies.Count > 0;
    }

    static bool IsUnsafe(string method)
        => HttpMethods.IsPost(method)
            || HttpMethods.IsPut(method)
            || HttpMethods.IsPatch(method)
            || HttpMethods.IsDelete(method);
}

/// <summary>Registering antiforgery.</summary>
public static class AntiforgeryServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IAntiforgery"/>.
    /// <code>
    /// builder.Services.AddAntiforgery(o => o.SecureCookie = true);
    /// </code>
    /// </summary>
    public static ShinyHttpServerBuilder AddAntiforgery(this ShinyHttpServerBuilder builder, Action<AntiforgeryOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton(_ =>
        {
            var options = new AntiforgeryOptions();
            configure?.Invoke(options);

            return options;
        });

        builder.Services.TryAddSingleton<IAntiforgery>(sp => new Antiforgery(sp.GetRequiredService<AntiforgeryOptions>()));

        return builder;
    }
}

/// <summary>Putting antiforgery in the pipeline.</summary>
public static class HttpServerAntiforgeryExtensions
{
    /// <summary>
    /// Validates antiforgery tokens on cookie-bearing unsafe requests.
    /// <para>
    /// Runs after routing so an endpoint can opt in or out, and after authentication so the check
    /// happens on a request that would otherwise have succeeded.
    /// </para>
    /// </summary>
    public static HttpServer UseAntiforgery(this HttpServer server)
    {
        ArgumentNullException.ThrowIfNull(server);

        var antiforgery = server.Services?.GetService<IAntiforgery>()
            ?? throw new InvalidOperationException(
                "UseAntiforgery needs the antiforgery service. Register it with builder.AddAntiforgery()."
            );

        return server.UseAfterRouting(new AntiforgeryMiddleware(antiforgery));
    }

    /// <summary>Validates tokens using a service built elsewhere.</summary>
    public static HttpServer UseAntiforgery(this HttpServer server, IAntiforgery antiforgery)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(antiforgery);

        return server.UseAfterRouting(new AntiforgeryMiddleware(antiforgery));
    }

    /// <summary>
    /// Requires a token on the most recently mapped route even when the default would not have —
    /// an endpoint reachable by a bearer token that you want checked anyway.
    /// </summary>
    public static HttpServer ValidateAntiforgery(this HttpServer server)
    {
        ArgumentNullException.ThrowIfNull(server);

        LastEndpointMetadata(server).Required = true;
        return server;
    }

    /// <summary>Exempts the most recently mapped route — a webhook, a device callback, an upload from a native client.</summary>
    public static HttpServer DisableAntiforgery(this HttpServer server)
    {
        ArgumentNullException.ThrowIfNull(server);

        LastEndpointMetadata(server).Disabled = true;
        return server;
    }

    static AntiforgeryMetadata LastEndpointMetadata(HttpServer server)
    {
        if (server.Router.Endpoints.Count == 0)
            throw new InvalidOperationException(
                "ValidateAntiforgery applies to the most recently mapped route, and no route has been mapped yet."
            );

        var endpoint = server.Router.Endpoints[^1];
        var metadata = endpoint.GetMetadata<AntiforgeryMetadata>();

        if (metadata is null)
        {
            metadata = new AntiforgeryMetadata();
            endpoint.WithMetadata(metadata);
        }

        return metadata;
    }
}
