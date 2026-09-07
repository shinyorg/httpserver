namespace Shiny.Net.HttpServer.Proxy;

/// <summary>Both halves of a cluster's health story.</summary>
public sealed class ClusterHealthCheckOptions
{
    /// <summary>Probing destinations on a timer, whether or not any traffic is flowing.</summary>
    public ActiveHealthCheckOptions Active { get; } = new();

    /// <summary>Watching what real requests do.</summary>
    public PassiveHealthCheckOptions Passive { get; } = new();
}

/// <summary>Polling destinations to find out whether they are alive.</summary>
public sealed class ActiveHealthCheckOptions
{
    /// <summary>Off by default: a probe every few seconds against every destination is not free,
    /// and on a battery-powered device it is the kind of not-free that shows up in a graph.</summary>
    public bool Enabled { get; set; }

    /// <summary>How often each destination is probed.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How long a probe may take before it counts as a failure.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Path probed on each destination, relative to its address (or its <see cref="ProxyDestination.Health"/>).</summary>
    public string Path { get; set; } = "/health";

    /// <summary>
    /// Decides whether a probe response means healthy. The default accepts any 2xx.
    /// <para>
    /// Worth replacing when the upstream reports its own readiness in a body or a header — an
    /// upstream that returns 200 while refusing work is exactly the case an active check exists for.
    /// </para>
    /// </summary>
    public Func<HttpResponseMessage, bool>? Policy { get; set; }
}

/// <summary>Taking a destination out of rotation because real requests to it are failing.</summary>
public sealed class PassiveHealthCheckOptions
{
    /// <summary>
    /// On by default for a cluster, off for the single-destination <c>MapProxy</c> shorthand.
    /// <para>
    /// The distinction matters: with one destination there is nowhere else to send the request, so
    /// quarantining it would only turn a 502 that names a real upstream failure into a 503 that
    /// names nothing.
    /// </para>
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Consecutive failures before the destination is taken out.</summary>
    public int FailureThreshold { get; set; } = 3;

    /// <summary>How long it stays out before traffic is tried again.</summary>
    public TimeSpan ReactivationPeriod { get; set; } = TimeSpan.FromSeconds(30);
}
