using System.Net;
using System.Text;
using Shiny.Net.HttpServer.CommandLine;
using Shiny.Net.HttpServer.FileBrowser;

namespace Shiny.Net.HttpServer.Tests;

/// <summary>
/// The tool's argument surface. Parsing is where a CLI is wrong in ways nothing else catches — a
/// misread flag serves a directory with permissions nobody asked for.
/// </summary>
public class CommandLineParsingTests
{
    /// <summary>
    /// The action is the injection point: it runs instead of the server, so a parse can be observed
    /// without a socket.
    /// </summary>
    static async Task<ServeSettings> ParseAsync(params string[] args)
    {
        ServeSettings? captured = null;

        var exitCode = await Cli
            .Build((settings, _) =>
            {
                captured = settings;
                return Task.FromResult(0);
            })
            .Parse(args)
            .InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);

        return captured!;
    }

    static IReadOnlyList<string> ErrorsFor(params string[] args)
        => Cli.Build((_, _) => Task.FromResult(0)).Parse(args).Errors.Select(x => x.Message).ToList();

    [Fact]
    public async Task Defaults_to_the_current_directory_read_only_and_unauthenticated()
    {
        var settings = await ParseAsync();

        Assert.Equal(Directory.GetCurrentDirectory(), settings.RootPath);
        Assert.Equal(Permissions.Read, settings.Permissions);
        Assert.False(settings.Permissions.AllowsChanges());
        Assert.False(settings.AuthEnabled);
        Assert.Equal("/", settings.UrlPrefix);
        Assert.Equal(8080, settings.Port);

        // Loopback rather than every interface: the safer of the two is the one you get by accident.
        Assert.Equal(IPAddress.Loopback, settings.Address);
    }

    [Theory]
    [InlineData(new[] { "--allow", "create" }, Permissions.Read | Permissions.Create)]
    [InlineData(new[] { "--allow", "create,delete" }, Permissions.Read | Permissions.Create | Permissions.Delete)]
    [InlineData(new[] { "-m", "update", "-m", "delete" }, Permissions.Read | Permissions.Update | Permissions.Delete)]
    [InlineData(new[] { "--allow", "all" }, Permissions.Read | Permissions.Create | Permissions.Update | Permissions.Delete)]
    public async Task Reads_operations_repeated_or_comma_separated(string[] args, Permissions expected)
        => Assert.Equal(expected, (await ParseAsync(args)).Permissions);

    /// <summary>Read is the one operation that cannot be switched off, so asking for anything implies it.</summary>
    [Fact]
    public async Task Always_allows_reading()
        => Assert.True((await ParseAsync("--allow", "delete")).Permissions.Has(Permissions.Read));

    [Theory]
    [InlineData("any", "0.0.0.0")]
    [InlineData("localhost", "127.0.0.1")]
    [InlineData("192.168.1.10", "192.168.1.10")]
    public async Task Reads_an_address_by_name_or_by_number(string argument, string expected)
        => Assert.Equal(IPAddress.Parse(expected), (await ParseAsync("--address", argument)).Address);

    [Theory]
    [InlineData("500k", 500 * 1024L)]
    [InlineData("64mb", 64 * 1024L * 1024)]
    [InlineData("2gb", 2 * 1024L * 1024 * 1024)]
    [InlineData("4096", 4096L)]
    public async Task Reads_an_upload_limit_with_or_without_a_suffix(string argument, long expected)
        => Assert.Equal(expected, (await ParseAsync("--max-upload", argument)).MaxUploadBytes);

    [Fact]
    public async Task Reads_credentials_and_keeps_a_password_containing_a_colon()
    {
        var settings = await ParseAsync("--user", "ada:pass:word", "-u", "grace:hopper");

        Assert.True(settings.AuthEnabled);
        Assert.Equal(["ada", "grace"], settings.Users.Select(x => x.Username));
        Assert.Equal("pass:word", settings.Users[0].Password);
    }

    [Fact]
    public async Task Normalizes_a_prefix()
    {
        Assert.Equal("/files", (await ParseAsync("--prefix", "files/")).UrlPrefix);
        Assert.Equal("/files", (await ParseAsync("--prefix", "/files/")).UrlPrefix);
        Assert.Equal("/", (await ParseAsync("--prefix", "/")).UrlPrefix);
    }

    [Theory]
    [InlineData(new[] { "--allow", "frobnicate" }, "is not an operation")]
    [InlineData(new[] { "--user", "nopassword" }, "is not a credential")]
    [InlineData(new[] { "--max-upload", "12quux" }, "is not a size")]
    [InlineData(new[] { "--address", "not-an-ip" }, "is not an IP address")]
    public void Refuses_an_argument_it_cannot_read(string[] args, string expected)
        => Assert.Contains(ErrorsFor(args), x => x.Contains(expected, StringComparison.Ordinal));
}


/// <summary>
/// The file browser has one write switch, so create and update are the same PUT to it. This is the
/// piece that tells them apart, and getting it backwards silently grants the operation that was
/// withheld.
/// </summary>
public class WriteGuardTests
{
    static Task<TestServer> StartAsync(ContentRoot root, Permissions permissions, string prefix = "/")
        => TestServer.StartAsync(app =>
        {
            app.Use(new WriteGuard(prefix, root.Path, permissions));
            app.MapFileBrowser(prefix, o =>
            {
                o.RootPath = root.Path;
                o.AllowWrite = true;
                o.AllowDelete = permissions.Has(Permissions.Delete);
                o.AllowCreateDirectories = permissions.Has(Permissions.Create);
            });
        });

    static StringContent Body() => new("written", Encoding.UTF8);

    [Fact]
    public async Task Create_alone_writes_a_new_file_but_never_over_one()
    {
        using var root = new ContentRoot().With("notes.txt", "original");
        await using var server = await StartAsync(root, Permissions.Read | Permissions.Create);
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(HttpStatusCode.Created, (await server.Client.PutAsync("/new.txt", Body(), ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await server.Client.PutAsync("/notes.txt", Body(), ct)).StatusCode);

        // Refused before the handler ran, so the original is untouched rather than half-replaced.
        Assert.Equal("original", File.ReadAllText(Path.Combine(root.Path, "notes.txt")));
    }

    [Fact]
    public async Task Update_alone_writes_over_a_file_but_never_makes_one()
    {
        using var root = new ContentRoot().With("notes.txt", "original");
        await using var server = await StartAsync(root, Permissions.Read | Permissions.Update);
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(HttpStatusCode.OK, (await server.Client.PutAsync("/notes.txt", Body(), ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await server.Client.PutAsync("/new.txt", Body(), ct)).StatusCode);
        Assert.False(File.Exists(Path.Combine(root.Path, "new.txt")));
    }

    /// <summary>A directory that does not exist yet is a create, whichever way the path is spelled.</summary>
    [Fact]
    public async Task Treats_making_a_directory_as_a_create()
    {
        using var root = new ContentRoot().With("notes.txt", "original");
        await using var server = await StartAsync(root, Permissions.Read | Permissions.Update);

        var response = await server.Client.PutAsync("/archive/", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(Directory.Exists(Path.Combine(root.Path, "archive")));
    }

    /// <summary>
    /// The guard resolves a path of its own to decide, so a path that leaves the root has to fall
    /// through rather than be guessed at — the browser's own containment check is what refuses it.
    /// Sent raw because <see cref="HttpClient"/> collapses the dot segments before they reach a socket.
    /// </summary>
    [Fact]
    public async Task Leaves_a_path_outside_the_root_to_the_browser()
    {
        using var root = new ContentRoot().With("notes.txt", "original");
        await using var server = await StartAsync(root, Permissions.Read | Permissions.Create);

        var response = await server.SendRawAsync(
            "PUT /%2e%2e/escaped.txt HTTP/1.1\r\nHost: localhost\r\nContent-Length: 7\r\n\r\nwritten"
        );

        Assert.DoesNotContain("201", response, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(root.Path)!, "escaped.txt")));
    }

    [Fact]
    public async Task Applies_under_a_prefix_too()
    {
        using var root = new ContentRoot().With("notes.txt", "original");
        await using var server = await StartAsync(root, Permissions.Read | Permissions.Create, "/files");
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(HttpStatusCode.Created, (await server.Client.PutAsync("/files/new.txt", Body(), ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await server.Client.PutAsync("/files/notes.txt", Body(), ct)).StatusCode);
    }
}
