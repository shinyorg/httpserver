using System.Diagnostics;

namespace Shiny.Net.HttpServer.HealthChecks;

/// <summary>The registered checks and how they are run.</summary>
public sealed class HealthCheckOptions
{
    /// <summary>Everything <c>AddHealthChecks()</c> registered.</summary>
    public IList<HealthCheckRegistration> Registrations { get; } = [];

    /// <summary>
    /// The ceiling on a check that did not name its own. A health endpoint that hangs is worse
    /// than one that answers Unhealthy, because a monitor cannot tell it apart from the server
    /// being gone.
    /// </summary>
    public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// Runs the registered checks and aggregates them.
/// <para>
/// Checks run concurrently: they are independent by construction, and a readiness probe that waits
/// for four sequential five-second timeouts has failed at its job long before it answers.
/// </para>
/// </summary>
public sealed class HealthCheckService(HealthCheckOptions options, IServiceProvider services)
{
    public HealthCheckOptions Options { get; } = options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>
    /// Runs every registration <paramref name="predicate"/> accepts (all of them, when it is null).
    /// </summary>
    public async Task<HealthReport> CheckHealthAsync(
        Func<HealthCheckRegistration, bool>? predicate = null,
        CancellationToken cancellationToken = default
    )
    {
        var selected = new List<HealthCheckRegistration>();

        foreach (var registration in this.Options.Registrations)
        {
            if (predicate is null || predicate(registration))
                selected.Add(registration);
        }

        var timestamp = Stopwatch.GetTimestamp();
        var running = new Task<HealthReportEntry>[selected.Count];

        for (var i = 0; i < selected.Count; i++)
            running[i] = this.RunAsync(selected[i], cancellationToken);

        var entries = await Task.WhenAll(running).ConfigureAwait(false);

        return new HealthReport(entries, Stopwatch.GetElapsedTime(timestamp));
    }

    async Task<HealthReportEntry> RunAsync(HealthCheckRegistration registration, CancellationToken cancellationToken)
    {
        var timestamp = Stopwatch.GetTimestamp();
        var timeout = registration.Timeout ?? this.Options.DefaultTimeout;

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout > TimeSpan.Zero)
            timeoutSource.CancelAfter(timeout);

        try
        {
            var check = registration.Factory(services);
            var result = await check
                .CheckAsync(new HealthCheckContext(registration), timeoutSource.Token)
                .ConfigureAwait(false);

            return new HealthReportEntry(
                registration.Name,
                result.Status,
                result.Description,
                Stopwatch.GetElapsedTime(timestamp),
                result.Exception,
                result.Data,
                [.. registration.Tags]
            );
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The check's own timeout, not the caller giving up. Reported as a failure with a
            // description a human can act on rather than as a cancelled task nobody sees.
            return this.Failed(registration, timestamp, $"Timed out after {timeout.TotalSeconds:0.##}s.", exception: null);
        }
        catch (Exception ex)
        {
            return this.Failed(registration, timestamp, ex.Message, ex);
        }
    }

    HealthReportEntry Failed(HealthCheckRegistration registration, long timestamp, string description, Exception? exception)
        => new(
            registration.Name,
            registration.FailureStatus,
            description,
            Stopwatch.GetElapsedTime(timestamp),
            exception,
            null,
            [.. registration.Tags]
        );
}
