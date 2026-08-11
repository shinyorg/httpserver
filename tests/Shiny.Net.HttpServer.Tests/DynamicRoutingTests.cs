using System.Net;
using Shiny.Net.HttpServer.Routing;

namespace Shiny.Net.HttpServer.Tests;

/// <summary>
/// Routes change at runtime. The table is swapped atomically, so a request in flight sees either
/// the whole change or none of it, and a route added after the server started is reachable
/// immediately.
/// </summary>
public class DynamicRoutingTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Adds_a_route_while_running()
    {
        await using var server = await TestServer.StartAsync(app => app.OnGet("/first", ctx => ctx.Response.WriteAsync("first")));

        Assert.Equal(HttpStatusCode.NotFound, (await server.Client.GetAsync("/second", Token)).StatusCode);

        server.Server.OnGet("/second", ctx => ctx.Response.WriteAsync("second"));

        Assert.Equal("second", await server.Client.GetStringAsync("/second", Token));
        Assert.Equal("first", await server.Client.GetStringAsync("/first", Token));
    }

    [Fact]
    public async Task Removes_a_route_while_running()
    {
        await using var server = await TestServer.StartAsync(app =>
        {
            app.OnGet("/keep", ctx => ctx.Response.WriteAsync("keep"));
            app.OnGet("/drop", ctx => ctx.Response.WriteAsync("drop"));
        });

        Assert.Equal("drop", await server.Client.GetStringAsync("/drop", Token));

        Assert.True(server.Server.Unmap("GET", "/drop"));

        Assert.Equal(HttpStatusCode.NotFound, (await server.Client.GetAsync("/drop", Token)).StatusCode);
        Assert.Equal("keep", await server.Client.GetStringAsync("/keep", Token));
    }

    [Fact]
    public async Task Removes_a_route_by_the_endpoint_it_returned()
    {
        await using var server = await TestServer.StartAsync(app => app.OnGet("/stay", ctx => ctx.Response.WriteAsync("stay")));

        var endpoint = server.Server.MapRoute("GET", "/temporary", ctx => ctx.Response.WriteAsync("temp"));
        Assert.Equal("temp", await server.Client.GetStringAsync("/temporary", Token));

        Assert.True(server.Server.Unmap(endpoint));
        Assert.False(server.Server.Unmap(endpoint));

        Assert.Equal(HttpStatusCode.NotFound, (await server.Client.GetAsync("/temporary", Token)).StatusCode);
    }

    [Fact]
    public async Task Reports_when_there_was_nothing_to_remove()
    {
        await using var server = await TestServer.StartAsync(app => app.OnGet("/x", ctx => ctx.Response.WriteAsync("x")));

        Assert.False(server.Server.Unmap("GET", "/never-registered"));
        Assert.False(server.Server.Unmap("POST", "/x"));
        Assert.True(server.Server.Unmap("GET", "/x"));
    }

    [Fact]
    public async Task Re_adds_a_route_that_was_removed()
    {
        await using var server = await TestServer.StartAsync(app => app.OnGet("/toggle", ctx => ctx.Response.WriteAsync("on")));

        server.Server.Unmap("GET", "/toggle");
        Assert.Equal(HttpStatusCode.NotFound, (await server.Client.GetAsync("/toggle", Token)).StatusCode);

        // Re-registering must not trip the duplicate check — the old one is genuinely gone.
        server.Server.OnGet("/toggle", ctx => ctx.Response.WriteAsync("back on"));
        Assert.Equal("back on", await server.Client.GetStringAsync("/toggle", Token));
    }

    [Fact]
    public async Task Clears_every_route_and_falls_through_to_the_terminal_handler()
    {
        await using var server = await TestServer.StartAsync(app =>
        {
            app.OnGet("/a", ctx => ctx.Response.WriteAsync("a"));
            app.OnGet("/b", ctx => ctx.Response.WriteAsync("b"));
            app.OnRequest(ctx => ctx.Response.WriteAsync("fallback"));
        });

        Assert.Equal("a", await server.Client.GetStringAsync("/a", Token));

        server.Server.ClearRoutes();

        Assert.Equal("fallback", await server.Client.GetStringAsync("/a", Token));
        Assert.Empty(server.Server.Router.Endpoints);
    }

    [Fact]
    public async Task Removes_a_group_of_routes_by_predicate()
    {
        await using var server = await TestServer.StartAsync(app =>
        {
            app.OnGet("/admin/one", ctx => ctx.Response.WriteAsync("1"));
            app.OnGet("/admin/two", ctx => ctx.Response.WriteAsync("2"));
            app.OnGet("/public", ctx => ctx.Response.WriteAsync("p"));
        });

        var removed = server.Server.UnmapAll(e => e.Template.RawText.StartsWith("/admin", StringComparison.Ordinal));

        Assert.Equal(2, removed);
        Assert.Equal(HttpStatusCode.NotFound, (await server.Client.GetAsync("/admin/one", Token)).StatusCode);
        Assert.Equal("p", await server.Client.GetStringAsync("/public", Token));
    }

    [Fact]
    public void Rejects_a_duplicate_without_disturbing_the_live_table()
    {
        var router = new Router();
        router.Add(new RouteEndpoint(_ => ValueTask.CompletedTask, "GET", RouteTemplate.Parse("/x")));

        Assert.Throws<InvalidOperationException>(
            () => router.Add(new RouteEndpoint(_ => ValueTask.CompletedTask, "GET", RouteTemplate.Parse("/x")))
        );

        // The rejected registration left the table exactly as it was, not half-changed.
        Assert.Single(router.Endpoints);
        Assert.True(router.Match("GET", "/x", new RouteValueDictionary()).IsMatch);
    }

    [Theory]
    [InlineData("/users/{id}", "users/{id}")]
    [InlineData("/users/{id}", "/users/{id}/")]
    [InlineData("/Users/{id}", "/users/{id}")]
    public void Recognises_the_same_route_written_differently(string registered, string removed)
    {
        var router = new Router();
        router.Add(new RouteEndpoint(_ => ValueTask.CompletedTask, "GET", RouteTemplate.Parse(registered)));

        Assert.True(router.Remove("GET", removed));
        Assert.Empty(router.Endpoints);
    }

    [Theory]
    [InlineData("/users/{id}", "/users/{userId}")]
    [InlineData("/users/{id}", "/users/{id:int}")]
    [InlineData("/users/{id}", "/users/{id?}")]
    [InlineData("/users/{id}", "/users/{*id}")]
    public void Does_not_confuse_routes_that_only_look_similar(string registered, string other)
    {
        var router = new Router();
        router.Add(new RouteEndpoint(_ => ValueTask.CompletedTask, "GET", RouteTemplate.Parse(registered)));

        Assert.False(router.Remove("GET", other));
        Assert.Single(router.Endpoints);
    }

    [Fact]
    public void Announces_changes()
    {
        var router = new Router();
        var counts = new List<int>();
        router.Changed += (_, count) => counts.Add(count);

        var endpoint = new RouteEndpoint(_ => ValueTask.CompletedTask, "GET", RouteTemplate.Parse("/x"));
        router.Add(endpoint);
        router.Remove(endpoint);

        Assert.Equal([1, 0], counts);
    }

    [Fact]
    public async Task Serves_correctly_while_routes_churn_underneath()
    {
        await using var server = await TestServer.StartAsync(app => app.OnGet("/stable", ctx => ctx.Response.WriteAsync("stable")));

        // Requests must never observe a half-built table: every response is either the stable
        // route's body or a clean 404 for the one being added and removed.
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var churn = Task.Run(
            () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    server.Server.OnGet("/churn", ctx => ctx.Response.WriteAsync("churn"));
                    server.Server.Unmap("GET", "/churn");
                }
            },
            CancellationToken.None
        );

        while (!stop.IsCancellationRequested)
        {
            Assert.Equal("stable", await server.Client.GetStringAsync("/stable", Token));

            var response = await server.Client.GetAsync("/churn", Token);
            Assert.Contains(response.StatusCode, (HttpStatusCode[])[HttpStatusCode.OK, HttpStatusCode.NotFound]);
        }

        await churn;
    }

    [Fact]
    public async Task Routes_survive_a_stop_and_start()
    {
        await using var server = await TestServer.StartAsync(app => app.OnGet("/x", ctx => ctx.Response.WriteAsync("x")));

        server.Server.OnGet("/added", ctx => ctx.Response.WriteAsync("added"));
        await server.Server.RestartAsync(Token);

        using var client = new HttpClient { BaseAddress = new Uri(server.Server.ListenUrl!) };
        Assert.Equal("added", await client.GetStringAsync("/added", Token));
    }
}
