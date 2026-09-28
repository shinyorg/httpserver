using System.Net;
using Shiny.Net.HttpServer.Tunneling;

namespace Shiny.Net.HttpServer.Tests;

/// <summary>
/// A relay and a tunnel in front of an already-configured server, for features that have to be shown
/// to survive the trip through an <see cref="ITunnelProvider"/> — every hop real, the client only ever
/// talking to the relay.
/// </summary>
sealed class RelayHarness : IAsyncDisposable
{
    readonly RelayServer relay;
    readonly RelayTunnelProvider provider;
    readonly CancellationTokenSource stopping;
    readonly Task running;
    readonly string host;

    RelayHarness(RelayServer relay, RelayTunnelProvider provider, CancellationTokenSource stopping, Task running, string host)
    {
        this.relay = relay;
        this.provider = provider;
        this.stopping = stopping;
        this.running = running;
        this.host = host;
        this.Client = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{relay.PublicPort}"),
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    public HttpClient Client { get; }

    public static async Task<RelayHarness> StartAsync(HttpServer server, string subdomain)
    {
        var relay = new RelayServer(new RelayServerOptions
        {
            Address = IPAddress.Loopback,
            ControlPort = 0,
            PublicPort = 0,
            Domain = "localhost",
            Token = "secret"
        });

        await relay.StartAsync();

        var provider = new RelayTunnelProvider(new RelayTunnelOptions
        {
            Host = "127.0.0.1",
            Port = relay.ControlPort,
            Subdomain = subdomain,
            Token = "secret",
            UseTls = false,
            ReconnectDelay = null
        });

        var stopping = new CancellationTokenSource();
        var running = server.RunTunnelAsync(provider, cancellationToken: stopping.Token);

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (provider.PublicUrl is null && DateTime.UtcNow < deadline)
            await Task.Delay(25);

        Assert.NotNull(provider.PublicUrl);

        return new RelayHarness(relay, provider, stopping, running, $"{subdomain}.localhost");
    }

    /// <summary>Addresses a request to the tunnel by its host, the way the relay routes it.</summary>
    public HttpRequestMessage Route(HttpRequestMessage request)
    {
        request.Headers.Host = this.host;
        return request;
    }

    public async ValueTask DisposeAsync()
    {
        this.Client.Dispose();
        await this.stopping.CancelAsync();

        try
        {
            await this.running.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
        }

        await this.provider.DisposeAsync();
        await this.relay.DisposeAsync();
        this.stopping.Dispose();
    }
}
