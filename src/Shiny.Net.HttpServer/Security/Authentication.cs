using System.Security.Claims;

namespace Shiny.Net.HttpServer.Security;

/// <summary>The outcome of trying to identify the caller.</summary>
public readonly struct AuthenticateResult
{
    AuthenticateResult(ClaimsPrincipal? principal, string? failure, bool attempted)
    {
        this.Principal = principal;
        this.Failure = failure;
        this.Attempted = attempted;
    }

    /// <summary>The caller, when the credentials checked out.</summary>
    public ClaimsPrincipal? Principal { get; }

    /// <summary>Why the credentials were rejected. Surfaced in <c>WWW-Authenticate</c>, never in the body.</summary>
    public string? Failure { get; }

    /// <summary>
    /// True when credentials were present. The distinction matters: a request with no token at all
    /// is anonymous, while one with a bad token is a failure worth reporting.
    /// </summary>
    public bool Attempted { get; }

    public bool Succeeded => this.Principal is not null;

    public static AuthenticateResult Success(ClaimsPrincipal principal) => new(principal, null, true);

    /// <summary>No credentials were supplied. The request continues as anonymous.</summary>
    public static AuthenticateResult NoResult() => new(null, null, false);

    /// <summary>Credentials were supplied and rejected.</summary>
    public static AuthenticateResult Fail(string reason) => new(null, reason, true);
}

/// <summary>
/// Turns whatever a request carries — a bearer token, an API key, a client certificate — into a
/// <see cref="ClaimsPrincipal"/>.
/// <para>
/// Handlers never decide whether a request is allowed; they only answer "who is this?". Refusing
/// is authorization's job, and keeping the two apart is what lets an endpoint be public on a server
/// that still knows who is calling it.
/// </para>
/// </summary>
public interface IAuthenticationHandler
{
    /// <summary>The scheme this handler implements, e.g. <c>Bearer</c>. Used in <c>WWW-Authenticate</c>.</summary>
    string Scheme { get; }

    ValueTask<AuthenticateResult> AuthenticateAsync(HttpContext context);
}

/// <summary>
/// Lets a scheme answer a denial its own way.
/// <para>
/// A 401 is the right answer for an API client and useless to a browser, which cannot act on it —
/// the useful reply there is a redirect to a login page. Implemented by the scheme rather than the
/// authorization middleware, because only the scheme knows what its callers can do with an answer.
/// </para>
/// </summary>
public interface IAuthenticationChallenge
{
    /// <summary>
    /// Writes a response and returns true, or returns false to let the default 401/403 stand.
    /// </summary>
    /// <param name="forbidden">
    /// True when the caller is known and still not allowed — a login page will not help them.
    /// </param>
    ValueTask<bool> TryChallengeAsync(HttpContext context, bool forbidden);
}

/// <summary>
/// Runs the registered <see cref="IAuthenticationHandler"/>s and publishes the result on
/// <see cref="HttpContext.User"/>.
/// <para>
/// Ordinary middleware, deliberately: identifying the caller does not depend on which endpoint was
/// selected, so it runs before routing and its result is available to everything after.
/// </para>
/// </summary>
public sealed class AuthenticationMiddleware(IEnumerable<IAuthenticationHandler> handlers) : IHttpMiddleware
{
    readonly IAuthenticationHandler[] handlers = [.. handlers];

    public async ValueTask InvokeAsync(HttpContext context, RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);

        foreach (var handler in this.handlers)
        {
            var result = await handler.AuthenticateAsync(context).ConfigureAwait(false);

            if (result.Succeeded)
            {
                context.User = result.Principal!;
                context.Authentication.Scheme = handler.Scheme;
                break;
            }

            if (result.Attempted)
            {
                // Credentials were offered and rejected. Stop rather than letting a later handler
                // paper over it — the caller meant to authenticate and got it wrong.
                context.Authentication.Scheme = handler.Scheme;
                context.Authentication.Failure = result.Failure;
                break;
            }
        }

        await next(context).ConfigureAwait(false);
    }
}
