using System.Net;
using System.Text;
using System.Xml.Linq;
using Shiny.Net.HttpServer.WebDav;

namespace Shiny.Net.HttpServer.Tests;

/// <summary>
/// A mount over something that is not one directory - <see cref="WebDavOptions.FileSystem"/> - and
/// the parts of the physical one that moved out of the handler with it.
/// </summary>
public class WebDavFileSystemTests
{
    static readonly XNamespace Dav = "DAV:";

    static Task<TestServer> StartAsync(IWebDavFileSystem fileSystem, Action<WebDavOptions>? configure = null)
        => TestServer.StartAsync(app => app.MapWebDav("/dav", o =>
        {
            o.FileSystem = fileSystem;
            o.AllowWrite = true;
            o.AllowDelete = true;
            configure?.Invoke(o);
        }));

    static async Task<XDocument> PropFindAsync(TestServer server, string path, string depth = "1")
    {
        var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), path);
        request.Headers.Add("Depth", depth);

        var response = await server.Client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.MultiStatus, response.StatusCode);

        return XDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_member_is_addressed_by_its_name_and_shown_by_its_display_name()
    {
        var fs = new MemoryFileSystem().Folder("@Work", displayName: "Work").File("@Work/plan.txt", "plan");
        await using var server = await StartAsync(fs);

        var document = await PropFindAsync(server, "/dav");

        var work = document.Root!
            .Elements(Dav + "response")
            .Single(r => r.Element(Dav + "href")!.Value == "/dav/%40Work/");

        Assert.Equal("Work", work.Descendants(Dav + "displayname").Single().Value);

        var file = await server.Client.GetStringAsync("/dav/@Work/plan.txt", TestContext.Current.CancellationToken);
        Assert.Equal("plan", file);
    }

    [Fact]
    public async Task Get_serves_the_length_of_the_stream_rather_than_the_one_the_listing_reported()
    {
        // A photo library reports the size of the original and serves a transcoded copy of it.
        var fs = new MemoryFileSystem().File("photo.jpg", "12345", reportedLength: 999_999);
        await using var server = await StartAsync(fs);

        var response = await server.Client.GetAsync("/dav/photo.jpg", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(5, response.Content.Headers.ContentLength);
        Assert.Equal("12345", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Put_mkcol_move_and_delete_reach_the_file_system()
    {
        var fs = new MemoryFileSystem();
        await using var server = await StartAsync(fs);
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(
            HttpStatusCode.Created,
            (await server.Client.SendAsync(new HttpRequestMessage(new HttpMethod("MKCOL"), "/dav/docs"), ct)).StatusCode
        );

        Assert.Equal(
            HttpStatusCode.Created,
            (await server.Client.PutAsync("/dav/docs/a.txt", new StringContent("hello"), ct)).StatusCode
        );

        var move = new HttpRequestMessage(new HttpMethod("MOVE"), "/dav/docs/a.txt");
        move.Headers.Add("Destination", "/dav/docs/b.txt");
        Assert.Equal(HttpStatusCode.Created, (await server.Client.SendAsync(move, ct)).StatusCode);

        Assert.Equal("hello", fs.Text("docs/b.txt"));
        Assert.Null(fs.GetEntry("docs/a.txt"));

        Assert.Equal(HttpStatusCode.NoContent, (await server.Client.DeleteAsync("/dav/docs", ct)).StatusCode);
        Assert.Null(fs.GetEntry("docs"));
    }

    [Theory]
    [InlineData("webdav", HttpStatusCode.Forbidden)]
    [InlineData("unauthorized", HttpStatusCode.Forbidden)]
    [InlineData("missing", HttpStatusCode.NotFound)]
    [InlineData("io", HttpStatusCode.Conflict)]
    public async Task A_refusal_from_the_file_system_is_answered_with_its_status(string refusal, HttpStatusCode expected)
    {
        var fs = new MemoryFileSystem { RefuseWrites = refusal };
        await using var server = await StartAsync(fs);

        var response = await server.Client.PutAsync(
            "/dav/a.txt",
            new StringContent("x"),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task An_upload_past_the_limit_is_refused_even_when_it_never_declared_a_length()
    {
        var fs = new MemoryFileSystem();
        await using var server = await StartAsync(fs, o => o.MaxUploadBytes = 8);

        // Streamed, so there is no Content-Length for the early check to refuse on: the limit has to
        // come from the stream the file system is reading.
        var content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes("far more than eight bytes")));
        content.Headers.ContentLength = null;

        var request = new HttpRequestMessage(HttpMethod.Put, "/dav/big.txt") { Content = content };
        request.Headers.TransferEncodingChunked = true;

        var response = await server.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Null(fs.GetEntry("big.txt"));
    }

    [Fact]
    public async Task A_physical_mount_refuses_a_write_under_a_link_that_leads_out_of_the_root()
    {
        using var root = new ContentRoot();
        using var outside = new ContentRoot();

        Directory.CreateSymbolicLink(Path.Combine(root.Path, "escape"), outside.Path);

        await using var server = await TestServer.StartAsync(app => app.MapWebDav("/dav", o =>
        {
            o.RootPath = root.Path;
            o.AllowWrite = true;
        }));

        var response = await server.Client.PutAsync(
            "/dav/escape/planted.txt",
            new StringContent("x"),
            TestContext.Current.CancellationToken
        );

        // 409 rather than 403: the linked folder does not resolve, so to the mount the file's parent
        // is simply not there. Either way what matters is where the bytes did not go - before the
        // file system checked every segment, only the final path was followed, and a new file has
        // no link of its own to follow.
        Assert.True(
            response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.Forbidden,
            $"answered {(int)response.StatusCode}"
        );
        Assert.False(File.Exists(Path.Combine(outside.Path, "planted.txt")));
    }

    [Fact]
    public void A_mount_with_neither_a_root_nor_a_file_system_is_a_startup_error()
    {
        var server = new HttpServer(new HttpServerOptions());

        Assert.Throws<InvalidOperationException>(() => server.MapWebDav("/dav", _ => { }));
    }

    /// <summary>A tree in a dictionary, which is as far from one directory as a file system gets.</summary>
    sealed class MemoryFileSystem : IWebDavFileSystem
    {
        readonly Dictionary<string, Node> nodes = new(StringComparer.Ordinal)
        {
            [""] = new Node(true, [], null, null)
        };

        public string? RefuseWrites { get; init; }

        public MemoryFileSystem Folder(string path, string? displayName = null)
        {
            this.nodes[path] = new Node(true, [], null, displayName);
            return this;
        }

        public MemoryFileSystem File(string path, string content, long? reportedLength = null)
        {
            this.nodes[path] = new Node(false, Encoding.UTF8.GetBytes(content), reportedLength, null);
            return this;
        }

        public string Text(string path) => Encoding.UTF8.GetString(this.nodes[path].Bytes);

        public WebDavEntry? GetEntry(string path)
            => this.nodes.TryGetValue(path, out var node) ? Describe(path, node) : null;

        public IEnumerable<WebDavEntry> GetChildren(string path)
            => this.nodes
                .Where(x => x.Key.Length > 0 && Parent(x.Key) == path)
                .Select(x => Describe(x.Key, x.Value))
                .ToArray();

        public ValueTask<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
            => new(new MemoryStream(this.nodes[path].Bytes, writable: false));

        public async ValueTask WriteAsync(string path, Stream content, CancellationToken cancellationToken)
        {
            switch (this.RefuseWrites)
            {
                case "webdav": throw new WebDavException(StatusCodes.Status403Forbidden);
                case "unauthorized": throw new UnauthorizedAccessException();
                case "missing": throw new FileNotFoundException();
                case "io": throw new IOException();
            }

            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);

            this.nodes[path] = new Node(false, buffer.ToArray(), null, null);
        }

        public ValueTask CreateDirectoryAsync(string path, CancellationToken cancellationToken)
        {
            this.Folder(path);
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteAsync(string path, CancellationToken cancellationToken)
        {
            foreach (var key in this.Under(path))
                this.nodes.Remove(key);

            return ValueTask.CompletedTask;
        }

        public ValueTask MoveAsync(string source, string destination, CancellationToken cancellationToken)
        {
            foreach (var key in this.Under(source))
            {
                this.nodes[destination + key[source.Length..]] = this.nodes[key];
                this.nodes.Remove(key);
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask CopyAsync(string source, string destination, bool recursive, CancellationToken cancellationToken)
        {
            foreach (var key in recursive ? this.Under(source) : [source])
                this.nodes[destination + key[source.Length..]] = this.nodes[key];

            return ValueTask.CompletedTask;
        }

        public WebDavQuota? GetQuota(string path) => null;

        string[] Under(string path)
            => this.nodes.Keys.Where(k => k == path || k.StartsWith(path + "/", StringComparison.Ordinal)).ToArray();

        static string Parent(string path)
        {
            var cut = path.LastIndexOf('/');
            return cut < 0 ? "" : path[..cut];
        }

        static WebDavEntry Describe(string path, Node node)
            => new(
                path[(path.LastIndexOf('/') + 1)..],
                node.IsCollection,
                node.ReportedLength ?? node.Bytes.Length,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch
            )
            {
                DisplayName = node.DisplayName
            };

        sealed record Node(bool IsCollection, byte[] Bytes, long? ReportedLength, string? DisplayName);
    }
}
