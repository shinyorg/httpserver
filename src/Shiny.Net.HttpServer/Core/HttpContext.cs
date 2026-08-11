using System.Security.Claims;
using Shiny.Net.HttpServer.Routing;
using Shiny.Net.HttpServer.Security;

namespace Shiny.Net.HttpServer;

/// <summary>
/// Everything a handler needs for one request/response exchange. Deliberately shaped after
/// ASP.NET Core's <c>HttpContext</c>.
/// <para>
/// Instances are pooled and reused across requests on the same connection, so do not hold onto a
/// context (or its request/response/items) past the end of your handler.
/// </para>
/// </summary>
public sealed class HttpContext
{
    Dictionary<object, object?>? items;

    // One per context rather than one shared static: contexts are pooled per connection, so this
    // costs an allocation per connection, and nothing can mutate one request's principal into
    // another's.
    readonly ClaimsPrincipal anonymous = new(new ClaimsIdentity());

    internal HttpContext()
    {
        this.Request = new HttpRequest(this);
        this.Response = new HttpResponse(this);
        this.Connection = new ConnectionInfo();
        this.User = this.anonymous;
    }

    public HttpRequest Request { get; }

    public HttpResponse Response { get; }

    /// <summary>Transport facts: remote IP and port, local endpoint, TLS state.</summary>
    public ConnectionInfo Connection { get; }

    /// <summary>
    /// Services scoped to this request. A fresh <c>IServiceScope</c> is created before the pipeline
    /// runs and disposed after it completes, so <c>Scoped</c> registrations behave exactly as they
    /// do in ASP.NET Core — one instance per request, shared by everything handling it.
    /// </summary>
    public IServiceProvider RequestServices { get; internal set; } = EmptyServiceProvider.Instance;

    /// <summary>
    /// Cancelled when the client disconnects or the request times out. Pass it into any async work
    /// you start on behalf of the request.
    /// </summary>
    public CancellationToken RequestAborted { get; internal set; }

    /// <summary>The endpoint the router selected, or null when no route matched.</summary>
    public Endpoint? Endpoint { get; internal set; }

    /// <summary>
    /// Who is making this request. An unauthenticated request gets an anonymous principal rather
    /// than null, so <c>ctx.User.Identity?.IsAuthenticated</c> is always a safe question to ask.
    /// Populated by <see cref="AuthenticationMiddleware"/>.
    /// </summary>
    public ClaimsPrincipal User { get; set; }

    /// <summary>How the caller was identified, and why it failed if it did.</summary>
    public AuthenticationInfo Authentication { get; } = new();

    /// <summary>
    /// Per-request scratch space for passing state between middleware. Allocated on first use.
    /// </summary>
    public IDictionary<object, object?> Items => this.items ??= new Dictionary<object, object?>();

    /// <summary>
    /// Per-visitor state that outlives this request, or null when the session middleware is not in
    /// the pipeline.
    /// <para>
    /// Prefer injecting <see cref="Sessions.ISession"/> — a handler that takes one in its
    /// constructor is testable without a context at all. This is here for middleware, which has a
    /// context and no container scope of its own.
    /// </para>
    /// </summary>
    public Sessions.ISession? Session { get; internal set; }

    /// <summary>Aborts the connection without attempting a graceful response.</summary>
    public void Abort() => this.AbortAction?.Invoke();

    internal Action? AbortAction { get; set; }

    /// <summary>
    /// The raw transport, for handlers that take the connection over entirely — a WebSocket
    /// upgrade, or any other protocol switch. Null when nothing can be upgraded.
    /// </summary>
    internal Transports.IConnection? Transport { get; set; }

    internal void Reset()
    {
        this.Request.Reset();
        this.Response.Reset();
        this.RequestServices = EmptyServiceProvider.Instance;
        this.RequestAborted = default;
        this.Endpoint = null;
        this.User = this.anonymous;
        this.Authentication.Reset();
        this.Session = null;
        this.items?.Clear();
    }
}

/// <summary>
/// Used when the server was constructed without a service provider. Every resolution fails with a
/// message that points at the actual fix rather than a bare null.
/// </summary>
sealed class EmptyServiceProvider : IServiceProvider
{
    public static readonly EmptyServiceProvider Instance = new();

    public object? GetService(Type serviceType) => null;
}
