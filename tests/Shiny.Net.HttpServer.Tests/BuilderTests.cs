using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shiny.Net.HttpServer.HealthChecks;
using Shiny.Net.HttpServer.RateLimiting;

namespace Shiny.Net.HttpServer.Tests;

/// <summary>
/// The registration surface itself. Both hosting shapes — a builder that owns its container and one
/// attached to an app's — have to end up with the same server, because the whole point of putting
/// every <c>Add…</c> on the builder is that there is one way to configure this thing.
/// </summary>
public class BuilderTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void The_options_the_callback_configures_are_the_ones_the_server_gets()
    {
        var services = new ServiceCollection();

        services.AddShinyHttpServer(http =>
        {
            http.Options.Port = 8123;
            http.Options.Address = IPAddress.Any;
            http.Options.Limits.MaxRequestBodySize = 4096;
        });

        var provider = services.BuildServiceProvider();
        var server = provider.GetRequiredService<HttpServer>();

        Assert.Equal(8123, server.Options.Port);
        Assert.Equal(IPAddress.Any, server.Options.Address);

        // Resolvable in their own right, which a middleware that asks for the limits depends on.
        Assert.Same(server.Options, provider.GetRequiredService<HttpServerOptions>());
        Assert.Same(server.Options.Limits, provider.GetRequiredService<HttpServerLimits>());
        Assert.Equal(4096, provider.GetRequiredService<HttpServerLimits>().MaxRequestBodySize);
    }

    [Fact]
    public void Configure_runs_when_the_server_is_resolved_and_can_reach_the_container()
    {
        var services = new ServiceCollection();
        services.AddSingleton("the payload");

        services.AddShinyHttpServer(http => http.Configure(server =>
            server.MapGet("/x", ctx => ctx.Response.WriteTextAsync(
                server.Services!.GetRequiredService<string>(),
                cancellationToken: ctx.RequestAborted
            ))
        ));

        var server = services.BuildServiceProvider().GetRequiredService<HttpServer>();

        Assert.Single(server.Router.Endpoints);
    }

    /// <summary>Resolving twice must not register the routes twice.</summary>
    [Fact]
    public void The_server_is_a_singleton()
    {
        var services = new ServiceCollection();
        services.AddShinyHttpServer(http => http.Configure(server => server.MapGet("/x", _ => default)));

        var provider = services.BuildServiceProvider();

        Assert.Same(provider.GetRequiredService<HttpServer>(), provider.GetRequiredService<HttpServer>());
        Assert.Single(provider.GetRequiredService<HttpServer>().Router.Endpoints);
    }

    [Fact]
    public void Registrations_made_on_the_builder_land_in_the_app_container()
    {
        var services = new ServiceCollection();

        services.AddShinyHttpServer(http =>
        {
            http.AddRateLimiter(o => o.GlobalPolicy = new FixedWindowRateLimitPolicy(10, TimeSpan.FromMinutes(1)));
            http.AddHealthChecks().AddCheck("ok", _ => new(HealthCheckResult.Healthy()));
        });

        var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetService<RateLimitOptions>());
        Assert.NotNull(provider.GetService<HealthCheckService>());
    }

    /// <summary>
    /// Two registrations over one collection have to add up to one server. Anything else means the
    /// second call silently does nothing, which is the worst possible failure for a registration API.
    /// </summary>
    [Fact]
    public void A_second_registration_composes_with_the_first()
    {
        var services = new ServiceCollection();

        services.AddShinyHttpServer(http =>
        {
            http.Options.Port = 8200;
            http.Configure(server => server.MapGet("/one", _ => default));
        });

        services.AddShinyHttpServer(http =>
        {
            http.Options.ServerHeader = "second";
            http.Configure(server => server.MapGet("/two", _ => default));
        });

        var server = services.BuildServiceProvider().GetRequiredService<HttpServer>();

        Assert.Equal(8200, server.Options.Port);
        Assert.Equal("second", server.Options.ServerHeader);
        Assert.Equal(2, server.Router.Endpoints.Count);
    }

    [Fact]
    public void A_builder_made_over_a_collection_that_already_has_one_adopts_its_options()
    {
        var services = new ServiceCollection();
        services.AddShinyHttpServer(http => http.Options.Port = 8300);

        var second = new ShinyHttpServerBuilder(services);

        Assert.Same(services.BuildServiceProvider().GetRequiredService<HttpServerOptions>(), second.Options);
        Assert.Equal(8300, second.Options.Port);
    }

    [Fact]
    public void AutoStart_decides_whether_the_host_starts_it()
    {
        var withAutoStart = new ServiceCollection();
        withAutoStart.AddShinyHttpServer();

        var without = new ServiceCollection();
        without.AddShinyHttpServer(autoStart: false);

        Assert.NotEmpty(withAutoStart.BuildServiceProvider().GetServices<IHostedService>());
        Assert.Empty(without.BuildServiceProvider().GetServices<IHostedService>());
    }

    [Fact]
    public async Task A_builder_that_owns_its_container_builds_a_working_server()
    {
        var builder = HttpServer.CreateBuilder();
        builder.Options.Port = 0;
        builder.Options.Address = IPAddress.Loopback;

        Assert.True(builder.OwnsContainer);

        await using var server = builder.Build();
        server.MapGet("/ping", ctx => ctx.Response.WriteTextAsync("pong", cancellationToken: ctx.RequestAborted));

        await server.StartAsync(Token);

        using var client = new HttpClient { BaseAddress = new Uri(server.ListenUrl!) };
        Assert.Equal("pong", await client.GetStringAsync("/ping", Token));
    }

    /// <summary>An endpoint class that injects the server has to get the real one, not a second copy.</summary>
    [Fact]
    public void An_injected_server_is_the_one_that_was_built()
    {
        var builder = HttpServer.CreateBuilder();
        var server = builder.Build();

        Assert.Same(server, server.Services!.GetRequiredService<HttpServer>());
    }

    [Fact]
    public void Build_is_refused_on_a_builder_that_does_not_own_its_container()
    {
        var builder = new ShinyHttpServerBuilder(new ServiceCollection());

        Assert.False(builder.OwnsContainer);

        var error = Assert.Throws<InvalidOperationException>(() => builder.Build());
        Assert.Contains("AddShinyHttpServer", error.Message);
    }

    [Fact]
    public void Build_is_refused_twice()
    {
        var builder = HttpServer.CreateBuilder();
        builder.Build();

        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }

    [Fact]
    public void Configure_of_options_chains()
    {
        var builder = HttpServer.CreateBuilder()
            .Configure(o => o.Port = 9100)
            .Configure(o => o.ServerHeader = null);

        Assert.Equal(9100, builder.Options.Port);
        Assert.Null(builder.Options.ServerHeader);
    }
}
