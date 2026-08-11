using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Shiny.Net.HttpServer.Mcp;

/// <summary>
/// The Streamable HTTP transport, as three verbs on one path.
/// <para>
/// <c>POST</c> carries a JSON-RPC message in and gets either an SSE stream of responses back or a
/// bare 202. <c>GET</c> opens the stream the server uses to speak first — sampling, elicitation,
/// notifications. <c>DELETE</c> ends the session. Everything protocol-shaped below this line is the
/// MCP SDK's <c>StreamableHttpServerTransport</c>; what is here is the HTTP around it, which is
/// the part the SDK's own ASP.NET Core package would otherwise supply.
/// </para>
/// </summary>
sealed class McpHttpHandler(McpHttpSessionManager sessions, ILoggerFactory? loggerFactory = null)
{
    // ILoggerFactory rather than ILogger<T>, and optional: the core server runs happily with no
    // logging registered at all, and an endpoint that refused to resolve for want of a logger
    // would be a gratuitous difference.
    readonly ILogger logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<McpHttpHandler>();

    public const string SessionIdHeader = "Mcp-Session-Id";
    public const string ProtocolVersionHeader = "MCP-Protocol-Version";

    const string EventStream = "text/event-stream";
    const string Json = "application/json";

    // Resolved once: it is the same type info for every request, and looking it up per message
    // would be pure overhead on the hot path.
    static readonly JsonTypeInfo<JsonRpcMessage> MessageTypeInfo =
        (JsonTypeInfo<JsonRpcMessage>)McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(JsonRpcMessage));

    McpHttpOptions Options => sessions.Options;

    // ---- POST: the client speaks ----

    public async ValueTask PostAsync(HttpContext context)
    {
        if (!this.ApplyOriginPolicy(context))
        {
            await ForbiddenOriginAsync(context).ConfigureAwait(false);
            return;
        }

        // The spec requires both, because the server chooses per response which one it is sending
        // and a client that only advertised one has no way to be told about the switch.
        if (this.Options.ValidateAcceptHeader && !(Accepts(context.Request, Json) && Accepts(context.Request, EventStream)))
        {
            await WriteErrorAsync(
                context,
                StatusCodes.Status406NotAcceptable,
                "Accept must include both application/json and text/event-stream."
            ).ConfigureAwait(false);
            return;
        }

        JsonRpcMessage? message;
        try
        {
            message = await JsonSerializer
                .DeserializeAsync(context.Request.Body, MessageTypeInfo, context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            this.logger.LogDebug(ex, "Rejected an MCP request with an unparseable body");
            await WriteErrorAsync(
                context,
                StatusCodes.Status400BadRequest,
                "The request body is not a JSON-RPC message.",
                McpErrorCode.ParseError
            ).ConfigureAwait(false);
            return;
        }

        if (message is null)
        {
            await WriteErrorAsync(
                context,
                StatusCodes.Status400BadRequest,
                "The request body is empty.",
                McpErrorCode.ParseError
            ).ConfigureAwait(false);
            return;
        }

        var (session, owned) = await this.ResolveSessionAsync(context, message).ConfigureAwait(false);
        if (session is null)
            return;

        session.Enter();
        try
        {
            // Set before the first write and never after: the response starts on that write, and a
            // header set afterwards throws rather than silently going nowhere.
            context.Response.ContentType = EventStream;
            context.Response.Headers.Set(HeaderNames.CacheControl, "no-cache");
            context.Response.Headers.Set("X-Accel-Buffering", "no");

            var wroteResponse = await session.Transport
                .HandlePostRequestAsync(message, context.Response.Body, context.RequestAborted)
                .ConfigureAwait(false);

            if (!wroteResponse && !context.Response.HasStarted)
            {
                // Nothing came back, which means the client sent only notifications or responses.
                // 202 with no body is what the spec asks for, so the content type has to go.
                context.Response.StatusCode = StatusCodes.Status202Accepted;
                context.Response.ContentType = null;
                context.Response.ContentLength = 0;

                await context.Response.StartAsync(context.RequestAborted).ConfigureAwait(false);
            }
        }
        finally
        {
            session.Exit();

            if (owned)
                await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Finds the session this request belongs to, creating one when the request is an initialize.
    /// Returns a null session when it has already written the failure response.
    /// </summary>
    async ValueTask<(McpHttpSession? Session, bool Owned)> ResolveSessionAsync(HttpContext context, JsonRpcMessage message)
    {
        if (this.Options.Stateless)
            return (sessions.CreateTransient(), true);

        var sessionId = context.Request.Headers.GetFirst(SessionIdHeader);

        if (sessionId is not null)
        {
            if (sessions.TryGet(sessionId, out var existing))
                return (existing, false);

            // 404 rather than 400: the spec has clients treat it as "start over with a fresh
            // initialize", which is exactly the right recovery after a server restart.
            await WriteErrorAsync(
                context,
                StatusCodes.Status404NotFound,
                "Unknown or expired session. Send an initialize request to start a new one."
            ).ConfigureAwait(false);

            return (null, false);
        }

        // An initialize with no session id is a client asking for one. It gets a real session, and
        // with it everything a session buys: state that survives between calls, and a GET stream
        // the server can speak down.
        if (message is JsonRpcRequest request && request.Method == RequestMethods.Initialize)
        {
            var created = sessions.Create();
            if (created is null)
            {
                await WriteErrorAsync(
                    context,
                    StatusCodes.Status429TooManyRequests,
                    "Too many open MCP sessions."
                ).ConfigureAwait(false);

                return (null, false);
            }

            // Set now, before the transport writes the InitializeResult: this is the response that
            // tells the client its session id, and after the first body byte it is too late.
            context.Response.Headers.Set(SessionIdHeader, created.Id);

            return (created, false);
        }

        // Anything else without a session id is the discovery flow: a client that skipped the
        // handshake because server/discover already told it everything it needed. Each such request
        // gets a server of its own and is answered on its own terms. Rejecting these would be
        // stricter than the protocol and would lock out every client that connects this way.
        return (sessions.CreateTransient(), true);
    }

    // ---- GET: the server speaks first ----

    public async ValueTask GetAsync(HttpContext context)
    {
        if (!this.ApplyOriginPolicy(context))
        {
            await ForbiddenOriginAsync(context).ConfigureAwait(false);
            return;
        }

        if (!this.Options.AllowServerToClientStream || this.Options.Stateless)
        {
            // 405 is the spec's "this server never initiates", and clients that see it stop asking.
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            context.Response.Headers.Set("Allow", "POST, DELETE");
            context.Response.ContentLength = 0;

            await context.Response.StartAsync(context.RequestAborted).ConfigureAwait(false);
            return;
        }

        if (this.Options.ValidateAcceptHeader && !Accepts(context.Request, EventStream))
        {
            await WriteErrorAsync(
                context,
                StatusCodes.Status406NotAcceptable,
                "Accept must include text/event-stream."
            ).ConfigureAwait(false);
            return;
        }

        var sessionId = context.Request.Headers.GetFirst(SessionIdHeader);
        if (sessionId is null || !sessions.TryGet(sessionId, out var session))
        {
            await WriteErrorAsync(
                context,
                StatusCodes.Status404NotFound,
                "Unknown or expired session."
            ).ConfigureAwait(false);
            return;
        }

        session.Enter();
        try
        {
            context.Response.ContentType = EventStream;
            context.Response.Headers.Set(HeaderNames.CacheControl, "no-cache");
            context.Response.Headers.Set("X-Accel-Buffering", "no");

            // Headers go out before anything is streamed, so the client's stream opens now rather
            // than whenever the server first has something to say.
            await context.Response.StartAsync(context.RequestAborted).ConfigureAwait(false);

            await session.Transport
                .HandleGetRequestAsync(context.Response.Body, context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The client closed the stream. That is how these end.
        }
        finally
        {
            session.Exit();
        }
    }

    // ---- DELETE: the client is done ----

    public async ValueTask DeleteAsync(HttpContext context)
    {
        if (!this.ApplyOriginPolicy(context))
        {
            await ForbiddenOriginAsync(context).ConfigureAwait(false);
            return;
        }

        var sessionId = context.Request.Headers.GetFirst(SessionIdHeader);
        if (sessionId is null || !await sessions.RemoveAsync(sessionId).ConfigureAwait(false))
        {
            await WriteErrorAsync(
                context,
                StatusCodes.Status404NotFound,
                "Unknown or expired session."
            ).ConfigureAwait(false);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status204NoContent;
        context.Response.ContentLength = 0;

        await context.Response.StartAsync(context.RequestAborted).ConfigureAwait(false);
    }

    // ---- OPTIONS: the browser asks first ----

    public async ValueTask PreflightAsync(HttpContext context)
    {
        if (!this.ApplyOriginPolicy(context))
        {
            await ForbiddenOriginAsync(context).ConfigureAwait(false);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status204NoContent;
        context.Response.Headers.Set("Access-Control-Allow-Methods", "GET, POST, DELETE, OPTIONS");
        context.Response.Headers.Set(
            "Access-Control-Allow-Headers",
            $"Content-Type, Authorization, Last-Event-ID, {SessionIdHeader}, {ProtocolVersionHeader}"
        );
        context.Response.Headers.Set("Access-Control-Max-Age", "86400");
        context.Response.ContentLength = 0;

        await context.Response.StartAsync(context.RequestAborted).ConfigureAwait(false);
    }

    // ---- Shared ----

    /// <summary>
    /// Decides whether a browser may talk to this endpoint, and stamps the CORS headers when it may.
    /// <para>
    /// A request with no <c>Origin</c> is not from a page and passes untouched — that is every
    /// native MCP client. A request with one is only allowed if it was named, because a server
    /// bound to localhost is otherwise reachable by any site the user visits, which is the whole
    /// DNS-rebinding attack the spec warns about.
    /// </para>
    /// </summary>
    bool ApplyOriginPolicy(HttpContext context)
    {
        var origin = context.Request.Headers.GetFirst("Origin");
        if (origin is null)
            return true;

        if (!this.Options.IsOriginAllowed(origin))
        {
            this.logger.LogWarning("Rejected an MCP request from origin {Origin}", origin);
            return false;
        }

        context.Response.Headers.Set("Access-Control-Allow-Origin", origin);
        context.Response.Headers.Append("Vary", "Origin");
        context.Response.Headers.Set("Access-Control-Expose-Headers", SessionIdHeader);

        return true;
    }

    static ValueTask ForbiddenOriginAsync(HttpContext context)
        => WriteErrorAsync(context, StatusCodes.Status403Forbidden, "Origin is not allowed.");

    /// <summary>
    /// Writes a JSON-RPC error. HTTP status codes are what the transport layer understands; MCP
    /// clients read the body, so a bare status with no body leaves them guessing.
    /// </summary>
    static async ValueTask WriteErrorAsync(
        HttpContext context,
        int statusCode,
        string message,
        McpErrorCode errorCode = McpErrorCode.InvalidRequest
    )
    {
        context.Response.StatusCode = statusCode;

        // Serialized through the SDK's own message type rather than hand-written: these bodies are
        // read by MCP clients, and an error response that is itself malformed is the worst possible
        // thing to hand something already having a bad time.
        var error = new JsonRpcError
        {
            Error = new JsonRpcErrorDetail { Code = (int)errorCode, Message = message }
        };

        var body = JsonSerializer.SerializeToUtf8Bytes(error, MessageTypeInfo);

        await context.Response.WriteBytesAsync(body, Json, context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether the request's <c>Accept</c> covers a media type, honouring <c>*/*</c> and
    /// <c>type/*</c>. Quality values are ignored: this is a yes/no question about whether the
    /// client can handle the response at all, not a negotiation.
    /// </summary>
    static bool Accepts(HttpRequest request, string mediaType)
    {
        if (!request.Headers.TryGetValue("Accept", out var values))
            return false;

        var slash = mediaType.IndexOf('/');

        foreach (var value in values)
        {
            if (value is null)
                continue;

            foreach (var range in value.Split(','))
            {
                var candidate = range.AsSpan().Trim();

                var parameters = candidate.IndexOf(';');
                if (parameters >= 0)
                    candidate = candidate[..parameters].TrimEnd();

                if (candidate.SequenceEqual("*/*") || candidate.Equals(mediaType, StringComparison.OrdinalIgnoreCase))
                    return true;

                if (candidate.EndsWith("/*") &&
                    candidate[..^2].Equals(mediaType.AsSpan(0, slash), StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }
}
