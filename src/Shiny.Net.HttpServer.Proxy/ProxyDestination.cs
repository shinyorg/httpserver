namespace Shiny.Net.HttpServer.Proxy;

/// <summary>What is currently believed about a destination.</summary>
public enum DestinationHealth
{
    /// <summary>Nothing has been observed yet. Treated as healthy — a proxy that refuses to send
    /// traffic until something has already succeeded never sends anything at all.</summary>
    Unknown = 0,

    Healthy = 1,

    Unhealthy = 2
}

/// <summary>
/// One upstream a cluster can send a request to, and everything known about how it is behaving.
/// <para>
/// Health is tracked in two independent places, exactly as the two checks observe it: the active
/// check knows whether the destination answers a probe, the passive one knows whether real requests
/// are failing. Either can take a destination out of rotation, and neither overwrites the other's
/// opinion — an active probe that passes while every real request is failing should not put a broken
/// destination back into service.
/// </para>
/// </summary>
public sealed class ProxyDestination
{
    int concurrent;
    int consecutiveFailures;
    long passiveUnhealthyUntil;

    public ProxyDestination(string id, Uri address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(address);

        if (!address.IsAbsoluteUri)
            throw new ArgumentException("A destination address must be an absolute URI.", nameof(address));

        this.Id = id;
        this.Address = address;
    }

    /// <summary>Stable identifier. Used in logs and as the seed for the affinity key.</summary>
    public string Id { get; }

    /// <summary>Where requests go.</summary>
    public Uri Address { get; }

    /// <summary>
    /// Where the active health probe goes, when that is not the same place as the traffic — a
    /// sidecar on another port, or a management endpoint. Null probes <see cref="Address"/>.
    /// </summary>
    public Uri? Health { get; set; }

    /// <summary>What the active probe last said. <see cref="DestinationHealth.Unknown"/> until one runs.</summary>
    public DestinationHealth ActiveHealth { get; internal set; } = DestinationHealth.Unknown;

    /// <summary>What real traffic says. Computed, because a passive failure heals by the clock.</summary>
    public DestinationHealth PassiveHealth
        => Volatile.Read(ref this.passiveUnhealthyUntil) > Environment.TickCount64
            ? DestinationHealth.Unhealthy
            : DestinationHealth.Healthy;

    /// <summary>Requests currently in flight to this destination. What the least-requests policies read.</summary>
    public int ConcurrentRequests => Volatile.Read(ref this.concurrent);

    /// <summary>True when neither check has taken it out of rotation.</summary>
    public bool IsAvailable
        => this.ActiveHealth != DestinationHealth.Unhealthy && this.PassiveHealth != DestinationHealth.Unhealthy;

    internal void BeginRequest() => Interlocked.Increment(ref this.concurrent);

    internal void EndRequest() => Interlocked.Decrement(ref this.concurrent);

    internal void ReportSuccess()
    {
        // A single success clears the streak but does not shorten an active quarantine: the point
        // of the reactivation period is that the destination gets a *small* amount of traffic back
        // and has to survive it, not that one lucky response ends the penalty early.
        Volatile.Write(ref this.consecutiveFailures, 0);
    }

    internal void ReportFailure(PassiveHealthCheckOptions options)
    {
        if (!options.Enabled)
            return;

        var failures = Interlocked.Increment(ref this.consecutiveFailures);

        if (failures >= options.FailureThreshold)
        {
            Volatile.Write(ref this.passiveUnhealthyUntil, Environment.TickCount64 + (long)options.ReactivationPeriod.TotalMilliseconds);
            Volatile.Write(ref this.consecutiveFailures, 0);
        }
    }

    /// <summary>Puts the destination back in rotation as far as the passive check is concerned.</summary>
    public void ResetPassiveHealth()
    {
        Volatile.Write(ref this.passiveUnhealthyUntil, 0);
        Volatile.Write(ref this.consecutiveFailures, 0);
    }

    public override string ToString() => $"{this.Id} ({this.Address})";
}
