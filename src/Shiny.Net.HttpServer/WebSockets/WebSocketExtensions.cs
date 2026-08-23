using System.Security.Cryptography;
using System.Text;

namespace Shiny.Net.HttpServer.WebSockets;

/// <summary>Options for accepting a WebSocket.</summary>
public sealed class WebSocketAcceptOptions
{
    /// <summary>
    /// Sub-protocols this endpoint speaks, most preferred first. The first one the client also
    /// offered is selected and echoed back.
    /// </summary>
    public IList<string> SupportedSubProtocols { get; } = [];

    /// <summary>
    /// Largest message assembled from fragments. A peer that keeps sending continuation frames is
    /// otherwise an unbounded allocation.
    /// </summary>
    public long MaxMessageLength { get; set; } = 4 * 1024 * 1024;

    /// <summary>
    /// Offers permessage-deflate (RFC 7692) when the client asks for it.
    /// <para>
    /// On by default: the messages an app sends over a socket are almost always JSON, and a
    /// tunnelled or cellular socket is where the saving lands. Compression is per message with no
    /// context takeover in either direction, so a socket costs no persistent compression window.
    /// </para>
    /// </summary>
    public bool EnablePerMessageDeflate { get; set; } = true;

    /// <summary>Messages smaller than this are sent uncompressed — deflate makes them bigger.</summary>
    public int CompressionThreshold { get; set; } = 256;

    /// <summary>
    /// How often to ping an idle peer. Null switches keepalive off.
    /// <para>
    /// A dropped mobile connection does not close the socket; it goes quiet. Without this the
    /// server holds the connection open for a peer that is never coming back.
    /// </para>
    /// </summary>
    public TimeSpan? KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How many pings may go unanswered before the socket is torn down.</summary>
    public int MissedPingsBeforeClosing { get; set; } = 2;
}

/// <summary>Accepting WebSocket connections.</summary>
public static class WebSocketExtensions
{
    // RFC 6455 §1.3. The magic string exists so a cache or proxy cannot accidentally produce a
    // valid-looking handshake from a replayed response.
    const string HandshakeGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    /// <summary>True when this request is a WebSocket upgrade this server can accept.</summary>
    public static bool IsWebSocketRequest(this HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return HttpMethods.IsGet(request.Method)
            && HasToken(request.Headers.GetFirst(HeaderNames.Connection), "upgrade")
            && string.Equals(request.Headers.GetFirst(HeaderNames.Upgrade), "websocket", StringComparison.OrdinalIgnoreCase)
            && request.Headers.GetFirst(HeaderNames.SecWebSocketKey) is { Length: > 0 };
    }

    /// <summary>
    /// Completes the handshake and hands back the socket.
    /// <para>
    /// The handler owns the socket for as long as it uses it: the connection is torn down when the
    /// handler returns, so a receive loop belongs inside it rather than on a background task.
    /// </para>
    /// <code>
    /// app.MapGet("/ws", async ctx =>
    /// {
    ///     if (!ctx.Request.IsWebSocketRequest())
    ///     {
    ///         ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
    ///         return;
    ///     }
    ///
    ///     await using var socket = await ctx.AcceptWebSocketAsync();
    ///     while (await socket.ReceiveAsync() is { } message)
    ///         await socket.SendAsync(message.Text);
    /// });
    /// </code>
    /// </summary>
    public static async ValueTask<WebSocket> AcceptWebSocketAsync(
        this HttpContext context,
        WebSocketAcceptOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(context);

        options ??= new WebSocketAcceptOptions();

        var request = context.Request;

        if (!request.IsWebSocketRequest())
            throw new InvalidOperationException("This request is not a WebSocket upgrade.");

        // Version 13 is the only one RFC 6455 defines. Anything else gets told which one to use
        // rather than a bare rejection.
        var version = request.Headers.GetFirst(HeaderNames.SecWebSocketVersion);
        if (version != "13")
        {
            context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
            context.Response.Headers[HeaderNames.SecWebSocketVersion] = "13";

            throw new InvalidOperationException($"Unsupported WebSocket version '{version}'.");
        }

        var transport = context.Transport
            ?? throw new InvalidOperationException("This connection cannot be upgraded.");

        var key = request.Headers.GetFirst(HeaderNames.SecWebSocketKey)!;
        var subProtocol = SelectSubProtocol(request.Headers.GetFirst(HeaderNames.SecWebSocketProtocol), options);

        var deflate = options.EnablePerMessageDeflate
            ? PerMessageDeflate.Negotiate(request.Headers.GetFirst(HeaderNames.SecWebSocketExtensions))
            : null;

        var response = context.Response;
        response.StatusCode = StatusCodes.Status101SwitchingProtocols;
        response.Headers[HeaderNames.Upgrade] = "websocket";
        response.Headers[HeaderNames.Connection] = "Upgrade";
        response.Headers[HeaderNames.SecWebSocketAccept] = ComputeAccept(key);

        if (subProtocol is not null)
            response.Headers[HeaderNames.SecWebSocketProtocol] = subProtocol;

        if (deflate is not null)
            response.Headers[HeaderNames.SecWebSocketExtensions] = deflate;

        // Flushes the 101 and its headers. Everything after this is WebSocket framing.
        await response.StartAsync(cancellationToken).ConfigureAwait(false);

        var socket = new WebSocket(transport, options.MaxMessageLength)
        {
            SubProtocol = subProtocol,
            PerMessageDeflate = deflate is not null,
            CompressionThreshold = options.CompressionThreshold
        };

        if (options.KeepAliveInterval is { } interval && interval > TimeSpan.Zero)
            socket.StartKeepAlive(interval, Math.Max(1, options.MissedPingsBeforeClosing));

        return socket;
    }

    /// <summary>The <c>Sec-WebSocket-Accept</c> value for a client key.</summary>
    public static string ComputeAccept(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        var hash = SHA1.HashData(Encoding.ASCII.GetBytes(key + HandshakeGuid));
        return Convert.ToBase64String(hash);
    }

    static string? SelectSubProtocol(string? requested, WebSocketAcceptOptions options)
    {
        if (options.SupportedSubProtocols.Count == 0 || requested is not { Length: > 0 })
            return null;

        var offered = requested.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        // The server's preference order wins, which is the side that actually knows what it can do.
        foreach (var supported in options.SupportedSubProtocols)
        {
            if (offered.Contains(supported, StringComparer.OrdinalIgnoreCase))
                return supported;
        }

        return null;
    }

    /// <summary>
    /// The Connection header is a comma-separated list, and browsers send "keep-alive, Upgrade" —
    /// so a plain equality check misses the upgrade on most real requests.
    /// </summary>
    static bool HasToken(string? header, string token)
    {
        if (header is not { Length: > 0 })
            return false;

        foreach (var part in header.Split(','))
        {
            if (part.Trim().Equals(token, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
