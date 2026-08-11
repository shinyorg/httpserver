using System.Security.Claims;

namespace Shiny.Net.HttpServer.Grpc;

/// <summary>
/// Everything a method handler knows about the call it is serving: who made it, when they stop
/// caring about the answer, and the headers and trailers going back.
/// </summary>
public sealed class GrpcCallContext
{
    internal GrpcCallContext(HttpContext httpContext, string serviceName, string methodName, DateTimeOffset? deadline, CancellationToken cancellationToken)
    {
        this.HttpContext = httpContext;
        this.ServiceName = serviceName;
        this.MethodName = methodName;
        this.Deadline = deadline;
        this.CancellationToken = cancellationToken;
    }

    /// <summary>The underlying request, for anything this context does not surface directly.</summary>
    public HttpContext HttpContext { get; }

    /// <summary>The fully qualified service name, e.g. <c>greet.Greeter</c>.</summary>
    public string ServiceName { get; }

    /// <summary>The method name, e.g. <c>SayHello</c>.</summary>
    public string MethodName { get; }

    /// <summary>The call's path, e.g. <c>/greet.Greeter/SayHello</c>.</summary>
    public string FullMethod => $"/{this.ServiceName}/{this.MethodName}";

    /// <summary>The request headers, which is where gRPC metadata arrives.</summary>
    public HeaderDictionary RequestHeaders => this.HttpContext.Request.Headers;

    /// <summary>
    /// Headers to send back with the response. Writable until the first message goes out — after
    /// that, use <see cref="ResponseTrailers"/>.
    /// </summary>
    public HeaderDictionary ResponseHeaders => this.HttpContext.Response.Headers;

    /// <summary>
    /// Metadata sent after the last message, alongside the call's status. Writable for the whole
    /// call, which makes it the only way to report something discovered while streaming.
    /// </summary>
    public HeaderDictionary ResponseTrailers { get; } = new(4);

    /// <summary>
    /// When the caller's deadline expires, or null when they set none. Past it the call is
    /// cancelled and the caller is told <see cref="GrpcStatusCode.DeadlineExceeded"/> — so a handler
    /// with expensive work left can check this and give up early rather than finish for nobody.
    /// </summary>
    public DateTimeOffset? Deadline { get; }

    /// <summary>
    /// Cancelled when the caller disconnects, the deadline passes, or the server shuts down. Pass it
    /// to everything the handler awaits.
    /// </summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>The request's DI scope.</summary>
    public IServiceProvider RequestServices => this.HttpContext.RequestServices;

    /// <summary>The authenticated caller, when authentication ran in front of this endpoint.</summary>
    public ClaimsPrincipal User => this.HttpContext.User;

    /// <summary>The caller's address, when the transport knows one.</summary>
    public string? Peer => this.HttpContext.Connection.RemoteIpAddress is { } address
        ? $"{address}:{this.HttpContext.Connection.RemotePort}"
        : null;

    /// <summary>
    /// Sends the response headers now, without waiting for the first message. Worth doing when the
    /// first message may be a long time coming and the caller is waiting to know the call was
    /// accepted at all.
    /// </summary>
    public ValueTask WriteResponseHeadersAsync(CancellationToken cancellationToken = default)
        => this.HttpContext.Response.StartAsync(cancellationToken);
}
