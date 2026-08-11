namespace Shiny.Net.HttpServer.Security;

/// <summary>
/// What an endpoint requires, attached to it as metadata. The generator emits this from
/// <c>[Authorize]</c> and <c>[AllowAnonymous]</c>; raw routes get it from
/// <see cref="HttpServerAuthorizationExtensions.RequireAuthorization"/>.
/// </summary>
public sealed class AuthorizationMetadata
{
    /// <summary>Named policies that must all pass. Empty with <see cref="Required"/> means the default policy.</summary>
    public IList<string> Policies { get; } = [];

    /// <summary>Roles, any one of which satisfies the requirement.</summary>
    public IList<string> Roles { get; } = [];

    /// <summary>True when the endpoint carries <c>[Authorize]</c> in any form.</summary>
    public bool Required { get; set; }

    /// <summary>True when the endpoint opted out. Beats everything else, including the fallback policy.</summary>
    public bool AllowAnonymous { get; set; }
}

/// <summary>Requiring authorization on a raw route, without an attribute to hang it on.</summary>
public static class HttpServerAuthorizationExtensions
{
    /// <summary>
    /// Requires authorization for the most recently mapped route.
    /// <code>
    /// app.OnGet("/secrets", ctx => ...).RequireAuthorization("admin");
    /// </code>
    /// With no policy named, the default policy applies — an authenticated caller.
    /// </summary>
    public static HttpServer RequireAuthorization(this HttpServer server, params string[] policies)
    {
        ArgumentNullException.ThrowIfNull(server);

        var metadata = LastEndpointMetadata(server);
        metadata.Required = true;

        foreach (var policy in policies)
            metadata.Policies.Add(policy);

        return server;
    }

    /// <summary>Exempts the most recently mapped route, including from a fallback policy.</summary>
    public static HttpServer AllowAnonymous(this HttpServer server)
    {
        ArgumentNullException.ThrowIfNull(server);

        LastEndpointMetadata(server).AllowAnonymous = true;
        return server;
    }

    static AuthorizationMetadata LastEndpointMetadata(HttpServer server)
    {
        if (server.Router.Endpoints.Count == 0)
            throw new InvalidOperationException(
                "RequireAuthorization applies to the most recently mapped route, and no route has been mapped yet."
            );

        var endpoint = server.Router.Endpoints[^1];
        var metadata = endpoint.GetMetadata<AuthorizationMetadata>();

        if (metadata is null)
        {
            metadata = new AuthorizationMetadata();
            endpoint.WithMetadata(metadata);
        }

        return metadata;
    }
}
