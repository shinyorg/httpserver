using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.Security;

namespace Sample.Maui.Server;

/// <summary>
/// Requires a signed-in caller for everything below it.
/// <para>
/// Endpoint authorization (<c>[Authorize]</c>, <c>RequireAuthorization</c>) runs <em>after</em>
/// routing, and static files are served before routing ever happens — so a page served by
/// <c>UseEmbeddedFiles</c> would be public no matter what the endpoints required. Putting the check
/// in front of the static file handler is what closes that, and it is the difference between a
/// password prompt and a password prompt that only covers the JSON.
/// </para>
/// </summary>
public sealed class RequireAuthenticationMiddleware(IEnumerable<IAuthenticationChallenge> challenges) : IHttpMiddleware
{
    readonly IAuthenticationChallenge[] challenges = [.. challenges];

    /// <summary>Paths served without credentials. A health check is no use if it needs a password.</summary>
    public IList<string> AllowAnonymous { get; } = ["/ping"];

    public async ValueTask InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var authenticated = context.User.Identity?.IsAuthenticated == true;

        if (authenticated || this.AllowAnonymous.Contains(context.Request.Path, StringComparer.OrdinalIgnoreCase))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        // The scheme writes its own challenge, which for Basic is what makes a browser show its
        // password box. A generic 401 would leave the visitor with a blank page.
        foreach (var challenge in this.challenges)
        {
            if (await challenge.TryChallengeAsync(context, forbidden: false).ConfigureAwait(false))
                return;
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentLength = 0;
    }
}
