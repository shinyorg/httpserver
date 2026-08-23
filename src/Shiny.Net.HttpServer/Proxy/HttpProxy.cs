using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;

namespace Shiny.Net.HttpServer.Proxy;

/// <summary>Where a proxied route sends the request, and what it does to it on the way.</summary>
public sealed class ProxyOptions
{
    /// <summary>
    /// The client used for the outbound call. One is created when this is null — redirects off,
    /// cookies off, which is what a forwarder wants: following a redirect on the caller's behalf
    /// hides it from them, and a shared cookie container mixes callers together.
    /// </summary>
    public HttpMessageInvoker? Client { get; set; }

    /// <summary>How long the upstream has to answer. Exceeding it is a 504.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(100);

    /// <summary>
    /// Sends <c>X-Forwarded-For</c>, <c>-Proto</c> and <c>-Host</c> describing the original caller.
    /// On by default: without them the upstream sees this server and nothing else.
    /// </summary>
    public bool AddForwardedHeaders { get; set; } = true;

    /// <summary>
    /// Sets the outbound <c>Host</c> to the destination's. On by default, because most upstreams
    /// route on it. Turn it off to pass the caller's host through — a virtual host that has to see
    /// the original name.
    /// </summary>
    public bool RewriteHost { get; set; } = true;

    /// <summary>Builds the upstream URI. Replaces the default "destination + the catch-all remainder".</summary>
    public Func<HttpContext, Uri>? RewriteUri { get; set; }

    /// <summary>Last chance to touch the outbound request — add an upstream API key, drop a header.</summary>
    public Action<HttpRequestMessage, HttpContext>? BeforeSend { get; set; }

    /// <summary>First look at the upstream response, before it is copied back.</summary>
    public Action<HttpResponseMessage, HttpContext>? AfterReceive { get; set; }
}

/// <summary>
/// Forwards a route to another server.
/// <para>
/// The reason this belongs in an embedded server: the device is often the only thing that can reach
/// what the caller wants. A phone bridges its own loopback services to a tunnel; a Raspberry Pi
/// fronts a printer or a camera that speaks HTTP but has no TLS and no authentication; a dev
/// server serves the app and forwards <c>/api</c> to the real backend so the browser sees one
/// origin.
/// </para>
/// <code>
/// app.MapProxy("/api/{*path}", "https://api.example.com");
/// </code>
/// <para>
/// Bodies stream in both directions — nothing is buffered, so a large upload through a proxy route
/// costs the same memory as a small one. A protocol upgrade (WebSockets) is <b>not</b> forwarded:
/// the response is passed through as-is, and the connection is not handed over.
/// </para>
/// </summary>
public static class ProxyExtensions
{
    static readonly string[] HopByHop =
    [
        HeaderNames.Connection,
        HeaderNames.KeepAlive,
        HeaderNames.TransferEncoding,
        HeaderNames.Upgrade,
        HeaderNames.TE,
        HeaderNames.Trailer,
        "Proxy-Authenticate",
        "Proxy-Authorization",
        "Proxy-Connection"
    ];

    /// <summary>Forwards every method on <paramref name="pattern"/> to <paramref name="destination"/>.</summary>
    public static HttpServer MapProxy(
        this HttpServer server,
        string pattern,
        string destination,
        Action<ProxyOptions>? configure = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        return server.MapProxy(pattern, new Uri(destination, UriKind.Absolute), configure);
    }

    /// <summary>Forwards every method on <paramref name="pattern"/> to <paramref name="destination"/>.</summary>
    public static HttpServer MapProxy(
        this HttpServer server,
        string pattern,
        Uri destination,
        Action<ProxyOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(destination);

        if (!destination.IsAbsoluteUri)
            throw new ArgumentException("The proxy destination must be an absolute URI.", nameof(destination));

        var options = new ProxyOptions();
        configure?.Invoke(options);

        var client = options.Client ?? CreateClient(options);
        var logger = server.Services is { } services
            ? (ILogger?)Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
                .GetService<ILoggerFactory>(services)
                ?.CreateLogger("Shiny.Net.HttpServer.Proxy")
            : null;

        var handler = Handler(destination, options, client, logger);

        foreach (var method in new[] { HttpMethods.Get, HttpMethods.Post, HttpMethods.Put, HttpMethods.Patch, HttpMethods.Delete, HttpMethods.Head, HttpMethods.Options })
            server.Map(method, pattern, handler);

        return server;
    }

    static HttpMessageInvoker CreateClient(ProxyOptions options)
        => new(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,

            // The caller's client decides what to do about compression. Decompressing here and
            // recompressing on the way out would spend the device's battery to change nothing.
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(10)
        });

    static RequestDelegate Handler(Uri destination, ProxyOptions options, HttpMessageInvoker client, ILogger? logger)
        => async context =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            timeout.CancelAfter(options.Timeout);

            using var outbound = BuildRequest(context, destination, options);

            HttpResponseMessage upstream;
            try
            {
                upstream = await client
                    .SendAsync(outbound, timeout.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
            {
                logger?.LogWarning("Proxy to {Destination} timed out", outbound.RequestUri);
                await FailAsync(context, StatusCodes.Status504GatewayTimeout).ConfigureAwait(false);
                return;
            }
            catch (HttpRequestException ex)
            {
                // The upstream is unreachable or answered nonsense. That is a 502 — the caller's
                // request was fine, ours was not answered.
                logger?.LogWarning(ex, "Proxy to {Destination} failed", outbound.RequestUri);
                await FailAsync(context, StatusCodes.Status502BadGateway).ConfigureAwait(false);
                return;
            }

            using (upstream)
            {
                options.AfterReceive?.Invoke(upstream, context);

                context.Response.StatusCode = (int)upstream.StatusCode;

                CopyHeaders(upstream.Headers, context.Response.Headers);
                CopyHeaders(upstream.Content.Headers, context.Response.Headers);

                // Whatever framing the upstream used described its connection, not ours.
                context.Response.Headers.Remove(HeaderNames.TransferEncoding);

                if (HttpMethods.IsHead(context.Request.Method))
                {
                    await context.Response.StartAsync(context.RequestAborted).ConfigureAwait(false);
                    return;
                }

                await using var body = await upstream.Content.ReadAsStreamAsync(context.RequestAborted).ConfigureAwait(false);
                await body.CopyToAsync(context.Response.Body, context.RequestAborted).ConfigureAwait(false);
            }
        };

    static HttpRequestMessage BuildRequest(HttpContext context, Uri destination, ProxyOptions options)
    {
        var request = context.Request;

        var message = new HttpRequestMessage(new HttpMethod(request.Method), options.RewriteUri?.Invoke(context) ?? TargetUri(context, destination));

        if (request.HasBody)
        {
            // Streamed, not buffered: the point of forwarding an upload is that it never lands
            // anywhere on the way through.
            message.Content = new StreamContent(request.Body);
        }

        foreach (var header in request.Headers)
        {
            if (IsHopByHop(header.Key) || string.Equals(header.Key, HeaderNames.Host, StringComparison.OrdinalIgnoreCase))
                continue;

            var values = new string[header.Value.Count];
            for (var i = 0; i < header.Value.Count; i++)
                values[i] = header.Value[i] ?? string.Empty;

            if (!message.Headers.TryAddWithoutValidation(header.Key, values))
                message.Content?.Headers.TryAddWithoutValidation(header.Key, values);
        }

        if (!options.RewriteHost && request.Host is { Length: > 0 } host)
            message.Headers.TryAddWithoutValidation(HeaderNames.Host, host);

        if (options.AddForwardedHeaders)
        {
            if (context.Connection.RemoteIpAddress is { } remote)
                message.Headers.TryAddWithoutValidation(HeaderNames.XForwardedFor, remote.ToString());

            message.Headers.TryAddWithoutValidation(HeaderNames.XForwardedProto, request.Scheme);

            if (request.Host is { Length: > 0 } original)
                message.Headers.TryAddWithoutValidation(HeaderNames.XForwardedHost, original);
        }

        options.BeforeSend?.Invoke(message, context);

        return message;
    }

    /// <summary>
    /// The destination, plus whatever the catch-all captured, plus the query.
    /// <para>
    /// The remainder is read from the template's catch-all parameter by name rather than by
    /// guessing at the captured values, so <c>/api/{tenant}/{*rest}</c> forwards <c>rest</c> and
    /// leaves <c>tenant</c> where it was. A route with no catch-all forwards to the destination
    /// exactly as given, which is what a one-to-one mapping of a single endpoint wants.
    /// </para>
    /// </summary>
    static Uri TargetUri(HttpContext context, Uri destination)
    {
        var remainder = string.Empty;

        if (context.Endpoint is Routing.RouteEndpoint route)
        {
            foreach (var segment in route.Template.Segments)
            {
                if (segment.Kind != Routing.RouteSegmentKind.CatchAll)
                    continue;

                remainder = context.Request.RouteValues[segment.Text] ?? string.Empty;
                break;
            }
        }

        var basePath = destination.AbsolutePath.TrimEnd('/');
        var path = remainder.Length > 0 ? basePath + "/" + remainder.TrimStart('/') : basePath;

        return new UriBuilder(destination)
        {
            Path = path.Length == 0 ? "/" : path,
            Query = context.Request.QueryString?.TrimStart('?') ?? string.Empty
        }.Uri;
    }

    static void CopyHeaders(HttpHeaders source, HeaderDictionary destination)
    {
        foreach (var header in source)
        {
            if (IsHopByHop(header.Key))
                continue;

            foreach (var value in header.Value)
                destination.Append(header.Key, value);
        }
    }

    static bool IsHopByHop(string name)
    {
        foreach (var hop in HopByHop)
        {
            if (string.Equals(name, hop, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    static ValueTask FailAsync(HttpContext context, int statusCode)
    {
        if (context.Response.HasStarted)
        {
            context.Abort();
            return default;
        }

        context.Response.StatusCode = statusCode;
        context.Response.ContentLength = 0;

        return context.Response.StartAsync(CancellationToken.None);
    }
}
