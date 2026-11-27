using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Shiny.DocumentDb;
using Shiny.Net.HttpServer.DocumentDb;
using Shiny.Net.HttpServer.DocumentDb.Internal;
using Shiny.Net.HttpServer.JsonPatch;

namespace Shiny.Net.HttpServer.Tests;

/// <summary>
/// Plays another client's write into the middle of an endpoint's own write, once.
/// <para>
/// The store runs a write and its interceptors as one unit, and <see cref="DocumentWriteContext.Store"/> is
/// bound to that unit, so a write made through it lands exactly where a concurrent client's would on a real
/// database — after the endpoint's read and before its write (<see cref="Before"/>), or after its write and
/// before it answers (<see cref="After"/>) — with no timing involved.
/// </para>
/// </summary>
sealed class InterleavedWrite(bool before) : IDocumentInterceptor
{
    int fired;

    public static InterleavedWrite Before => new(true);

    public static InterleavedWrite After => new(false);

    public Task BeforeWrite(DocumentWriteContext ctx, CancellationToken ct)
        => before ? this.WriteOnce(ctx, ct) : Task.CompletedTask;

    public Task AfterWrite(DocumentWriteContext ctx, CancellationToken ct)
        => before ? Task.CompletedTask : this.WriteOnce(ctx, ct);

    async Task WriteOnce(DocumentWriteContext ctx, CancellationToken ct)
    {
        // Not the seed's insert, and only once — the write below comes back through this same interceptor.
        if (ctx.Operation == DocumentOperation.Insert || Interlocked.Exchange(ref this.fired, 1) == 1)
            return;

        var order = await ctx.Store.Get("a", OrderJson.Default.Order, ct);
        order!.Notes = "written by someone else";
        await ctx.Store.Update(order, OrderJson.Default.Order, ct);
    }
}

public class DocumentConcurrencyTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    static HttpRequestMessage Request(HttpMethod method, string path, string? etag = null, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        if (etag != null)
            request.Headers.IfMatch.Add(new EntityTagHeaderValue(etag));

        return request;
    }

    async Task<Order> Stored(DocumentServer server)
    {
        var store = server.Server.Services!.GetRequiredService<IDocumentStore>();
        var order = await store.Get("a", OrderJson.Default.Order, Token);

        return order!;
    }

    [Fact]
    public async Task A_replace_answers_with_the_etag_of_its_own_write()
    {
        await using var server = await DocumentServer.StartAsync(interceptor: InterleavedWrite.After);
        await server.SeedAsync(new Order { Id = "a", CustomerId = "c1" });

        var replace = await server.Client.PutAsync(
            "/orders/a",
            Json("""{"id":"a","status":"closed","customerId":"c1","total":5}"""),
            Token
        );

        Assert.Equal(HttpStatusCode.NoContent, replace.StatusCode);

        // Seeded at 1, this write made 2, the other client's made 3. Handing out 3 would vouch for a change
        // this caller never saw.
        Assert.Equal("\"2\"", replace.Headers.ETag?.Tag);
        Assert.Equal(3, (await Stored(server)).Version);

        // And so the follow-up that trusts the ETag is refused instead of overwriting the other write.
        var followUp = await server.Client.SendAsync(
            Request(HttpMethod.Put, "/orders/a", replace.Headers.ETag!.Tag, Json("""{"id":"a","status":"open","customerId":"c1","total":5}""")),
            Token
        );

        Assert.Equal(HttpStatusCode.PreconditionFailed, followUp.StatusCode);
        Assert.Equal("written by someone else", (await Stored(server)).Notes);
    }

    [Fact]
    public async Task A_patch_answers_with_the_etag_of_its_own_write()
    {
        await using var server = await DocumentServer.StartAsync(interceptor: InterleavedWrite.After);
        await server.SeedAsync(new Order { Id = "a", CustomerId = "c1" });

        var patch = await server.Client.PatchAsync("/orders/a", Json("""{"status":"closed"}"""), Token);

        Assert.Equal(HttpStatusCode.NoContent, patch.StatusCode);
        Assert.Equal("\"2\"", patch.Headers.ETag?.Tag);
        Assert.Equal(3, (await Stored(server)).Version);
    }

    [Fact]
    public async Task A_delete_with_if_match_refuses_a_write_that_landed_after_the_check()
    {
        await using var server = await DocumentServer.StartAsync(interceptor: InterleavedWrite.Before);
        await server.SeedAsync(new Order { Id = "a", CustomerId = "c1" });

        var delete = await server.Client.SendAsync(Request(HttpMethod.Delete, "/orders/a", "\"1\""), Token);

        Assert.Equal(HttpStatusCode.PreconditionFailed, delete.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await server.Client.GetAsync("/orders/a", Token)).StatusCode);
    }

    [Fact]
    public async Task A_delete_with_a_current_if_match_still_deletes()
    {
        await using var server = await DocumentServer.StartAsync();
        await server.SeedAsync(new Order { Id = "a", CustomerId = "c1" });

        var delete = await server.Client.SendAsync(Request(HttpMethod.Delete, "/orders/a", "\"1\""), Token);

        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await server.Client.GetAsync("/orders/a", Token)).StatusCode);
    }

    [Fact]
    public async Task A_patch_refuses_a_write_that_landed_after_its_read()
    {
        await using var server = await DocumentServer.StartAsync(interceptor: InterleavedWrite.Before);
        await server.SeedAsync(new Order { Id = "a", CustomerId = "c1" });

        var patch = await server.Client.PatchAsync("/orders/a", Json("""{"status":"closed"}"""), Token);

        Assert.Equal(HttpStatusCode.PreconditionFailed, patch.StatusCode);

        var stored = await Stored(server);
        Assert.Equal("open", stored.Status);
        Assert.Equal(1, stored.Version);
    }

    [Fact]
    public async Task A_patch_that_leaves_the_scope_is_refused_before_it_is_written()
    {
        await using var server = await DocumentServer.StartAsync(o => o.Scope(ctx =>
        {
            var customer = ctx.Http.Request.Headers.GetFirst("X-Customer") ?? "";
            return x => x.CustomerId == customer;
        }));
        await server.SeedAsync(new Order { Id = "a", CustomerId = "c1" });

        var request = Request(HttpMethod.Patch, "/orders/a", content: Json("""{"customerId":"c2"}"""));
        request.Headers.Add("X-Customer", "c1");

        var patch = await server.Client.SendAsync(request, Token);

        Assert.Equal(HttpStatusCode.BadRequest, patch.StatusCode);

        var stored = await Stored(server);
        Assert.Equal("c1", stored.CustomerId);
        Assert.Equal(1, stored.Version);
    }

    [Fact]
    public async Task Json_patches_to_one_schema_free_document_do_not_lose_each_other()
    {
        await using var server = await DocumentServer.StartAsync(
            mapExtra: app => app.MapDocumentCollection("/notes", "notes", o => o.Operations = DocumentEndpoints.All)
        );

        var created = await server.Client.PostAsync("/notes", Json("""{"id":"n1","tags":[]}"""), Token);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var store = server.Server.Services!.GetRequiredService<IDocumentStore>();

        // Holding the document's lock lines both patches up behind it, so both have been issued before either
        // reads — the interleaving where each would read the same empty array and the second write erased the
        // first.
        Task<HttpResponseMessage> first, second;
        using (await DocumentWriteLocks.AcquireAsync(store, "notes", "n1", Token))
        {
            first = server.Client.PatchAsync("/notes/n1", Patch("""[{"op":"add","path":"/tags/-","value":"x"}]"""), Token);
            second = server.Client.PatchAsync("/notes/n1", Patch("""[{"op":"add","path":"/tags/-","value":"y"}]"""), Token);

            await Wait.UntilAsync(() => DocumentWriteLocks.Users(store, "notes", "n1") == 3, Token);
            Assert.Equal(3, DocumentWriteLocks.Users(store, "notes", "n1"));
        }

        Assert.Equal(HttpStatusCode.NoContent, (await first).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await second).StatusCode);

        var after = JsonNode.Parse(await server.Client.GetStringAsync("/notes/n1", Token))!;
        var tags = after["tags"]!.AsArray().Select(t => t!.GetValue<string>()).Order().ToArray();

        Assert.Equal(["x", "y"], tags);
        Assert.Equal(0, DocumentWriteLocks.Users(store, "notes", "n1"));
    }

    [Fact]
    public async Task A_scoped_schema_free_delete_only_removes_inside_the_scope()
    {
        await using var server = await DocumentServer.StartAsync(
            mapExtra: app => app.MapDocumentCollection("/notes", "notes", o =>
            {
                o.Operations = DocumentEndpoints.All;
                o.Scope(_ => "owner == 'c1'");
            })
        );

        var store = server.Server.Services!.GetRequiredService<IDocumentStore>();
        var notes = store.Collection("notes", "id");
        await notes.Insert(new JsonObject { ["id"] = "mine", ["owner"] = "c1" }, Token);
        await notes.Insert(new JsonObject { ["id"] = "theirs", ["owner"] = "c2" }, Token);

        Assert.Equal(HttpStatusCode.NotFound, (await server.Client.DeleteAsync("/notes/theirs", Token)).StatusCode);
        Assert.NotNull(await notes.Get("theirs", Token));

        Assert.Equal(HttpStatusCode.NoContent, (await server.Client.DeleteAsync("/notes/mine", Token)).StatusCode);
        Assert.Null(await notes.Get("mine", Token));
    }

    static StringContent Patch(string json) => new(json, Encoding.UTF8, JsonPatchDocument.MediaType);
}
