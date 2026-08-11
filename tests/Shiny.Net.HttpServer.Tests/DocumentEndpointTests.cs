using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Shiny.DocumentDb;
using Shiny.DocumentDb.Sqlite;
using Shiny.Net.HttpServer.DocumentDb;
using Shiny.Net.HttpServer.Security;

namespace Shiny.Net.HttpServer.Tests;

// ---------------------------------------------------------------------------
// A real SQLite document store, a real server, a real socket. The endpoints are
// a port of Shiny.DocumentDb.AspNetCore onto this server, so what matters is
// that the HTTP contract came across intact — status codes, ETags, the RFC 7396
// null-means-remove rule, and the scope that has to hold on both sides of a
// write.
// ---------------------------------------------------------------------------

public class Order
{
    public string Id { get; set; } = "";
    public string Status { get; set; } = "open";
    public string CustomerId { get; set; } = "";
    public decimal Total { get; set; }
    public string? Notes { get; set; }
    public int Version { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Order))]
public partial class OrderJson : JsonSerializerContext;

/// <summary>A per-request service a scope can be built from, to exercise Scope&lt;TService&gt;.</summary>
public interface ICustomerContext
{
    string Resolve(HttpContext http);
}

sealed class CustomerContext : ICustomerContext
{
    public string Resolve(HttpContext http) => http.Request.Headers.GetFirst("X-Customer") ?? "";
}

/// <summary>A server with one mapped document resource over an in-memory SQLite store.</summary>
sealed class DocumentServer : IAsyncDisposable
{
    TestServer server = null!;

    public HttpClient Client => this.server.Client;

    public HttpServer Server => this.server.Server;

    public static async Task<DocumentServer> StartAsync(
        Action<DocumentEndpointOptions<Order>>? configure = null,
        Action<HttpServer>? mapExtra = null,
        Action<DocumentResourceBuilder>? compose = null,
        bool secured = false
    )
    {
        var fixture = new DocumentServer();
        var database = $"Data Source=rest_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";

        fixture.server = await TestServer.StartAsync(
            app =>
            {
                if (secured)
                {
                    app.UseAuthentication();
                    app.UseAuthorization();
                }

                var resource = app.MapDocuments<Order>("/orders", o =>
                {
                    o.Operations = DocumentEndpoints.All;
                    o.TypeInfo = OrderJson.Default.Order;
                    configure?.Invoke(o);
                });

                compose?.Invoke(resource);
                mapExtra?.Invoke(app);
            },
            builder =>
            {
                builder.Services.AddScoped<ICustomerContext, CustomerContext>();
                // A scheme has to exist for the authorization middleware to have something to challenge
                // with; without one a protected route is a 500 rather than a 401.
                builder.Services
                    .AddAuthentication()
                    .AddBasic((Action<BasicAuthenticationOptions>)(o => o.AddUser("u", "p")));

                builder.Services.AddAuthorization();
                builder.Services.AddDocumentStore(o =>
                {
                    o.DatabaseProvider = new SqliteDatabaseProvider(database);
                    o.JsonSerializerOptions = OrderJson.Default.Options;
                    o.ConfigureDocument<Order>(cfg => cfg.MapVersionProperty(x => x.Version));
                });
            }
        );

        return fixture;
    }

    public async Task SeedAsync(params Order[] orders)
    {
        var store = this.Server.Services!.GetRequiredService<IDocumentStore>();

        foreach (var order in orders)
            await store.Insert(order, OrderJson.Default.Order, TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => this.server.DisposeAsync();
}

public class DocumentEndpointTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    static Order NewOrder(string id, string customer = "c1", string status = "open", decimal total = 10m)
        => new() { Id = id, CustomerId = customer, Status = status, Total = total };

    [Fact]
    public async Task Lists_and_reads_by_id()
    {
        await using var server = await DocumentServer.StartAsync();
        await server.SeedAsync(NewOrder("a"), NewOrder("b"));

        var list = JsonNode.Parse(await server.Client.GetStringAsync("/orders", Token))!.AsArray();
        Assert.Equal(2, list.Count);

        var one = JsonNode.Parse(await server.Client.GetStringAsync("/orders/a", Token))!.AsObject();
        Assert.Equal("a", one["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Counts_with_a_filter()
    {
        await using var server = await DocumentServer.StartAsync();
        await server.SeedAsync(NewOrder("a", status: "open"), NewOrder("b", status: "closed"));

        var all = JsonNode.Parse(await server.Client.GetStringAsync("/orders/count", Token))!.AsObject();
        Assert.Equal(2, all["count"]!.GetValue<int>());

        var open = JsonNode.Parse(
            await server.Client.GetStringAsync("/orders/count?filter=status == 'open'", Token)
        )!.AsObject();

        Assert.Equal(1, open["count"]!.GetValue<int>());
    }

    [Fact]
    public async Task Pages_and_clamps_take_to_the_maximum()
    {
        await using var server = await DocumentServer.StartAsync(o => o.MaxPageSize = 2);
        await server.SeedAsync(NewOrder("a"), NewOrder("b"), NewOrder("c"), NewOrder("d"));

        // Clamped rather than refused: asking for more than the cap is a friendlier contract than a 400.
        var clamped = JsonNode.Parse(await server.Client.GetStringAsync("/orders?take=100", Token))!.AsArray();
        Assert.Equal(2, clamped.Count);

        var skipped = JsonNode.Parse(await server.Client.GetStringAsync("/orders?skip=2&take=2", Token))!.AsArray();
        Assert.Equal(2, skipped.Count);
    }

    [Fact]
    public async Task Projects_a_sparse_fieldset()
    {
        await using var server = await DocumentServer.StartAsync();
        await server.SeedAsync(NewOrder("a", total: 42m));

        var rows = JsonNode.Parse(await server.Client.GetStringAsync("/orders?fields=id,total", Token))!.AsArray();
        var row = rows[0]!.AsObject();

        Assert.True(row.ContainsKey("total"));
        Assert.False(row.ContainsKey("status"));
    }

    /// <summary>
    /// The allowlist is lexical and runs before the grammar parser, so an unlisted field is a 400 rather
    /// than a scan of a column nobody meant to publish.
    /// </summary>
    [Fact]
    public async Task Refuses_a_field_outside_the_allowlist()
    {
        await using var server = await DocumentServer.StartAsync(o => o.AllowFilterOn(x => x.Status));
        await server.SeedAsync(NewOrder("a"));

        var allowed = await server.Client.GetAsync("/orders?filter=status == 'open'", Token);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);

        var refused = await server.Client.GetAsync("/orders?filter=notes == 'x'", Token);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        // The id is always available, whatever the allowlist says — it is how a client addresses a document.
        var byId = await server.Client.GetAsync("/orders?filter=id == 'a'", Token);
        Assert.Equal(HttpStatusCode.OK, byId.StatusCode);
    }

    [Fact]
    public async Task Creates_and_reports_the_location()
    {
        await using var server = await DocumentServer.StartAsync();

        var response = await server.Client.PostAsync(
            "/orders",
            Json("""{"id":"new-1","status":"open","customerId":"c1","total":5}"""),
            Token
        );

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("/orders/new-1", response.Headers.Location?.ToString());

        Assert.Contains("new-1", await server.Client.GetStringAsync("/orders/new-1", Token));
    }

    [Fact]
    public async Task Replaces_and_moves_the_etag_on()
    {
        await using var server = await DocumentServer.StartAsync();
        await server.SeedAsync(NewOrder("a"));

        var before = await server.Client.GetAsync("/orders/a", Token);
        var etag = before.Headers.ETag?.Tag;
        Assert.NotNull(etag);

        var replace = await server.Client.PutAsync(
            "/orders/a",
            Json("""{"id":"a","status":"closed","customerId":"c1","total":99}"""),
            Token
        );

        Assert.Equal(HttpStatusCode.NoContent, replace.StatusCode);
        Assert.Contains("closed", await server.Client.GetStringAsync("/orders/a", Token));
    }

    [Fact]
    public async Task Refuses_a_stale_if_match()
    {
        await using var server = await DocumentServer.StartAsync();
        await server.SeedAsync(NewOrder("a"));

        using var request = new HttpRequestMessage(HttpMethod.Put, "/orders/a")
        {
            Content = Json("""{"id":"a","status":"closed","customerId":"c1","total":1}""")
        };
        request.Headers.IfMatch.Add(new EntityTagHeaderValue("\"999\""));

        var response = await server.Client.SendAsync(request, Token);

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
    }

    [Fact]
    public async Task Demands_an_if_match_when_the_resource_requires_one()
    {
        await using var server = await DocumentServer.StartAsync(o => o.RequireIfMatch = true);
        await server.SeedAsync(NewOrder("a"));

        var response = await server.Client.DeleteAsync("/orders/a", Token);

        Assert.Equal(428, (int)response.StatusCode);
    }

    /// <summary>
    /// RFC 7396: over HTTP an explicit null was written by the caller and means <b>remove</b>. The store's
    /// own merge cannot assume that — a serialized document carries nulls for every unset member — which is
    /// why the endpoints apply the merge themselves.
    /// </summary>
    [Fact]
    public async Task Patch_treats_an_explicit_null_as_a_removal()
    {
        await using var server = await DocumentServer.StartAsync();
        await server.SeedAsync(new Order { Id = "a", CustomerId = "c1", Notes = "keep me" });

        var patch = await server.Client.PatchAsync("/orders/a", Json("""{"notes":null}"""), Token);
        Assert.Equal(HttpStatusCode.NoContent, patch.StatusCode);

        var after = JsonNode.Parse(await server.Client.GetStringAsync("/orders/a", Token))!.AsObject();
        var notes = after["notes"];
        Assert.True(notes is null || notes.GetValueKind() == JsonValueKind.Null);
    }

    [Fact]
    public async Task Patch_leaves_untouched_members_alone()
    {
        await using var server = await DocumentServer.StartAsync();
        await server.SeedAsync(new Order { Id = "a", CustomerId = "c1", Status = "open", Total = 7m, Notes = "hello" });

        await server.Client.PatchAsync("/orders/a", Json("""{"status":"closed"}"""), Token);

        var after = JsonNode.Parse(await server.Client.GetStringAsync("/orders/a", Token))!.AsObject();

        Assert.Equal("closed", after["status"]!.GetValue<string>());
        Assert.Equal("hello", after["notes"]!.GetValue<string>());
        Assert.Equal(7m, after["total"]!.GetValue<decimal>());
    }

    [Fact]
    public async Task Deletes_and_then_reports_not_found()
    {
        await using var server = await DocumentServer.StartAsync();
        await server.SeedAsync(NewOrder("a"));

        Assert.Equal(HttpStatusCode.NoContent, (await server.Client.DeleteAsync("/orders/a", Token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await server.Client.GetAsync("/orders/a", Token)).StatusCode);
    }

    [Fact]
    public async Task Maps_only_the_operations_that_were_asked_for()
    {
        await using var server = await DocumentServer.StartAsync(o => o.Operations = DocumentEndpoints.Read);
        await server.SeedAsync(NewOrder("a"));

        Assert.Equal(HttpStatusCode.OK, (await server.Client.GetAsync("/orders/a", Token)).StatusCode);

        // Never mapped. The path still exists for GET, so refusing the verb is a 405 rather than a 404 —
        // routing's answer, not an authorization one.
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await server.Client.DeleteAsync("/orders/a", Token)).StatusCode);

        // /count was not mapped either, so this falls through to /orders/{id} and finds no such document.
        Assert.Equal(HttpStatusCode.NotFound, (await server.Client.GetAsync("/orders/count", Token)).StatusCode);
    }
}

/// <summary>
/// The scope is the security boundary, so it gets its own class. A document outside it is 404 on every
/// verb — never 403, which would confirm the record exists.
/// </summary>
public class DocumentScopeTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    // Scope<TService> resolves the service from the request scope and hands it the context. The ambient
    // IHttpContextAccessor is deliberately not used: on this server it is only published when
    // UseSessions() is in the pipeline, and a scope callback is given the context anyway.
    static Task<DocumentServer> StartAsync() => DocumentServer.StartAsync(
        o => o.Scope<ICustomerContext>((customer, ctx) =>
        {
            var id = customer.Resolve(ctx.Http);
            return x => x.CustomerId == id;
        })
    );

    static HttpRequestMessage As(string customer, HttpMethod method, string path, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Add("X-Customer", customer);

        return request;
    }

    [Fact]
    public async Task Filters_a_list_to_the_callers_scope()
    {
        await using var server = await StartAsync();
        await server.SeedAsync(
            new Order { Id = "a", CustomerId = "c1" },
            new Order { Id = "b", CustomerId = "c2" }
        );

        var response = await server.Client.SendAsync(As("c1", HttpMethod.Get, "/orders"), Token);
        var list = JsonNode.Parse(await response.Content.ReadAsStringAsync(Token))!.AsArray();

        Assert.Single(list);
        Assert.Equal("a", list[0]!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Reports_an_out_of_scope_document_as_missing()
    {
        await using var server = await StartAsync();
        await server.SeedAsync(new Order { Id = "b", CustomerId = "c2" });

        var response = await server.Client.SendAsync(As("c1", HttpMethod.Get, "/orders/b"), Token);

        // 404, not 403 — a 403 would confirm the document exists.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Refuses_a_write_that_lands_outside_the_scope()
    {
        await using var server = await StartAsync();

        var response = await server.Client.SendAsync(
            As("c1", HttpMethod.Post, "/orders", new StringContent(
                """{"id":"x","status":"open","customerId":"someone-else","total":1}""",
                Encoding.UTF8,
                "application/json"
            )),
            Token
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Refuses_a_delete_outside_the_scope()
    {
        await using var server = await StartAsync();
        await server.SeedAsync(new Order { Id = "b", CustomerId = "c2" });

        var response = await server.Client.SendAsync(As("c1", HttpMethod.Delete, "/orders/b"), Token);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // And it is still there.
        var still = await server.Client.SendAsync(As("c2", HttpMethod.Get, "/orders/b"), Token);
        Assert.Equal(HttpStatusCode.OK, still.StatusCode);
    }

    /// <summary>A scope whose service is not registered is a startup error, not a per-request surprise.</summary>
    [Fact]
    public async Task Refuses_to_map_a_scope_whose_service_is_missing()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await TestServer.StartAsync(
                app => app.MapDocuments<Order>("/orders", o =>
                {
                    o.TypeInfo = OrderJson.Default.Order;
                    o.Scope<IUnregistered>((_, _) => x => true);
                }),
                builder => builder.Services.AddDocumentStore(o =>
                    o.DatabaseProvider = new SqliteDatabaseProvider("Data Source=missing;Mode=Memory;Cache=Shared"))
            )
        );

        Assert.Contains("IUnregistered", error.Message);
    }
}

public interface IUnregistered;

/// <summary>
/// The builder that replaces ASP.NET's RouteGroupBuilder. What matters is that a policy stated once lands
/// on every route the resource mapped — including the ones added by a later Operations flag.
/// </summary>
public class DocumentResourceBuilderTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Fans_authorization_out_across_every_route()
    {
        await using var server = await DocumentServer.StartAsync(
            compose: resource => resource.RequireAuthorization(),
            secured: true
        );

        foreach (var path in new[] { "/orders", "/orders/a", "/orders/count" })
        {
            var response = await server.Client.GetAsync(path, Token);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        var write = await server.Client.PostAsync("/orders", new StringContent("{}", Encoding.UTF8, "application/json"), Token);
        Assert.Equal(HttpStatusCode.Unauthorized, write.StatusCode);
    }

    [Fact]
    public async Task Exposes_the_routes_it_mapped()
    {
        DocumentResourceBuilder? builder = null;

        await using var server = await DocumentServer.StartAsync(compose: resource => builder = resource);

        // read (2) + count + stream + write (3) + delete
        Assert.NotNull(builder);
        Assert.Equal(8, builder!.Routes.Count);
    }
}
