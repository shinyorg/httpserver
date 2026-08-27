using System.Net;
using Shiny.Net.Discovery;
using Shiny.Net.HttpServer.Discovery;

namespace Shiny.Net.HttpServer.Tests;

/// <summary>
/// The advertiser and the locator against a stand-in responder. What is worth testing here is the
/// wiring — that the advertisement follows the server's port and lifecycle, and that a browse
/// result becomes a URL a client can actually call — not whether multicast DNS works, which is the
/// discovery library's own problem.
/// </summary>
public class HttpServerAdvertiserTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Advertises_the_port_the_server_actually_bound()
    {
        var mdns = new FakeMdns();
        await using var test = await TestServer.StartAsync(server => { });

        await using var advertiser = (HttpServerAdvertiser)await test.Server.AdvertiseAsync(mdns, o => o.ServiceType = "_myapp._tcp", Token);

        var registration = Assert.Single(mdns.Published);

        Assert.Equal(test.Port, registration.Port);
        Assert.Equal("_myapp._tcp", registration.ServiceType);
        Assert.Equal(Environment.MachineName, registration.InstanceName);
    }

    [Fact]
    public async Task Publishes_the_conventional_txt_records()
    {
        var mdns = new FakeMdns();
        await using var test = await TestServer.StartAsync(server => { });

        await using var advertiser = (HttpServerAdvertiser)await test.Server.AdvertiseAsync(
            mdns,
            o =>
            {
                o.Path = "/api";
                o.TxtRecords["role"] = "controller";
            },
            Token
        );

        var records = Assert.Single(mdns.Published).TxtRecords!;

        Assert.Equal("/api", records["path"]);
        Assert.Equal("http", records["scheme"]);
        Assert.Equal("controller", records["role"]);
    }

    /// <summary>A server that is restarted onto a different port must not leave a stale record behind.</summary>
    [Fact]
    public async Task Follows_the_server_across_a_restart()
    {
        var mdns = new FakeMdns();
        await using var test = await TestServer.StartAsync(server => { });

        await using var advertiser = (HttpServerAdvertiser)await test.Server.AdvertiseAsync(mdns, cancellationToken: Token);

        var first = Assert.Single(mdns.Published).Port;

        await test.Server.RestartAsync(Token);
        await WaitUntil(() => mdns.Published.Count > 1 || mdns.Published[0].Port != first, Token, "the advertisement to follow the restart onto its new port");

        // Port 0 was configured, so the restart lands somewhere new and the advertisement follows.
        Assert.Equal(test.Server.ListenUrl, $"http://127.0.0.1:{mdns.Published[^1].Port}");
        Assert.True(mdns.GoodbyesSent >= 1, "the superseded publication should have been withdrawn");
    }

    [Fact]
    public async Task Withdraws_when_the_server_stops()
    {
        var mdns = new FakeMdns();
        await using var test = await TestServer.StartAsync(server => { });

        var advertiser = (HttpServerAdvertiser)await test.Server.AdvertiseAsync(mdns, cancellationToken: Token);

        await test.Server.StopAsync(Token);

        // The goodbye is what says the withdrawal finished, and the cleared property is not: the
        // advertiser drops Publication before it awaits the dispose that sends the packet, so a wait
        // on the property alone returns mid-withdrawal and races the very thing being asserted.
        await WaitUntil(() => mdns.GoodbyesSent == 1, Token, "the publication to be withdrawn");

        Assert.Null(advertiser.Publication);
        Assert.Equal(1, mdns.GoodbyesSent);
    }

    [Fact]
    public async Task Reports_the_name_the_responder_settled_on()
    {
        var mdns = new FakeMdns { RenameTo = "Kitchen Pi (2)" };
        await using var test = await TestServer.StartAsync(server => { });

        await using var advertiser = (HttpServerAdvertiser)await test.Server.AdvertiseAsync(
            mdns,
            o => o.InstanceName = "Kitchen Pi",
            Token
        );

        Assert.Equal("Kitchen Pi (2)", advertiser.Publication!.InstanceName);
    }

    /// <summary>
    /// The moment the advertiser publishes is the moment a responder is least likely to answer — the
    /// server has just bound after a network change and the platform's mDNS stack is coming back at
    /// its own pace. One refused registration used to be the end of it.
    /// </summary>
    [Fact]
    public async Task Retries_a_registration_the_responder_refused()
    {
        var mdns = new FakeMdns { PublishFailures = 2 };
        var logger = new RecordingLogger<HttpServerAdvertiser>();
        await using var test = await TestServer.StartAsync(server => { });

        await using var advertiser = new HttpServerAdvertiser(mdns, test.Server, FastRetries(), logger);
        await advertiser.StartAsync(Token);

        Assert.Equal(3, mdns.PublishAttempts);
        Assert.NotNull(advertiser.Publication);
        Assert.Equal(test.Port, advertiser.Publication!.Port);
        Assert.Empty(logger.At(Microsoft.Extensions.Logging.LogLevel.Error));
    }

    /// <summary>A server nothing on the link can find looks perfectly healthy from the inside, so it has to say so.</summary>
    [Fact]
    public async Task Says_so_at_error_when_the_advertisement_will_not_publish()
    {
        var mdns = new FakeMdns { PublishFailures = int.MaxValue };
        var logger = new RecordingLogger<HttpServerAdvertiser>();
        await using var test = await TestServer.StartAsync(server => { });

        await using var advertiser = new HttpServerAdvertiser(mdns, test.Server, FastRetries(), logger);
        await advertiser.StartAsync(Token);

        Assert.Equal(3, mdns.PublishAttempts);
        Assert.Null(advertiser.Publication);

        var error = Assert.Single(logger.At(Microsoft.Extensions.Logging.LogLevel.Error));

        Assert.NotNull(error.Exception);
        Assert.Contains("will not be discovered", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A restart is a Stopped and a Running milliseconds apart. The work each one causes is pushed
    /// off the server's lifecycle thread — it has to be — and once it is, the two are free to land in
    /// either order. A withdrawal applied on top of the publication that followed it leaves a server
    /// running and unfindable, with nothing further coming to correct it.
    /// </summary>
    [Fact]
    public async Task A_state_change_that_lands_out_of_order_does_not_undo_the_newer_one()
    {
        var mdns = new FakeMdns();
        var logger = new RecordingLogger<HttpServerAdvertiser>();
        await using var test = await TestServer.StartAsync(server => { });

        await using var advertiser = new HttpServerAdvertiser(mdns, test.Server, FastRetries(), logger);

        // Stamped in the order the server actually moved in, then applied the other way round.
        var stopped = advertiser.Next();
        var running = advertiser.Next();

        await advertiser.ApplyStateAsync(running, new HttpServerStateChange(HttpServerState.Running, HttpServerStateReason.Requested));
        await advertiser.ApplyStateAsync(stopped, new HttpServerStateChange(HttpServerState.Stopped, HttpServerStateReason.Requested));

        Assert.NotNull(advertiser.Publication);
        Assert.Equal(test.Port, advertiser.Publication!.Port);
        Assert.Equal(0, mdns.GoodbyesSent);
    }

    /// <summary>
    /// A restart's stop half must not make the service blink out and back. Peers holding a resolved
    /// address would drop it and have to find the device again, over a gap of milliseconds.
    /// </summary>
    [Theory]
    [InlineData(HttpServerStateReason.Restarting)]
    [InlineData(HttpServerStateReason.NetworkChanged)]
    public async Task Holds_the_record_through_a_stop_that_is_half_of_a_restart(HttpServerStateReason reason)
    {
        var mdns = new FakeMdns();
        await using var test = await TestServer.StartAsync(server => { });

        await using var advertiser = new HttpServerAdvertiser(mdns, test.Server, FastRetries());
        await advertiser.StartAsync(Token);

        await advertiser.ApplyStateAsync(advertiser.Next(), new HttpServerStateChange(HttpServerState.Stopped, reason));

        Assert.NotNull(advertiser.Publication);
        Assert.Equal(0, mdns.GoodbyesSent);
    }

    /// <summary>The other half of it: when the start never lands, that stop is real and the record has to go.</summary>
    [Fact]
    public async Task Withdraws_when_the_restart_it_was_holding_for_fails_to_bind()
    {
        var mdns = new FakeMdns();
        await using var test = await TestServer.StartAsync(server => { });

        await using var advertiser = new HttpServerAdvertiser(mdns, test.Server, FastRetries());
        await advertiser.StartAsync(Token);

        await advertiser.ApplyStateAsync(advertiser.Next(), new HttpServerStateChange(HttpServerState.Stopped, HttpServerStateReason.Restarting));
        await advertiser.ApplyStateAsync(
            advertiser.Next(),
            new HttpServerStateChange(HttpServerState.Stopped, HttpServerStateReason.BindFailed, new InvalidOperationException("the port is taken"))
        );

        Assert.Null(advertiser.Publication);
        Assert.Equal(1, mdns.GoodbyesSent);
    }

    /// <summary>The shipped policy with the waiting taken out of it.</summary>
    static HttpServerAdvertisementOptions FastRetries() => new()
    {
        PublishAttempts = 3,
        PublishRetryDelay = TimeSpan.FromMilliseconds(5),
        MaxPublishRetryDelay = TimeSpan.FromMilliseconds(20)
    };

    /// <summary>
    /// Polls until the condition holds, and says so when it never does. Returning quietly on the
    /// deadline left the timeout to be reported by whichever assert came next, which describes the
    /// state the wait was still waiting for rather than the wait that ran out.
    /// </summary>
    static async Task WaitUntil(Func<bool> condition, CancellationToken cancellationToken, string? what = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;

            await Task.Delay(20, cancellationToken);
        }

        Assert.Fail($"Timed out waiting for {what ?? "the condition"}");
    }
}

public class HttpServerLocatorTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Turns_a_resolved_service_into_a_base_address()
    {
        var mdns = new FakeMdns();
        mdns.Announce(new MdnsService
        {
            InstanceName = "Kitchen Pi",
            ServiceType = "_myapp._tcp",
            HostName = "kitchen.local",
            Port = 5000,
            Addresses = [IPAddress.Parse("192.168.1.40")],
            TxtRecords = new Dictionary<string, string> { ["path"] = "/api", ["scheme"] = "http" }
        });

        var found = await new HttpServerLocator(mdns).FindFirstAsync("_myapp._tcp", TimeSpan.FromSeconds(2), cancellationToken: Token);

        Assert.NotNull(found);
        Assert.Equal("Kitchen Pi", found.InstanceName);
        Assert.Equal("http://192.168.1.40:5000/api", found.BaseAddress.ToString());
    }

    [Fact]
    public async Task Prefers_an_ipv4_address_for_the_base_url()
    {
        var mdns = new FakeMdns();
        mdns.Announce(new MdnsService
        {
            InstanceName = "Dual",
            ServiceType = "_myapp._tcp",
            Port = 8080,
            Addresses = [IPAddress.Parse("fd00::1"), IPAddress.Parse("10.0.0.5")]
        });

        var found = await new HttpServerLocator(mdns).FindFirstAsync("_myapp._tcp", TimeSpan.FromSeconds(2), cancellationToken: Token);

        Assert.Equal("http://10.0.0.5:8080/", found!.BaseAddress.ToString());
        Assert.Equal("http://[fd00::1]:8080/", found.BaseAddressFor(IPAddress.Parse("fd00::1")).ToString());
    }

    [Fact]
    public async Task A_filter_picks_the_instance_the_app_cares_about()
    {
        var mdns = new FakeMdns();
        mdns.Announce(new MdnsService
        {
            InstanceName = "Printer",
            ServiceType = "_http._tcp",
            Port = 631,
            Addresses = [IPAddress.Parse("192.168.1.10")]
        });
        mdns.Announce(new MdnsService
        {
            InstanceName = "Kitchen Pi",
            ServiceType = "_http._tcp",
            Port = 5000,
            Addresses = [IPAddress.Parse("192.168.1.40")],
            TxtRecords = new Dictionary<string, string> { ["role"] = "controller" }
        });

        var found = await new HttpServerLocator(mdns).FindFirstAsync(
            "_http._tcp",
            TimeSpan.FromSeconds(2),
            x => x.TxtRecords.TryGetValue("role", out var role) && role == "controller",
            Token
        );

        Assert.Equal("Kitchen Pi", found!.InstanceName);
    }

    [Fact]
    public async Task Nothing_on_the_link_is_an_answer_rather_than_a_failure()
    {
        var found = await new HttpServerLocator(new FakeMdns())
            .FindFirstAsync("_nothing._tcp", TimeSpan.FromMilliseconds(200), cancellationToken: Token);

        Assert.Null(found);
    }

    [Fact]
    public async Task An_unresolved_instance_is_skipped()
    {
        var mdns = new FakeMdns();
        mdns.Announce(new MdnsService { InstanceName = "Half", ServiceType = "_myapp._tcp" });

        var found = await new HttpServerLocator(mdns)
            .FindFirstAsync("_myapp._tcp", TimeSpan.FromMilliseconds(300), cancellationToken: Token);

        Assert.Null(found);
    }
}

/// <summary>A responder that records what it was asked to publish and replays what it was told to find.</summary>
sealed class FakeMdns : IMdnsManager
{
    readonly List<MdnsBrowseResult> announcements = [];
    readonly List<MdnsServiceRegistration> published = [];
    readonly Lock sync = new();

    int goodbyes;
    int attempts;

    /// <summary>
    /// A snapshot, because the advertiser publishes and withdraws on its own task while the test
    /// thread is polling this — an unsynchronized list read that way tears or throws under load.
    /// </summary>
    public IReadOnlyList<MdnsServiceRegistration> Published
    {
        get
        {
            lock (this.sync)
                return [.. this.published];
        }
    }

    public int GoodbyesSent => Volatile.Read(ref this.goodbyes);

    /// <summary>Simulates the responder resolving a name conflict, which it is entitled to do.</summary>
    public string? RenameTo { get; set; }

    /// <summary>How many registrations to refuse before letting one through — a responder that is not up yet.</summary>
    public int PublishFailures { get; set; }

    /// <summary>Every attempt, refused ones included, so a test can see the retry rather than only its outcome.</summary>
    public int PublishAttempts => Volatile.Read(ref this.attempts);

    public void Announce(MdnsService service) => this.announcements.Add(new MdnsBrowseResult(MdnsBrowseStatus.Found, service));

    public async IAsyncEnumerable<MdnsBrowseResult> Browse(
        MdnsBrowseConfig config,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default
    )
    {
        foreach (var announcement in this.announcements)
        {
            if (announcement.Service.ServiceType == config.ServiceType)
                yield return announcement;
        }

        // Browsing never completes on its own — the contract the real one has, and the reason the
        // locator has to impose its own deadline.
        await Task.Delay(Timeout.Infinite, ct);
    }

    public Task<MdnsService?> Resolve(string instanceName, string serviceType, TimeSpan? timeout = null, CancellationToken ct = default)
        => Task.FromResult<MdnsService?>(null);

    public Task<IMdnsPublication> Publish(MdnsServiceRegistration registration, CancellationToken ct = default)
    {
        Interlocked.Increment(ref this.attempts);

        if (this.PublishFailures > 0)
        {
            this.PublishFailures--;
            return Task.FromException<IMdnsPublication>(new InvalidOperationException("the responder refused the registration"));
        }

        lock (this.sync)
            this.published.Add(registration);

        return Task.FromResult<IMdnsPublication>(new FakePublication(
            this,
            this.RenameTo ?? registration.InstanceName,
            registration
        ));
    }

    sealed class FakePublication(FakeMdns owner, string instanceName, MdnsServiceRegistration registration) : IMdnsPublication
    {
        public string InstanceName { get; } = instanceName;
        public string ServiceType { get; } = registration.ServiceType;
        public string Domain { get; } = registration.Domain;
        public int Port { get; } = registration.Port;

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref owner.goodbyes);
            return default;
        }
    }
}
