using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Shiny.Net.HttpServer.Security;

/// <summary>
/// Decides whether the caller may have the endpoint the router selected.
/// <para>
/// Runs after routing, because what is required is a property of the endpoint. The distinction it
/// enforces is the one most worth getting right: <b>401</b> means "I do not know who you are", and
/// <b>403</b> means "I know exactly who you are and the answer is still no". Returning 401 to an
/// authenticated user invites them to log in again forever; returning 403 to an anonymous one hides
/// the fact that a token would have helped.
/// </para>
/// </summary>
public sealed class AuthorizationMiddleware(
    AuthorizationOptions options,
    ILogger<AuthorizationMiddleware>? logger = null,
    IEnumerable<IAuthenticationChallenge>? challenges = null
) : IHttpMiddleware
{
    readonly ILogger logger = logger ?? NullLogger<AuthorizationMiddleware>.Instance;

    // Empty unless a scheme wants to answer denial its own way — cookie authentication redirecting
    // a browser to a login page rather than handing it a 401 it cannot act on.
    readonly IAuthenticationChallenge[] challenges = challenges is null ? [] : [.. challenges];

    public async ValueTask InvokeAsync(HttpContext context, RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);

        var metadata = context.Endpoint?.GetMetadata<AuthorizationMetadata>();

        if (metadata is { AllowAnonymous: true })
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var policies = this.PoliciesFor(metadata);
        if (policies.Count == 0)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var authorizationContext = new AuthorizationContext(context, context.User);

        foreach (var policy in policies)
        {
            var failure = await policy.EvaluateAsync(authorizationContext).ConfigureAwait(false);
            if (failure is null)
                continue;

            await this.DenyAsync(context, failure).ConfigureAwait(false);
            return;
        }

        await next(context).ConfigureAwait(false);
    }

    List<AuthorizationPolicy> PoliciesFor(AuthorizationMetadata? metadata)
    {
        if (metadata is null || !metadata.Required)
        {
            // Nothing asked for authorization. The fallback policy is what turns that into a denial
            // for an app that chose deny-by-default.
            return options.FallbackPolicy is { } fallback ? [fallback] : [];
        }

        var policies = new List<AuthorizationPolicy>();

        foreach (var name in metadata.Policies)
            policies.Add(options.GetPolicy(name));

        if (metadata.Roles.Count > 0)
            policies.Add(new AuthorizationPolicyBuilder().RequireRole([.. metadata.Roles]).Build());

        // A bare [Authorize] with no policy and no roles means the default policy.
        if (policies.Count == 0)
            policies.Add(options.DefaultPolicy);

        return policies;
    }

    async ValueTask DenyAsync(HttpContext context, string requirement)
    {
        var authenticated = context.User.Identity?.IsAuthenticated == true;

        this.logger.LogInformation(
            "Denied {Method} {Path} for {User}: requires {Requirement}",
            context.Request.Method,
            context.Request.Path,
            authenticated ? context.User.Identity!.Name ?? "an authenticated user" : "an anonymous caller",
            requirement
        );

        foreach (var challenge in this.challenges)
        {
            if (await challenge.TryChallengeAsync(context, forbidden: authenticated).ConfigureAwait(false))
                return;
        }

        if (authenticated)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentLength = 0;
            await context.Response.StartAsync(context.RequestAborted).ConfigureAwait(false);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;

        // RFC 9110 requires a challenge on a 401. Naming the scheme is what tells a client which
        // kind of credential to go and get.
        var scheme = context.Authentication.Scheme ?? "Bearer";
        context.Response.Headers[HeaderNames.WwwAuthenticate] = context.Authentication.Failure is { } failure
            ? $"{scheme} error=\"invalid_token\", error_description=\"{Sanitize(failure)}\""
            : scheme;

        context.Response.ContentLength = 0;
        await context.Response.StartAsync(context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>Keeps a failure reason from breaking out of the quoted header parameter.</summary>
    static string Sanitize(string value)
    {
        Span<char> buffer = stackalloc char[Math.Min(value.Length, 120)];
        var written = 0;

        foreach (var c in value)
        {
            if (written == buffer.Length)
                break;

            buffer[written++] = c is '"' or '\\' or '\r' or '\n' ? ' ' : c;
        }

        return new string(buffer[..written]);
    }
}
