using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Shiny.Net.HttpServer;

/// <summary>
/// Builds a <see cref="HttpServer"/> together with its container.
/// <code>
/// var builder = HttpServer.CreateBuilder();
/// builder.Options.Port = 8080;
/// builder.Services.AddSingleton&lt;IClock, SystemClock&gt;();
/// builder.Services.AddScoped&lt;IUnitOfWork, UnitOfWork&gt;();
///
/// var app = builder.Build();
/// app.OnRequest(ctx => ctx.Response.WriteTextAsync(ctx.GetRequiredService&lt;IClock&gt;().Now.ToString()));
/// await app.RunAsync();
/// </code>
/// <para>
/// If the app already has a container — a MAUI app, a generic host — skip the builder and pass the
/// existing provider to the <see cref="HttpServer"/> constructor, or call
/// <c>services.AddHttpServer()</c>.
/// </para>
/// </summary>
public sealed class HttpServerBuilder
{
    bool built;

    internal HttpServerBuilder()
    {
        this.Services.AddSingleton(this.Options);
        this.Services.AddSingleton(this.Options.Limits);

        // Registered through a holder because the server does not exist until the provider it
        // depends on has been built. Endpoints that inject HttpServer get the real instance.
        this.Services.AddSingleton<HttpServerHolder>();
        this.Services.AddSingleton(sp => sp.GetRequiredService<HttpServerHolder>().Server);
    }

    /// <summary>Services available to handlers. Scoped registrations are per request/response exchange.</summary>
    public IServiceCollection Services { get; } = new ServiceCollection();

    /// <summary>Server configuration — address, port, TLS, limits.</summary>
    public HttpServerOptions Options { get; } = new();

    public HttpServerBuilder Configure(Action<HttpServerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(this.Options);
        return this;
    }

    public HttpServer Build()
    {
        if (this.built)
            throw new InvalidOperationException($"{nameof(this.Build)} can only be called once.");

        this.built = true;

        var provider = this.Services.BuildServiceProvider();

        // Logging is optional: apps that never called AddLogging get a no-op factory rather than
        // a resolution failure at startup.
        var server = new HttpServer(this.Options, provider, provider.GetService<ILoggerFactory>());
        provider.GetRequiredService<HttpServerHolder>().Server = server;

        return server;
    }
}

sealed class HttpServerHolder
{
    HttpServer? server;

    public HttpServer Server
    {
        get => this.server ?? throw new InvalidOperationException(
            "The HttpServer is not available until the builder has finished building it."
        );
        set => this.server = value;
    }
}
