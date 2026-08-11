using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.Jwt;

namespace Sample.Api;

// ---------------------------------------------------------------------------
// Issuing and consuming tokens with the same configuration. AddJwtBearer
// registers the validator and a JwtTokenGenerator built from the same key, so
// a token this endpoint hands out is one this server will accept — which is not
// true when the two are configured separately and drift.
// ---------------------------------------------------------------------------

[Route("/api/auth")]
[ApiTags("auth")]
public class AuthEndpoints(JwtTokenGenerator tokens, IUserDirectory users)
{
    /// <summary>Exchanges a username and password for a bearer token.</summary>
    [Post("/login")]
    [AllowAnonymous]
    [Produces(200, typeof(TokenResponse))]
    [Produces(401, Description = "The credentials were not accepted")]
    public IActionResult Login(LoginRequest request)
    {
        if (users.Verify(request.Username, request.Password) is not { } user)
            return new UnauthorizedResult();

        var descriptor = new JwtTokenDescriptor
        {
            Issuer = SampleAuth.Issuer,
            Audiences = { SampleAuth.Audience },
            Subject = user.Username,
            Lifetime = TimeSpan.FromHours(1)
        };

        descriptor.AddRoles(user.Roles);
        descriptor.AddClaim(JwtClaimNames.Name, user.DisplayName);

        return new OkObjectResult(new TokenResponse(tokens.Create(descriptor), 3600));
    }

    /// <summary>Returns the claims on the caller's token. Any authenticated caller.</summary>
    [Get("/me")]
    [Authorize]
    [Produces(200, typeof(Identity))]
    public Identity Me(HttpContext context) => new(
        context.User.FindFirst(JwtClaimNames.Subject)?.Value ?? "?",
        context.User.Identity?.Name ?? "?",
        [.. context.User.FindAll(JwtClaimNames.Role).Select(c => c.Value)]
    );

    /// <summary>Admin-only. Demonstrates a named policy registered through DI.</summary>
    [Get("/admin")]
    [Authorize("admin")]
    [Produces(200, typeof(string))]
    [Produces(403, Description = "Authenticated, but not an admin")]
    public string AdminOnly() => "You are an administrator.";
}

public record LoginRequest(string Username, string Password);

public record TokenResponse(string AccessToken, int ExpiresIn);

public record Identity(string Subject, string Name, string[] Roles);

public sealed record SampleUser(string Username, string DisplayName, string Password, string[] Roles);

public interface IUserDirectory
{
    SampleUser? Verify(string username, string password);
}

/// <summary>Constants shared by the token issuer and the validator.</summary>
public static class SampleAuth
{
    public const string Issuer = "shiny-sample";
    public const string Audience = "shiny-sample-app";
}
