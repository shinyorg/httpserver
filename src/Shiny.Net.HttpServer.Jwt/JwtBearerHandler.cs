using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shiny.Net.HttpServer.Security;

namespace Shiny.Net.HttpServer.Jwt;

/// <summary>Configuration for the <c>Bearer</c> scheme.</summary>
public sealed class JwtBearerOptions
{
    /// <summary>The key tokens are signed with. Shorthand for a single-entry <see cref="Validation"/>.</summary>
    public JwtSigningKey? SigningKey { get; set; }

    /// <summary>Issuer this server accepts, and stamps into tokens it creates.</summary>
    public string? Issuer { get; set; }

    /// <summary>Audience this server accepts, and stamps into tokens it creates.</summary>
    public string? Audience { get; set; }

    /// <summary>
    /// Full control over validation. <see cref="SigningKey"/>, <see cref="Issuer"/> and
    /// <see cref="Audience"/> are folded into this at registration.
    /// </summary>
    public JwtValidationParameters Validation { get; } = new();

    /// <summary>
    /// How long tokens created by <see cref="JwtTokenGenerator"/> last, when the descriptor does not
    /// say otherwise.
    /// </summary>
    public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromHours(1);

    internal JwtValidationParameters BuildValidation()
    {
        if (this.SigningKey is { } key && !this.Validation.SigningKeys.Contains(key))
            this.Validation.SigningKeys.Add(key);

        if (this.Issuer is { Length: > 0 } issuer && !this.Validation.ValidIssuers.Contains(issuer))
            this.Validation.ValidIssuers.Add(issuer);

        if (this.Audience is { Length: > 0 } audience && !this.Validation.ValidAudiences.Contains(audience))
            this.Validation.ValidAudiences.Add(audience);

        // Turning a check off is a decision; forgetting to configure it is a mistake. Failing here
        // beats accepting anyone's tokens in production.
        if (this.Validation.SigningKeys.Count == 0)
            throw new InvalidOperationException(
                $"{nameof(JwtBearerOptions)} needs a signing key. Set {nameof(this.SigningKey)} or add one to {nameof(this.Validation)}."
            );

        if (this.Validation.ValidateIssuer && this.Validation.ValidIssuers.Count == 0)
            throw new InvalidOperationException(
                $"Set {nameof(this.Issuer)}, or turn issuer validation off deliberately with Validation.ValidateIssuer = false."
            );

        if (this.Validation.ValidateAudience && this.Validation.ValidAudiences.Count == 0)
            throw new InvalidOperationException(
                $"Set {nameof(this.Audience)}, or turn audience validation off deliberately with Validation.ValidateAudience = false."
            );

        return this.Validation;
    }
}

/// <summary>
/// Reads <c>Authorization: Bearer &lt;token&gt;</c> and validates it.
/// <para>
/// Absent header means anonymous, not denied — whether the endpoint needs a caller is authorization's
/// decision, not this one's.
/// </para>
/// </summary>
public sealed class JwtBearerHandler(JwtTokenValidator validator) : IAuthenticationHandler
{
    const string Prefix = "Bearer ";

    public string Scheme => "Bearer";

    public JwtTokenValidator Validator { get; } = validator;

    public ValueTask<AuthenticateResult> AuthenticateAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var header = context.Request.Headers.GetFirst(HeaderNames.Authorization);
        if (header is null || !header.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            return new ValueTask<AuthenticateResult>(AuthenticateResult.NoResult());

        var token = header[Prefix.Length..].Trim();
        if (token.Length == 0)
            return new ValueTask<AuthenticateResult>(AuthenticateResult.Fail("The bearer token is empty."));

        var result = this.Validator.Validate(token);

        return new ValueTask<AuthenticateResult>(
            result.IsValid
                ? AuthenticateResult.Success(result.Principal)
                : AuthenticateResult.Fail(result.Error ?? "The token is invalid.")
        );
    }
}

/// <summary>Registering JWT bearer authentication.</summary>
public static class JwtAuthenticationBuilderExtensions
{
    /// <summary>
    /// Adds the <c>Bearer</c> scheme, and registers a <see cref="JwtTokenGenerator"/> so the same
    /// configuration that validates tokens is the one that creates them — a login endpoint issuing
    /// tokens the server cannot then accept is an easy and miserable bug.
    /// <code>
    /// builder.Services.AddAuthentication().AddJwtBearer(o =>
    /// {
    ///     o.Issuer = "shiny";
    ///     o.Audience = "shiny-app";
    ///     o.SigningKey = JwtSigningKey.FromSecret(secret);
    /// });
    /// </code>
    /// </summary>
    public static AuthenticationBuilder AddJwtBearer(
        this AuthenticationBuilder builder,
        Action<JwtBearerOptions> configure
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        builder.Services.TryAddSingleton(_ =>
        {
            var options = new JwtBearerOptions();
            configure(options);
            options.BuildValidation();

            return options;
        });

        builder.Services.TryAddSingleton(sp => new JwtTokenValidator(
            sp.GetRequiredService<JwtBearerOptions>().Validation,
            sp.GetService<TimeProvider>()
        ));

        builder.Services.TryAddSingleton(sp =>
        {
            var options = sp.GetRequiredService<JwtBearerOptions>();
            var signingKey = options.SigningKey
                ?? options.Validation.SigningKeys.FirstOrDefault(k => k.CanSign)
                ?? throw new InvalidOperationException(
                    "No signing-capable key is configured, so this server can validate tokens but not create them."
                );

            return new JwtTokenGenerator(signingKey, sp.GetService<TimeProvider>());
        });

        return builder.AddScheme(sp => new JwtBearerHandler(sp.GetRequiredService<JwtTokenValidator>()));
    }
}
