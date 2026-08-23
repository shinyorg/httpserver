namespace Shiny.Net.HttpServer.HealthChecks;

/// <summary>One check's outcome, with what it cost to find out.</summary>
/// <param name="Name">The registration's name.</param>
/// <param name="Status">The verdict, after a thrown exception has been mapped to the registration's failure status.</param>
/// <param name="Description">Human-readable detail from the check.</param>
/// <param name="Duration">How long the check took.</param>
/// <param name="Exception">What the check threw, when it threw.</param>
/// <param name="Data">Extra facts the check published.</param>
/// <param name="Tags">The registration's tags.</param>
public sealed record HealthReportEntry(
    string Name,
    HealthStatus Status,
    string? Description,
    TimeSpan Duration,
    Exception? Exception,
    IReadOnlyDictionary<string, string>? Data,
    IReadOnlyList<string> Tags
);

/// <summary>Everything the checks said, plus the one status that summarises them.</summary>
public sealed class HealthReport(IReadOnlyList<HealthReportEntry> entries, TimeSpan totalDuration)
{
    public IReadOnlyList<HealthReportEntry> Entries { get; } = entries;

    public TimeSpan TotalDuration { get; } = totalDuration;

    /// <summary>
    /// The worst status any check reported, and <see cref="HealthStatus.Healthy"/> when there were
    /// no checks at all — a server with nothing to verify is up, not broken.
    /// </summary>
    public HealthStatus Status
    {
        get
        {
            var status = HealthStatus.Healthy;

            foreach (var entry in this.Entries)
            {
                if (entry.Status < status)
                    status = entry.Status;
            }

            return status;
        }
    }
}
