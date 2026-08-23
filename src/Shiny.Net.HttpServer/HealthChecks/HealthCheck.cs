namespace Shiny.Net.HttpServer.HealthChecks;

/// <summary>How healthy something is. Ordered worst-first so the aggregate is a <c>Min</c>.</summary>
public enum HealthStatus
{
    /// <summary>Broken. The aggregate report answers 503.</summary>
    Unhealthy = 0,

    /// <summary>Working, but not well — a queue backing up, a cache missing. Still a 200.</summary>
    Degraded = 1,

    Healthy = 2
}

/// <summary>The verdict from one check.</summary>
public readonly record struct HealthCheckResult
{
    public HealthCheckResult(
        HealthStatus status,
        string? description = null,
        Exception? exception = null,
        IReadOnlyDictionary<string, string>? data = null
    )
    {
        this.Status = status;
        this.Description = description;
        this.Exception = exception;
        this.Data = data;
    }

    public HealthStatus Status { get; }

    /// <summary>Human-readable detail, shown in the report.</summary>
    public string? Description { get; }

    /// <summary>What went wrong, when something did. Never serialised into the response body.</summary>
    public Exception? Exception { get; }

    /// <summary>
    /// Extra facts to publish with the entry — a version, a queue depth, a last-sync timestamp.
    /// <para>
    /// Strings rather than <c>object</c> deliberately: the report is written straight to the wire
    /// with no serializer context in sight, and "any object" would mean reflection.
    /// </para>
    /// </summary>
    public IReadOnlyDictionary<string, string>? Data { get; }

    public static HealthCheckResult Healthy(string? description = null, IReadOnlyDictionary<string, string>? data = null)
        => new(HealthStatus.Healthy, description, data: data);

    public static HealthCheckResult Degraded(string? description = null, Exception? exception = null, IReadOnlyDictionary<string, string>? data = null)
        => new(HealthStatus.Degraded, description, exception, data);

    public static HealthCheckResult Unhealthy(string? description = null, Exception? exception = null, IReadOnlyDictionary<string, string>? data = null)
        => new(HealthStatus.Unhealthy, description, exception, data);
}

/// <summary>What a check is being asked about.</summary>
public sealed class HealthCheckContext(HealthCheckRegistration registration)
{
    /// <summary>The registration being run — its name, tags and configured failure status.</summary>
    public HealthCheckRegistration Registration { get; } = registration;
}

/// <summary>
/// One thing worth knowing about before the server is called healthy.
/// <code>
/// public sealed class DatabaseHealthCheck(IDbConnection db) : IHealthCheck
/// {
///     public async ValueTask&lt;HealthCheckResult&gt; CheckAsync(HealthCheckContext context, CancellationToken ct)
///         => await db.CanConnectAsync(ct) ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("no connection");
/// }
/// </code>
/// </summary>
public interface IHealthCheck
{
    ValueTask<HealthCheckResult> CheckAsync(HealthCheckContext context, CancellationToken cancellationToken);
}

/// <summary>A check plus everything the runner needs to know about how to treat it.</summary>
public sealed class HealthCheckRegistration
{
    public HealthCheckRegistration(
        string name,
        Func<IServiceProvider, IHealthCheck> factory,
        HealthStatus failureStatus = HealthStatus.Unhealthy,
        IEnumerable<string>? tags = null,
        TimeSpan? timeout = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(factory);

        this.Name = name;
        this.Factory = factory;
        this.FailureStatus = failureStatus;
        this.Timeout = timeout;

        if (tags is not null)
        {
            foreach (var tag in tags)
                this.Tags.Add(tag);
        }
    }

    /// <summary>The name the entry appears under. Unique within the set.</summary>
    public string Name { get; }

    public Func<IServiceProvider, IHealthCheck> Factory { get; }

    /// <summary>What a thrown exception is reported as. Set it to <see cref="HealthStatus.Degraded"/> for a check whose failure is survivable.</summary>
    public HealthStatus FailureStatus { get; }

    /// <summary>
    /// Labels the endpoint filters on. The convention is <c>live</c> for "the process is up" and
    /// <c>ready</c> for "it can actually serve" — an orchestrator that cannot tell them apart
    /// restarts a container that was only waiting on a dependency.
    /// </summary>
    public ISet<string> Tags { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How long this check may take before it is failed for you. Null falls back to
    /// <see cref="HealthCheckOptions.DefaultTimeout"/>.
    /// </summary>
    public TimeSpan? Timeout { get; }
}
