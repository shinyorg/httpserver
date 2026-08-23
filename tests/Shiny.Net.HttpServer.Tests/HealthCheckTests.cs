using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shiny.Net.HttpServer.HealthChecks;

namespace Shiny.Net.HttpServer.Tests;

public class HealthCheckTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Reports_healthy_when_every_check_passes()
    {
        await using var test = await TestServer.StartAsync(
            server => server.MapHealthChecks(),
            builder => builder
                .AddHealthChecks()
                .AddCheck("one", _ => new(HealthCheckResult.Healthy()))
                .AddCheck("two", _ => new(HealthCheckResult.Healthy("fine")))
        );

        var response = await test.Client.GetAsync("/health", Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        Assert.Equal("Healthy", document.RootElement.GetProperty("status").GetString());
        Assert.Equal("fine", document.RootElement.GetProperty("entries").GetProperty("two").GetProperty("description").GetString());
    }

    /// <summary>The aggregate is the worst entry, and unhealthy is the one status that changes the code.</summary>
    [Fact]
    public async Task Answers_503_when_a_check_fails()
    {
        await using var test = await TestServer.StartAsync(
            server => server.MapHealthChecks(),
            builder => builder
                .AddHealthChecks()
                .AddCheck("ok", _ => new(HealthCheckResult.Healthy()))
                .AddCheck("broken", _ => new(HealthCheckResult.Unhealthy("no disk")))
        );

        var response = await test.Client.GetAsync("/health", Token);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("no disk", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Degraded_still_serves_a_200()
    {
        await using var test = await TestServer.StartAsync(
            server => server.MapHealthChecks(),
            builder => builder.AddHealthChecks().AddCheck("slow", _ => new(HealthCheckResult.Degraded("queue deep")))
        );

        var response = await test.Client.GetAsync("/health", Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Degraded", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task A_thrown_check_becomes_its_failure_status()
    {
        var builder = new ShinyHttpServerBuilder(new ServiceCollection());
        builder
            .AddHealthChecks()
            .Add(new HealthCheckRegistration("throws", _ => new ThrowingCheck(), HealthStatus.Degraded));

        var provider = builder.Services.BuildServiceProvider();
        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(cancellationToken: Token);

        Assert.Equal(HealthStatus.Degraded, report.Status);
        Assert.Equal("boom", report.Entries[0].Description);
        Assert.IsType<InvalidOperationException>(report.Entries[0].Exception);
    }

    /// <summary>A check that hangs is failed rather than allowed to hang the probe.</summary>
    [Fact]
    public async Task A_check_that_never_returns_is_timed_out()
    {
        var builder = new ShinyHttpServerBuilder(new ServiceCollection());
        builder.AddHealthChecks().Add(new HealthCheckRegistration(
            "hangs",
            _ => new DelayCheck(),
            timeout: TimeSpan.FromMilliseconds(100)
        ));

        var provider = builder.Services.BuildServiceProvider();
        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(cancellationToken: Token);

        Assert.Equal(HealthStatus.Unhealthy, report.Status);
        Assert.Contains("Timed out", report.Entries[0].Description);
    }

    [Fact]
    public async Task Tags_split_liveness_from_readiness()
    {
        await using var test = await TestServer.StartAsync(
            server =>
            {
                server.MapHealthChecks("/health/live", "live");
                server.MapHealthChecks("/health/ready", "ready");
            },
            builder => builder
                .AddHealthChecks()
                .AddCheck("process", _ => new(HealthCheckResult.Healthy()), "live")
                .AddCheck("database", _ => new(HealthCheckResult.Unhealthy("not connected")), "ready")
        );

        var live = await test.Client.GetAsync("/health/live", Token);
        var ready = await test.Client.GetAsync("/health/ready", Token);

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.DoesNotContain("database", await live.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task The_server_check_reports_the_running_server()
    {
        await using var test = await TestServer.StartAsync(
            server => server.MapHealthChecks(),
            builder => builder.AddHealthChecks().AddServerCheck()
        );

        var body = await test.Client.GetStringAsync("/health", Token);

        using var document = JsonDocument.Parse(body);
        var entry = document.RootElement.GetProperty("entries").GetProperty("server");

        Assert.Equal("Healthy", entry.GetProperty("status").GetString());
        Assert.Equal("Running", entry.GetProperty("data").GetProperty("state").GetString());
    }

    [Fact]
    public async Task Health_responses_are_never_cached()
    {
        await using var test = await TestServer.StartAsync(
            server => server.MapHealthChecks(),
            builder => builder.AddHealthChecks().AddCheck("ok", _ => new(HealthCheckResult.Healthy()))
        );

        var response = await test.Client.GetAsync("/health", Token);

        Assert.Equal("no-store, no-cache", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Details_can_be_withheld()
    {
        await using var test = await TestServer.StartAsync(
            server => server.MapHealthChecks("/health", o => o.IncludeDetails = false),
            builder => builder.AddHealthChecks().AddCheck("secret-dependency", _ => new(HealthCheckResult.Healthy()))
        );

        var body = await test.Client.GetStringAsync("/health", Token);

        Assert.Contains("Healthy", body);
        Assert.DoesNotContain("secret-dependency", body);
    }

    sealed class ThrowingCheck : IHealthCheck
    {
        public ValueTask<HealthCheckResult> CheckAsync(HealthCheckContext context, CancellationToken cancellationToken)
            => throw new InvalidOperationException("boom");
    }

    sealed class DelayCheck : IHealthCheck
    {
        public async ValueTask<HealthCheckResult> CheckAsync(HealthCheckContext context, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return HealthCheckResult.Healthy();
        }
    }
}
