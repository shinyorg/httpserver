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
        await WaitUntil(() => mdns.Published.Count > 1 || mdns.Published[0].Port != first, Token);

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
        await WaitUntil(() => advertiser.Publication is null, Token);

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

    static async Task WaitUntil(Func<bool> condition, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);

        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(20, cancellationToken);
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

    public List<MdnsServiceRegistration> Published { get; } = [];

    public int GoodbyesSent { get; private set; }

    /// <summary>Simulates the responder resolving a name conflict, which it is entitled to do.</summary>
    public string? RenameTo { get; set; }

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
        this.Published.Add(registration);

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
            owner.GoodbyesSent++;
            return default;
        }
    }
}
