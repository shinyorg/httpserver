using System.Globalization;

namespace Shiny.Net.HttpServer.Security;

/// <summary>
/// The response headers a browser reads as instructions. Every one of them defaults to the strict
/// setting: an embedded server usually serves an app's own UI, and the app knows when it needs to
/// be looser.
/// </summary>
public sealed class SecurityHeaderOptions
{
    /// <summary>
    /// <c>X-Content-Type-Options: nosniff</c>. Stops a browser second-guessing a content type,
    /// which is how an uploaded "image" gets executed as script.
    /// </summary>
    public bool ContentTypeOptions { get; set; } = true;

    /// <summary>
    /// <c>X-Frame-Options</c>. <c>DENY</c> by default. Null omits it — do that only when the
    /// <see cref="ContentSecurityPolicy"/> carries a <c>frame-ancestors</c> directive instead.
    /// </summary>
    public string? FrameOptions { get; set; } = "DENY";

    /// <summary>
    /// <c>Referrer-Policy</c>. <c>no-referrer</c> by default: a device server's URLs contain
    /// identifiers, and there is nowhere they usefully leak to.
    /// </summary>
    public string? ReferrerPolicy { get; set; } = "no-referrer";

    /// <summary>
    /// <c>Content-Security-Policy</c>. Null by default, because a wrong CSP breaks a working page
    /// and only the app knows what it loads. <see cref="SelfOnlyContentSecurityPolicy"/> is a
    /// starting point for a UI that ships with the app.
    /// </summary>
    public string? ContentSecurityPolicy { get; set; }

    /// <summary><c>Permissions-Policy</c>. Null omits it.</summary>
    public string? PermissionsPolicy { get; set; }

    /// <summary><c>Cross-Origin-Opener-Policy</c>. Null omits it.</summary>
    public string? CrossOriginOpenerPolicy { get; set; }

    /// <summary>
    /// <c>Cross-Origin-Resource-Policy</c>. <c>same-origin</c> by default, which stops another
    /// site embedding this server's responses as images or scripts.
    /// </summary>
    public string? CrossOriginResourcePolicy { get; set; } = "same-origin";

    /// <summary>
    /// <c>Strict-Transport-Security</c>, emitted only over HTTPS. Null by default, and that is the
    /// right default here: HSTS is remembered by the browser for the whole host, and a phone that
    /// serves <c>localhost</c> or a LAN address over plain HTTP tomorrow would be locked out of it.
    /// Turn it on for a stable public hostname.
    /// </summary>
    public HstsOptions? Hsts { get; set; }

    /// <summary>A policy that allows only what the app itself served. A sane start for a bundled UI.</summary>
    public static string SelfOnlyContentSecurityPolicy
        => "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; frame-ancestors 'none'";
}

/// <summary>HTTP Strict Transport Security.</summary>
public sealed class HstsOptions
{
    /// <summary>How long the browser should refuse plain HTTP for this host. 30 days by default.</summary>
    public TimeSpan MaxAge { get; set; } = TimeSpan.FromDays(30);

    public bool IncludeSubDomains { get; set; }

    /// <summary>
    /// Asks for inclusion in the browsers' preload list. Effectively permanent and not undoable on
    /// any useful timescale — only for a hostname that will serve HTTPS forever.
    /// </summary>
    public bool Preload { get; set; }

    internal string ToHeaderValue()
    {
        var value = "max-age=" + ((long)this.MaxAge.TotalSeconds).ToString(CultureInfo.InvariantCulture);

        if (this.IncludeSubDomains)
            value += "; includeSubDomains";

        if (this.Preload)
            value += "; preload";

        return value;
    }
}

/// <summary>
/// Adds the browser-facing security headers.
/// <code>
/// app.UseSecurityHeaders();
/// </code>
/// <para>
/// Applied as the response starts rather than up front, so a handler that set its own CSP for one
/// page keeps it — these are defaults, not overrides.
/// </para>
/// </summary>
public sealed class SecurityHeaderMiddleware(SecurityHeaderOptions options) : IHttpMiddleware
{
    readonly SecurityHeaderOptions options = options ?? throw new ArgumentNullException(nameof(options));

    public ValueTask InvokeAsync(HttpContext context, RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;

            if (this.options.ContentTypeOptions)
                SetIfAbsent(headers, "X-Content-Type-Options", "nosniff");

            SetIfAbsent(headers, "X-Frame-Options", this.options.FrameOptions);
            SetIfAbsent(headers, "Referrer-Policy", this.options.ReferrerPolicy);
            SetIfAbsent(headers, "Content-Security-Policy", this.options.ContentSecurityPolicy);
            SetIfAbsent(headers, "Permissions-Policy", this.options.PermissionsPolicy);
            SetIfAbsent(headers, "Cross-Origin-Opener-Policy", this.options.CrossOriginOpenerPolicy);
            SetIfAbsent(headers, "Cross-Origin-Resource-Policy", this.options.CrossOriginResourcePolicy);

            // Only over TLS. A browser that is told this on a cleartext connection is entitled to
            // ignore it, and a proxy that is not is one downgrade away from doing damage.
            if (this.options.Hsts is { } hsts && context.Request.IsHttps)
                SetIfAbsent(headers, "Strict-Transport-Security", hsts.ToHeaderValue());

            return default;
        });

        return next(context);
    }

    static void SetIfAbsent(HeaderDictionary headers, string name, string? value)
    {
        if (value is { Length: > 0 } && !headers.ContainsKey(name))
            headers.Set(name, value);
    }
}

/// <summary>Wiring the security headers and the HTTPS redirect.</summary>
public static class SecurityHeaderExtensions
{
    /// <summary>
    /// Adds the browser-facing security headers to every response. Register it early — it applies
    /// to static files and error responses too, which are exactly the ones a handler will not set
    /// headers on.
    /// </summary>
    public static HttpServer UseSecurityHeaders(this HttpServer server, Action<SecurityHeaderOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(server);

        var options = new SecurityHeaderOptions();
        configure?.Invoke(options);

        return server.Use(new SecurityHeaderMiddleware(options));
    }

    /// <summary>Adds the security headers using options built elsewhere.</summary>
    public static HttpServer UseSecurityHeaders(this HttpServer server, SecurityHeaderOptions options)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(options);

        return server.Use(new SecurityHeaderMiddleware(options));
    }

    /// <summary>
    /// Redirects cleartext requests to the HTTPS endpoint.
    /// <code>
    /// app.UseHttpsRedirection();
    /// </code>
    /// <para>
    /// The port is taken from the first TLS endpoint configured, or given explicitly. A 307 keeps
    /// the method and body, so a redirected POST is still a POST — but a redirected POST has
    /// already sent its body in the clear, which is why this belongs in front of an app served to
    /// a browser and not in front of an API.
    /// </para>
    /// </summary>
    public static HttpServer UseHttpsRedirection(
        this HttpServer server,
        int? httpsPort = null,
        int statusCode = StatusCodes.Status307TemporaryRedirect
    )
    {
        ArgumentNullException.ThrowIfNull(server);

        return server.Use(async (context, next) =>
        {
            if (context.Request.IsHttps)
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            var port = httpsPort ?? TlsPortOf(server);
            if (port is null)
            {
                // Nothing to redirect to. Better to serve the request than to send the caller in
                // a circle back to the endpoint it is already on.
                await next(context).ConfigureAwait(false);
                return;
            }

            var host = context.Request.Host is { Length: > 0 } value
                ? value.Split(':')[0]
                : context.Connection.LocalIpAddress?.ToString() ?? "localhost";

            var target = port == 443
                ? $"https://{host}{context.Request.Path}{context.Request.QueryString}"
                : $"https://{host}:{port}{context.Request.Path}{context.Request.QueryString}";

            context.Response.StatusCode = statusCode;
            context.Response.Headers.Set(HeaderNames.Location, target);
            context.Response.ContentLength = 0;

            await context.Response.StartAsync(context.RequestAborted).ConfigureAwait(false);
        });
    }

    static int? TlsPortOf(HttpServer server)
    {
        foreach (var endpoint in server.Options.ResolveEndpoints())
        {
            if (endpoint.Https is not null)
                return endpoint.Port;
        }

        return null;
    }
}
