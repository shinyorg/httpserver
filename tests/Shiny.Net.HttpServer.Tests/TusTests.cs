using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Shiny.Net.HttpServer.Cors;
using Shiny.Net.HttpServer.Security;
using Shiny.Net.HttpServer.Testing;
using Shiny.Net.HttpServer.Tus;

namespace Shiny.Net.HttpServer.Tests;

/// <summary>
/// The tus endpoint driven the way tus clients drive it: create, HEAD for the offset, PATCH from
/// there. The tests that matter most are the ones where the connection goes away mid-body, since
/// keeping those bytes is the reason the package exists.
/// </summary>
public class TusTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>A throwaway upload directory, removed with the test.</summary>
    sealed class UploadDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "shiny-tus-" + Guid.NewGuid().ToString("n")[..8]
        );

        public DiskTusStore Store() => new(this.Path);

        public long LengthOf(string id)
        {
            var file = new FileInfo(System.IO.Path.Combine(this.Path, id));
            return file.Exists ? file.Length : -1;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(this.Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    sealed class ManualTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => this.Now;
    }

    static Task<TestServer> StartAsync(UploadDirectory dir, Action<TusOptions>? configure = null, Action<ShinyHttpServerBuilder>? builder = null)
        => TestServer.StartAsync(
            app => app.MapTus("/files", o =>
            {
                o.Store = dir.Store();
                configure?.Invoke(o);
            }),
            b =>
            {
                b.Options.Http2.AllowCleartext = true;
                builder?.Invoke(b);
            }
        );

    static HttpClient Http2Client(int port) => new(new SocketsHttpHandler())
    {
        BaseAddress = new Uri($"http://127.0.0.1:{port}"),
        DefaultRequestVersion = HttpVersion.Version20,
        DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        Timeout = TimeSpan.FromSeconds(30)
    };

    /// <summary>
    /// Sends with the client's own version policy. A hand-built request message is HTTP/1.1 whatever
    /// the client's defaults say, which would quietly turn every HTTP/2 test into an HTTP/1.1 one.
    /// </summary>
    static Task<HttpResponseMessage> Send(HttpClient client, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Version = client.DefaultRequestVersion;
        request.VersionPolicy = client.DefaultVersionPolicy;

        return client.SendAsync(request, cancellationToken);
    }

    static HttpRequestMessage Tus(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add(TusHeaderNames.TusResumable, TusProtocol.Version);
        return request;
    }

    static ByteArrayContent Chunk(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(TusProtocol.OffsetOctetStream);
        return content;
    }

    static async Task<string> CreateAsync(HttpClient client, long? length, string? metadata = null, string? concat = null)
    {
        using var request = Tus(HttpMethod.Post, "/files");

        if (length is { } l)
            request.Headers.Add(TusHeaderNames.UploadLength, l.ToString());
        else if (concat is null || concat == "partial")
            request.Headers.Add(TusHeaderNames.UploadDeferLength, "1");

        if (metadata is not null)
            request.Headers.Add(TusHeaderNames.UploadMetadata, metadata);

        if (concat is not null)
            request.Headers.Add(TusHeaderNames.UploadConcat, concat);

        var response = await Send(client, request, Token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return response.Headers.Location!.AbsolutePath;
    }

    static async Task<HttpResponseMessage> PatchAsync(HttpClient client, string url, long offset, byte[] bytes, Action<HttpRequestMessage>? configure = null)
    {
        var request = Tus(HttpMethod.Patch, url);
        request.Headers.Add(TusHeaderNames.UploadOffset, offset.ToString());
        request.Content = Chunk(bytes);
        configure?.Invoke(request);

        return await Send(client, request, Token);
    }

    static async Task<HttpResponseMessage> HeadAsync(HttpClient client, string url)
        => await Send(client, Tus(HttpMethod.Head, url), Token);

    static long OffsetOf(HttpResponseMessage response)
        => long.Parse(response.Headers.GetValues(TusHeaderNames.UploadOffset).Single());

    static string? Header(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;

    static byte[] Bytes(int count, int seed = 1)
    {
        var bytes = new byte[count];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    static string IdOf(string url) => url[(url.LastIndexOf('/') + 1)..];

    static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition was not met in time.");

            await Task.Delay(20, Token);
        }
    }

    /// <summary>
    /// A body that sends <paramref name="first"/> and then does whatever <paramref name="then"/>
    /// does - throws, to simulate the connection dropping, or waits, to simulate one that stalled.
    /// </summary>
    sealed class ScriptedContent(byte[] first, long declaredLength, Func<CancellationToken, Task> then) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            await stream.WriteAsync(first, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            await then(cancellationToken);
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => this.SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override bool TryComputeLength(out long length)
        {
            length = declaredLength;
            return true;
        }
    }

    static HttpRequestMessage InterruptedPatch(string url, long offset, byte[] sent, long declared, Func<CancellationToken, Task> then)
    {
        var request = Tus(HttpMethod.Patch, url);
        request.Headers.Add(TusHeaderNames.UploadOffset, offset.ToString());

        var content = new ScriptedContent(sent, declared, then);
        content.Headers.ContentType = new MediaTypeHeaderValue(TusProtocol.OffsetOctetStream);
        request.Content = content;

        return request;
    }

    // ---- discovery and version negotiation ----

    [Fact]
    public async Task Options_advertises_the_version_extensions_and_limits_without_needing_tus_resumable()
    {
        using var dir = new UploadDirectory();
        await using var server = await StartAsync(dir, o =>
        {
            o.MaxSize = 1024;
            o.Expiration = TimeSpan.FromHours(1);
        });

        var response = await Send(server.Client, new HttpRequestMessage(HttpMethod.Options, "/files"), Token);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("1.0.0", Header(response, TusHeaderNames.TusVersion));
        Assert.Equal("1024", Header(response, TusHeaderNames.TusMaxSize));
        Assert.Equal("sha1,sha256,md5", Header(response, TusHeaderNames.TusChecksumAlgorithm));

        var extensions = Header(response, TusHeaderNames.TusExtension)!.Split(',');

        foreach (var extension in (string[])["creation", "creation-with-upload", "creation-defer-length", "termination", "expiration", "checksum", "concatenation"])
            Assert.Contains(extension, extensions);
    }

    [Fact]
    public async Task Options_leaves_out_the_extensions_that_are_turned_off()
    {
        using var dir = new UploadDirectory();
        await using var server = await StartAsync(dir, o =>
        {
            o.AllowTermination = false;
            o.AllowConcatenation = false;
            o.AllowDeferredLength = false;
        });

        var response = await Send(server.Client, new HttpRequestMessage(HttpMethod.Options, "/files"), Token);
        var extensions = Header(response, TusHeaderNames.TusExtension)!.Split(',');

        Assert.DoesNotContain("termination", extensions);
        Assert.DoesNotContain("concatenation", extensions);
        Assert.DoesNotContain("creation-defer-length", extensions);
        Assert.DoesNotContain("expiration", extensions);
        Assert.Null(Header(response, TusHeaderNames.TusMaxSize));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0.2.2")]
    public async Task A_request_without_the_supported_version_is_refused_with_412(string? version)
    {
        using var dir = new UploadDirectory();
        await using var server = await StartAsync(dir);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/files");
        request.Headers.Add(TusHeaderNames.UploadLength, "10");

        if (version is not null)
            request.Headers.Add(TusHeaderNames.TusResumable, version);

        var response = await Send(server.Client, request, Token);

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        Assert.Equal("1.0.0", Header(response, TusHeaderNames.TusVersion));
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Empty(Directory.GetFiles(dir.Path));
    }

    // ---- the core flow ----

    [Fact]
    public async Task Uploads_in_chunks_and_raises_completion_once_with_the_whole_file()
    {
        using var dir = new UploadDirectory();
        var payload = Bytes(100_000);
        var completions = new List<byte[]>();

        await using var server = await StartAsync(dir, o => o.OnUploadCompleteAsync = async ctx =>
        {
            await using var stream = await ctx.OpenReadAsync();
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            completions.Add(copy.ToArray());
            Assert.Equal("clip.mp4", ctx.Upload.Metadata.GetValueOrDefault("filename"));
        });

        var url = await CreateAsync(server.Client, payload.Length, "filename " + Convert.ToBase64String("clip.mp4"u8.ToArray()));
        Assert.StartsWith("/files/", url);

        var head = await HeadAsync(server.Client, url);
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(0, OffsetOf(head));
        Assert.Equal("100000", Header(head, TusHeaderNames.UploadLength));
        Assert.Equal("1.0.0", Header(head, TusHeaderNames.TusResumable));
        Assert.Equal("no-store", head.Headers.CacheControl?.ToString());

        var first = await PatchAsync(server.Client, url, 0, payload[..40_000]);
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(40_000, OffsetOf(first));
        Assert.Empty(completions);

        Assert.Equal(40_000, OffsetOf(await HeadAsync(server.Client, url)));

        var second = await PatchAsync(server.Client, url, 40_000, payload[40_000..]);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        Assert.Equal(100_000, OffsetOf(second));

        Assert.Single(completions);
        Assert.Equal(payload, completions[0]);

        // An empty PATCH to a finished upload is harmless and does not raise completion again.
        var again = await PatchAsync(server.Client, url, 100_000, []);
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        Assert.Single(completions);
    }

    [Fact]
    public async Task Location_is_absolute_and_names_the_mount()
    {
        using var dir = new UploadDirectory();
        await using var server = await StartAsync(dir);

        using var request = Tus(HttpMethod.Post, "/files");
        request.Headers.Add(TusHeaderNames.UploadLength, "5");

        var response = await Send(server.Client, request, Token);
        var location = response.Headers.Location!;

        Assert.True(location.IsAbsoluteUri);
        Assert.Equal(server.Port, location.Port);
        Assert.StartsWith("/files/", location.AbsolutePath);
    }

    [Fact]
    public async Task A_patch_at_the_wrong_offset_is_a_409_and_changes_nothing()
    {
        using var dir = new UploadDirectory();
        await using var server = await StartAsync(dir);

        var url = await CreateAsync(server.Client, 10);
        await PatchAsync(server.Client, url, 0, Bytes(4));

        var response = await PatchAsync(server.Client, url, 2, Bytes(4));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(4, OffsetOf(await HeadAsync(server.Client, url)));
    }

    [Fact]
    public async Task A_patch_with_another_content_type_is_a_415()
    {
        using var dir = new UploadDirectory();
        await using var server = await StartAsync(dir);

        var url = await CreateAsync(server.Client, 10);

        var response = await PatchAsync(server.Client, url, 0, Bytes(4), r =>
            r.Content!.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task A_patch_past_the_upload_length_is_a_413_and_keeps_nothing()
    {
        using var dir = new UploadDirectory();
        await using var server = await StartAsync(dir);

        var url = await CreateAsync(server.Client, 10);
        var response = await PatchAsync(server.Client, url, 0, Bytes(11));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0, OffsetOf(await HeadAsync(server.Client, url)));
    }

    [Fact]
    public async Task An_unknown_upload_is_a_404_and_a_hostile_id_never_reaches_the_store()
    {
        using var dir = new UploadDirectory();
        await using var server = await StartAsync(dir);

        Assert.Equal(HttpStatusCode.NotFound, (await HeadAsync(server.Client, "/files/nope")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await HeadAsync(server.Client, "/files/..%2F..%2Fetc")).StatusCode);
    }

    // ---- creation ----

    [Fact]
    public async Task Creation_with_upload_stores_the_first_chunk_with_the_post()
    {
        using var dir = new UploadDirectory();
        await using var server = await StartAsync(dir);

        using var request = Tus(HttpMethod.Post, "/files");
        request.Headers.Add(TusHeaderNames.UploadLength, "10");
        request.Content = Chunk(Bytes(6));

        var response = await Send(server.Client, request, Token);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(6, OffsetOf(response));
        Assert.Equal(6, OffsetOf(await HeadAsync(server.Client, response.Headers.Location!.AbsolutePath)));
    }

    [Fact]
    public async Task Creation_with_upload_and_a_bad_checksum_still_creates_the_upload_at_offset_zero()
    {
        using var dir = new UploadDirectory();
        await using var server = await StartAsync(dir);

        using var request = Tus(HttpMethod.Post, "/files");
        request.Headers.Add(TusHeaderNames.UploadLength, "10");
        request.Headers.Add(TusHeaderNames.UploadChecksum, "sha1 " + Convert.ToBase64String(new byte[20]));
        request.Content = Chunk(Bytes(6));

        var response = await Send(server.Client, request, Token);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(0, OffsetOf(response));
    }

    [Fact]
    public async Task A_deferred_length_is_stated_on_a_later_patch()
    {
        using var dir = new UploadDirectory();
        var completed = 0;
        await using var server = await StartAsync(dir, o => o.OnUploadCompleteAsync = _ =>
        {
            completed++;
            return ValueTask.CompletedTask;
        });

        var url = await CreateAsync(server.Client, length: null);

        var head = await HeadAsync(server.Client, url);
        Assert.Equal("1", Header(head, TusHeaderNames.UploadDeferLength));
        Assert.Null(Header(head, TusHeaderNames.UploadLength));

        await PatchAsync(server.Client, url, 0, Bytes(5));
        Assert.Equal(0, completed);

        var last = await PatchAsync(server.Client, url, 5, Bytes(5), r => r.Headers.Add(TusHeaderNames.UploadLength, "10"));

        Assert.Equal(HttpStatusCode.NoContent, last.StatusCode);
        Assert.Equal(1, completed);
        Assert.Equal("10", Header(await HeadAsync(server.Client, url), TusHeaderNames.UploadLength));
    }

    [Fact]
    public async Task Deferred_length_is_refused_when_turned_off()
    {
        using var dir = new UploadDirectory();
        await using var server = await StartAsync(dir, o => o.AllowDeferredLength = false);

        using var request = Tus(HttpMethod.Post, "/files");
        request.Headers.Add(TusHeaderNames.UploadDeferLength, "1");

        Assert.Equal(HttpStatusCode.BadRequest, (await Send(server.Client, request, Token)).StatusCode);
    }

    [Fact]
    public async Task An_upload_larger_than_max_size_is_refused_before_it_exists()
    {
        using var dir = new UploadDirectory();
        await using var server = await StartAsync(dir, o => o.MaxSize = 100);

        using var request = Tus(HttpMethod.Post, "/files");
        request.Headers.Add(TusHeaderNames.UploadLength, "101");

        var response = await Send(server.Client, request, Token);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(Directory.GetFiles(dir.Path));
    }

    [Fact]
    public async Task Metadata_round_trips_through_head()
    {
        using var dir = new UploadDirectory();
        await using var server = await StartAsync(dir);

        var metadata = "filename " + Convert.ToBase64String("résumé.pdf"u8.ToArray()) + ",is_confidential";
        var url = await CreateAsync(server.Client, 10, metadata);

        var head = await HeadAsync(server.Client, url);
        var echoed = TusMetadata.Parse(Header(head, TusHeaderNames.UploadMetadata));

        Assert.Equal("résumé.pdf", echoed["filename"]);
        Assert.True(echoed.ContainsKey("is_confidential"));
        Assert.Null(echoed["is_confidential"]);
    }

    [Fact]
    public async Task Malformed_metadata_is_a_400()
    {
        using var dir = new UploadDirectory();
        await using var server = await StartAsync(dir);

        using var request = Tus(HttpMethod.Post, "/files");
        request.Headers.Add(TusHeaderNames.UploadLength, "10");
        request.Headers.TryAddWithoutValidation(TusHeaderNames.UploadMetadata, "filename not*base64!");

        Assert.Equal(HttpStatusCode.BadRequest, (await Send(server.Client, request, Token)).StatusCode);
    }

    [Fact]
    public async Task On_before_create_can_refuse_an_upload()
    {
        using var dir = new UploadDirectory();
        await using var server = await StartAsync(dir, o => o.OnBeforeCreateAsync = ctx =>
        {
            if (!ctx.Metadata.ContainsKey("filename"))
                ctx.Reject(StatusCodes.Status422UnprocessableEntity, "A filename is required.");

            return ValueTask.CompletedTask;
        });

        using var request = Tus(HttpMethod.Post, "/files");
        request.Headers.Add(TusHeaderNames.UploadLength, "10");

        var response = await Send(server.Client, request, Token);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("A filename is required.", await response.Content.ReadAsStringAsync(Token));
        Assert.Empty(Directory.GetFiles(dir.Path));

        Assert.StartsWith("/files/", await CreateAsync(server.Client, 10, "filename " + Convert.ToBase64String("a"u8.ToArray())));
    }

    // ---- termination ----

    [Fact]
    public async Task Delete_removes_the_upload()
    {
        using var dir = new UploadDirectory();
        await using var server = await StartAsync(dir);

        var url = await CreateAsync(server.Client, 10);
        await PatchAsync(server.Client, url, 0, Bytes(3));

        var response = await Send(server.Client, Tus(HttpMethod.Delete, url), Token);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await HeadAsync(server.Client, url)).StatusCode);
        Assert.Empty(Directory.GetFiles(dir.Path));
    }

    [Fact]
    public async Task Delete_is_refused_when_termination_is_off()
    {
        using var dir = new UploadDirectory();
        await using var server = await StartAsync(dir, o => o.AllowTermination = false);

        var url = await CreateAsync(server.Client, 10);
        var response = await Send(server.Client, Tus(HttpMethod.Delete, url), Token);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await HeadAsync(server.Client, url)).StatusCode);
    }

    // ---- X-HTTP-Method-Override ----

    [Fact]
    public async Task A_post_with_method_override_is_treated_as_the_method_it_names()
    {
        using var dir = new UploadDirectory();
        await using var server = await StartAsync(dir);

        var url = await CreateAsync(server.Client, 10);

        using var patch = Tus(HttpMethod.Post, url);
        patch.Headers.Add(TusHeaderNames.XHttpMethodOverride, "PATCH");
        patch.Headers.Add(TusHeaderNames.UploadOffset, "0");
        patch.Content = Chunk(Bytes(4));

        var patched = await Send(server.Client, patch, Token);
        Assert.Equal(HttpStatusCode.NoContent, patched.StatusCode);
        Assert.Equal(4, OffsetOf(patched));

        using var delete = Tus(HttpMethod.Post, url);
        delete.Headers.Add(TusHeaderNames.XHttpMethodOverride, "DELETE");

        Assert.Equal(HttpStatusCode.NoContent, (await Send(server.Client, delete, Token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await HeadAsync(server.Client, url)).StatusCode);
    }

    [Fact]
    public async Task A_post_to_an_upload_without_an_override_is_a_405()
    {
        using var dir = new UploadDirectory();
        await using var server = await StartAsync(dir);

        var url = await CreateAsync(server.Client, 10);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await Send(server.Client, Tus(HttpMethod.Post, url), Token)).StatusCode);
    }

    // ---- checksum ----

    [Theory]
    [InlineData("sha1")]
    [InlineData("sha256")]
    [InlineData("md5")]
    public async Task A_patch_with_a_matching_checksum_is_kept(string algorithm)
    {
        using var dir = new UploadDirectory();
        await using var server = await StartAsync(dir);

        var bytes = Bytes(5000);
        var digest = algorithm switch
        {
            "sha1" => SHA1.HashData(bytes),
            "sha256" => SHA256.HashData(bytes),
            _ => MD5.HashData(bytes)
        };

        var url = await CreateAsync(server.Client, 5000);
        var response = await PatchAsync(server.Client, url, 0, bytes, r =>
            r.Headers.Add(TusHeaderNames.UploadChecksum, $"{algorithm} {Convert.ToBase64String(digest)}"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(5000, OffsetOf(response));
    }

    [Fact]
    public async Task A_patch_with_a_mismatched_checksum_is_a_460_and_none_of_it_is_kept()
    {
        using var dir = new UploadDirectory();
        await using var server = await StartAsync(dir);

        var url = await CreateAsync(server.Client, 5000);
        await PatchAsync(server.Client, url, 0, Bytes(1000));

        var response = await PatchAsync(server.Client, url, 1000, Bytes(1000, seed: 2), r =>
            r.Headers.Add(TusHeaderNames.UploadChecksum, "sha1 " + Convert.ToBase64String(SHA1.HashData(Bytes(1000, seed: 3)))));

        Assert.Equal(460, (int)response.StatusCode);
        Assert.Equal(1000, OffsetOf(await HeadAsync(server.Client, url)));
    }

    [Fact]
    public async Task An_unsupported_checksum_algorithm_is_a_400()
    {
        using var dir = new UploadDirectory();
        await using var server = await StartAsync(dir);

        var url = await CreateAsync(server.Client, 10);
        var response = await PatchAsync(server.Client, url, 0, Bytes(4), r =>
            r.Headers.Add(TusHeaderNames.UploadChecksum, "crc32 AAAAAA=="));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- expiration ----

    [Fact]
    public async Task An_unfinished_upload_expires_and_answers_410()
    {
        using var dir = new UploadDirectory();
        var clock = new ManualTimeProvider();

        await using var server = await StartAsync(dir, o =>
        {
            o.Expiration = TimeSpan.FromHours(1);
            o.TimeProvider = clock;
        });

        using var create = Tus(HttpMethod.Post, "/files");
        create.Headers.Add(TusHeaderNames.UploadLength, "10");

        var created = await Send(server.Client, create, Token);
        var url = created.Headers.Location!.AbsolutePath;

        Assert.Equal(clock.Now.AddHours(1).ToString("r"), Header(created, TusHeaderNames.UploadExpires));

        // Progress slides the expiry forward.
        clock.Now = clock.Now.AddMinutes(50);
        var patched = await PatchAsync(server.Client, url, 0, Bytes(3));
        Assert.Equal(clock.Now.AddHours(1).ToString("r"), Header(patched, TusHeaderNames.UploadExpires));

        clock.Now = clock.Now.AddMinutes(50);
        Assert.Equal(HttpStatusCode.OK, (await HeadAsync(server.Client, url)).StatusCode);

        clock.Now = clock.Now.AddMinutes(11);
        Assert.Equal((HttpStatusCode)410, (await HeadAsync(server.Client, url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await HeadAsync(server.Client, url)).StatusCode);
    }

    [Fact]
    public async Task The_sweep_removes_expired_uploads_but_not_finished_ones()
    {
        using var dir = new UploadDirectory();
        var clock = new ManualTimeProvider();
        TusMountBuilder? mount = null;

        await using var server = await TestServer.StartAsync(app => mount = app.MapTus("/files", o =>
        {
            o.Store = dir.Store();
            o.Expiration = TimeSpan.FromHours(1);
            o.TimeProvider = clock;
        }));

        var abandoned = await CreateAsync(server.Client, 10);
        var finished = await CreateAsync(server.Client, 3);
        await PatchAsync(server.Client, finished, 0, Bytes(3));

        Assert.Equal(0, await mount!.RemoveExpiredUploadsAsync(Token));

        clock.Now = clock.Now.AddHours(2);

        Assert.Equal(1, await mount.RemoveExpiredUploadsAsync(Token));
        Assert.Equal(-1, dir.LengthOf(IdOf(abandoned)));
        Assert.Equal(3, dir.LengthOf(IdOf(finished)));
        Assert.Equal(HttpStatusCode.OK, (await HeadAsync(server.Client, finished)).StatusCode);
    }

    // ---- concatenation ----

    [Fact]
    public async Task Partial_uploads_are_joined_into_a_final_one_in_order()
    {
        using var dir = new UploadDirectory();
        var completions = new List<TusUpload>();

        await using var server = await StartAsync(dir, o => o.OnUploadCompleteAsync = ctx =>
        {
            completions.Add(ctx.Upload);
            return ValueTask.CompletedTask;
        });

        var first = Bytes(3000, seed: 1);
        var second = Bytes(2000, seed: 2);

        var a = await CreateAsync(server.Client, first.Length, concat: "partial");
        var b = await CreateAsync(server.Client, second.Length, concat: "partial");

        // In parallel, as a client that splits an upload does.
        await Task.WhenAll(
            PatchAsync(server.Client, b, 0, second),
            PatchAsync(server.Client, a, 0, first)
        );

        Assert.Empty(completions);

        var head = await HeadAsync(server.Client, a);
        Assert.Equal("partial", Header(head, TusHeaderNames.UploadConcat));

        var final = await CreateAsync(server.Client, length: null, concat: $"final;{a} http://127.0.0.1:{server.Port}{b}");

        var finalHead = await HeadAsync(server.Client, final);
        Assert.Equal(5000, OffsetOf(finalHead));
        Assert.Equal("5000", Header(finalHead, TusHeaderNames.UploadLength));
        Assert.Equal($"final;{a} {b}", Header(finalHead, TusHeaderNames.UploadConcat));

        var joined = await File.ReadAllBytesAsync(Path.Combine(dir.Path, IdOf(final)), Token);
        Assert.Equal(first.Concat(second).ToArray(), joined);

        var completion = Assert.Single(completions);
        Assert.Equal(TusConcatenation.Final, completion.Concatenation);

        Assert.Equal(HttpStatusCode.Forbidden, (await PatchAsync(server.Client, final, 5000, Bytes(1))).StatusCode);
    }

    [Fact]
    public async Task A_final_upload_of_unfinished_parts_is_refused()
    {
        using var dir = new UploadDirectory();
        await using var server = await StartAsync(dir);

        var a = await CreateAsync(server.Client, 10, concat: "partial");

        using var request = Tus(HttpMethod.Post, "/files");
        request.Headers.Add(TusHeaderNames.UploadConcat, $"final;{a}");

        Assert.Equal(HttpStatusCode.BadRequest, (await Send(server.Client, request, Token)).StatusCode);
    }

    // ---- interrupted, stalled and concurrent requests ----

    [Fact]
    public async Task A_patch_cut_off_mid_body_keeps_what_arrived_and_the_upload_resumes_from_there()
    {
        using var dir = new UploadDirectory();
        var payload = Bytes(200_000);
        byte[]? completed = null;

        await using var server = await StartAsync(dir, o => o.OnUploadCompleteAsync = async ctx =>
        {
            await using var stream = await ctx.OpenReadAsync();
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            completed = copy.ToArray();
        });

        var url = await CreateAsync(server.Client, payload.Length);

        // The phone goes into a tunnel: 70 KB of a 200 KB body, then the connection is gone.
        using (var request = InterruptedPatch(url, 0, payload[..70_000], payload.Length, _ => throw new IOException("Signal lost.")))
            await Assert.ThrowsAnyAsync<Exception>(() => Send(server.Client, request, Token));

        await WaitForAsync(() => dir.LengthOf(IdOf(url)) == 70_000);

        var head = await HeadAsync(server.Client, url);
        Assert.Equal(70_000, OffsetOf(head));

        var resumed = await PatchAsync(server.Client, url, 70_000, payload[70_000..]);

        Assert.Equal(HttpStatusCode.NoContent, resumed.StatusCode);
        Assert.Equal(payload, completed);
    }

    [Fact]
    public async Task A_patch_cut_off_mid_body_keeps_nothing_when_it_carried_a_checksum()
    {
        using var dir = new UploadDirectory();
        await using var server = await StartAsync(dir);

        var payload = Bytes(50_000);
        var url = await CreateAsync(server.Client, payload.Length);

        // Stalled rather than thrown, so the test can see the bytes land on disk before the request
        // is cut off - otherwise an offset of zero would pass just as well for a server that never
        // received anything.
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(Token);
        using var stalledClient = new HttpClient { BaseAddress = server.Client.BaseAddress };

        var request = InterruptedPatch(url, 0, payload[..20_000], payload.Length, ct => Task.Delay(Timeout.Infinite, ct));
        request.Headers.Add(TusHeaderNames.UploadChecksum, "sha1 " + Convert.ToBase64String(SHA1.HashData(payload)));

        var stalled = Send(stalledClient, request, stall.Token);

        await WaitForAsync(() => dir.LengthOf(IdOf(url)) == 20_000);

        // The HEAD takes the upload over; the interrupted body can no longer be checked against its
        // checksum, so all of it goes.
        Assert.Equal(0, OffsetOf(await HeadAsync(server.Client, url)));
        Assert.Equal(0, dir.LengthOf(IdOf(url)));

        stall.Cancel();
        await Assert.ThrowsAnyAsync<Exception>(() => stalled);
    }

    [Fact]
    public async Task A_head_takes_over_from_a_stalled_patch_and_reports_what_it_had_received()
    {
        using var dir = new UploadDirectory();
        await using var server = await StartAsync(dir);

        var payload = Bytes(100_000);
        var url = await CreateAsync(server.Client, payload.Length);

        // A connection that is not dead yet, only silent - the server cannot tell the difference,
        // and without a take-over the upload would be held until a read timeout.
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(Token);
        using var stalledClient = new HttpClient { BaseAddress = server.Client.BaseAddress };
        var stalled = Send(stalledClient, 
            InterruptedPatch(url, 0, payload[..30_000], payload.Length, ct => Task.Delay(Timeout.Infinite, ct)),
            stall.Token
        );

        await WaitForAsync(() => dir.LengthOf(IdOf(url)) == 30_000);

        var head = await HeadAsync(server.Client, url);

        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(30_000, OffsetOf(head));

        var resumed = await PatchAsync(server.Client, url, 30_000, payload[30_000..]);
        Assert.Equal(HttpStatusCode.NoContent, resumed.StatusCode);

        stall.Cancel();
        await Assert.ThrowsAnyAsync<Exception>(() => stalled);
    }

    [Fact]
    public async Task Two_patches_to_one_upload_never_interleave_the_second_is_locked_out()
    {
        using var dir = new UploadDirectory();
        await using var server = await StartAsync(dir, o => o.LockReleaseTimeout = TimeSpan.Zero);

        var payload = Bytes(100_000);
        var url = await CreateAsync(server.Client, payload.Length);

        using var stall = CancellationTokenSource.CreateLinkedTokenSource(Token);
        using var stalledClient = new HttpClient { BaseAddress = server.Client.BaseAddress };
        var stalled = Send(stalledClient, 
            InterruptedPatch(url, 0, payload[..10_000], payload.Length, ct => Task.Delay(Timeout.Infinite, ct)),
            stall.Token
        );

        await WaitForAsync(() => dir.LengthOf(IdOf(url)) == 10_000);

        var second = await PatchAsync(server.Client, url, 0, payload[..500]);
        Assert.Equal((HttpStatusCode)423, second.StatusCode);

        var head = await HeadAsync(server.Client, url);
        Assert.Equal((HttpStatusCode)423, head.StatusCode);

        stall.Cancel();
        await Assert.ThrowsAnyAsync<Exception>(() => stalled);

        // Once the first lets go, what it received is there and nothing of the second is.
        await WaitForAsync(() => HeadAsync(server.Client, url).GetAwaiter().GetResult().StatusCode == HttpStatusCode.OK);
        Assert.Equal(10_000, OffsetOf(await HeadAsync(server.Client, url)));
    }

    // ---- durability ----

    [Fact]
    public async Task An_upload_resumes_after_the_server_restarts()
    {
        using var dir = new UploadDirectory();
        var payload = Bytes(20_000);
        string url;

        await using (var first = await StartAsync(dir))
        {
            url = await CreateAsync(first.Client, payload.Length, "filename " + Convert.ToBase64String("a.bin"u8.ToArray()));
            await PatchAsync(first.Client, url, 0, payload[..12_345]);
        }

        byte[]? completed = null;

        await using var second = await StartAsync(dir, o => o.OnUploadCompleteAsync = async ctx =>
        {
            await using var stream = await ctx.OpenReadAsync();
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            completed = copy.ToArray();
        });

        var head = await HeadAsync(second.Client, url);
        Assert.Equal(12_345, OffsetOf(head));
        Assert.Equal("a.bin", TusMetadata.Parse(Header(head, TusHeaderNames.UploadMetadata))["filename"]);

        await PatchAsync(second.Client, url, 12_345, payload[12_345..]);
        Assert.Equal(payload, completed);
    }

    // ---- protocols and transports ----

    [Fact]
    public async Task The_whole_flow_works_over_http2_including_an_interrupted_patch()
    {
        using var dir = new UploadDirectory();
        var payload = Bytes(150_000);
        byte[]? completed = null;

        await using var server = await StartAsync(dir, o => o.OnUploadCompleteAsync = async ctx =>
        {
            Assert.Equal("HTTP/2", ctx.HttpContext.Request.Protocol);
            await using var stream = await ctx.OpenReadAsync();
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            completed = copy.ToArray();
        });

        using var client = Http2Client(server.Port);

        var url = await CreateAsync(client, payload.Length);

        using (var request = InterruptedPatch(url, 0, payload[..50_000], payload.Length, _ => throw new IOException("Signal lost.")))
            await Assert.ThrowsAnyAsync<Exception>(() => Send(client, request, Token));

        // How much of the 50 KB made it is the client's business: HTTP/2 lets it reset the stream
        // with DATA frames still queued. What matters is that the server's answer is exactly what it
        // stored, and that resuming from there produces the file. The deterministic "bytes received
        // are kept" case over HTTP/2 is the stalled test below.
        var head = await HeadAsync(client, url);

        // Same connection: a stream reset must not have taken it down.
        Assert.Equal(HttpVersion.Version20, head.Version);

        var offset = OffsetOf(head);
        Assert.InRange(offset, 0, 50_000);
        Assert.Equal(offset, dir.LengthOf(IdOf(url)));

        var resumed = await PatchAsync(client, url, offset, payload[(int)offset..]);
        Assert.Equal(HttpStatusCode.NoContent, resumed.StatusCode);
        Assert.Equal(payload, completed);
    }

    [Fact]
    public async Task A_stalled_http2_patch_is_taken_over_without_closing_the_connection()
    {
        using var dir = new UploadDirectory();
        await using var server = await StartAsync(dir);

        var payload = Bytes(60_000);
        using var client = Http2Client(server.Port);
        var url = await CreateAsync(client, payload.Length);

        using var stall = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var stalled = Send(client, 
            InterruptedPatch(url, 0, payload[..20_000], payload.Length, ct => Task.Delay(Timeout.Infinite, ct)),
            stall.Token
        );

        await WaitForAsync(() => dir.LengthOf(IdOf(url)) == 20_000);

        var head = await HeadAsync(client, url);
        Assert.Equal(20_000, OffsetOf(head));

        var resumed = await PatchAsync(client, url, 20_000, payload[20_000..]);
        Assert.Equal(HttpStatusCode.NoContent, resumed.StatusCode);

        stall.Cancel();

        try
        {
            await stalled;
        }
        catch (Exception)
        {
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Works_over_an_in_memory_connection_as_a_tunnel_delivers_it(bool useHttp2)
    {
        using var dir = new UploadDirectory();

        await using var app = TestHttpServer.Create(
            server => server.MapTus("/files", o => o.Store = dir.Store()),
            useHttp2: useHttp2
        );

        var payload = Bytes(10_000);
        var url = await CreateAsync(app.Client, payload.Length);

        await PatchAsync(app.Client, url, 0, payload[..4_000]);
        var last = await PatchAsync(app.Client, url, 4_000, payload[4_000..]);

        Assert.Equal(HttpStatusCode.NoContent, last.StatusCode);
        Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(dir.Path, IdOf(url)), Token));
    }

    [Fact]
    public async Task Maps_inside_a_group_and_hands_out_urls_with_the_outer_prefix()
    {
        using var dir = new UploadDirectory();
        await using var server = await TestServer.StartAsync(app =>
            app.MapGroup("/api", api => api.MapTus("/files", new TusOptions { Store = dir.Store() })));

        using var request = Tus(HttpMethod.Post, "/api/files");
        request.Headers.Add(TusHeaderNames.UploadLength, "4");

        var created = await Send(server.Client, request, Token);
        var url = created.Headers.Location!.AbsolutePath;

        Assert.StartsWith("/api/files/", url);
        Assert.Equal(HttpStatusCode.NoContent, (await PatchAsync(server.Client, url, 0, Bytes(4))).StatusCode);
    }

    // ---- composition with the rest of the server ----

    [Fact]
    public async Task Require_authorization_covers_every_route()
    {
        using var dir = new UploadDirectory();

        await using var server = await TestServer.StartAsync(
            app =>
            {
                app.UseAuthentication();
                app.UseAuthorization();
                app.MapTus("/files", o => o.Store = dir.Store()).RequireAuthorization();
            },
            builder =>
            {
                builder.AddAuthentication().AddApiKey(o => o.AddKey("k3y", "uploader"));
                builder.AddAuthorization();
            }
        );

        using var anonymous = Tus(HttpMethod.Post, "/files");
        anonymous.Headers.Add(TusHeaderNames.UploadLength, "4");
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(server.Client, anonymous, Token)).StatusCode);

        server.Client.DefaultRequestHeaders.Add("X-API-Key", "k3y");
        var url = await CreateAsync(server.Client, 4);

        server.Client.DefaultRequestHeaders.Remove("X-API-Key");
        Assert.Equal(HttpStatusCode.Unauthorized, (await PatchAsync(server.Client, url, 0, Bytes(4))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(server.Client, Tus(HttpMethod.Delete, url), Token)).StatusCode);
    }

    [Fact]
    public async Task Cors_responses_expose_the_tus_headers_even_when_the_policy_forgot_them()
    {
        using var dir = new UploadDirectory();
        const string origin = "https://app.example.com";

        await using var server = await TestServer.StartAsync(app =>
        {
            app.UseCors(p => p.WithOrigins(origin).AllowAnyHeader().AllowAnyMethod().WithExposedHeaders("X-Mine"));
            app.MapTus("/files", o => o.Store = dir.Store());
        });

        using var request = Tus(HttpMethod.Post, "/files");
        request.Headers.Add(TusHeaderNames.UploadLength, "4");
        request.Headers.Add(HeaderNames.Origin, origin);

        var response = await Send(server.Client, request, Token);
        var exposed = Header(response, HeaderNames.AccessControlExposeHeaders)!.Split(',').Select(h => h.Trim()).ToList();

        Assert.Equal(origin, Header(response, HeaderNames.AccessControlAllowOrigin));
        Assert.Contains("X-Mine", exposed);
        Assert.Contains("Upload-Offset", exposed);
        Assert.Contains("Location", exposed);
        Assert.Contains("Tus-Resumable", exposed);
    }

    [Fact]
    public async Task A_policy_with_tus_headers_passes_the_preflight_for_a_patch()
    {
        using var dir = new UploadDirectory();
        const string origin = "https://app.example.com";

        await using var server = await TestServer.StartAsync(app =>
        {
            app.UseCors(p => p.WithOrigins(origin).WithTusHeaders());
            app.MapTus("/files", o => o.Store = dir.Store());
        });

        var url = await CreateAsync(server.Client, 4);

        using var preflight = new HttpRequestMessage(HttpMethod.Options, url);
        preflight.Headers.Add(HeaderNames.Origin, origin);
        preflight.Headers.Add(HeaderNames.AccessControlRequestMethod, "PATCH");
        preflight.Headers.Add(HeaderNames.AccessControlRequestHeaders, "tus-resumable, upload-offset, content-type");

        var response = await Send(server.Client, preflight, Token);

        Assert.Equal(origin, Header(response, HeaderNames.AccessControlAllowOrigin));
        Assert.Contains("PATCH", Header(response, HeaderNames.AccessControlAllowMethods));
    }

    // ---- metadata parsing ----

    [Theory]
    [InlineData("filename ZmlsZS50eHQ=,type dGV4dC9wbGFpbg==", true)]
    [InlineData("flag", true)]
    [InlineData("", true)]
    [InlineData("a YQ==,a Yg==", false)]
    [InlineData("a YQ==,,b Yg==", false)]
    [InlineData("a not-base64", false)]
    public void Parses_upload_metadata(string header, bool valid)
    {
        Assert.Equal(valid, TusMetadata.TryParse(header, out var metadata));

        if (valid && header.StartsWith("filename", StringComparison.Ordinal))
        {
            Assert.Equal("file.txt", metadata!["filename"]);
            Assert.Equal("text/plain", metadata["type"]);
        }
    }

    [Fact]
    public void Metadata_built_from_text_round_trips()
    {
        var metadata = TusMetadata.Create([new("filename", "ü.txt"), new("flag", null)]);
        var parsed = TusMetadata.Parse(metadata.Header);

        Assert.Equal("ü.txt", parsed["filename"]);
        Assert.Null(parsed["flag"]);
    }
}
