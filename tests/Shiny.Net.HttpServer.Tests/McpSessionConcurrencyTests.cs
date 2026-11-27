using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Shiny.Net.HttpServer.Mcp;

namespace Shiny.Net.HttpServer.Tests;

/// <summary>A request entering a session against the idle sweeper and shutdown reclaiming it.</summary>
public class McpSessionConcurrencyTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    // Anything counts as idle: the clock is out of the picture, so what is tested is only who wins.
    static readonly TimeSpan AlwaysIdle = TimeSpan.FromMilliseconds(-1);

    static Task<TestServer> StartAsync()
        => TestServer.StartAsync(
            app => app.MapMcp(),
            builder => builder.Services
                .AddMcpServer(o => o.ServerInfo = new Implementation { Name = "shiny-test", Version = "1.0.0" })
                .WithTools<McpTestTools>()
                .WithHttpTransport()
        );

    static McpHttpSessionManager Sessions(TestServer server)
        => server.Server.Services!.GetRequiredService<McpHttpSessionManager>();

    [Fact]
    public async Task A_session_claimed_by_the_sweeper_refuses_a_request_that_already_looked_it_up()
    {
        await using var server = await StartAsync();
        var sessions = Sessions(server);

        var session = (await sessions.CreateAsync())!;
        session.Exit();

        // The request has found the session (the lookup half of what used to be TryGet then Enter)...
        Assert.Equal(1, sessions.Count);

        // ...the sweeper claims it in between...
        Assert.True(session.TryClaimIdle(AlwaysIdle));

        // ...and the request's entry is refused rather than running on a session about to be disposed.
        Assert.False(session.TryEnter());
        Assert.False(sessions.TryAcquire(session.Id, out _));

        await session.DisposeAsync();
    }

    [Fact]
    public async Task A_session_with_a_request_in_it_cannot_be_claimed()
    {
        await using var server = await StartAsync();
        var sessions = Sessions(server);

        var session = (await sessions.CreateAsync())!;
        session.Exit();

        Assert.True(sessions.TryAcquire(session.Id, out var acquired));
        Assert.Same(session, acquired);
        Assert.False(session.TryClaimIdle(AlwaysIdle));

        session.Exit();
        Assert.True(session.TryClaimIdle(AlwaysIdle));

        await session.DisposeAsync();
    }

    [Fact]
    public async Task A_session_used_since_the_sweeper_looked_is_handed_back()
    {
        await using var server = await StartAsync();
        var sessions = Sessions(server);

        var session = (await sessions.CreateAsync())!;
        session.Exit();

        // Just touched, and the timeout is generous: not idle, and the claim leaves it usable.
        Assert.False(session.TryClaimIdle(TimeSpan.FromHours(1)));
        Assert.True(session.TryEnter());
        session.Exit();
    }

    [Fact]
    public async Task A_disposed_session_refuses_new_requests()
    {
        await using var server = await StartAsync();
        var sessions = Sessions(server);

        var session = (await sessions.CreateAsync())!;
        session.Exit();
        Assert.True(await sessions.RemoveAsync(session.Id));

        Assert.False(session.TryEnter());
    }

    [Fact]
    public async Task A_request_to_a_reclaimed_session_is_told_it_is_gone()
    {
        await using var server = await StartAsync();
        var sessions = Sessions(server);

        var session = (await sessions.CreateAsync())!;
        session.Exit();
        Assert.True(session.TryClaimIdle(AlwaysIdle));

        // Claimed but not yet removed: still in the table, and still refused.
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""", Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        request.Headers.Add(McpHttpHandler.SessionIdHeader, session.Id);

        using var response = await server.Client.SendAsync(request, Token);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        await sessions.RemoveAsync(session.Id);
    }

    [Fact]
    public async Task No_session_is_created_once_shutdown_has_started()
    {
        await using var server = await StartAsync();
        var sessions = Sessions(server);

        await sessions.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await sessions.CreateAsync());
        Assert.Equal(0, sessions.Count);
    }
}
