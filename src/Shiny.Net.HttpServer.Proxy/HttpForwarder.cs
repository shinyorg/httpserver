using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Shiny.Net.HttpServer.Transports;

namespace Shiny.Net.HttpServer.Proxy;

/// <summary>
/// Forwards one exchange to one destination.
/// <para>
/// This is the whole proxy, minus the deciding: clusters, load balancing and health checks all end
/// up here with a destination already chosen. Bodies stream in both directions — nothing is
/// buffered, so a large upload through a proxy route costs the same memory as a small one — and a
/// WebSocket handshake is completed against the upstream and then handed the connection outright.
/// </para>
/// </summary>
public static class HttpForwarder
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

    /// <summary>
    /// Forwards this request to a single destination, for a handler that does its own routing.
    /// <code>
    /// app.MapGet("/thing/{id}", ctx => HttpForwarder.ForwardAsync(ctx, "http://192.168.1.50").AsTask());
    /// </code>
    /// </summary>
    public static ValueTask<ProxyError> ForwardAsync(
        HttpContext context,
        string destinationPrefix,
        ProxyOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPrefix);

        return ForwardAsync(context, new Uri(destinationPrefix, UriKind.Absolute), options, cancellationToken);
    }

    /// <summary>Forwards this request to a single destination.</summary>
    public static ValueTask<ProxyError> ForwardAsync(
        HttpContext context,
        Uri destinationPrefix,
        ProxyOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(destinationPrefix);

        options ??= new ProxyOptions();

        return SendAsync(
            context,
            new ProxyDestination(destinationPrefix.Authority, destinationPrefix),
            options,
            options.Client ?? SharedClient(),
            null,
            cancellationToken
        );
    }

    /// <summary>
    /// The client used when a route did not bring its own — redirects off, cookies off, no automatic
    /// decompression. Shared, because a handler per route is a connection pool per route.
    /// </summary>
    internal static HttpMessageInvoker SharedClient() => Default.Instance;

    internal static async ValueTask<ProxyError> SendAsync(
        HttpContext context,
        ProxyDestination destination,
        ProxyOptions options,
        HttpMessageInvoker client,
        ILogger? logger,
        CancellationToken cancellationToken
    )
    {
        var upgrading = options.ForwardUpgrades && IsUpgradeRequest(context) && context.Transport is not null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, cancellationToken);

        // An upgraded connection lives as long as the two peers want it to, so the timeout covers
        // the handshake only. Applying a request timeout to a WebSocket would close it on the dot.
        if (!upgrading)
            timeout.CancelAfter(options.Timeout);

        using var outbound = await BuildRequestAsync(context, destination, options, upgrading).ConfigureAwait(false);

        HttpResponseMessage upstream;
        try
        {
            upstream = await client.SendAsync(outbound, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            return ProxyError.RequestCanceled;
        }
        catch (OperationCanceledException ex)
        {
            logger?.LogWarning("Proxy to {Destination} timed out", outbound.RequestUri);
            await FailAsync(context, options, ProxyError.RequestTimeout, ex).ConfigureAwait(false);

            return ProxyError.RequestTimeout;
        }
        catch (HttpRequestException ex)
        {
            // The upstream is unreachable or answered nonsense. That is a 502 — the caller's
            // request was fine, ours was not answered.
            logger?.LogWarning(ex, "Proxy to {Destination} failed", outbound.RequestUri);
            await FailAsync(context, options, ProxyError.Request, ex).ConfigureAwait(false);

            return ProxyError.Request;
        }

        using (upstream)
        {
            options.AfterReceive?.Invoke(upstream, context);

            if (upgrading && upstream.StatusCode == HttpStatusCode.SwitchingProtocols)
                return await UpgradeAsync(context, upstream, options, logger).ConfigureAwait(false);

            context.Response.StatusCode = (int)upstream.StatusCode;

            CopyHeaders(upstream.Headers, context.Response.Headers);
            CopyHeaders(upstream.Content.Headers, context.Response.Headers);

            // Whatever framing the upstream used described its connection, not ours.
            context.Response.Headers.Remove(HeaderNames.TransferEncoding);

            if (options.Transforms.HasResponseTransforms)
                await options.Transforms.ApplyResponseAsync(new ResponseTransformContext(context, upstream)).ConfigureAwait(false);

            if (HttpMethods.IsHead(context.Request.Method))
            {
                await context.Response.StartAsync(context.RequestAborted).ConfigureAwait(false);
                return ProxyError.None;
            }

            try
            {
                await using var body = await upstream.Content.ReadAsStreamAsync(context.RequestAborted).ConfigureAwait(false);
                await body.CopyToAsync(context.Response.Body, context.RequestAborted).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                return ProxyError.RequestCanceled;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                // The head has already gone out, so there is no status code left to say this with.
                // Aborting is the only honest signal that the body the caller is reading is truncated.
                logger?.LogWarning(ex, "Proxy response body from {Destination} ended early", outbound.RequestUri);
                context.Abort();

                return ProxyError.ResponseBody;
            }

            return ProxyError.None;
        }
    }

    /// <summary>True when the caller is asking to switch protocols on an HTTP/1.1 connection.</summary>
    internal static bool IsUpgradeRequest(HttpContext context)
        => context.Request.Headers.GetFirst(HeaderNames.Upgrade) is { Length: > 0 }
            && HasToken(context.Request.Headers.GetFirst(HeaderNames.Connection), "upgrade")
            && string.Equals(context.Request.Protocol, HttpProtocols.Http11, StringComparison.Ordinal);

    static async ValueTask<HttpRequestMessage> BuildRequestAsync(
        HttpContext context,
        ProxyDestination destination,
        ProxyOptions options,
        bool upgrading
    )
    {
        var request = context.Request;
        var message = new HttpRequestMessage(new HttpMethod(request.Method), destination.Address);

        // An upgrade is an HTTP/1.1 concept and the handler has to be told exactly that, or the
        // handler negotiates 2.0 and there is no connection to hand over at the end of it.
        message.Version = upgrading ? HttpVersion.Version11 : options.RequestVersion;
        message.VersionPolicy = upgrading ? HttpVersionPolicy.RequestVersionExact : options.VersionPolicy;

        if (!upgrading && request.HasBody)
        {
            // Streamed, not buffered: the point of forwarding an upload is that it never lands
            // anywhere on the way through.
            message.Content = new StreamContent(request.Body);
        }

        foreach (var header in request.Headers)
        {
            if (string.Equals(header.Key, HeaderNames.Host, StringComparison.OrdinalIgnoreCase))
                continue;

            // The handshake headers *are* the upgrade. Stripping them as hop-by-hop, which they
            // otherwise are, would turn a WebSocket request into an ordinary GET.
            if (!upgrading && IsHopByHop(header.Key))
                continue;

            var values = new string[header.Value.Count];
            for (var i = 0; i < header.Value.Count; i++)
                values[i] = header.Value[i] ?? string.Empty;

            if (!message.Headers.TryAddWithoutValidation(header.Key, values))
                message.Content?.Headers.TryAddWithoutValidation(header.Key, values);
        }

        var host = options.RewriteHost ? null : request.Host;

        ApplyForwardedHeaders(context, message, options);

        var rewritten = options.RewriteUri?.Invoke(context);
        var basis = rewritten ?? destination.Address;

        var path = rewritten is not null ? rewritten.AbsolutePath : DefaultPath(context, destination.Address);
        var query = new ProxyQuery(rewritten is not null ? rewritten.Query : request.QueryString);

        if (options.Transforms.HasRequestTransforms)
        {
            var transform = new RequestTransformContext(context, message, destination, path, query);
            await options.Transforms.ApplyRequestAsync(transform).ConfigureAwait(false);

            path = transform.Path;
            host = transform.Host ?? host;
        }

        message.RequestUri = new UriBuilder(basis)
        {
            Path = path.Length == 0 ? "/" : path,
            Query = query.ToQueryString()
        }.Uri;

        if (host is { Length: > 0 })
            message.Headers.TryAddWithoutValidation(HeaderNames.Host, host);

        options.BeforeSend?.Invoke(message, context);

        return message;
    }

    static void ApplyForwardedHeaders(HttpContext context, HttpRequestMessage message, ProxyOptions options)
    {
        var mode = options.Transforms.ForwardedHeaders;
        if (mode == ForwardedHeadersMode.Off)
            return;

        var request = context.Request;

        // Set replaces whatever the caller sent, which is the point: a caller that supplies its own
        // X-Forwarded-For is claiming to be a proxy, and believing it is how an IP allow-list is
        // walked around.
        if (mode == ForwardedHeadersMode.Set)
        {
            message.Headers.Remove(HeaderNames.XForwardedFor);
            message.Headers.Remove(HeaderNames.XForwardedProto);
            message.Headers.Remove(HeaderNames.XForwardedHost);
        }

        if (context.Connection.RemoteIpAddress is { } remote)
            message.Headers.TryAddWithoutValidation(HeaderNames.XForwardedFor, remote.ToString());

        message.Headers.TryAddWithoutValidation(HeaderNames.XForwardedProto, request.Scheme);

        if (request.Host is { Length: > 0 } original)
            message.Headers.TryAddWithoutValidation(HeaderNames.XForwardedHost, original);
    }

    /// <summary>
    /// The destination, plus whatever the catch-all captured.
    /// <para>
    /// The remainder is read from the template's catch-all parameter by name rather than by
    /// guessing at the captured values, so <c>/api/{tenant}/{*rest}</c> forwards <c>rest</c> and
    /// leaves <c>tenant</c> where it was. A route with no catch-all forwards to the destination
    /// exactly as given, which is what a one-to-one mapping of a single endpoint wants.
    /// </para>
    /// </summary>
    static string DefaultPath(HttpContext context, Uri destination)
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

        return remainder.Length > 0 ? basePath + "/" + remainder.TrimStart('/') : basePath;
    }

    static async ValueTask<ProxyError> UpgradeAsync(
        HttpContext context,
        HttpResponseMessage upstream,
        ProxyOptions options,
        ILogger? logger
    )
    {
        var transport = context.Transport;
        if (transport is null)
            return ProxyError.Upgrade;

        context.Response.StatusCode = StatusCodes.Status101SwitchingProtocols;

        // Everything the upstream sent, hop-by-hop included: on a 101 the Connection and Upgrade
        // headers are the handshake rather than connection bookkeeping, and Sec-WebSocket-Accept is
        // a signature the client checks.
        foreach (var header in upstream.Headers)
        {
            foreach (var value in header.Value)
                context.Response.Headers.Append(header.Key, value);
        }

        if (options.Transforms.HasResponseTransforms)
            await options.Transforms.ApplyResponseAsync(new ResponseTransformContext(context, upstream)).ConfigureAwait(false);

        await context.Response.StartAsync(context.RequestAborted).ConfigureAwait(false);

        Stream upstreamStream;
        try
        {
            upstreamStream = await upstream.Content.ReadAsStreamAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or ObjectDisposedException)
        {
            logger?.LogWarning(ex, "Upgraded connection to the upstream could not be taken over");
            context.Abort();

            return ProxyError.Upgrade;
        }

        await using (upstreamStream)
        {
            await PumpAsync(transport, upstreamStream, context.RequestAborted).ConfigureAwait(false);
        }

        return ProxyError.None;
    }

    /// <summary>
    /// Copies bytes both ways until either side stops. Neither direction is trusted to end first —
    /// a WebSocket close can come from the client or the server, and whichever arrives, the other
    /// pump has to be woken up rather than left blocked on a read that will never return.
    /// </summary>
    static async Task PumpAsync(IConnection transport, Stream upstream, CancellationToken cancellationToken)
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var toUpstream = ClientToUpstreamAsync(transport, upstream, stopping.Token);
        var toClient = UpstreamToClientAsync(upstream, transport, stopping.Token);

        await Task.WhenAny(toUpstream, toClient).ConfigureAwait(false);

        await stopping.CancelAsync().ConfigureAwait(false);

        try
        {
            await Task.WhenAll(toUpstream, toClient).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Both pumps swallow their own transport failures; anything reaching here is the
            // cancellation that ended the one still running.
        }
    }

    static async Task ClientToUpstreamAsync(IConnection transport, Stream upstream, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await transport.Input.ReadAsync(cancellationToken).ConfigureAwait(false);
                var buffer = result.Buffer;

                foreach (var segment in buffer)
                    await upstream.WriteAsync(segment, cancellationToken).ConfigureAwait(false);

                if (!buffer.IsEmpty)
                    await upstream.FlushAsync(cancellationToken).ConfigureAwait(false);

                transport.Input.AdvanceTo(buffer.End);

                if (result.IsCompleted)
                    break;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    static async Task UpstreamToClientAsync(Stream upstream, IConnection transport, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var buffer = transport.Output.GetMemory(8 * 1024);

                var read = await upstream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;

                transport.Output.Advance(read);

                var flush = await transport.Output.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (flush.IsCompleted || flush.IsCanceled)
                    break;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
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

    static bool HasToken(string? headerValue, string token)
    {
        if (headerValue is null)
            return false;

        foreach (var part in headerValue.Split(','))
        {
            if (part.Trim().Equals(token, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    internal static async ValueTask FailAsync(HttpContext context, ProxyOptions options, ProxyError error, Exception? exception)
    {
        if (context.Response.HasStarted)
        {
            context.Abort();
            return;
        }

        if (options.OnError is { } handler)
        {
            await handler(context, error, exception).ConfigureAwait(false);

            if (!context.Response.HasStarted)
                await context.Response.StartAsync(CancellationToken.None).ConfigureAwait(false);

            return;
        }

        context.Response.StatusCode = error switch
        {
            ProxyError.RequestTimeout => StatusCodes.Status504GatewayTimeout,
            ProxyError.NoAvailableDestination => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status502BadGateway
        };
        context.Response.ContentLength = 0;

        await context.Response.StartAsync(CancellationToken.None).ConfigureAwait(false);
    }

    static class Default
    {
        public static readonly HttpMessageInvoker Instance = new(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,

            // The caller's client decides what to do about compression. Decompressing here and
            // recompressing on the way out would spend the device's battery to change nothing.
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(10)
        });
    }
}
