using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Shiny.Net.HttpServer.HealthChecks;

/// <summary>Collects health check registrations. Returned by <c>AddHealthChecks()</c>.</summary>
public sealed class HealthCheckBuilder(IServiceCollection services, HealthCheckOptions options)
{
    public IServiceCollection Services { get; } = services;

    public HealthCheckOptions Options { get; } = options;

    /// <summary>Registers a check by registration, for anything the shorthands do not cover.</summary>
    public HealthCheckBuilder Add(HealthCheckRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);

        this.Options.Registrations.Add(registration);
        return this;
    }

    /// <summary>
    /// Registers a check as a lambda — the common case.
    /// <code>
    /// builder.Services.AddHealthChecks()
    ///     .AddCheck("disk", _ => new(Space() > 50_000_000 ? HealthCheckResult.Healthy() : HealthCheckResult.Degraded("low")), "ready");
    /// </code>
    /// </summary>
    public HealthCheckBuilder AddCheck(
        string name,
        Func<CancellationToken, ValueTask<HealthCheckResult>> check,
        params string[] tags
    )
    {
        ArgumentNullException.ThrowIfNull(check);
        return this.Add(new HealthCheckRegistration(name, _ => new DelegateHealthCheck(check), tags: tags));
    }

    /// <summary>Registers an already-constructed check.</summary>
    public HealthCheckBuilder AddCheck(string name, IHealthCheck check, params string[] tags)
    {
        ArgumentNullException.ThrowIfNull(check);
        return this.Add(new HealthCheckRegistration(name, _ => check, tags: tags));
    }

    /// <summary>
    /// Registers a check resolved from the container per run, so it can take dependencies.
    /// <typeparamref name="TCheck"/> has to be registered itself — this resolves it, it does not
    /// construct it, because constructing it would mean reflection.
    /// </summary>
    public HealthCheckBuilder AddCheck<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TCheck
    >(
        string name,
        HealthStatus failureStatus = HealthStatus.Unhealthy,
        IEnumerable<string>? tags = null,
        TimeSpan? timeout = null
    ) where TCheck : class, IHealthCheck
    {
        this.Services.TryAddSingleton<TCheck>();

        return this.Add(new HealthCheckRegistration(
            name,
            sp => sp.GetRequiredService<TCheck>(),
            failureStatus,
            tags,
            timeout
        ));
    }

    /// <summary>
    /// Registers a liveness check that reports whether the server is actually listening.
    /// <para>
    /// Worth having on a device: the process being alive says nothing about whether the listener
    /// survived the last time the app was backgrounded or the Wi-Fi changed underneath it.
    /// </para>
    /// </summary>
    public HealthCheckBuilder AddServerCheck(string name = "server", params string[] tags)
        => this.Add(new HealthCheckRegistration(
            name,
            sp =>
            {
                var server = sp.GetRequiredService<HttpServer>();

                return new DelegateHealthCheck(_ => new ValueTask<HealthCheckResult>(
                    server.IsRunning
                        ? HealthCheckResult.Healthy(null, new Dictionary<string, string>
                        {
                            ["state"] = server.State.ToString(),
                            ["connections"] = server.ActiveConnections.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        })
                        : HealthCheckResult.Unhealthy($"The server is {server.State}.")
                ));
            },
            tags: tags.Length > 0 ? tags : ["live"]
        ));

    sealed class DelegateHealthCheck(Func<CancellationToken, ValueTask<HealthCheckResult>> check) : IHealthCheck
    {
        public ValueTask<HealthCheckResult> CheckAsync(HealthCheckContext context, CancellationToken cancellationToken)
            => check(cancellationToken);
    }
}

/// <summary>Registering health checks.</summary>
public static class HealthCheckServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="HealthCheckService"/> and returns the builder the checks go on. Safe to
    /// call more than once — the second call adds to the same set.
    /// </summary>
    public static HealthCheckBuilder AddHealthChecks(this ShinyHttpServerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = (HealthCheckOptions?)builder.Services
            .FirstOrDefault(x => x.ServiceType == typeof(HealthCheckOptions))
            ?.ImplementationInstance;

        if (options is null)
        {
            options = new HealthCheckOptions();
            builder.Services.AddSingleton(options);
            builder.Services.TryAddSingleton(sp => new HealthCheckService(sp.GetRequiredService<HealthCheckOptions>(), sp));
        }

        return new HealthCheckBuilder(builder.Services, options);
    }
}
