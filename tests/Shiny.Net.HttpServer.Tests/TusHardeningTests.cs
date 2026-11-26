using System.Net;
using Shiny.Net.HttpServer.Tus;

namespace Shiny.Net.HttpServer.Tests;

public partial class TusTests
{
    // ---- request size limits ----

    /// <summary>
    /// The PATCH routes were bounded by the server-wide body limit, so a client sending one PATCH
    /// bigger than it was cut off, resumed, and cut off again without ever seeing why.
    /// </summary>
    [Fact]
    public async Task Accepts_a_single_patch_larger_than_the_server_body_limit()
    {
        using var dir = new UploadDirectory();
        await using var server = await StartAsync(dir, builder: b => b.Options.Limits.MaxRequestBodySize = 1000);

        var url = await CreateAsync(server.Client, 5000);
        var response = await PatchAsync(server.Client, url, 0, Bytes(5000));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(5000, OffsetOf(response));
    }

    static long? PatchLimit(TusMountBuilder mount)
        => mount.Routes.Single(r => r.Endpoint.Method == HttpMethods.Patch).Endpoint.GetMetadata<IRequestSizeLimitMetadata>()?.MaxBodySize;

    [Fact]
    public async Task Bounds_the_upload_routes_by_max_size_instead()
    {
        using var dir = new UploadDirectory();
        TusMountBuilder? mount = null;

        await using var server = await TestServer.StartAsync(
            app => mount = app.MapTus("/files", o =>
            {
                o.Store = dir.Store();
                o.MaxSize = 3000;
            }),
            b => b.Options.Limits.MaxRequestBodySize = 1000
        );

        var url = await CreateAsync(server.Client, 3000);
        var response = await PatchAsync(server.Client, url, 0, Bytes(3000));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(3000, PatchLimit(mount!));
    }

    [Fact]
    public async Task Lets_an_explicit_route_limit_win()
    {
        using var dir = new UploadDirectory();
        TusMountBuilder? mount = null;

        await using var server = await TestServer.StartAsync(app =>
            mount = app.MapTus("/files", o => o.Store = dir.Store()).ForEach(r => r.WithRequestSizeLimit(123)));

        Assert.Equal(123, PatchLimit(mount!));
    }

    // ---- durability ----

    static TusMountBuilder MapExpiring(HttpServer app, UploadDirectory dir, ManualTimeProvider clock)
        => app.MapTus("/files", o =>
        {
            o.Store = dir.Store();
            o.Expiration = TimeSpan.FromHours(1);
            o.TimeProvider = clock;
        });

    /// <summary>
    /// A crash between creating the data file and writing its info - or a power cut that loses the
    /// info - left bytes nothing would ever sweep, because the sweep only looked at info files.
    /// </summary>
    [Fact]
    public async Task Sweeps_data_whose_info_is_missing_once_it_is_old_enough()
    {
        using var dir = new UploadDirectory();
        var clock = new ManualTimeProvider { Now = DateTimeOffset.UtcNow };
        TusMountBuilder? mount = null;

        await using var server = await TestServer.StartAsync(app => mount = MapExpiring(app, dir, clock));

        var url = await CreateAsync(server.Client, 10);
        await PatchAsync(server.Client, url, 0, Bytes(4));
        File.Delete(Path.Combine(dir.Path, IdOf(url) + ".info"));

        Assert.Equal(HttpStatusCode.NotFound, (await HeadAsync(server.Client, url)).StatusCode);

        // Young enough to be an upload still being created: left alone.
        Assert.Equal(0, await mount!.RemoveExpiredUploadsAsync(Token));
        Assert.Equal(4, dir.LengthOf(IdOf(url)));

        clock.Now = clock.Now.AddHours(2);

        Assert.Equal(1, await mount.RemoveExpiredUploadsAsync(Token));
        Assert.Equal(-1, dir.LengthOf(IdOf(url)));
    }

    /// <summary>An info file a power cut left empty is an upload that is gone, and is cleaned up as one.</summary>
    [Fact]
    public async Task Treats_an_unreadable_info_as_gone_and_sweeps_it()
    {
        using var dir = new UploadDirectory();
        var clock = new ManualTimeProvider { Now = DateTimeOffset.UtcNow };
        TusMountBuilder? mount = null;

        await using var server = await TestServer.StartAsync(app => mount = MapExpiring(app, dir, clock));

        var url = await CreateAsync(server.Client, 10);
        var info = Path.Combine(dir.Path, IdOf(url) + ".info");
        File.WriteAllBytes(info, []);

        Assert.Equal(HttpStatusCode.NotFound, (await HeadAsync(server.Client, url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PatchAsync(server.Client, url, 0, Bytes(2))).StatusCode);

        clock.Now = clock.Now.AddHours(2);

        Assert.Equal(1, await mount!.RemoveExpiredUploadsAsync(Token));
        Assert.Empty(Directory.EnumerateFiles(dir.Path));
    }

    [Fact]
    public async Task Leaves_no_temporary_files_behind()
    {
        using var dir = new UploadDirectory();
        await using var server = await StartAsync(dir, o => o.Expiration = TimeSpan.FromHours(1));

        var url = await CreateAsync(server.Client, 10);
        await PatchAsync(server.Client, url, 0, Bytes(5));
        await PatchAsync(server.Client, url, 5, Bytes(5));

        Assert.Equal(
            [IdOf(url), IdOf(url) + ".info"],
            Directory.EnumerateFiles(dir.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal)
        );
    }

    // ---- ownership ----

    static Task<TestServer> StartOwnedAsync(UploadDirectory dir, Action<TusOptions>? configure = null)
        => StartAsync(dir, o =>
        {
            o.OwnerSelector = ctx => ctx.Request.Headers.GetFirst("X-User");
            configure?.Invoke(o);
        });

    static HttpRequestMessage As(string user, HttpRequestMessage request)
    {
        request.Headers.Add("X-User", user);
        return request;
    }

    static async Task<string> CreateAsAsync(HttpClient client, string user, long length, string? concat = null)
    {
        using var request = As(user, Tus(HttpMethod.Post, "/files"));
        request.Headers.Add(TusHeaderNames.UploadLength, length.ToString());

        if (concat is not null)
            request.Headers.Add(TusHeaderNames.UploadConcat, concat);

        var response = await Send(client, request, Token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return response.Headers.Location!.AbsolutePath;
    }

    /// <summary>
    /// Any authorised caller holding an upload's URL could read, append to or delete it. With an
    /// owner recorded, everyone else is told it does not exist.
    /// </summary>
    [Fact]
    public async Task Refuses_another_owners_upload()
    {
        using var dir = new UploadDirectory();
        await using var server = await StartOwnedAsync(dir);

        var url = await CreateAsAsync(server.Client, "alice", 10);

        Assert.Equal(HttpStatusCode.NotFound, (await Send(server.Client, As("bob", Tus(HttpMethod.Head, url)), Token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PatchAsync(server.Client, url, 0, Bytes(4), r => r.Headers.Add("X-User", "bob"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(server.Client, As("bob", Tus(HttpMethod.Delete, url)), Token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await HeadAsync(server.Client, url)).StatusCode);

        Assert.Equal(0, dir.LengthOf(IdOf(url)));

        var patched = await PatchAsync(server.Client, url, 0, Bytes(4), r => r.Headers.Add("X-User", "alice"));

        Assert.Equal(HttpStatusCode.NoContent, patched.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(server.Client, As("alice", Tus(HttpMethod.Head, url)), Token)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(server.Client, As("alice", Tus(HttpMethod.Delete, url)), Token)).StatusCode);
    }

    /// <summary>Naming someone else's part in a final upload would copy their bytes into one you own.</summary>
    [Fact]
    public async Task Refuses_to_concatenate_another_owners_part()
    {
        using var dir = new UploadDirectory();
        await using var server = await StartOwnedAsync(dir);

        var part = await CreateAsAsync(server.Client, "alice", 3, concat: "partial");
        await PatchAsync(server.Client, part, 0, Bytes(3), r => r.Headers.Add("X-User", "alice"));

        using var theft = As("bob", Tus(HttpMethod.Post, "/files"));
        theft.Headers.Add(TusHeaderNames.UploadConcat, "final;" + part);

        Assert.Equal(HttpStatusCode.BadRequest, (await Send(server.Client, theft, Token)).StatusCode);

        using var own = As("alice", Tus(HttpMethod.Post, "/files"));
        own.Headers.Add(TusHeaderNames.UploadConcat, "final;" + part);

        Assert.Equal(HttpStatusCode.Created, (await Send(server.Client, own, Token)).StatusCode);
    }

    [Fact]
    public async Task Asks_the_access_hook_per_operation()
    {
        using var dir = new UploadDirectory();
        var seen = new List<TusUploadOperation>();

        await using var server = await StartAsync(dir, o => o.AuthorizeUploadAsync = ctx =>
        {
            lock (seen)
                seen.Add(ctx.Operation);

            return ValueTask.FromResult(ctx.Operation != TusUploadOperation.Delete);
        });

        var url = await CreateAsync(server.Client, 10);

        Assert.Equal(HttpStatusCode.OK, (await HeadAsync(server.Client, url)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await PatchAsync(server.Client, url, 0, Bytes(2))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(server.Client, Tus(HttpMethod.Delete, url), Token)).StatusCode);
        Assert.Equal(2, dir.LengthOf(IdOf(url)));

        Assert.Equal([TusUploadOperation.Head, TusUploadOperation.Patch, TusUploadOperation.Delete], seen);
    }

    [Fact]
    public async Task Keeps_the_owner_with_the_upload_on_disk()
    {
        using var dir = new UploadDirectory();
        var store = dir.Store();

        var id = await store.CreateAsync(new TusCreateRequest { Length = 5, Owner = "alice" }, Token);
        var reopened = await new DiskTusStore(dir.Path).GetAsync(id, Token);

        Assert.Equal("alice", reopened?.Owner);
        Assert.Null((await store.GetAsync(await store.CreateAsync(new TusCreateRequest { Length = 5 }, Token), Token))?.Owner);
    }
}
