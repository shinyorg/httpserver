namespace Shiny.Net.HttpServer.Proxy;

/// <summary>The built-in ways of choosing between healthy destinations.</summary>
public enum LoadBalancingPolicy
{
    /// <summary>
    /// Samples two destinations at random and takes the less busy of the two.
    /// <para>
    /// The default, and the one to keep unless there is a reason not to: it costs two random numbers
    /// rather than a scan of every destination, and it avoids the failure mode that makes plain
    /// least-requests dangerous — every proxy instance agreeing that the same idle destination is
    /// the best one and stampeding it simultaneously.
    /// </para>
    /// </summary>
    PowerOfTwoChoices = 0,

    /// <summary>Each request goes to the next destination in order.</summary>
    RoundRobin,

    /// <summary>The destination with the fewest requests in flight.</summary>
    LeastRequests,

    /// <summary>Uniformly random.</summary>
    Random,

    /// <summary>Always the first available destination. Active/standby, not balancing.</summary>
    First
}

/// <summary>Chooses which destination gets a request.</summary>
public interface ILoadBalancingPolicy
{
    /// <summary>Name, for logs and for the <c>LoadBalancingPolicy</c> value in configuration.</summary>
    string Name { get; }

    /// <summary>
    /// Picks one of <paramref name="available"/>, which is never empty and contains only destinations
    /// both health checks currently agree on. Returning null is a 503.
    /// </summary>
    ProxyDestination? Pick(IReadOnlyList<ProxyDestination> available, HttpContext context);
}

/// <summary>The built-in policies, and the mapping from <see cref="LoadBalancingPolicy"/> to one.</summary>
public static class LoadBalancingPolicies
{
    public static ILoadBalancingPolicy PowerOfTwoChoices { get; } = new PowerOfTwoChoicesPolicy();
    public static ILoadBalancingPolicy RoundRobin { get; } = new RoundRobinPolicy();
    public static ILoadBalancingPolicy LeastRequests { get; } = new LeastRequestsPolicy();
    public static ILoadBalancingPolicy Random { get; } = new RandomPolicy();
    public static ILoadBalancingPolicy First { get; } = new FirstPolicy();

    /// <summary>The policy implementing an enum value.</summary>
    public static ILoadBalancingPolicy For(LoadBalancingPolicy policy) => policy switch
    {
        LoadBalancingPolicy.RoundRobin => RoundRobin,
        LoadBalancingPolicy.LeastRequests => LeastRequests,
        LoadBalancingPolicy.Random => Random,
        LoadBalancingPolicy.First => First,
        _ => PowerOfTwoChoices
    };

    /// <summary>
    /// Parses a configuration value. Null when nothing was configured; a misspelled name throws
    /// rather than quietly falling back to the default, because a typo that silently changes how
    /// traffic is spread is not something anyone will notice from the outside.
    /// </summary>
    public static ILoadBalancingPolicy? Parse(string? name) => name?.ToLowerInvariant() switch
    {
        null or "" => null,
        "poweroftwochoices" or "p2c" => PowerOfTwoChoices,
        "roundrobin" => RoundRobin,
        "leastrequests" => LeastRequests,
        "random" => Random,
        "first" => First,
        _ => throw new InvalidOperationException(
            $"'{name}' is not a load-balancing policy. Use PowerOfTwoChoices, RoundRobin, LeastRequests, Random or First, " +
            "or set ProxyCluster.LoadBalancer to an ILoadBalancingPolicy of your own."
        )
    };

    sealed class PowerOfTwoChoicesPolicy : ILoadBalancingPolicy
    {
        public string Name => "PowerOfTwoChoices";

        public ProxyDestination? Pick(IReadOnlyList<ProxyDestination> available, HttpContext context)
        {
            if (available.Count == 1)
                return available[0];

            var first = available[System.Random.Shared.Next(available.Count)];
            var second = available[System.Random.Shared.Next(available.Count)];

            return first.ConcurrentRequests <= second.ConcurrentRequests ? first : second;
        }
    }

    sealed class RoundRobinPolicy : ILoadBalancingPolicy
    {
        // One counter for every cluster using the shared instance. That is deliberate: the counter
        // only has to advance, and which destination index it lands on for a given cluster does not
        // need to be predictable — only that it keeps moving.
        int counter;

        public string Name => "RoundRobin";

        public ProxyDestination Pick(IReadOnlyList<ProxyDestination> available, HttpContext context)
            => available[(int)((uint)Interlocked.Increment(ref this.counter) % (uint)available.Count)];
    }

    sealed class LeastRequestsPolicy : ILoadBalancingPolicy
    {
        public string Name => "LeastRequests";

        public ProxyDestination Pick(IReadOnlyList<ProxyDestination> available, HttpContext context)
        {
            var best = available[0];
            var bestLoad = best.ConcurrentRequests;

            for (var i = 1; i < available.Count; i++)
            {
                var load = available[i].ConcurrentRequests;
                if (load < bestLoad)
                {
                    best = available[i];
                    bestLoad = load;
                }
            }

            return best;
        }
    }

    sealed class RandomPolicy : ILoadBalancingPolicy
    {
        public string Name => "Random";

        public ProxyDestination Pick(IReadOnlyList<ProxyDestination> available, HttpContext context)
            => available[System.Random.Shared.Next(available.Count)];
    }

    sealed class FirstPolicy : ILoadBalancingPolicy
    {
        public string Name => "First";

        public ProxyDestination Pick(IReadOnlyList<ProxyDestination> available, HttpContext context)
            => available[0];
    }
}
