using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Shiny.Net.HttpServer.Sessions;

namespace Shiny.Net.HttpServer.Security;

/// <summary>When the cookie is marked <c>secure</c>.</summary>
public enum CookieSecurePolicy
{
    /// <summary>Secure when the request arrived over HTTPS. The sane default for a server that serves both.</summary>
    SameAsRequest,

    /// <summary>Always. Correct in production, and it will silently break a plain-HTTP dev loop.</summary>
    Always,

    /// <summary>Never. Only for a local development server.</summary>
    Never
}

/// <summary>How the authentication cookie is issued and read back.</summary>
public sealed class CookieAuthenticationOptions
{
    /// <summary>The scheme name, reported on <c>ctx.Authentication</c>.</summary>
    public string Scheme { get; set; } = "Cookies";

    public string CookieName { get; set; } = ".shiny.auth";

    public string CookiePath { get; set; } = "/";

    public string? CookieDomain { get; set; }

    /// <summary>
    /// Protects the ticket. Required — without it the cookie would be a claim the client could
    /// rewrite, which is the difference between authentication and a suggestion.
    /// </summary>
    public TicketProtector Protector { get; set; } = null!;

    /// <summary>How long a ticket stays valid.</summary>
    public TimeSpan ExpireTimeSpan { get; set; } = TimeSpan.FromDays(14);

    /// <summary>
    /// Reissues the cookie with a fresh expiry once a request arrives past the halfway mark, so an
    /// active user is not signed out mid-session while an idle one still ages out.
    /// </summary>
    public bool SlidingExpiration { get; set; } = true;

    /// <summary>
    /// Blocks scripts from reading the cookie. On, and it should stay on: the entire value of an
    /// HttpOnly cookie is that a cross-site scripting bug cannot exfiltrate the session.
    /// </summary>
    public bool HttpOnly { get; set; } = true;

    public CookieSecurePolicy SecurePolicy { get; set; } = CookieSecurePolicy.SameAsRequest;

    /// <summary>
    /// <c>Lax</c> by default: the cookie rides top-level navigations but not cross-site form posts
    /// or subresource requests, which is most of CSRF gone for free. <c>None</c> requires
    /// <c>secure</c> and needs its own anti-forgery story.
    /// </summary>
    public SameSiteMode SameSite { get; set; } = SameSiteMode.Lax;

    /// <summary>
    /// Where to send an unauthenticated *browser* — a login page. Null answers 401 instead, which is
    /// what an API wants.
    /// </summary>
    public string? LoginPath { get; set; }

    /// <summary>Where to send an authenticated browser that is not allowed. Null answers 403.</summary>
    public string? AccessDeniedPath { get; set; }

    /// <summary>Query parameter carrying the original URL on a login redirect.</summary>
    public string ReturnUrlParameter { get; set; } = "returnUrl";

    /// <summary>
    /// Last check on a decoded ticket — has the user been deleted, has their password changed since.
    /// Return false to reject a ticket that is cryptographically fine but no longer true. To refresh the
    /// claims rather than only accept or reject them, use <see cref="OnValidatePrincipal"/>.
    /// </summary>
    public Func<AuthenticationTicket, HttpContext, ValueTask<bool>>? ValidateTicketAsync { get; set; }

    /// <summary>
    /// Runs after <see cref="ValidateTicketAsync"/> on every request that carries a valid cookie, and can
    /// replace the principal (<see cref="CookieValidatePrincipalContext.ReplacePrincipal"/>), ask for the
    /// cookie to be reissued (<see cref="CookieValidatePrincipalContext.ShouldRenew"/>) or reject it
    /// (<see cref="CookieValidatePrincipalContext.RejectPrincipal"/>).
    /// <code>
    /// o.OnValidatePrincipal = async ctx =>
    /// {
    ///     var user = await users.FindAsync(ctx.Principal.Identity!.Name!);
    ///     if (user is null) { ctx.RejectPrincipal(); return; }
    ///     if (user.Version != ctx.Principal.FindFirst("ver")?.Value)
    ///         ctx.ReplacePrincipal(user.ToPrincipal());   // reissued with fresh roles
    /// };
    /// </code>
    /// Without it, a sliding renewal copies the claims the ticket already had — a role removed from a user
    /// would stay in their cookie for as long as they kept using it.
    /// </summary>
    public Func<CookieValidatePrincipalContext, ValueTask>? OnValidatePrincipal { get; set; }

    /// <summary>
    /// What the ticket is bound to, so a ticket protected by the same <see cref="Protector"/> for another
    /// purpose — a session cookie, another cookie scheme — is not accepted as this scheme's sign-in.
    /// </summary>
    internal string Purpose => "Shiny.Net.HttpServer.Cookies:" + this.Scheme;

    internal CookieOptions BuildCookieOptions(HttpContext context, DateTimeOffset? expires)
    {
        return new CookieOptions
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
            Expires = expires
        };
    }
}

/// <summary>
/// What <see cref="CookieAuthenticationOptions.OnValidatePrincipal"/> sees and can change for one request.
/// </summary>
public sealed class CookieValidatePrincipalContext(HttpContext httpContext, AuthenticationTicket ticket)
{
    public HttpContext HttpContext { get; } = httpContext;

    /// <summary>The ticket as it arrived in the cookie.</summary>
    public AuthenticationTicket Ticket { get; } = ticket;

    /// <summary>The principal the request will be authenticated as. Starts as the ticket's.</summary>
    public ClaimsPrincipal Principal { get; private set; } = ticket.Principal;

    /// <summary>When true, the cookie is reissued with a fresh expiry on this response.</summary>
    public bool ShouldRenew { get; set; }

    /// <summary>True once <see cref="RejectPrincipal"/> has been called.</summary>
    public bool IsRejected { get; private set; }

    /// <summary>
    /// Authenticates the request as <paramref name="principal"/> instead, and reissues the cookie carrying
    /// it — so the change sticks on later requests and on every sliding renewal after.
    /// </summary>
    public void ReplacePrincipal(ClaimsPrincipal principal)
        => this.Principal = principal ?? throw new ArgumentNullException(nameof(principal));

    /// <summary>Refuses the cookie: the request is anonymous and the cookie is deleted.</summary>
    public void RejectPrincipal() => this.IsRejected = true;
}

/// <summary>
/// Authenticates a caller from an encrypted cookie.
/// <para>
/// The scheme a browser wants: the session survives a page load without JavaScript holding a token,
/// and an <c>HttpOnly</c> cookie cannot be read by a script that gets injected into the page. The
/// cookie carries the claims themselves rather than a session id, so there is no server-side store
/// to keep — at the cost of not being revocable before it expires, which
/// <see cref="CookieAuthenticationOptions.ValidateTicketAsync"/> exists to solve.
/// </para>
/// <code>
/// builder.AddAuthentication().AddCookie(o =>
/// {
///     o.Protector = new TicketProtector(keyBytes);
///     o.LoginPath = "/login";
/// });
///
/// app.MapPost("/login", async ctx =>
/// {
///     var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "ada")], "Cookies"));
///     await ctx.SignInAsync(user);
/// });
/// </code>
/// </summary>
public sealed class CookieAuthenticationHandler(CookieAuthenticationOptions options)
    : IAuthenticationHandler, IAuthenticationChallenge
{
    readonly CookieAuthenticationOptions options = Validate(options);

    public string Scheme => this.options.Scheme;

    public CookieAuthenticationOptions Options => this.options;

    public async ValueTask<AuthenticateResult> AuthenticateAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var cookie = context.Request.Cookies[this.options.CookieName];
        if (string.IsNullOrEmpty(cookie))
            return AuthenticateResult.NoResult();

        var ticket = this.options.Protector.Unprotect(cookie, this.options.Purpose);

        // A session cookie from before tickets carried a purpose decrypts under a shared protector, and its
        // "Session" identity is authenticated. It is a session id, never a sign-in.
        if (ticket is not null && ticket.Principal.Identities.Any(i => i.AuthenticationType == SessionMiddleware.SessionIdentityType))
            ticket = null;

        if (ticket is null)
        {
            // Could be tampering, could be a key rotated out from under an old cookie. Either way
            // it is dead weight on every subsequent request, so it goes.
            this.DeleteCookie(context);

            return AuthenticateResult.Fail("The authentication cookie is not valid.");
        }

        var now = DateTimeOffset.UtcNow;

        if (ticket.HasExpired(now))
        {
            this.DeleteCookie(context);
            return AuthenticateResult.Fail("The authentication cookie has expired.");
        }

        if (this.options.ValidateTicketAsync is { } validate
            && !await validate(ticket, context).ConfigureAwait(false))
        {
            this.DeleteCookie(context);
            return AuthenticateResult.Fail("The authentication cookie was rejected.");
        }

        var principal = ticket.Principal;
        var renew = false;

        if (this.options.OnValidatePrincipal is { } onValidate)
        {
            var validation = new CookieValidatePrincipalContext(context, ticket);
            await onValidate(validation).ConfigureAwait(false);

            if (validation.IsRejected)
            {
                this.DeleteCookie(context);
                return AuthenticateResult.Fail("The authentication cookie was rejected.");
            }

            principal = validation.Principal;
            renew = validation.ShouldRenew;
        }

        this.Reissue(context, ticket, principal, renew, now);

        return AuthenticateResult.Success(principal);
    }

    /// <summary>Issues the cookie. The response must not have started.</summary>
    public void SignIn(HttpContext context, ClaimsPrincipal principal, bool persistent = true)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(principal);

        var now = DateTimeOffset.UtcNow;

        // A non-persistent cookie has no Expires and dies with the browser session — but the ticket
        // inside it still carries an absolute expiry, so a captured cookie cannot outlive it. The ticket
        // also records which it was, so a renewal does not turn one into the other.
        this.AppendCookie(context, new AuthenticationTicket(principal, now, now + this.options.ExpireTimeSpan, persistent));

        context.User = principal;
        context.Authentication.Scheme = this.Scheme;
    }

    /// <summary>
    /// Redirects a browser to the login page instead of handing it a 401 it cannot act on.
    /// <para>
    /// Only for requests that look like navigations. A fetch call expecting JSON gets the status
    /// code, because following a redirect to an HTML login form would surface as a parse error a
    /// long way from the actual problem.
    /// </para>
    /// </summary>
    public ValueTask<bool> TryChallengeAsync(HttpContext context, bool forbidden)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Response.HasStarted || !WantsHtml(context.Request))
            return new ValueTask<bool>(false);

        if (forbidden)
        {
            if (this.options.AccessDeniedPath is not { Length: > 0 } denied)
                return new ValueTask<bool>(false);

            context.Response.Redirect(denied);
            return new ValueTask<bool>(true);
        }

        if (this.options.LoginPath is not { Length: > 0 } login)
            return new ValueTask<bool>(false);

        // The original target rides along so the login page can send them back. Encoded, because it
        // is a URL inside a URL.
        var target = context.Request.Path + context.Request.QueryString;
        var location = $"{login}?{this.options.ReturnUrlParameter}={Uri.EscapeDataString(target)}";

        context.Response.Redirect(location);

        return new ValueTask<bool>(true);
    }

    static bool WantsHtml(HttpRequest request)
    {
        // A navigation says so twice: browsers send Sec-Fetch-Mode on every request, and every
        // browser has said text/html since long before that existed.
        if (request.Headers.GetFirst("Sec-Fetch-Mode") is { Length: > 0 } mode)
            return mode.Equals("navigate", StringComparison.OrdinalIgnoreCase);

        return request.Headers.GetFirst(HeaderNames.Accept) is { Length: > 0 } accept
            && accept.Contains("text/html", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Clears the cookie.</summary>
    public void SignOut(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        this.DeleteCookie(context);

        context.User = new ClaimsPrincipal(new ClaimsIdentity());
        context.Authentication.Scheme = null;
    }

    /// <summary>
    /// Reissues the cookie when it is more than halfway through its life, when validation asked for a
    /// renewal, or when validation replaced the principal.
    /// <para>
    /// Halfway rather than every request: rewriting the cookie on each call would put a
    /// <c>Set-Cookie</c> on every response, which breaks shared caching and costs bytes on a
    /// connection that may be metered. A replaced principal without a renewal keeps the ticket's
    /// original lifetime, so refreshing claims never extends a sign-in on its own.
    /// </para>
    /// </summary>
    void Reissue(HttpContext context, AuthenticationTicket ticket, ClaimsPrincipal principal, bool renew, DateTimeOffset now)
    {
        if (context.Response.HasStarted)
            return;

        var lifetime = ticket.ExpiresUtc - ticket.IssuedUtc;
        var halfSpent = this.options.SlidingExpiration && lifetime > TimeSpan.Zero && now - ticket.IssuedUtc >= lifetime / 2;

        if (renew || halfSpent)
        {
            this.AppendCookie(context, new AuthenticationTicket(principal, now, now + this.options.ExpireTimeSpan, ticket.IsPersistent));
            return;
        }

        if (!ReferenceEquals(principal, ticket.Principal))
            this.AppendCookie(context, new AuthenticationTicket(principal, ticket.IssuedUtc, ticket.ExpiresUtc, ticket.IsPersistent));
    }

    void AppendCookie(HttpContext context, AuthenticationTicket ticket)
        => context.Response.Cookies.Append(
            this.options.CookieName,
            this.options.Protector.Protect(ticket, this.options.Purpose),
            this.options.BuildCookieOptions(context, ticket.IsPersistent ? ticket.ExpiresUtc : null)
        );

    void DeleteCookie(HttpContext context)
    {
        if (context.Response.HasStarted)
            return;

        // Deleting must repeat path and domain: a cookie set for "/app" is not cleared by an
        // expiry for "/", and the stale one keeps being sent.
        context.Response.Cookies.Delete(
            this.options.CookieName,
            this.options.BuildCookieOptions(context, expires: null)
        );
    }

    static CookieAuthenticationOptions Validate(CookieAuthenticationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.Protector is null)
            throw new InvalidOperationException(
                $"{nameof(CookieAuthenticationOptions)}.{nameof(CookieAuthenticationOptions.Protector)} is required — " +
                "an unprotected cookie is a claim the client can rewrite."
            );

        if (options.SameSite == SameSiteMode.None && options.SecurePolicy == CookieSecurePolicy.Never)
            throw new InvalidOperationException(
                "SameSite=None requires a secure cookie; browsers reject the combination outright."
            );

        return options;
    }
}

/// <summary>Signing in and out from a handler.</summary>
public static class CookieAuthenticationExtensions
{
    /// <summary>
    /// Issues the authentication cookie for <paramref name="principal"/>.
    /// <code>
    /// app.MapPost("/login", async ctx =>
    /// {
    ///     if (!await users.CheckPasswordAsync(name, password))
    ///         return Results.Unauthorized();
    ///
    ///     await ctx.SignInAsync(new ClaimsPrincipal(new ClaimsIdentity(
    ///         [new Claim(ClaimTypes.Name, name)], "Cookies"
    ///     )));
    ///
    ///     return Results.Ok();
    /// });
    /// </code>
    /// </summary>
    public static ValueTask SignInAsync(this HttpContext context, ClaimsPrincipal principal, bool persistent = true)
    {
        ArgumentNullException.ThrowIfNull(context);

        RequireHandler(context).SignIn(context, principal, persistent);

        return default;
    }

    /// <summary>Clears the authentication cookie.</summary>
    public static ValueTask SignOutAsync(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        RequireHandler(context).SignOut(context);

        return default;
    }

    static CookieAuthenticationHandler RequireHandler(HttpContext context)
        => context.RequestServices.GetService<CookieAuthenticationHandler>()
            ?? throw new InvalidOperationException(
                "Cookie authentication is not registered. Call services.AddAuthentication().AddCookie(...)."
            );

    /// <summary>
    /// Adds the <c>Cookies</c> scheme.
    /// <code>
    /// builder.AddAuthentication().AddCookie(o =>
    /// {
    ///     o.Protector = new TicketProtector(keyBytes);
    ///     o.LoginPath = "/login";
    /// });
    /// </code>
    /// </summary>
    public static AuthenticationBuilder AddCookie(
        this AuthenticationBuilder builder,
        Action<CookieAuthenticationOptions> configure
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        builder.Services.AddSingleton(_ =>
        {
            var options = new CookieAuthenticationOptions();
            configure(options);

            return options;
        });

        // Registered as itself as well as a handler, so SignInAsync can reach the same instance
        // that reads the cookie back — two configurations of the same scheme would be a bug that
        // only shows up as a session that never sticks.
        builder.Services.AddSingleton(sp => new CookieAuthenticationHandler(
            sp.GetRequiredService<CookieAuthenticationOptions>()
        ));

        builder.Services.AddSingleton<IAuthenticationChallenge>(
            sp => sp.GetRequiredService<CookieAuthenticationHandler>()
        );

        return builder.AddScheme(sp => sp.GetRequiredService<CookieAuthenticationHandler>());
    }
}
