using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Shiny.Net.Http;
using Shiny.Net.HttpServer.FileSync;
using Shiny.Net.HttpServer.FileSync.Client;
using Shiny.Net.HttpServer.FileSync.Client.Internal;
using Wire = Shiny.Net.HttpServer.FileSync.Internal;

namespace Shiny.Net.HttpServer.Tests;

/// <summary>
/// The sync server driven raw, and the sync client driven against it as two devices sharing one
/// account - which is the only way to see a change on one arrive on the other.
/// </summary>
public class FileSyncTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    sealed class Scratch : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "shiny-sync-" + Guid.NewGuid().ToString("n")[..8]);

        public string Dir(string name) => Directory.CreateDirectory(Path.Combine(this.Root, name)).FullName;

        public void Dispose()
        {
            try
            {
                Directory.Delete(this.Root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    static Task<TestServer> StartAsync(Scratch scratch, Action<FileSyncOptions>? configure = null)
        => TestServer.StartAsync(
            app => app.MapFileSync("/sync", o =>
            {
                o.Store = new DiskFileSyncStore(scratch.Dir("server"));
                configure?.Invoke(o);
            }),
            b => b.Options.Http2.AllowCleartext = true
        );

    static FileSyncClient Device(TestServer server, string folder, Action<FileSyncClientOptions>? configure = null, IHttpTransferManager? transfers = null)
    {
        var options = new FileSyncClientOptions
        {
            ServerUri = new Uri($"http://127.0.0.1:{server.Port}/sync"),
            LocalPath = folder,
            DeviceName = Path.GetFileName(folder),
            WatchLocalChanges = false
        };

        configure?.Invoke(options);
        return new FileSyncClient(options, transfers);
    }

    static byte[] Random(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    static async Task<byte[]> PackAsync(params byte[][] chunks)
    {
        using var stream = new MemoryStream();
        await PackFormat.WriteHeaderAsync(stream, Token);

        foreach (var chunk in chunks)
            await PackFormat.WriteChunkAsync(stream, Sha(chunk), chunk, Token);

        return stream.ToArray();
    }

    static async Task<HttpResponseMessage> PutPackAsync(HttpClient client, byte[] pack)
        => await client.PutAsync("/sync/chunks/pack", new ByteArrayContent(pack), Token);

    static async Task<HttpResponseMessage> CommitAsync(HttpClient client, string path, long? baseRevision, params byte[][] chunks)
    {
        var all = chunks.SelectMany(c => c).ToArray();
        var commit = new Wire.WireCommit(
            baseRevision,
            all.Length,
            Sha(all),
            DateTimeOffset.UtcNow,
            [.. chunks.Select(c => new Wire.WireChunk(Sha(c), c.Length))]
        );

        return await client.PutAsync(
            "/sync/files/manifest?path=" + Uri.EscapeDataString(path),
            JsonContent.Create(commit, Wire.WireJson.Default.WireCommit),
            Token
        );
    }

    // ---- the server, raw ----

    [Fact]
    public async Task CommitNeedsItsChunksThenServesTheFile()
    {
        using var scratch = new Scratch();
        await using var server = await StartAsync(scratch);
        var a = Random(5000, 1);
        var b = Random(7000, 2);

        var early = await CommitAsync(server.Client, "docs/report.bin", null, a, b);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, early.StatusCode);
        var missing = await early.Content.ReadFromJsonAsync(Wire.WireJson.Default.WireHashes, Token);
        Assert.Equal(new[] { Sha(a), Sha(b) }.Order(), missing!.Hashes.Order());

        var ask = await server.Client.PostAsync("/sync/chunks/missing", JsonContent.Create(new Wire.WireHashes([Sha(a), Sha(b)]), Wire.WireJson.Default.WireHashes), Token);
        Assert.Equal(2, (await ask.Content.ReadFromJsonAsync(Wire.WireJson.Default.WireHashes, Token))!.Hashes.Count);

        Assert.Equal(HttpStatusCode.OK, (await PutPackAsync(server.Client, await PackAsync(a, b))).StatusCode);

        var committed = await CommitAsync(server.Client, "docs/report.bin", null, a, b);
        Assert.Equal(HttpStatusCode.OK, committed.StatusCode);
        var entry = await committed.Content.ReadFromJsonAsync(Wire.WireJson.Default.WireEntry, Token);
        Assert.Equal(1, entry!.Revision);

        var manifest = await server.Client.GetFromJsonAsync("/sync/files/manifest?path=docs/report.bin", Wire.WireJson.Default.WireEntry, Token);
        Assert.Equal([Sha(a), Sha(b)], manifest!.Chunks!.Select(c => c.Hash).ToArray());

        var content = await server.Client.GetByteArrayAsync("/sync/files/content?path=docs/report.bin", Token);
        Assert.Equal(a.Concat(b).ToArray(), content);
    }

    [Fact]
    public async Task StaleBaseRevisionIsAConflictCarryingTheCurrentVersion()
    {
        using var scratch = new Scratch();
        await using var server = await StartAsync(scratch);
        var v1 = Random(100, 1);
        var v2 = Random(100, 2);
        var v3 = Random(100, 3);
        await PutPackAsync(server.Client, await PackAsync(v1, v2, v3));

        Assert.Equal(HttpStatusCode.OK, (await CommitAsync(server.Client, "a.txt", null, v1)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await CommitAsync(server.Client, "a.txt", 1, v2)).StatusCode);

        var stale = await CommitAsync(server.Client, "A.TXT", 1, v3);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var conflict = await stale.Content.ReadFromJsonAsync(Wire.WireJson.Default.WireConflict, Token);
        Assert.Equal(2, conflict!.Current!.Revision);
        Assert.Equal(Sha(v2), conflict.Current.Hash);

        // Creating over an existing file is a conflict too.
        Assert.Equal(HttpStatusCode.Conflict, (await CommitAsync(server.Client, "a.txt", null, v3)).StatusCode);
    }

    [Fact]
    public async Task DeleteLeavesATombstoneInTheChangeFeed()
    {
        using var scratch = new Scratch();
        await using var server = await StartAsync(scratch);
        var data = Random(100, 1);
        await PutPackAsync(server.Client, await PackAsync(data));

        await CommitAsync(server.Client, "one.txt", null, data);
        await CommitAsync(server.Client, "two.txt", null, data);

        Assert.Equal(HttpStatusCode.Conflict, (await server.Client.DeleteAsync("/sync/files/manifest?path=one.txt&revision=99", Token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await server.Client.DeleteAsync("/sync/files/manifest?path=one.txt&revision=1", Token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await server.Client.GetAsync("/sync/files/manifest?path=one.txt", Token)).StatusCode);

        var changes = await server.Client.GetFromJsonAsync("/sync/changes?cursor=0", Wire.WireJson.Default.WireChanges, Token);
        Assert.Equal(["two.txt", "one.txt"], changes!.Entries.Select(e => e.Path).ToArray());
        Assert.True(changes.Entries[1].Deleted);
        Assert.Equal(3, changes.Cursor);

        var paged = await server.Client.GetFromJsonAsync("/sync/changes?cursor=0&limit=1", Wire.WireJson.Default.WireChanges, Token);
        Assert.True(paged!.HasMore);
        Assert.Equal(2, paged.Cursor);

        var after = await server.Client.GetFromJsonAsync("/sync/changes?cursor=3", Wire.WireJson.Default.WireChanges, Token);
        Assert.Empty(after!.Entries);
        Assert.Equal(3, after.Cursor);
    }

    [Fact]
    public async Task LongPollWakesOnACommit()
    {
        using var scratch = new Scratch();
        await using var server = await StartAsync(scratch);
        var data = Random(100, 1);
        await PutPackAsync(server.Client, await PackAsync(data));

        var waiting = server.Client.GetFromJsonAsync("/sync/changes/wait?cursor=0&timeout=30", Wire.WireJson.Default.WireWait, Token);
        await Task.Delay(200, Token);
        Assert.False(waiting.IsCompleted);

        await CommitAsync(server.Client, "x.bin", null, data);
        var woke = await waiting.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.True(woke!.Changes);
        Assert.Equal(1, woke.Cursor);

        var quiet = await server.Client.GetFromJsonAsync("/sync/changes/wait?cursor=1&timeout=1", Wire.WireJson.Default.WireWait, Token);
        Assert.False(quiet!.Changes);
    }

    [Fact]
    public async Task PackDownloadResumesFromARange()
    {
        using var scratch = new Scratch();
        await using var server = await StartAsync(scratch);
        var a = Random(3000, 1);
        var b = Random(4000, 2);
        var pack = await PackAsync(a, b);
        await PutPackAsync(server.Client, pack);

        var url = $"/sync/chunks/pack?h={Sha(a)},{Sha(b)}";
        Assert.Equal(pack, await server.Client.GetByteArrayAsync(url, Token));

        using var ranged = new HttpRequestMessage(HttpMethod.Get, url);
        ranged.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(1000, null);
        var response = await server.Client.SendAsync(ranged, Token);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(pack[1000..], await response.Content.ReadAsByteArrayAsync(Token));
        Assert.Equal(pack.Length, response.Content.Headers.ContentRange!.Length);

        Assert.Equal(a, await server.Client.GetByteArrayAsync("/sync/chunks/" + Sha(a), Token));
    }

    [Fact]
    public async Task CorruptPacksAndBadPathsAreRefused()
    {
        using var scratch = new Scratch();
        await using var server = await StartAsync(scratch);

        var pack = await PackAsync(Random(1000, 1));
        pack[^1] ^= 0xFF;
        Assert.Equal(HttpStatusCode.BadRequest, (await PutPackAsync(server.Client, pack)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutPackAsync(server.Client, "nope"u8.ToArray())).StatusCode);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(scratch.Root, "server", "chunks"), "*", SearchOption.AllDirectories));

        foreach (var bad in (string[])["../x", "a/../b", "a//b", "trailing.", "c:x", ""])
            Assert.Equal(HttpStatusCode.BadRequest, (await server.Client.GetAsync("/sync/files/manifest?path=" + Uri.EscapeDataString(bad), Token)).StatusCode);
    }

    [Fact]
    public async Task GarbageCollectionKeepsWhatFilesUse()
    {
        using var scratch = new Scratch();
        var clock = new ManualClock();
        FileSyncMountBuilder? mount = null;

        await using var server = await TestServer.StartAsync(app => mount = app.MapFileSync("/sync", o =>
        {
            o.Store = new DiskFileSyncStore(scratch.Dir("server"));
            o.TimeProvider = clock;
        }));

        var used = Random(100, 1);
        var orphan = Random(100, 2);
        await PutPackAsync(server.Client, await PackAsync(used, orphan));
        await CommitAsync(server.Client, "keep.bin", null, used);

        // Within the grace period an orphan may be a commit still on its way.
        Assert.Equal(0, await mount!.CollectGarbageAsync(cancellationToken: Token));

        clock.Now += TimeSpan.FromDays(2);
        Assert.Equal(1, await mount.CollectGarbageAsync(cancellationToken: Token));

        Assert.Equal(HttpStatusCode.OK, (await server.Client.GetAsync("/sync/chunks/" + Sha(used), Token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await server.Client.GetAsync("/sync/chunks/" + Sha(orphan), Token)).StatusCode);
    }

    [Fact]
    public async Task DiskStoreReplaysItsJournal()
    {
        using var scratch = new Scratch();
        var dir = scratch.Dir("server");
        var data = Random(100, 1);

        var first = new DiskFileSyncStore(dir);
        await first.PutChunkAsync(Sha(data), data, Token);
        await first.CommitAsync(new FileSyncCommit { Path = "a.bin", Size = 100, Hash = Sha(data), Chunks = [new(Sha(data), 100)] }, Token);
        await first.CommitAsync(new FileSyncCommit { Path = "a.bin", ExpectedRevision = 1, Deleted = true }, Token);
        await first.CommitAsync(new FileSyncCommit { Path = "b.bin", Size = 100, Hash = Sha(data), Chunks = [new(Sha(data), 100)] }, Token);

        // A torn last line, as a crash mid-append would leave.
        await File.AppendAllTextAsync(Path.Combine(dir, "journal.jsonl"), "{\"path\":\"c.b", Token);

        var second = new DiskFileSyncStore(dir);
        Assert.Equal(3, await second.GetLatestRevisionAsync(Token));
        Assert.True((await second.GetEntryAsync("A.BIN", Token))!.Deleted);
        Assert.Equal(100, (await second.GetEntryAsync("b.bin", Token))!.Size);

        var next = await second.CommitAsync(new FileSyncCommit { Path = "c.bin", Size = 100, Hash = Sha(data), Chunks = [new(Sha(data), 100)] }, Token);
        Assert.Equal(4, next.Entry!.Revision);
    }

    sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => this.Now;
    }

    // ---- the chunker ----

    [Fact]
    public async Task ChunkingSurvivesAnInsertion()
    {
        using var scratch = new Scratch();
        var original = Random(4 * 1024 * 1024, 7);
        var edited = original[..2_000_000].Concat("an insertion in the middle"u8.ToArray()).Concat(original[2_000_000..]).ToArray();

        var a = Path.Combine(scratch.Dir("c"), "a.bin");
        var b = Path.Combine(scratch.Dir("c"), "b.bin");
        await File.WriteAllBytesAsync(a, original, Token);
        await File.WriteAllBytesAsync(b, edited, Token);

        var before = await ContentChunker.ChunkFileAsync(a, 64 * 1024, 256 * 1024, 1024 * 1024, Token);
        var after = await ContentChunker.ChunkFileAsync(b, 64 * 1024, 256 * 1024, 1024 * 1024, Token);

        Assert.Equal(original.Length, before.Size);
        Assert.Equal(Sha(original), before.Hash);
        Assert.All(before.Chunks, c => Assert.InRange(c.Length, 1, 1024 * 1024));
        Assert.InRange(before.Chunks.Count, 8, 40);

        // Fixed-size blocks would change every block after the insertion; content-defined ones
        // change the one it landed in, perhaps a neighbour.
        var known = before.Chunks.Select(c => c.Hash).ToHashSet();
        var changed = after.Chunks.Count(c => !known.Contains(c.Hash));
        Assert.InRange(changed, 1, 2);

        var again = await ContentChunker.ChunkFileAsync(a, 64 * 1024, 256 * 1024, 1024 * 1024, Token);
        Assert.Equal(before.Chunks, again.Chunks);
    }

    // ---- two devices, one account ----

    [Fact]
    public async Task AFolderSyncsToASecondDevice()
    {
        using var scratch = new Scratch();
        await using var server = await StartAsync(scratch);
        var phone = scratch.Dir("phone");
        var laptop = scratch.Dir("laptop");

        Directory.CreateDirectory(Path.Combine(phone, "photos", "2026"));
        await File.WriteAllBytesAsync(Path.Combine(phone, "photos", "2026", "beach.jpg"), Random(900_000, 1), Token);
        await File.WriteAllTextAsync(Path.Combine(phone, "notes.txt"), "hello", Token);
        await File.WriteAllBytesAsync(Path.Combine(phone, "empty.bin"), [], Token);
        await File.WriteAllTextAsync(Path.Combine(phone, ".DS_Store"), "ignored", Token);

        using var a = Device(server, phone);
        using var b = Device(server, laptop);

        var up = await a.SyncAsync(Token);
        Assert.Equal(3, up.Uploaded);
        Assert.Equal(0, up.Failed);

        var down = await b.SyncAsync(Token);
        Assert.Equal(3, down.Downloaded);
        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(phone, "photos", "2026", "beach.jpg"), Token), await File.ReadAllBytesAsync(Path.Combine(laptop, "photos", "2026", "beach.jpg"), Token));
        Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(laptop, "notes.txt"), Token));
        Assert.Equal(0, new FileInfo(Path.Combine(laptop, "empty.bin")).Length);
        Assert.False(File.Exists(Path.Combine(laptop, ".DS_Store")));

        // Nothing changed: a pass does nothing.
        Assert.Empty((await a.SyncAsync(Token)).Activities);
        Assert.Empty((await b.SyncAsync(Token)).Activities);
    }

    [Fact]
    public async Task AnEditToABigFileMovesOnlyWhatChanged()
    {
        using var scratch = new Scratch();
        await using var server = await StartAsync(scratch);
        var phone = scratch.Dir("phone");
        var laptop = scratch.Dir("laptop");
        var original = Random(8 * 1024 * 1024, 3);
        await File.WriteAllBytesAsync(Path.Combine(phone, "video.mov"), original, Token);

        using var a = Device(server, phone);
        using var b = Device(server, laptop);
        await a.SyncAsync(Token);
        await b.SyncAsync(Token);

        // Insert into the middle, which shifts every byte after it.
        var edited = original[..4_000_000].Concat(Random(5000, 9)).Concat(original[4_000_000..]).ToArray();
        await File.WriteAllBytesAsync(Path.Combine(phone, "video.mov"), edited, Token);

        var up = await a.SyncAsync(Token);
        Assert.Equal(1, up.Uploaded);
        Assert.InRange(up.BytesTransferred, 1, 2 * 1024 * 1024);
        Assert.True(up.BytesReused > 6 * 1024 * 1024, $"reused only {up.BytesReused}");

        var down = await b.SyncAsync(Token);
        Assert.Equal(1, down.Downloaded);
        Assert.InRange(down.BytesTransferred, 1, 2 * 1024 * 1024);
        Assert.True(down.BytesReused > 6 * 1024 * 1024, $"reused only {down.BytesReused}");
        Assert.Equal(edited, await File.ReadAllBytesAsync(Path.Combine(laptop, "video.mov"), Token));
    }

    [Fact]
    public async Task ACopyCostsNothingToUpload()
    {
        using var scratch = new Scratch();
        await using var server = await StartAsync(scratch);
        var phone = scratch.Dir("phone");
        var data = Random(3 * 1024 * 1024, 4);
        await File.WriteAllBytesAsync(Path.Combine(phone, "a.bin"), data, Token);

        using var a = Device(server, phone);
        await a.SyncAsync(Token);

        File.Move(Path.Combine(phone, "a.bin"), Path.Combine(phone, "renamed.bin"));
        File.Copy(Path.Combine(phone, "renamed.bin"), Path.Combine(phone, "copy.bin"));

        var result = await a.SyncAsync(Token);
        Assert.Equal(2, result.Uploaded);
        Assert.Equal(1, result.Deleted);
        Assert.Equal(0, result.BytesTransferred);
    }

    [Fact]
    public async Task DeletesPropagateBothWays()
    {
        using var scratch = new Scratch();
        await using var server = await StartAsync(scratch);
        var phone = scratch.Dir("phone");
        var laptop = scratch.Dir("laptop");
        Directory.CreateDirectory(Path.Combine(phone, "sub"));
        await File.WriteAllTextAsync(Path.Combine(phone, "sub", "one.txt"), "1", Token);
        await File.WriteAllTextAsync(Path.Combine(phone, "two.txt"), "2", Token);

        using var a = Device(server, phone);
        using var b = Device(server, laptop);
        await a.SyncAsync(Token);
        await b.SyncAsync(Token);

        File.Delete(Path.Combine(phone, "sub", "one.txt"));
        File.Delete(Path.Combine(laptop, "two.txt"));

        Assert.Equal(1, (await a.SyncAsync(Token)).Deleted);
        var laptopPass = await b.SyncAsync(Token);
        Assert.Equal(2, laptopPass.Deleted);
        Assert.False(File.Exists(Path.Combine(laptop, "sub", "one.txt")));
        Assert.False(Directory.Exists(Path.Combine(laptop, "sub")));

        await a.SyncAsync(Token);
        Assert.False(File.Exists(Path.Combine(phone, "two.txt")));
    }

    [Fact]
    public async Task EditsOnBothSidesBecomeAConflictedCopy()
    {
        using var scratch = new Scratch();
        await using var server = await StartAsync(scratch);
        var phone = scratch.Dir("phone");
        var laptop = scratch.Dir("laptop");
        await File.WriteAllTextAsync(Path.Combine(phone, "plan.txt"), "v1", Token);

        using var a = Device(server, phone);
        using var b = Device(server, laptop);
        await a.SyncAsync(Token);
        await b.SyncAsync(Token);

        await File.WriteAllTextAsync(Path.Combine(phone, "plan.txt"), "phone edit", Token);
        await File.WriteAllTextAsync(Path.Combine(laptop, "plan.txt"), "laptop edit", Token);

        await a.SyncAsync(Token);
        var conflicted = await b.SyncAsync(Token);

        var conflict = Assert.Single(conflicted.Activities, x => x.Kind == FileSyncActivityKind.Conflict);
        Assert.Contains("(conflicted copy laptop ", conflict.ConflictCopyPath);

        // The first to reach the server keeps the name; the other edit is kept, not lost.
        Assert.Equal("phone edit", await File.ReadAllTextAsync(Path.Combine(laptop, "plan.txt"), Token));
        Assert.Equal("laptop edit", await File.ReadAllTextAsync(Path.Combine(laptop, conflict.ConflictCopyPath!), Token));

        await a.SyncAsync(Token);
        Assert.Equal("laptop edit", await File.ReadAllTextAsync(Path.Combine(phone, conflict.ConflictCopyPath!), Token));
    }

    [Fact]
    public async Task IdenticalFilesOnBothSidesAreAdoptedNotConflicted()
    {
        using var scratch = new Scratch();
        await using var server = await StartAsync(scratch);
        var phone = scratch.Dir("phone");
        var laptop = scratch.Dir("laptop");
        var data = Random(500_000, 5);
        await File.WriteAllBytesAsync(Path.Combine(phone, "same.bin"), data, Token);
        await File.WriteAllBytesAsync(Path.Combine(laptop, "same.bin"), data, Token);

        using var a = Device(server, phone);
        using var b = Device(server, laptop);
        await a.SyncAsync(Token);

        var second = await b.SyncAsync(Token);
        Assert.Equal(0, second.Conflicts);
        Assert.Equal(0, second.BytesTransferred);
        Assert.Single(Directory.GetFiles(laptop));
    }

    [Fact]
    public async Task ShinyTransferManagerMovesPacksOverTus()
    {
        using var scratch = new Scratch();
        var tusCompleted = 0;

        await using var server = await TestServer.StartAsync(app =>
        {
            app.Use(async (ctx, next) =>
            {
                await next(ctx);

                if (HttpMethods.IsPatch(ctx.Request.Method) && ctx.Request.Path.StartsWith("/sync/uploads", StringComparison.Ordinal))
                    Interlocked.Increment(ref tusCompleted);
            });

            app.MapFileSync("/sync", o =>
            {
                o.Store = new DiskFileSyncStore(scratch.Dir("server"));
                o.TusStore = new Shiny.Net.HttpServer.Tus.DiskTusStore(scratch.Dir("tus"));
            });
        });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<Shiny.Net.IConnectivity>(new AlwaysOnline());
        Shiny.HttpTransferServiceCollectionExtensions.AddHttpClientTransfers<FileSyncTestDelegate>(services);
        await using var provider = services.BuildServiceProvider();
        var transfers = provider.GetRequiredService<IHttpTransferManager>();

        // Shiny's host starts the managed transfer loop at app startup; a bare container does not.
        (transfers as HttpClientHttpTransferManager)?.Start();

        var phone = scratch.Dir("phone");
        var laptop = scratch.Dir("laptop");
        var data = Random(3 * 1024 * 1024, 6);
        await File.WriteAllBytesAsync(Path.Combine(phone, "big.bin"), data, Token);

        using var a = Device(server, phone, transfers: transfers);
        using var b = Device(server, laptop, transfers: transfers);

        var up = await a.SyncAsync(Token).WaitAsync(TimeSpan.FromSeconds(60), Token);
        Assert.Equal(1, up.Uploaded);
        Assert.True(tusCompleted > 0, "the pack did not go over tus");

        var down = await b.SyncAsync(Token).WaitAsync(TimeSpan.FromSeconds(60), Token);
        Assert.Equal(1, down.Downloaded);
        Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(laptop, "big.bin"), Token));
    }

    [Fact]
    public void AddFileSyncClientResolves()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFileSyncClient(o =>
        {
            o.ServerUri = new Uri("http://127.0.0.1:1/sync");
            o.LocalPath = Path.GetTempPath();
        });

        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<FileSyncClient>());
    }

    sealed class AlwaysOnline : Shiny.Net.IConnectivity
    {
        public Shiny.Net.ConnectionTypes ConnectionTypes => Shiny.Net.ConnectionTypes.Wired;

        public Shiny.Net.NetworkAccess Access => Shiny.Net.NetworkAccess.Internet;

        public event EventHandler? Changed { add { } remove { } }
    }
}

public sealed class FileSyncTestDelegate(Microsoft.Extensions.Logging.ILogger<FileSyncTestDelegate> logger, IHttpTransferManager manager)
    : HttpTransferDelegate(logger, manager, 0)
{
    public override Task OnCompleted(HttpTransferRequest request) => Task.CompletedTask;
}
