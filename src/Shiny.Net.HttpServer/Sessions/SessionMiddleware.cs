using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shiny.Net.HttpServer.Security;

namespace Shiny.Net.HttpServer.Sessions;

/// <summary>How sessions are carried and how long they last.</summary>
public sealed class SessionOptions
{
    public string CookieName { get; set; } = ".shiny.session";

    public string CookiePath { get; set; } = "/";

    public string? CookieDomain { get; set; }

    /// <summary>
    /// Protects the session id in the cookie.
    /// <para>
    /// Required. An id sent in the clear can be read from a log or a proxy and replayed — session
    /// fixation is the oldest trick there is, and encrypting the cookie is what closes it.
    /// </para>
    /// </summary>
    public TicketProtector Protector { get; set; } = null!;

    /// <summary>
    /// How long a session survives without being touched. Every request that loads it starts the
    /// clock again.
    /// </summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(20);

    public bool HttpOnly { get; set; } = true;

    public CookieSecurePolicy SecurePolicy { get; set; } = CookieSecurePolicy.SameAsRequest;

    /// <summary>
    /// <c>Lax</c>: the cookie rides top-level navigations but not cross-site posts, which is most of
    /// CSRF gone without an anti-forgery token.
    /// </summary>
    public SameSiteMode SameSite { get; set; } = SameSiteMode.Lax;

    internal CookieOptions BuildCookieOptions(HttpContext context) => new()
    {
        Path = this.CookiePath,
        Domain = this.CookieDomain,
        HttpOnly = this.HttpOnly,
        SameSite = this.SameSite,
        Secure = this.SecurePolicy switch
        {
            CookieSecurePolicy.Always => true,
            CookieSecurePolicy.Never => false,
            _ => context.Request.IsHttps
        },

        // Deliberately no Expires: a session cookie dies with the browser session, and the store's
        // idle timeout is what actually bounds its life.
        Expires = null
    };
}

/// <summary>
/// A session bound to one request.
/// <para>
/// Everything is lazy. The store is not touched until something is read or written, and no cookie
/// is issued for a visitor whose session stayed empty — otherwise every request to a static file
/// would mint a session nobody asked for.
/// </para>
/// </summary>
sealed class Session(string id, ISessionStore store, TimeSpan idleTimeout) : ISession
{
    SessionData? data;
    bool loaded;
    bool changed;

    public string Id { get; } = id;

    public bool IsAvailable => this.loaded;

    /// <summary>True when this session was created by this request rather than loaded from a cookie.</summary>
    public bool IsNew { get; init; }

    /// <summary>True while there are unsaved writes.</summary>
    public bool HasChanged => this.changed;

    /// <summary>
    /// True once anything has been written, and it stays true after a commit.
    /// <para>
    /// Distinct from <see cref="HasChanged"/> on purpose: the cookie is issued from an
    /// <c>OnStarting</c> callback, which runs after the commit has cleared the dirty flag. Deciding
    /// on the dirty flag meant a request that wrote a session and then failed issued no cookie, so
    /// the next request started a new session and the write was invisible.
    /// </para>
    /// </summary>
    public bool WasWritten { get; private set; }

    public IReadOnlyCollection<string> Keys
    {
        get
        {
            this.EnsureLoaded();
            return (IReadOnlyCollection<string>)this.data!.Values.Keys;
        }
    }

    public async ValueTask LoadAsync(CancellationToken cancellationToken = default)
    {
        if (this.loaded)
            return;

        this.data = await store.LoadAsync(this.Id, cancellationToken).ConfigureAwait(false) ?? new SessionData();
        this.loaded = true;
    }

    public bool TryGetValue(string key, [NotNullWhen(true)] out byte[]? value)
    {
        ArgumentNullException.ThrowIfNull(key);

        this.EnsureLoaded();

        return this.data!.Values.TryGetValue(key, out value);
    }

    public void Set(string key, byte[] value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);

        this.EnsureLoaded();

        this.data!.Values[key] = value;
        this.MarkChanged();
    }

    public void Remove(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        this.EnsureLoaded();

        if (this.data!.Values.Remove(key))
            this.MarkChanged();
    }

    public void Clear()
    {
        this.EnsureLoaded();

        if (this.data!.Values.Count == 0)
            return;

        this.data.Values.Clear();
        this.MarkChanged();
    }

    void MarkChanged()
    {
        this.changed = true;
        this.WasWritten = true;
    }

    public async ValueTask CommitAsync(CancellationToken cancellationToken = default)
    {
        if (!this.loaded)
            return;

        if (this.changed)
        {
            await store.SaveAsync(this.Id, this.data!, idleTimeout, cancellationToken).ConfigureAwait(false);
            this.changed = false;

            return;
        }

        // Read but not written: the visitor is active, so the idle timeout restarts without the
        // cost of rewriting the contents.
        if (!this.IsNew)
            await store.RefreshAsync(this.Id, idleTimeout, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Loading is asynchronous, but the accessors are not — a synchronous read on a session that was
    /// never loaded would otherwise block, and blocking on the request thread is how a server stops
    /// answering under load. So it is a clear error instead.
    /// </summary>
    void EnsureLoaded()
    {
        if (!this.loaded)
            throw new InvalidOperationException(
                $"The session has not been loaded. Await {nameof(ISession)}.{nameof(ISession.LoadAsync)} first, " +
                "or add the session middleware, which loads it for every request that has one."
            );
    }
}

/// <summary>
/// Loads a session for each request and saves it afterwards.
/// <para>
/// Ordinary middleware rather than something bolted onto routing: whether a request has a session
/// does not depend on which endpoint it reaches, and a handler that wants one should not have to
/// declare it.
/// </para>
/// </summary>
public sealed class SessionMiddleware(SessionOptions options, ISessionStore store) : IHttpMiddleware
{
    readonly SessionOptions options = Validate(options);

    public async ValueTask InvokeAsync(HttpContext context, RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var (id, isNew) = this.ResolveSessionId(context);
        var session = new Session(id, store, this.options.IdleTimeout) { IsNew = isNew };

        context.Session = session;

        // Published for the scoped ISession registration to find. Cleared in the finally below,
        // because contexts are pooled and the next request on this connection reuses this one.
        HttpContextAccessor.Set(context);

        // Loaded up front so handlers can use the session synchronously — the whole point of an
        // injected ISession is that a handler does not have to remember to await anything.
        await session.LoadAsync(context.RequestAborted).ConfigureAwait(false);

        // Committed here rather than only after the pipeline, because the body is flushed as the
        // handler writes it — so a client could receive its response, immediately make a second
        // request on another connection, and read a session the store had not been told about yet.
        // Running before the first byte goes out closes that race; the commit after the pipeline
        // then only has to catch writes made later.
        context.Response.OnStarting(async () =>
        {
            await session.CommitAsync(context.RequestAborted).ConfigureAwait(false);

            if (session.WasWritten && isNew)
                this.IssueCookie(context, id);
        });

        try
        {
            await next(context).ConfigureAwait(false);
        }
        finally
        {
            // Committed even when the handler threw: a session written before the failure is state
            // the user already caused, and losing it would be a second, quieter bug.
            await session.CommitAsync(CancellationToken.None).ConfigureAwait(false);

            HttpContextAccessor.Set(null);
        }
    }

    (string Id, bool IsNew) ResolveSessionId(HttpContext context)
    {
        if (context.Request.Cookies[this.options.CookieName] is { Length: > 0 } cookie
            && this.options.Protector.Unprotect(cookie) is { } ticket
            && ticket.Principal.FindFirst("sid")?.Value is { Length: > 0 } id
            && !ticket.HasExpired(DateTimeOffset.UtcNow))
            return (id, false);

        // 256 bits from a cryptographic source. A guessable session id is the same failure as a
        // guessable password, with none of the warning signs.
        return (Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(), true);
    }

    void IssueCookie(HttpContext context, string id)
    {
        if (context.Response.HasStarted)
            return;

        var now = DateTimeOffset.UtcNow;

        // The id travels inside the same protected envelope the auth cookie uses, so it is
        // encrypted and tamper-evident without a second mechanism to get right.
        var ticket = new AuthenticationTicket(
            new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim("sid", id)],
                "Session"
            )),
            now,
            now + this.options.IdleTimeout
        );

        context.Response.Cookies.Append(
            this.options.CookieName,
            this.options.Protector.Protect(ticket),
            this.options.BuildCookieOptions(context)
        );
    }

    static SessionOptions Validate(SessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.Protector is null)
            throw new InvalidOperationException(
                $"{nameof(SessionOptions)}.{nameof(SessionOptions.Protector)} is required — a session id sent " +
                "in the clear can be lifted from a log and replayed."
            );

        return options;
    }
}

/// <summary>Registering sessions.</summary>
public static class SessionExtensionsForRegistration
{
    /// <summary>
    /// Registers sessions with an in-memory store.
    /// <code>
    /// builder.Services.AddSessions(o =>
    /// {
    ///     o.Protector = new TicketProtector(keyBytes);
    ///     o.IdleTimeout = TimeSpan.FromMinutes(30);
    /// });
    ///
    /// app.UseSessions();
    /// </code>
    /// <para>
    /// <see cref="ISession"/> is registered as scoped, so a handler or endpoint class can take one
    /// in its constructor and never reach for <see cref="HttpContext"/>.
    /// </para>
    /// </summary>
    public static IServiceCollection AddSessions(this IServiceCollection services, Action<SessionOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.TryAddSingleton(_ =>
        {
            var options = new SessionOptions();
            configure(options);

            return options;
        });

        services.TryAddSingleton<ISessionStore>(_ => new InMemorySessionStore());
        services.TryAddSingleton(sp => new SessionMiddleware(
            sp.GetRequiredService<SessionOptions>(),
            sp.GetRequiredService<ISessionStore>()
        ));

        // Resolved from the request's context rather than constructed here: the middleware owns the
        // session's lifetime, and two ISession instances for one request would each hold half the
        // state.
        services.TryAddScoped(sp => sp.GetRequiredService<IHttpContextAccessor>().HttpContext?.Session
            ?? throw new InvalidOperationException(
                "There is no session on this request. Add app.UseSessions() to the pipeline."
            ));

        services.TryAddSingleton<IHttpContextAccessor, HttpContextAccessor>();

        return services;
    }

    /// <summary>Registers sessions with a store of your own.</summary>
    public static IServiceCollection AddSessions<TStore>(
        this IServiceCollection services,
        Func<IServiceProvider, TStore> storeFactory,
        Action<SessionOptions> configure
    ) where TStore : class, ISessionStore
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(storeFactory);

        services.AddSingleton<ISessionStore>(storeFactory);

        return services.AddSessions(configure);
    }

    /// <summary>
    /// Loads and saves a session on every request. Put it before anything that reads one, and after
    /// authentication if a handler keys session state by user.
    /// </summary>
    public static HttpServer UseSessions(this HttpServer server)
    {
        ArgumentNullException.ThrowIfNull(server);

        var middleware = server.Services?.GetService<SessionMiddleware>()
            ?? throw new InvalidOperationException(
                $"Call services.{nameof(AddSessions)}(…) before {nameof(UseSessions)}."
            );

        return server.Use(middleware);
    }

    /// <summary>Sessions for a server built without a container.</summary>
    public static HttpServer UseSessions(this HttpServer server, SessionOptions options, ISessionStore? store = null)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(options);

        return server.Use(new SessionMiddleware(options, store ?? new InMemorySessionStore()));
    }
}
