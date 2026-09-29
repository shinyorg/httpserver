using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shiny.Net.HttpServer.Security;

namespace Shiny.Net.HttpServer.Switchboard.Internal;

/// <summary>
/// Authorization for switchboard requests, evaluated here rather than left to
/// <c>UseAuthorization</c> alone.
/// <para>
/// Every method on a switchboard shares one <c>POST /invoke</c> route, so a method's own
/// <c>[Authorize]</c> cannot be route metadata — something has to check it once the target is
/// known. And since that something exists, it checks the route's requirements too: a switchboard
/// marked <c>[Authorize]</c> is protected whether or not the app remembered the middleware.
/// </para>
/// </summary>
static class SwitchboardAuthorization
{
    /// <summary>Folds <c>[Authorize]</c> / <c>[AllowAnonymous]</c> instances into one requirement, the way endpoint metadata does.</summary>
    public static AuthorizationMetadata? Fold(IEnumerable<object> metadata)
    {
        AuthorizationMetadata? folded = null;

        foreach (var item in metadata)
        {
            switch (item)
            {
                case AuthorizeAttribute authorize:
                    folded ??= new AuthorizationMetadata();
                    folded.Required = true;

                    if (!string.IsNullOrWhiteSpace(authorize.Policy))
                        folded.Policies.Add(authorize.Policy);

                    if (authorize.Roles is { } roles)
                    {
                        foreach (var role in roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                            folded.Roles.Add(role);
                    }

                    break;

                case AllowAnonymousAttribute:
                    folded ??= new AuthorizationMetadata();
                    folded.AllowAnonymous = true;
                    break;

                case AuthorizationMetadata existing:
                    folded ??= new AuthorizationMetadata();
                    folded.Required |= existing.Required;
                    folded.AllowAnonymous |= existing.AllowAnonymous;

                    foreach (var policy in existing.Policies)
                        folded.Policies.Add(policy);

                    foreach (var role in existing.Roles)
                        folded.Roles.Add(role);

                    break;
            }
        }

        return folded;
    }

    /// <summary>
    /// Checks every requirement in turn. On failure writes the 401 or 403 and returns false: 401 when
    /// nobody is signed in, 403 when somebody is and the answer is still no.
    /// </summary>
    public static async ValueTask<bool> AuthorizeAsync(HttpContext context, params AuthorizationMetadata?[] requirements)
    {
        AuthorizationOptions? options = null;
        AuthorizationContext? authorizationContext = null;

        foreach (var requirement in requirements)
        {
            if (requirement is null || !requirement.Required || requirement.AllowAnonymous)
                continue;

            options ??= context.RequestServices.GetService<AuthorizationOptions>() ?? new AuthorizationOptions();
            authorizationContext ??= new AuthorizationContext(context, context.User);

            foreach (var policy in PoliciesFor(requirement, options))
            {
                var failure = await policy.EvaluateAsync(authorizationContext).ConfigureAwait(false);
                if (failure is null)
                    continue;

                await DenyAsync(context, $"Requires {failure}").ConfigureAwait(false);
                return false;
            }
        }

        return true;
    }

    static List<AuthorizationPolicy> PoliciesFor(AuthorizationMetadata metadata, AuthorizationOptions options)
    {
        var policies = new List<AuthorizationPolicy>();

        foreach (var name in metadata.Policies)
            policies.Add(options.GetPolicy(name));

        if (metadata.Roles.Count > 0)
            policies.Add(new AuthorizationPolicyBuilder().RequireRole([.. metadata.Roles]).Build());

        if (policies.Count == 0)
            policies.Add(options.DefaultPolicy);

        return policies;
    }

    public static ValueTask DenyAsync(HttpContext context, string message)
    {
        var authenticated = context.User.Identity?.IsAuthenticated == true;

        if (!authenticated)
            context.Response.Headers[HeaderNames.WwwAuthenticate] = context.Authentication.Scheme ?? "Bearer";

        return SwitchboardResponses.ErrorAsync(
            context,
            authenticated ? StatusCodes.Status403Forbidden : StatusCodes.Status401Unauthorized,
            message
        );
    }
}

static class SwitchboardResponses
{
    public static async ValueTask ErrorAsync(HttpContext context, int statusCode, string message)
    {
        if (context.Response.HasStarted)
            return;

        context.Response.StatusCode = statusCode;

        var json = JsonSerializer.SerializeToUtf8Bytes(new ErrorPayload(message), SwitchboardProtocolJson.Default.ErrorPayload);
        await context.Response.WriteBytesAsync(json, "application/json", context.RequestAborted).ConfigureAwait(false);
    }
}
