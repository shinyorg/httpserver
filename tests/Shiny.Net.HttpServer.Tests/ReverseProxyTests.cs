using System.Net;
using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Shiny.Net.HttpServer.Proxy;
using Shiny.Net.HttpServer.WebSockets;

namespace Shiny.Net.HttpServer.Tests;

public class ReverseProxyTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>An upstream that says which one it is, so a balancing test can count.</summary>
    static Task<TestServer> Named(string name) => TestServer.StartAsync(server =>
    {
        server.MapGet("/who", ctx => ctx.Response.WriteTextAsync(name, cancellationToken: ctx.RequestAborted));
        server.MapGet("/health", ctx => ctx.Response.WriteTextAsync("ok", cancellationToken: ctx.RequestAborted));
        server.MapGet("/echo-path", ctx => ctx.Response.WriteTextAsync(
            ctx.Request.Path + (ctx.Request.QueryString ?? ""),
            cancellationToken: ctx.RequestAborted
        ));
        server.MapGet("/echo-header", ctx => ctx.Response.WriteTextAsync(
            ctx.Request.Headers.GetFirst("X-Tenant") ?? "(none)",
            cancellationToken: ctx.RequestAborted
        ));
        server.MapGet("/branded", ctx =>
        {
            ctx.Response.Headers.Set("X-Upstream-Secret", "leaky");
            return ctx.Response.WriteTextAsync(name, cancellationToken: ctx.RequestAborted);
        });
    });

    static async Task<Dictionary<string, int>> CountAsync(TestServer edge, string path, int requests)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var i = 0; i < requests; i++)
        {
            var who = await edge.Client.GetStringAsync(path, Token);
            counts[who] = counts.TryGetValue(who, out var count) ? count + 1 : 1;
        }

        return counts;
    }

    [Fact]
    public async Task Round_robin_spreads_requests_over_the_cluster()
    {
        await using var a = await Named("a");
        await using var b = await Named("b");

        await using var edge = await TestServer.StartAsync(server => server.MapProxy("/api/{*path}", cluster =>
        {
            cluster.LoadBalancing = LoadBalancingPolicy.RoundRobin;
            cluster.AddDestination("a", $"http://127.0.0.1:{a.Port}");
            cluster.AddDestination("b", $"http://127.0.0.1:{b.Port}");
        }));

        var counts = await CountAsync(edge, "/api/who", 10);

        Assert.Equal(5, counts["a"]);
        Assert.Equal(5, counts["b"]);
    }

    [Fact]
    public async Task First_policy_is_active_standby()
    {
        await using var a = await Named("a");
        await using var b = await Named("b");

        await using var edge = await TestServer.StartAsync(server => server.MapProxy("/api/{*path}", cluster =>
        {
            cluster.LoadBalancing = LoadBalancingPolicy.First;
            cluster.AddDestination("a", $"http://127.0.0.1:{a.Port}");
            cluster.AddDestination("b", $"http://127.0.0.1:{b.Port}");
        }));

        var counts = await CountAsync(edge, "/api/who", 5);

        Assert.Equal(5, counts["a"]);
        Assert.False(counts.ContainsKey("b"));
    }

    [Fact]
    public async Task A_failing_destination_is_taken_out_by_the_passive_check()
    {
        await using var good = await Named("good");

        ProxyCluster? cluster = null;

        await using var edge = await TestServer.StartAsync(server => cluster = server.MapProxy("/api/{*path}", c =>
        {
            c.LoadBalancing = LoadBalancingPolicy.RoundRobin;
            c.HealthCheck.Passive.FailureThreshold = 2;
            c.HealthCheck.Passive.ReactivationPeriod = TimeSpan.FromMinutes(5);
            c.Forwarding.Timeout = TimeSpan.FromSeconds(5);

            // Port 1 refuses immediately, which is what a dead instance looks like from here.
            c.AddDestination("dead", "http://127.0.0.1:1");
            c.AddDestination("good", $"http://127.0.0.1:{good.Port}");
        }));

        // Round robin hands the dead destination every other request until its streak runs out.
        for (var i = 0; i < 6; i++)
            await edge.Client.GetAsync("/api/who", Token);

        var dead = cluster!.Destinations.Single(x => x.Id == "dead");
        Assert.Equal(DestinationHealth.Unhealthy, dead.PassiveHealth);
        Assert.False(dead.IsAvailable);

        var counts = await CountAsync(edge, "/api/who", 4);
        Assert.Equal(4, counts["good"]);
    }

    [Fact]
    public async Task A_cluster_with_nothing_available_is_a_503()
    {
        await using var edge = await TestServer.StartAsync(server => server.MapProxy("/api/{*path}", cluster =>
        {
            cluster.HealthCheck.Passive.FailureThreshold = 1;
            cluster.AddDestination("dead", "http://127.0.0.1:1");
        }));

        Assert.Equal(HttpStatusCode.BadGateway, (await edge.Client.GetAsync("/api/who", Token)).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await edge.Client.GetAsync("/api/who", Token)).StatusCode);
    }

    [Fact]
    public async Task The_active_check_marks_a_dead_destination_before_any_traffic()
    {
        await using var good = await Named("good");

        ProxyCluster? cluster = null;

        await using var edge = await TestServer.StartAsync(server => cluster = server.MapProxy("/api/{*path}", c =>
        {
            c.HealthCheck.Active.Enabled = true;
            c.HealthCheck.Active.Interval = TimeSpan.FromMilliseconds(200);
            c.HealthCheck.Active.Timeout = TimeSpan.FromSeconds(2);
            c.AddDestination("dead", "http://127.0.0.1:1");
            c.AddDestination("good", $"http://127.0.0.1:{good.Port}");
        }));

        var dead = cluster!.Destinations.Single(x => x.Id == "dead");
        var alive = cluster.Destinations.Single(x => x.Id == "good");

        while (dead.ActiveHealth == DestinationHealth.Unknown || alive.ActiveHealth == DestinationHealth.Unknown)
            await Task.Delay(50, Token);

        Assert.Equal(DestinationHealth.Unhealthy, dead.ActiveHealth);
        Assert.Equal(DestinationHealth.Healthy, alive.ActiveHealth);

        var counts = await CountAsync(edge, "/api/who", 4);
        Assert.Equal(4, counts["good"]);
    }

    [Fact]
    public async Task Cookie_affinity_pins_a_caller_to_one_destination()
    {
        await using var a = await Named("a");
        await using var b = await Named("b");

        await using var edge = await TestServer.StartAsync(server => server.MapProxy("/api/{*path}", cluster =>
        {
            cluster.LoadBalancing = LoadBalancingPolicy.RoundRobin;
            cluster.SessionAffinity.Mode = SessionAffinityMode.Cookie;
            cluster.AddDestination("a", $"http://127.0.0.1:{a.Port}");
            cluster.AddDestination("b", $"http://127.0.0.1:{b.Port}");
        }));

        var first = await edge.Client.GetAsync("/api/who", Token);
        var who = await first.Content.ReadAsStringAsync(Token);

        var setCookie = Assert.Single(first.Headers.GetValues("Set-Cookie"));
        var cookie = setCookie.Split(';')[0];

        for (var i = 0; i < 6; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/who");
            request.Headers.Add("Cookie", cookie);

            using var response = await edge.Client.SendAsync(request, Token);
            Assert.Equal(who, await response.Content.ReadAsStringAsync(Token));
        }
    }

    [Fact]
    public async Task Transforms_rewrite_the_path_the_query_and_the_headers()
    {
        await using var upstream = await Named("up");

        await using var edge = await TestServer.StartAsync(server => server.MapProxy("/edge/{*path}", cluster =>
        {
            cluster.AddDestination("up", $"http://127.0.0.1:{upstream.Port}");
            cluster.Transforms
                .AddPathPrefix("/nested")
                .RemovePathPrefix("/nested")
                .SetQueryValue("tenant", "acme")
                .SetRequestHeader("X-Tenant", "acme")
                .RemoveResponseHeader("X-Upstream-Secret");
        }));

        Assert.Equal("/echo-path?tenant=acme", await edge.Client.GetStringAsync("/edge/echo-path", Token));
        Assert.Equal("acme", await edge.Client.GetStringAsync("/edge/echo-header", Token));

        using var branded = await edge.Client.GetAsync("/edge/branded", Token);
        Assert.False(branded.Headers.Contains("X-Upstream-Secret"));
    }

    [Fact]
    public async Task A_forwarded_query_the_transforms_did_not_touch_survives_intact()
    {
        await using var upstream = await Named("up");

        await using var edge = await TestServer.StartAsync(server =>
            server.MapProxy("/api/{*path}", $"http://127.0.0.1:{upstream.Port}"));

        Assert.Equal("/echo-path?a=1&b=2", await edge.Client.GetStringAsync("/api/echo-path?a=1&b=2", Token));
    }

    [Fact]
    public async Task A_websocket_is_forwarded_end_to_end()
    {
        await using var upstream = await TestServer.StartAsync(server => server.MapGet("/ws", async ctx =>
        {
            await using var socket = await ctx.AcceptWebSocketAsync(cancellationToken: ctx.RequestAborted);

            while (await socket.ReceiveAsync(ctx.RequestAborted) is { } message)
                await socket.SendAsync("echo:" + message.Text, cancellationToken: ctx.RequestAborted);
        }));

        await using var edge = await TestServer.StartAsync(server =>
            server.MapProxy("/proxy/{*path}", $"http://127.0.0.1:{upstream.Port}"));

        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{edge.Port}/proxy/ws"), Token);

        await client.SendAsync(Encoding.UTF8.GetBytes("hello"), WebSocketMessageType.Text, true, Token);

        var buffer = new byte[128];
        var received = await client.ReceiveAsync(buffer, Token);

        Assert.Equal("echo:hello", Encoding.UTF8.GetString(buffer, 0, received.Count));
    }

    [Fact]
    public async Task An_upgrade_is_not_forwarded_when_that_is_turned_off()
    {
        await using var upstream = await TestServer.StartAsync(server => server.MapGet("/ws", async ctx =>
        {
            await using var socket = await ctx.AcceptWebSocketAsync(cancellationToken: ctx.RequestAborted);
            while (await socket.ReceiveAsync(ctx.RequestAborted) is not null)
            {
            }
        }));

        await using var edge = await TestServer.StartAsync(server => server.MapProxy(
            "/proxy/{*path}",
            $"http://127.0.0.1:{upstream.Port}",
            o => o.ForwardUpgrades = false
        ));

        using var client = new ClientWebSocket();

        await Assert.ThrowsAnyAsync<WebSocketException>(
            async () => await client.ConnectAsync(new Uri($"ws://127.0.0.1:{edge.Port}/proxy/ws"), Token)
        );
    }

    // ---- configuration ----

    static (IConfigurationRoot Root, MemoryConfigurationProvider Provider) Config(Dictionary<string, string?> values)
    {
        var source = new MemoryConfigurationSource { InitialData = values };
        var root = new ConfigurationBuilder().Add(source).Build();

        return (root, (MemoryConfigurationProvider)root.Providers.Single());
    }

    [Fact]
    public async Task Routes_and_clusters_come_out_of_configuration()
    {
        await using var a = await Named("a");
        await using var b = await Named("b");

        var (root, _) = Config(new Dictionary<string, string?>
        {
            ["ReverseProxy:Routes:api:ClusterId"] = "backend",

            // Written the way YARP writes it, double star and all.
            ["ReverseProxy:Routes:api:Match:Path"] = "/api/{**rest}",
            ["ReverseProxy:Routes:api:Transforms:0:PathRemovePrefix"] = "/api",
            ["ReverseProxy:Clusters:backend:LoadBalancingPolicy"] = "RoundRobin",
            ["ReverseProxy:Clusters:backend:Destinations:a:Address"] = $"http://127.0.0.1:{a.Port}",
            ["ReverseProxy:Clusters:backend:Destinations:b:Address"] = $"http://127.0.0.1:{b.Port}"
        });

        await using var edge = await TestServer.StartAsync(server =>
            server.MapReverseProxy(root.GetSection("ReverseProxy")));

        var counts = await CountAsync(edge, "/api/who", 4);

        Assert.Equal(2, counts["a"]);
        Assert.Equal(2, counts["b"]);
    }

    [Fact]
    public async Task Two_routes_on_one_template_are_told_apart_by_host()
    {
        await using var a = await Named("a");
        await using var b = await Named("b");

        var (root, _) = Config(new Dictionary<string, string?>
        {
            ["ReverseProxy:Routes:first:ClusterId"] = "ca",
            ["ReverseProxy:Routes:first:Order"] = "0",
            ["ReverseProxy:Routes:first:Match:Path"] = "/{**rest}",
            ["ReverseProxy:Routes:first:Match:Hosts:0"] = "a.example.com",
            ["ReverseProxy:Routes:second:ClusterId"] = "cb",
            ["ReverseProxy:Routes:second:Order"] = "1",
            ["ReverseProxy:Routes:second:Match:Path"] = "/{**rest}",
            ["ReverseProxy:Routes:second:Match:Hosts:0"] = "*.other.com",
            ["ReverseProxy:Clusters:ca:Destinations:a:Address"] = $"http://127.0.0.1:{a.Port}",
            ["ReverseProxy:Clusters:cb:Destinations:b:Address"] = $"http://127.0.0.1:{b.Port}"
        });

        await using var edge = await TestServer.StartAsync(
            server => server.MapReverseProxy(root.GetSection("ReverseProxy")));

        Assert.Equal("a", await GetWithHostAsync(edge, "a.example.com"));
        Assert.Equal("b", await GetWithHostAsync(edge, "x.other.com"));

        using var unmatched = new HttpRequestMessage(HttpMethod.Get, "/who");
        unmatched.Headers.Host = "nobody.example.org";

        Assert.Equal(HttpStatusCode.NotFound, (await edge.Client.SendAsync(unmatched, Token)).StatusCode);
    }

    static async Task<string> GetWithHostAsync(TestServer edge, string host)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/who");
        request.Headers.Host = host;

        using var response = await edge.Client.SendAsync(request, Token);
        return await response.Content.ReadAsStringAsync(Token);
    }

    [Fact]
    public async Task A_reload_swaps_the_destination_without_restarting_the_server()
    {
        await using var a = await Named("a");
        await using var b = await Named("b");

        var (root, provider) = Config(new Dictionary<string, string?>
        {
            ["ReverseProxy:Routes:api:ClusterId"] = "backend",
            ["ReverseProxy:Routes:api:Match:Path"] = "/api/{**rest}",
            ["ReverseProxy:Clusters:backend:Destinations:only:Address"] = $"http://127.0.0.1:{a.Port}"
        });

        ReverseProxyRuntime? runtime = null;

        await using var edge = await TestServer.StartAsync(server =>
            runtime = server.MapReverseProxy(root.GetSection("ReverseProxy")));

        using var _ = runtime;

        Assert.Equal("a", await edge.Client.GetStringAsync("/api/who", Token));

        var reloaded = new TaskCompletionSource();
        runtime!.Reloaded += (_, _) => reloaded.TrySetResult();

        provider.Set("ReverseProxy:Clusters:backend:Destinations:only:Address", $"http://127.0.0.1:{b.Port}");
        root.Reload();

        await reloaded.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);

        Assert.Equal("b", await edge.Client.GetStringAsync("/api/who", Token));
    }

    [Fact]
    public async Task A_reload_leaves_routes_that_were_mapped_in_code_alone()
    {
        await using var upstream = await Named("up");

        var (root, provider) = Config(new Dictionary<string, string?>
        {
            ["ReverseProxy:Routes:api:ClusterId"] = "backend",
            ["ReverseProxy:Routes:api:Match:Path"] = "/api/{**rest}",
            ["ReverseProxy:Clusters:backend:Destinations:only:Address"] = $"http://127.0.0.1:{upstream.Port}"
        });

        ReverseProxyRuntime? runtime = null;

        await using var edge = await TestServer.StartAsync(server =>
        {
            server.MapGet("/mine", ctx => ctx.Response.WriteTextAsync("mine", cancellationToken: ctx.RequestAborted));
            runtime = server.MapReverseProxy(root.GetSection("ReverseProxy"));
        });

        using var _ = runtime;

        var reloaded = new TaskCompletionSource();
        runtime!.Reloaded += (_, _) => reloaded.TrySetResult();

        provider.Set("ReverseProxy:Routes:api:Order", "3");
        root.Reload();

        await reloaded.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);

        Assert.Equal("mine", await edge.Client.GetStringAsync("/mine", Token));
        Assert.Equal("up", await edge.Client.GetStringAsync("/api/who", Token));
    }

    /// <summary>
    /// A reload used to unmap every proxy route and map them back one at a time, and a request in
    /// between got a 404. Every change to the route table is watched here, and the route has to be in
    /// every table the reload publishes.
    /// </summary>
    [Fact]
    public async Task A_reload_never_publishes_a_table_without_the_routes_it_keeps()
    {
        await using var a = await Named("a");
        await using var b = await Named("b");

        var (root, provider) = Config(new Dictionary<string, string?>
        {
            ["ReverseProxy:Routes:api:ClusterId"] = "backend",
            ["ReverseProxy:Routes:api:Match:Path"] = "/api/{**rest}",
            ["ReverseProxy:Clusters:backend:Destinations:only:Address"] = $"http://127.0.0.1:{a.Port}"
        });

        ReverseProxyRuntime? runtime = null;

        await using var edge = await TestServer.StartAsync(server =>
            runtime = server.MapReverseProxy(root.GetSection("ReverseProxy")));

        using var _ = runtime;

        var router = edge.Server.Router;
        var tables = 0;
        var missing = 0;

        router.Changed += (_, _) =>
        {
            tables++;

            if (router.Find("GET", "/api/{*rest}") is null)
                missing++;
        };

        provider.Set("ReverseProxy:Clusters:backend:Destinations:only:Address", $"http://127.0.0.1:{b.Port}");
        provider.Set("ReverseProxy:Routes:extra:ClusterId", "backend");
        provider.Set("ReverseProxy:Routes:extra:Match:Path", "/extra/{**rest}");
        root.Reload();

        Assert.True(tables > 0);
        Assert.Equal(0, missing);
        Assert.Equal("b", await edge.Client.GetStringAsync("/api/who", Token));
        Assert.Equal("b", await edge.Client.GetStringAsync("/extra/who", Token));
    }

    /// <summary>
    /// The loader used to write each carried cluster's settings as it parsed them, so a file that
    /// failed further down left the live cluster half reconfigured while the reload claimed the old
    /// configuration stayed in force.
    /// </summary>
    [Fact]
    public async Task A_reload_that_fails_leaves_the_live_clusters_untouched()
    {
        await using var a = await Named("a");
        await using var b = await Named("b");

        var (root, provider) = Config(new Dictionary<string, string?>
        {
            ["ReverseProxy:Routes:api:ClusterId"] = "backend",
            ["ReverseProxy:Routes:api:Match:Path"] = "/api/{**rest}",
            ["ReverseProxy:Clusters:backend:Destinations:only:Address"] = $"http://127.0.0.1:{a.Port}"
        });

        ReverseProxyRuntime? runtime = null;

        await using var edge = await TestServer.StartAsync(server =>
            runtime = server.MapReverseProxy(root.GetSection("ReverseProxy")));

        using var _ = runtime;

        var cluster = runtime!.Current.Clusters["backend"];
        var reloaded = false;
        runtime.Reloaded += (_, _) => reloaded = true;

        provider.Set("ReverseProxy:Clusters:backend:Destinations:only:Address", $"http://127.0.0.1:{b.Port}");
        provider.Set("ReverseProxy:Clusters:backend:HttpRequest:ActivityTimeout", "7");

        // A route with no ClusterId fails the load after the cluster above has been read.
        provider.Set("ReverseProxy:Routes:broken:Match:Path", "/broken/{**rest}");
        root.Reload();

        Assert.False(reloaded);
        Assert.Equal($"http://127.0.0.1:{a.Port}/", cluster.Destinations.Single().Address.ToString());
        Assert.NotEqual(TimeSpan.FromSeconds(7), cluster.Forwarding.Timeout);
        Assert.Equal("a", await edge.Client.GetStringAsync("/api/who", Token));
    }

    /// <summary>A cluster dropped from the configuration used to keep its probe loop running for good.</summary>
    [Fact]
    public async Task A_reload_disposes_the_clusters_it_drops()
    {
        await using var a = await Named("a");

        var kept = new Dictionary<string, string?>
        {
            ["ReverseProxy:Routes:api:ClusterId"] = "backend",
            ["ReverseProxy:Routes:api:Match:Path"] = "/api/{**rest}",
            ["ReverseProxy:Clusters:backend:Destinations:only:Address"] = $"http://127.0.0.1:{a.Port}"
        };

        // A key cannot be taken out of the in-memory provider, so this one swaps its data whole.
        var provider = new SwappableProvider(new Dictionary<string, string?>(kept)
        {
            ["ReverseProxy:Routes:old:ClusterId"] = "retired",
            ["ReverseProxy:Routes:old:Match:Path"] = "/old/{**rest}",
            ["ReverseProxy:Clusters:retired:HealthCheck:Active:Enabled"] = "true",
            ["ReverseProxy:Clusters:retired:HealthCheck:Active:Interval"] = "60",
            ["ReverseProxy:Clusters:retired:Destinations:only:Address"] = $"http://127.0.0.1:{a.Port}"
        });
        var root = new ConfigurationBuilder().Add(new SwappableSource(provider)).Build();

        ReverseProxyRuntime? runtime = null;

        await using var edge = await TestServer.StartAsync(server =>
            runtime = server.MapReverseProxy(root.GetSection("ReverseProxy")));

        using var _ = runtime;

        var retired = runtime!.Current.Clusters["retired"];
        var backend = runtime.Current.Clusters["backend"];
        Assert.NotNull(retired.ProbeLoop);

        provider.Swap(kept);
        root.Reload();

        Assert.True(retired.IsDisposed);
        Assert.False(backend.IsDisposed);
        await retired.ProbeLoop!.WaitAsync(TimeSpan.FromSeconds(5), Token);

        Assert.Equal(HttpStatusCode.NotFound, (await edge.Client.GetAsync("/old/who", Token)).StatusCode);
        Assert.Equal("a", await edge.Client.GetStringAsync("/api/who", Token));
    }

    sealed class SwappableProvider(Dictionary<string, string?> initial) : ConfigurationProvider
    {
        public override void Load() => this.Data = new Dictionary<string, string?>(this.current, StringComparer.OrdinalIgnoreCase);

        Dictionary<string, string?> current = initial;

        public void Swap(Dictionary<string, string?> data) => this.current = data;
    }

    sealed class SwappableSource(SwappableProvider provider) : IConfigurationSource
    {
        public IConfigurationProvider Build(IConfigurationBuilder builder) => provider;
    }

    /// <summary>
    /// The probe loop used to read the cancellation source when it ran rather than when it was
    /// started, so a stop that beat the thread pool to it threw a NullReferenceException inside the
    /// loop, and a stop and a start ran two loops on the second token. The loops are held back here
    /// until they have been stopped.
    /// </summary>
    [Fact]
    public async Task A_probe_loop_that_starts_after_a_stop_ends_quietly()
    {
        using var cluster = new ProxyCluster("held");
        cluster.HealthCheck.Active.Enabled = true;
        var destination = cluster.AddDestination("dead", "http://127.0.0.1:1");

        var gates = new List<TaskCompletionSource>();
        cluster.RunProbeLoop = loop =>
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            gates.Add(gate);

            return gate.Task.ContinueWith(_ => loop(), TaskScheduler.Default).Unwrap();
        };

        cluster.StartProbing();
        var first = cluster.ProbeLoop!;
        cluster.StopProbing();

        cluster.StartProbing();
        var second = cluster.ProbeLoop!;

        // The first loop runs while the second start is live, and must still end on its own token.
        gates[0].SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(5), Token);

        cluster.StopProbing();
        gates[1].SetResult();
        await second.WaitAsync(TimeSpan.FromSeconds(5), Token);

        Assert.Equal(DestinationHealth.Unknown, destination.ActiveHealth);
    }

    /// <summary>
    /// Disposing a cluster used to dispose the probe client under probes still using it, and the
    /// ObjectDisposedException that followed faulted the loop with nobody observing it.
    /// </summary>
    [Fact]
    public async Task Disposing_a_cluster_mid_probe_ends_the_loop_cleanly()
    {
        var cluster = new ProxyCluster("disposed");
        cluster.HealthCheck.Active.Enabled = true;
        var destination = cluster.AddDestination("dead", "http://127.0.0.1:1");

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cluster.RunProbeLoop = loop => gate.Task.ContinueWith(_ => loop(), TaskScheduler.Default).Unwrap();

        cluster.StartProbing();
        var loop = cluster.ProbeLoop!;

        cluster.Dispose();
        gate.SetResult();

        await loop.WaitAsync(TimeSpan.FromSeconds(5), Token);

        Assert.True(cluster.IsDisposed);
        Assert.Equal(DestinationHealth.Unknown, destination.ActiveHealth);
    }

    [Fact]
    public async Task An_encoded_query_the_transforms_did_not_touch_is_forwarded_byte_for_byte()
    {
        await using var upstream = await Named("up");

        await using var edge = await TestServer.StartAsync(server =>
            server.MapProxy("/api/{*path}", $"http://127.0.0.1:{upstream.Port}"));

        // An upstream that signs its own URLs, or one that tells %2F from /, notices a re-encode.
        Assert.Equal(
            "/echo-path?path=a%2Fb&q=x+y",
            await edge.Client.GetStringAsync("/api/echo-path?path=a%2Fb&q=x+y", Token)
        );
    }

    [Fact]
    public void A_misspelled_load_balancing_policy_is_rejected_rather_than_ignored()
    {
        var (root, _) = Config(new Dictionary<string, string?>
        {
            ["ReverseProxy:Routes:api:ClusterId"] = "backend",
            ["ReverseProxy:Routes:api:Match:Path"] = "/api/{**rest}",
            ["ReverseProxy:Clusters:backend:LoadBalancingPolicy"] = "LeastRequestz",
            ["ReverseProxy:Clusters:backend:Destinations:d1:Address"] = "http://127.0.0.1:9"
        });

        var error = Assert.Throws<InvalidOperationException>(
            () => ReverseProxyConfigurationLoader.Load(root.GetSection("ReverseProxy"))
        );

        Assert.Contains("LeastRequestz", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_route_with_no_cluster_is_rejected_when_it_is_read()
    {
        var (root, _) = Config(new Dictionary<string, string?>
        {
            ["ReverseProxy:Routes:api:Match:Path"] = "/api/{**rest}"
        });

        var error = Assert.Throws<InvalidOperationException>(
            () => ReverseProxyConfigurationLoader.Load(root.GetSection("ReverseProxy"))
        );

        Assert.Contains("ClusterId", error.Message, StringComparison.Ordinal);
    }
}
