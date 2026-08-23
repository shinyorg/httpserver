using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Shiny.Net.HttpServer.Security;

/// <summary>
/// Registering the authorization mechanics. Everything about <em>who may do what</em> is decided
/// here, at composition time, so the pipeline only has to enforce it.
/// <code>
/// builder.Services
///     .AddAuthentication()
///     .AddJwtBearer(o => { o.Issuer = "shiny"; o.SigningKey = JwtSigningKey.FromSecret(secret); });
///
/// builder.Services.AddAuthorization(o =>
/// {
///     o.AddPolicy("admin", p => p.RequireRole("admin"));
///     o.SetFallbackPolicy(p => p.RequireAuthenticatedUser());   // deny by default
/// });
///
/// app.UseAuthentication();
/// app.UseAuthorization();
/// </code>
/// </summary>
public static class SecurityServiceCollectionExtensions
{
    /// <summary>
    /// Registers the authentication pipeline. Add handlers to the returned builder — one per scheme,
    /// tried in registration order.
    /// </summary>
    public static AuthenticationBuilder AddAuthentication(this ShinyHttpServerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton<AuthenticationMiddleware>();
        return new AuthenticationBuilder(builder.Services);
    }

    /// <summary>Registers the authorization policies and the middleware that enforces them.</summary>
    public static ShinyHttpServerBuilder AddAuthorization(
        this ShinyHttpServerBuilder builder,
        Action<AuthorizationOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton(_ =>
        {
            var options = new AuthorizationOptions();
            configure?.Invoke(options);
            return options;
        });

        builder.Services.TryAddSingleton<AuthorizationMiddleware>();
        return builder;
    }
}

/// <summary>Collects authentication handlers. One per scheme, tried in registration order.</summary>
public sealed class AuthenticationBuilder(IServiceCollection services)
{
    public IServiceCollection Services { get; } = services;

    /// <summary>
    /// Adds a handler. Registered as a singleton against <see cref="IAuthenticationHandler"/>, so
    /// several schemes coexist and the first one to recognise a request wins.
    /// <para>
    /// The container activates <typeparamref name="THandler"/> by reflection, so its constructors
    /// are annotated as preserved. Use the factory overload to avoid that entirely.
    /// </para>
    /// </summary>
    public AuthenticationBuilder AddScheme<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler
    >() where THandler : class, IAuthenticationHandler
    {
        this.Services.AddSingleton<IAuthenticationHandler, THandler>();
        return this;
    }

    /// <summary>Adds a handler built by a factory, for schemes that need options of their own.</summary>
    public AuthenticationBuilder AddScheme<THandler>(Func<IServiceProvider, THandler> factory)
        where THandler : class, IAuthenticationHandler
    {
        ArgumentNullException.ThrowIfNull(factory);

        this.Services.AddSingleton<IAuthenticationHandler>(factory);
        return this;
    }
}

/// <summary>Putting the security middleware in the pipeline.</summary>
public static class HttpServerSecurityExtensions
{
    /// <summary>
    /// Identifies the caller and publishes the result on <c>ctx.User</c>. Runs before routing, so
    /// everything downstream — including handlers on open endpoints — knows who is calling.
    /// </summary>
    public static HttpServer UseAuthentication(this HttpServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        RequireContainer(server, nameof(UseAuthentication), "AddAuthentication()");

        return server.Use<AuthenticationMiddleware>();
    }

    /// <summary>
    /// Enforces <c>[Authorize]</c> and the fallback policy.
    /// <para>
    /// Runs after routing, because what an endpoint requires is metadata on that endpoint. Call it
    /// after <see cref="UseAuthentication"/> — authorization has nothing to work with until the
    /// caller has been identified.
    /// </para>
    /// </summary>
    public static HttpServer UseAuthorization(this HttpServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        RequireContainer(server, nameof(UseAuthorization), "AddAuthorization()");

        return server.UseAfterRouting<AuthorizationMiddleware>();
    }

    static void RequireContainer(HttpServer server, string method, string registration)
    {
        if (server.Services is null)
            throw new InvalidOperationException(
                $"{method} needs a service provider. Build the server with HttpServer.CreateBuilder() " +
                $"and call services.{registration}."
            );
    }
}
