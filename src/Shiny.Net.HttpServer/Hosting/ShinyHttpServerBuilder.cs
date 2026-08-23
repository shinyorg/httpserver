using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Shiny.Net.HttpServer;

/// <summary>
/// Everything the server is configured with, in one place.
/// <para>
/// This is the registration surface: every <c>Add…</c> in this library and its sibling packages
/// hangs off this type rather than off <see cref="IServiceCollection"/>. The reason is
/// discoverability — typing <c>builder.</c> lists what this server can do, where typing
/// <c>services.</c> lists everything every library in the app has ever registered, and an embedded
/// server's features are exactly the things nobody knows to go looking for.
/// </para>
/// <para>
/// One shape, both hosting arrangements. Standalone:
/// <code>
/// var builder = HttpServer.CreateBuilder();
/// builder.Options.Port = 8080;
/// builder.AddRateLimiter(o => o.GlobalPolicy = new FixedWindowRateLimitPolicy(100, TimeSpan.FromMinutes(1)));
/// builder.AddHealthChecks().AddServerCheck();
///
/// var app = builder.Build();
/// app.MapGet("/ping", ctx => ctx.Response.WriteTextAsync("pong"));
/// await app.RunAsync();
/// </code>
/// Inside an app that already owns a container — a MAUI app, a generic host — the same calls, in a
/// callback:
/// <code>
/// services.AddShinyHttpServer(builder =>
/// {
///     builder.Options.Port = 8080;
///     builder.AddRateLimiter(o => ...);
///     builder.AddHealthChecks().AddServerCheck();
///     builder.Configure(server => server.MapMyAppEndpoints());
/// });
/// </code>
/// </para>
/// </summary>
public sealed class ShinyHttpServerBuilder
{
    readonly List<Action<HttpServer>> configurations = [];
    bool built;

    /// <summary>
    /// Attaches to a service collection the caller already owns. <c>AddShinyHttpServer</c> is the
    /// usual way in; this constructor is for a host that wants to hold the builder itself.
    /// </summary>
    public ShinyHttpServerBuilder(IServiceCollection services)
        : this(services, ownsContainer: false)
    {
    }

    internal ShinyHttpServerBuilder(IServiceCollection services, bool ownsContainer)
    {
        ArgumentNullException.ThrowIfNull(services);

        this.Services = services;
        this.OwnsContainer = ownsContainer;

        // Registered as the instance rather than through a factory, so that what the caller
        // configures on this.Options and what the server resolves are the same object. A middleware
        // that asks for HttpServerLimits gets the ones actually in force either way.
        services.TryAddSingleton(this.Options);
        services.TryAddSingleton(this.Options.Limits);

        // One registration for both hosting shapes: Build() resolves this, and so does a host.
        // Endpoint classes that inject HttpServer therefore get the real instance in both.
        services.TryAddSingleton(sp =>
        {
            var server = new HttpServer(this.Options, sp, sp.GetService<ILoggerFactory>());

            foreach (var configure in this.configurations)
                configure(server);

            return server;
        });
    }

    /// <summary>The underlying service collection, for anything this builder does not cover.</summary>
    public IServiceCollection Services { get; }

    /// <summary>Server configuration — addresses, TLS, limits, protocol settings.</summary>
    public HttpServerOptions Options { get; } = new();

    /// <summary>True when this builder created its own container and can therefore <see cref="Build"/>.</summary>
    public bool OwnsContainer { get; }

    /// <summary>Configures <see cref="Options"/>, for a fluent chain.</summary>
    public ShinyHttpServerBuilder Configure(Action<HttpServerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        configure(this.Options);
        return this;
    }

    /// <summary>
    /// Registers routes and middleware. The callback runs once, when the server is first resolved,
    /// so it can take anything out of the container while it registers.
    /// <code>
    /// builder.Configure(server =>
    /// {
    ///     server.UseAuthentication();
    ///     server.MapMyAppEndpoints();
    /// });
    /// </code>
    /// </summary>
    public ShinyHttpServerBuilder Configure(Action<HttpServer> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        this.configurations.Add(configure);
        return this;
    }

    /// <summary>
    /// Builds the container and the server. Only for a builder that owns its container — inside a
    /// host, the server is resolved from the host's provider instead.
    /// </summary>
    public HttpServer Build()
    {
        if (!this.OwnsContainer)
            throw new InvalidOperationException(
                "This builder is attached to a service collection it does not own, so it cannot build " +
                "a provider. Resolve HttpServer from your own provider instead — AddShinyHttpServer " +
                "has already registered it."
            );

        if (this.built)
            throw new InvalidOperationException($"{nameof(this.Build)} can only be called once.");

        this.built = true;

        return this.Services.BuildServiceProvider().GetRequiredService<HttpServer>();
    }
}
