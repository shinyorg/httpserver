using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using NuGet.Configuration;
using NuGet.Packaging.Core;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using Shiny.Net.HttpServer.NuGet;
using NuGetNullLogger = global::NuGet.Common.NullLogger;

namespace Shiny.Net.HttpServer.Tests;

/// <summary>
/// The feed driven by NuGet's own client stack (NuGet.Protocol - what <c>dotnet restore</c> and
/// <c>dotnet nuget push</c> are built on), with raw HTTP only for the protocol details the client
/// hides and for the requests it cannot make.
/// </summary>
public class NuGetFeedTests
{
    const string ApiKey = "test-key";

    static CancellationToken Token => TestContext.Current.CancellationToken;

    sealed class FeedDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "shiny-nuget-" + Guid.NewGuid().ToString("n")[..8]
        );

        public string Packages => System.IO.Path.Combine(this.Path, "feed");

        public string Work => Directory.CreateDirectory(System.IO.Path.Combine(this.Path, "work")).FullName;

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

    static Task<TestServer> StartAsync(FeedDirectory dir, Action<NuGetFeedOptions>? configure = null)
        => TestServer.StartAsync(
            app => app.MapNuGetFeed("/nuget", o =>
            {
                o.Store = new DiskNuGetPackageStore(dir.Packages);
                o.ApiKeys.Add(ApiKey);
                configure?.Invoke(o);
            }),
            b => b.Options.Http2.AllowCleartext = true
        );

    static string SourceUrl(TestServer server) => $"http://127.0.0.1:{server.Port}/nuget/v3/index.json";

    static SourceRepository Source(TestServer server)
        => Repository.Factory.GetCoreV3(new PackageSource(SourceUrl(server), "test") { AllowInsecureConnections = true });

    static async Task<T> ResourceAsync<T>(TestServer server) where T : class, INuGetResource
        => await Source(server).GetResourceAsync<T>(Token) ?? throw new InvalidOperationException($"The client found no {typeof(T).Name}.");

    static SourceCacheContext NoCache() => new() { NoCache = true, DirectDownload = true };

    /// <summary>
    /// A real .nupkg: a zip with a nuspec at its root and a file in lib/, which is all NuGet asks of
    /// one.
    /// </summary>
    static byte[] Nupkg(string id, string version, string? dependencies = null, string? description = null, string? tags = null, string? packageType = null)
    {
        var nuspec = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata minClientVersion="2.12">
                <id>{id}</id>
                <version>{version}</version>
                <authors>Test Author, Someone Else</authors>
                <description>{description ?? "A test package called " + id}</description>
                <title>{id} title</title>
                <tags>{tags ?? "test shiny"}</tags>
                <projectUrl>https://shinylib.net</projectUrl>
                <license type="expression">MIT</license>
                {(packageType is null ? "" : $"<packageTypes><packageType name=\"{packageType}\" /></packageTypes>")}
                {dependencies ?? ""}
              </metadata>
            </package>
            """;

        using var buffer = new MemoryStream();

        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var writer = new StreamWriter(zip.CreateEntry(id + ".nuspec").Open(), Encoding.UTF8))
                writer.Write(nuspec);

            using (var dll = zip.CreateEntry("lib/net10.0/" + id + ".dll").Open())
                dll.Write(Encoding.UTF8.GetBytes("not really a dll " + id + " " + version));
        }

        return buffer.ToArray();
    }

    static string Save(FeedDirectory dir, byte[] bytes, string id, string version)
    {
        var path = Path.Combine(dir.Work, $"{id}.{version}.nupkg");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    static async Task PushWithClientAsync(TestServer server, string path, string? apiKey = ApiKey)
    {
        var update = await ResourceAsync<PackageUpdateResource>(server);

        await update.Push(
            [path],
            symbolSource: null,
            timeoutInSecond: 30,
            disableBuffering: false,
            getApiKey: _ => apiKey,
            getSymbolApiKey: _ => null,
            noServiceEndpoint: false,
            skipDuplicate: false,
            symbolPackageUpdateResource: null,
            allowInsecureConnections: true,
            log: NuGetNullLogger.Instance
        );
    }

    static async Task<HttpResponseMessage> PushRawAsync(HttpClient client, byte[] package, string? apiKey = ApiKey)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(package);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(file, "package", "package.nupkg");

        using var request = new HttpRequestMessage(HttpMethod.Put, "/nuget/api/v2/package")
        {
            Content = content,
            Version = client.DefaultRequestVersion,
            VersionPolicy = client.DefaultVersionPolicy
        };

        if (apiKey is not null)
            request.Headers.Add("X-NuGet-ApiKey", apiKey);

        return await client.SendAsync(request, Token);
    }

    static async Task<JsonElement> GetJsonAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("application/json", response.Content.Headers.ContentType?.MediaType);

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token)).RootElement;
    }

    // ---- service index ----

    [Fact]
    public async Task ServiceIndexAdvertisesEveryResourceAsAbsoluteUrls()
    {
        using var dir = new FeedDirectory();
        await using var server = await StartAsync(dir);

        var index = await GetJsonAsync(server.Client, "/nuget/v3/index.json");
        Assert.Equal("3.0.0", index.GetProperty("version").GetString());

        var resources = index.GetProperty("resources").EnumerateArray()
            .ToDictionary(r => r.GetProperty("@type").GetString()!, r => r.GetProperty("@id").GetString()!);

        var root = $"http://127.0.0.1:{server.Port}/nuget";
        Assert.Equal(root + "/v3/package/", resources["PackageBaseAddress/3.0.0"]);
        Assert.Equal(root + "/v3/registration/", resources["RegistrationsBaseUrl/3.6.0"]);
        Assert.Equal(root + "/v3/search", resources["SearchQueryService"]);
        Assert.Equal(root + "/v3/autocomplete", resources["SearchAutocompleteService"]);
        Assert.Equal(root + "/api/v2/package", resources["PackagePublish/2.0.0"]);
    }

    [Fact]
    public async Task ReadOnlyFeedNeitherAdvertisesNorMapsPublish()
    {
        using var dir = new FeedDirectory();
        await using var server = await StartAsync(dir, o =>
        {
            o.ReadOnly = true;
            o.ApiKeys.Clear();
        });

        var index = await GetJsonAsync(server.Client, "/nuget/v3/index.json");
        Assert.DoesNotContain(index.GetProperty("resources").EnumerateArray(), r => r.GetProperty("@type").GetString() == "PackagePublish/2.0.0");

        var push = await PushRawAsync(server.Client, Nupkg("Read.Only", "1.0.0"));
        Assert.NotEqual(HttpStatusCode.Created, push.StatusCode);
    }

    [Fact]
    public async Task PublicBaseUrlReplacesTheRequestHost()
    {
        using var dir = new FeedDirectory();
        await using var server = await StartAsync(dir, o => o.PublicBaseUrl = "https://pkgs.example.com/nuget/");

        var index = await GetJsonAsync(server.Client, "/nuget/v3/index.json");

        Assert.All(index.GetProperty("resources").EnumerateArray(), r =>
            Assert.StartsWith("https://pkgs.example.com/nuget/", r.GetProperty("@id").GetString()));
    }

    // ---- push and download through NuGet's client ----

    [Fact]
    public async Task ClientPushesThenDownloadsTheSameBytes()
    {
        using var dir = new FeedDirectory();
        await using var server = await StartAsync(dir);

        var bytes = Nupkg("Shiny.Sample", "1.2.3");
        await PushWithClientAsync(server, Save(dir, bytes, "Shiny.Sample", "1.2.3"));

        var finder = await ResourceAsync<FindPackageByIdResource>(server);
        using var cache = NoCache();

        var versions = await finder.GetAllVersionsAsync("shiny.sample", cache, NuGetNullLogger.Instance, Token);
        Assert.Equal([NuGetVersion.Parse("1.2.3")], versions);

        using var downloaded = new MemoryStream();
        Assert.True(await finder.CopyNupkgToStreamAsync("Shiny.Sample", NuGetVersion.Parse("1.2.3"), downloaded, cache, NuGetNullLogger.Instance, Token));
        Assert.Equal(bytes, downloaded.ToArray());

        // On disk in NuGet's own folder-feed layout.
        Assert.True(File.Exists(Path.Combine(dir.Packages, "shiny.sample", "1.2.3", "shiny.sample.1.2.3.nupkg")));
        Assert.True(File.Exists(Path.Combine(dir.Packages, "shiny.sample", "1.2.3", "shiny.sample.1.2.3.nupkg.sha512")));
        Assert.True(File.Exists(Path.Combine(dir.Packages, "shiny.sample", "1.2.3", "shiny.sample.nuspec")));
    }

    [Fact]
    public async Task NuspecIsServedFromThePackageBaseAddress()
    {
        using var dir = new FeedDirectory();
        await using var server = await StartAsync(dir);

        Assert.Equal(HttpStatusCode.Created, (await PushRawAsync(server.Client, Nupkg("Has.Spec", "2.0.0"))).StatusCode);

        var response = await server.Client.GetAsync("/nuget/v3/package/has.spec/2.0.0/has.spec.nuspec", Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<id>Has.Spec</id>", await response.Content.ReadAsStringAsync(Token));

        var wrongFile = await server.Client.GetAsync("/nuget/v3/package/has.spec/2.0.0/other.nuspec", Token);
        Assert.Equal(HttpStatusCode.NotFound, wrongFile.StatusCode);

        var missing = await server.Client.GetAsync("/nuget/v3/package/has.spec/9.9.9/has.spec.9.9.9.nupkg", Token);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task VersionListIsLowerCaseNormalizedAndSorted()
    {
        using var dir = new FeedDirectory();
        await using var server = await StartAsync(dir);

        foreach (var version in (string[])["2.0.0", "1.0", "1.5.0-Beta.1", "1.10.0"])
            Assert.Equal(HttpStatusCode.Created, (await PushRawAsync(server.Client, Nupkg("Sorted", version))).StatusCode);

        var index = await GetJsonAsync(server.Client, "/nuget/v3/package/SORTED/index.json");

        Assert.Equal(
            ["1.0.0", "1.5.0-beta.1", "1.10.0", "2.0.0"],
            index.GetProperty("versions").EnumerateArray().Select(v => v.GetString()!).ToArray()
        );

        Assert.Equal(HttpStatusCode.NotFound, (await server.Client.GetAsync("/nuget/v3/package/nothing/index.json", Token)).StatusCode);
    }

    // ---- push rules ----

    [Fact]
    public async Task PushNeedsAValidApiKey()
    {
        using var dir = new FeedDirectory();
        await using var server = await StartAsync(dir);
        var package = Nupkg("Keyed", "1.0.0");

        Assert.Equal(HttpStatusCode.Forbidden, (await PushRawAsync(server.Client, package, apiKey: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PushRawAsync(server.Client, package, apiKey: "wrong")).StatusCode);
        Assert.Empty(Directory.EnumerateFileSystemEntries(dir.Packages));

        // And the client surfaces it as a failure rather than carrying on.
        await Assert.ThrowsAnyAsync<Exception>(() => PushWithClientAsync(server, Save(dir, package, "Keyed", "1.0.0"), apiKey: "wrong"));
    }

    [Fact]
    public async Task ValidateApiKeyAsyncIsConsultedForUnknownKeys()
    {
        using var dir = new FeedDirectory();
        await using var server = await StartAsync(dir, o => o.ValidateApiKeyAsync = ctx => ValueTask.FromResult(ctx.ApiKey == "from-callback"));

        Assert.Equal(HttpStatusCode.Created, (await PushRawAsync(server.Client, Nupkg("Callback", "1.0.0"), "from-callback")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PushRawAsync(server.Client, Nupkg("Callback", "1.0.1"), "nope")).StatusCode);
    }

    [Fact]
    public async Task DuplicatePushIsAConflict()
    {
        using var dir = new FeedDirectory();
        await using var server = await StartAsync(dir);

        Assert.Equal(HttpStatusCode.Created, (await PushRawAsync(server.Client, Nupkg("Twice", "1.0.0"))).StatusCode);

        // 1.0 is the same version as 1.0.0, and the id compares without case.
        Assert.Equal(HttpStatusCode.Conflict, (await PushRawAsync(server.Client, Nupkg("TWICE", "1.0"))).StatusCode);
    }

    [Fact]
    public async Task AllowOverwriteReplacesTheBytes()
    {
        using var dir = new FeedDirectory();
        await using var server = await StartAsync(dir, o => o.AllowOverwrite = true);

        Assert.Equal(HttpStatusCode.Created, (await PushRawAsync(server.Client, Nupkg("Over", "1.0.0", description: "first"))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PushRawAsync(server.Client, Nupkg("Over", "1.0.0", description: "second"))).StatusCode);

        var nuspec = await server.Client.GetStringAsync("/nuget/v3/package/over/1.0.0/over.nuspec", Token);
        Assert.Contains("second", nuspec);
    }

    [Fact]
    public async Task InvalidPackagesAreRejected()
    {
        using var dir = new FeedDirectory();
        await using var server = await StartAsync(dir);

        Assert.Equal(HttpStatusCode.BadRequest, (await PushRawAsync(server.Client, "not a zip"u8.ToArray())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PushRawAsync(server.Client, Nupkg("Bad..Id", "1.0.0"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PushRawAsync(server.Client, Nupkg("Fine", "not-a-version"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PushRawAsync(server.Client, Nupkg("Symbols", "1.0.0", packageType: "SymbolsPackage"))).StatusCode);
    }

    [Fact]
    public async Task OversizedPackageIsRefused()
    {
        using var dir = new FeedDirectory();
        await using var server = await StartAsync(dir, o => o.MaxPackageSize = 200);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await PushRawAsync(server.Client, Nupkg("Large", "1.0.0"))).StatusCode);
    }

    [Fact]
    public async Task OnBeforePushCanRejectAndOnPushedSeesThePackage()
    {
        using var dir = new FeedDirectory();
        NuGetPackage? pushed = null;
        string? pushedWith = null;

        await using var server = await StartAsync(dir, o =>
        {
            o.OnBeforePushAsync = ctx =>
            {
                if (!ctx.Package.Id.StartsWith("Company.", StringComparison.Ordinal))
                    ctx.Reject(StatusCodes.Status403Forbidden, "Only Company.* packages.");

                return ValueTask.CompletedTask;
            };
            o.OnPackagePushedAsync = ctx =>
            {
                pushed = ctx.Package;
                pushedWith = ctx.ApiKey;
                return ValueTask.CompletedTask;
            };
        });

        Assert.Equal(HttpStatusCode.Forbidden, (await PushRawAsync(server.Client, Nupkg("Other.Thing", "1.0.0"))).StatusCode);
        Assert.Null(pushed);

        Assert.Equal(HttpStatusCode.Created, (await PushRawAsync(server.Client, Nupkg("Company.Thing", "1.0.0"))).StatusCode);
        Assert.Equal("Company.Thing", pushed?.Id);
        Assert.Equal(ApiKey, pushedWith);
        Assert.NotNull(pushed!.Sha512);
    }

    [Fact]
    public void MappingAWritableFeedWithoutKeysFails()
    {
        var server = HttpServer.CreateBuilder().Build();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            server.MapNuGetFeed("/nuget", o => o.Store = new DiskNuGetPackageStore(Path.Combine(Path.GetTempPath(), "shiny-nuget-unused"))));

        Assert.Contains(nameof(NuGetFeedOptions.ApiKeys), ex.Message);
    }

    // ---- metadata, search, autocomplete through NuGet's client ----

    [Fact]
    public async Task ClientReadsMetadataIncludingDependencies()
    {
        using var dir = new FeedDirectory();
        await using var server = await StartAsync(dir);

        const string dependencies = """
            <dependencies>
              <group targetFramework="net10.0">
                <dependency id="Shiny.Core" version="5.8.0" />
              </group>
              <group targetFramework="netstandard2.0" />
            </dependencies>
            """;

        await PushWithClientAsync(server, Save(dir, Nupkg("With.Deps", "3.0.0", dependencies), "With.Deps", "3.0.0"));

        var metadata = await ResourceAsync<PackageMetadataResource>(server);
        using var cache = NoCache();
        var all = (await metadata.GetMetadataAsync("with.deps", includePrerelease: true, includeUnlisted: true, cache, NuGetNullLogger.Instance, Token)).ToList();

        var package = Assert.Single(all);
        Assert.Equal("With.Deps", package.Identity.Id);
        Assert.Equal(NuGetVersion.Parse("3.0.0"), package.Identity.Version);
        Assert.Equal("A test package called With.Deps", package.Description);
        Assert.Equal("Test Author, Someone Else", package.Authors);
        Assert.Equal("MIT", package.LicenseMetadata?.License);
        Assert.Equal(new Uri("https://shinylib.net"), package.ProjectUrl);
        Assert.True(package.IsListed);

        var groups = package.DependencySets.ToList();
        Assert.Equal(2, groups.Count);

        var net10 = groups.Single(g => g.TargetFramework.GetShortFolderName() == "net10.0");
        var dependency = Assert.Single(net10.Packages);
        Assert.Equal("Shiny.Core", dependency.Id);
        Assert.Equal(VersionRange.Parse("5.8.0"), dependency.VersionRange);

        var exact = await metadata.GetMetadataAsync(new PackageIdentity("With.Deps", NuGetVersion.Parse("3.0.0")), cache, NuGetNullLogger.Instance, Token);
        Assert.NotNull(exact);
    }

    [Fact]
    public async Task ClientSearchesWithPrereleaseFiltering()
    {
        using var dir = new FeedDirectory();
        await using var server = await StartAsync(dir);

        Assert.Equal(HttpStatusCode.Created, (await PushRawAsync(server.Client, Nupkg("Shiny.Alpha", "1.0.0", tags: "mobile"))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PushRawAsync(server.Client, Nupkg("Shiny.Alpha", "2.0.0-preview.1", tags: "mobile"))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PushRawAsync(server.Client, Nupkg("Shiny.Beta", "1.0.0-rc.1"))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PushRawAsync(server.Client, Nupkg("Unrelated", "1.0.0", description: "nothing to see", tags: "other"))).StatusCode);

        var search = await ResourceAsync<PackageSearchResource>(server);

        var stable = (await search.SearchAsync("shiny", new SearchFilter(includePrerelease: false), 0, 20, NuGetNullLogger.Instance, Token)).ToList();
        var alpha = Assert.Single(stable);
        Assert.Equal("Shiny.Alpha", alpha.Identity.Id);
        Assert.Equal(NuGetVersion.Parse("1.0.0"), alpha.Identity.Version);

        var withPrerelease = (await search.SearchAsync("shiny", new SearchFilter(includePrerelease: true), 0, 20, NuGetNullLogger.Instance, Token)).ToList();
        Assert.Equal(["Shiny.Alpha", "Shiny.Beta"], withPrerelease.Select(p => p.Identity.Id).ToArray());
        Assert.Equal(NuGetVersion.Parse("2.0.0-preview.1"), withPrerelease[0].Identity.Version);

        var byTag = (await search.SearchAsync("tags:mobile", new SearchFilter(includePrerelease: true), 0, 20, NuGetNullLogger.Instance, Token)).ToList();
        Assert.Equal("Shiny.Alpha", Assert.Single(byTag).Identity.Id);

        var everything = (await search.SearchAsync("", new SearchFilter(includePrerelease: true), 0, 20, NuGetNullLogger.Instance, Token)).ToList();
        Assert.Equal(3, everything.Count);

        var paged = (await search.SearchAsync("", new SearchFilter(includePrerelease: true), 1, 1, NuGetNullLogger.Instance, Token)).ToList();
        Assert.Equal("Shiny.Beta", Assert.Single(paged).Identity.Id);
    }

    [Fact]
    public async Task SemVer2PackagesNeedSemVerLevel()
    {
        using var dir = new FeedDirectory();
        await using var server = await StartAsync(dir);

        Assert.Equal(HttpStatusCode.Created, (await PushRawAsync(server.Client, Nupkg("Semver.Two", "1.0.0+build.5"))).StatusCode);

        var old = await GetJsonAsync(server.Client, "/nuget/v3/search?q=semver");
        Assert.Equal(0, old.GetProperty("totalHits").GetInt32());

        var modern = await GetJsonAsync(server.Client, "/nuget/v3/search?q=semver&semVerLevel=2.0.0");
        Assert.Equal("1.0.0+build.5", modern.GetProperty("data")[0].GetProperty("version").GetString());
    }

    [Fact]
    public async Task ClientAutocompletesIdsAndVersions()
    {
        using var dir = new FeedDirectory();
        await using var server = await StartAsync(dir);

        foreach (var (id, version) in (ValueTuple<string, string>[])[("Shiny.Core", "1.0.0"), ("Shiny.Core", "1.1.0"), ("Shiny.Jobs", "1.0.0"), ("Other", "1.0.0")])
            Assert.Equal(HttpStatusCode.Created, (await PushRawAsync(server.Client, Nupkg(id, version))).StatusCode);

        var autocomplete = await ResourceAsync<AutoCompleteResource>(server);

        var ids = await autocomplete.IdStartsWith("shiny", includePrerelease: false, NuGetNullLogger.Instance, Token);
        Assert.Equal(["Shiny.Core", "Shiny.Jobs"], ids.ToArray());

        using var cache = NoCache();
        var versions = await autocomplete.VersionStartsWith("shiny.core", "1", includePrerelease: false, cache, NuGetNullLogger.Instance, Token);
        Assert.Equal([NuGetVersion.Parse("1.0.0"), NuGetVersion.Parse("1.1.0")], versions.OrderBy(v => v).ToArray());
    }

    // ---- unlist, relist, delete ----

    [Fact]
    public async Task ClientDeleteUnlistsButKeepsTheVersionRestorable()
    {
        using var dir = new FeedDirectory();
        await using var server = await StartAsync(dir);

        Assert.Equal(HttpStatusCode.Created, (await PushRawAsync(server.Client, Nupkg("Hidden", "1.0.0"))).StatusCode);

        var update = await ResourceAsync<PackageUpdateResource>(server);
        await update.Delete("Hidden", "1.0.0", _ => ApiKey, _ => true, noServiceEndpoint: false, allowInsecureConnections: true, NuGetNullLogger.Instance);

        var search = await GetJsonAsync(server.Client, "/nuget/v3/search?q=hidden");
        Assert.Equal(0, search.GetProperty("totalHits").GetInt32());

        var registration = await GetJsonAsync(server.Client, "/nuget/v3/registration/hidden/index.json");
        var entry = registration.GetProperty("items")[0].GetProperty("items")[0].GetProperty("catalogEntry");
        Assert.False(entry.GetProperty("listed").GetBoolean());
        Assert.StartsWith("1900-01-01", entry.GetProperty("published").GetString());

        // Restore still works: the flat container still has it.
        var download = await server.Client.GetAsync("/nuget/v3/package/hidden/1.0.0/hidden.1.0.0.nupkg", Token);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);

        // POST relists - the protocol's verb, which no client command sends.
        using var relist = new HttpRequestMessage(HttpMethod.Post, "/nuget/api/v2/package/Hidden/1.0.0");
        relist.Headers.Add("X-NuGet-ApiKey", ApiKey);
        Assert.Equal(HttpStatusCode.OK, (await server.Client.SendAsync(relist, Token)).StatusCode);

        Assert.Equal(1, (await GetJsonAsync(server.Client, "/nuget/v3/search?q=hidden")).GetProperty("totalHits").GetInt32());
    }

    [Fact]
    public async Task DeleteBehaviorDeleteRemovesTheFiles()
    {
        using var dir = new FeedDirectory();
        await using var server = await StartAsync(dir, o => o.DeleteBehavior = NuGetDeleteBehavior.Delete);

        Assert.Equal(HttpStatusCode.Created, (await PushRawAsync(server.Client, Nupkg("Gone", "1.0.0"))).StatusCode);

        using var delete = new HttpRequestMessage(HttpMethod.Delete, "/nuget/api/v2/package/gone/1.0.0");
        delete.Headers.Add("X-NuGet-ApiKey", ApiKey);
        Assert.Equal(HttpStatusCode.NoContent, (await server.Client.SendAsync(delete, Token)).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await server.Client.GetAsync("/nuget/v3/package/gone/1.0.0/gone.1.0.0.nupkg", Token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await server.Client.GetAsync("/nuget/v3/registration/gone/index.json", Token)).StatusCode);
        Assert.False(Directory.Exists(Path.Combine(dir.Packages, "gone")));

        using var again = new HttpRequestMessage(HttpMethod.Delete, "/nuget/api/v2/package/gone/1.0.0");
        again.Headers.Add("X-NuGet-ApiKey", ApiKey);
        Assert.Equal(HttpStatusCode.NotFound, (await server.Client.SendAsync(again, Token)).StatusCode);
    }

    [Fact]
    public async Task DeleteNeedsAnApiKey()
    {
        using var dir = new FeedDirectory();
        await using var server = await StartAsync(dir);

        Assert.Equal(HttpStatusCode.Created, (await PushRawAsync(server.Client, Nupkg("Kept", "1.0.0"))).StatusCode);

        var delete = await server.Client.DeleteAsync("/nuget/api/v2/package/kept/1.0.0", Token);
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
    }

    // ---- the store ----

    [Fact]
    public async Task DiskStoreReadsBackWhatAnEarlierInstanceWrote()
    {
        using var dir = new FeedDirectory();

        await using (var server = await StartAsync(dir))
        {
            Assert.Equal(HttpStatusCode.Created, (await PushRawAsync(server.Client, Nupkg("Durable", "1.0.0"))).StatusCode);
            Assert.Equal(HttpStatusCode.Created, (await PushRawAsync(server.Client, Nupkg("Durable", "2.0.0"))).StatusCode);

            using var delete = new HttpRequestMessage(HttpMethod.Delete, "/nuget/api/v2/package/durable/2.0.0");
            delete.Headers.Add("X-NuGet-ApiKey", ApiKey);
            Assert.Equal(HttpStatusCode.NoContent, (await server.Client.SendAsync(delete, Token)).StatusCode);
        }

        var store = new DiskNuGetPackageStore(dir.Packages);
        var versions = (await store.GetVersionsAsync("DURABLE", Token)).OrderBy(p => p.Version).ToList();

        Assert.Equal(2, versions.Count);
        Assert.True(versions[0].Listed);
        Assert.False(versions[1].Listed);
        Assert.Equal("Durable", versions[0].Id);
        Assert.NotNull(versions[0].Sha512);
        Assert.True(versions[0].Size > 0);
    }

    [Fact]
    public async Task ReadOnlyFeedServesAFolderFilledByHand()
    {
        using var dir = new FeedDirectory();

        // Laid out the way `nuget add` / the global packages folder lays it out, nuspec and all.
        var bytes = Nupkg("Seeded", "4.0.0");
        var folder = Directory.CreateDirectory(Path.Combine(dir.Packages, "seeded", "4.0.0")).FullName;
        File.WriteAllBytes(Path.Combine(folder, "seeded.4.0.0.nupkg"), bytes);

        await using var server = await StartAsync(dir, o =>
        {
            o.ReadOnly = true;
            o.ApiKeys.Clear();
        });

        var finder = await ResourceAsync<FindPackageByIdResource>(server);
        using var cache = NoCache();
        using var downloaded = new MemoryStream();

        Assert.True(await finder.CopyNupkgToStreamAsync("Seeded", NuGetVersion.Parse("4.0.0"), downloaded, cache, NuGetNullLogger.Instance, Token));
        Assert.Equal(bytes, downloaded.ToArray());

        var search = await GetJsonAsync(server.Client, "/nuget/v3/search?q=seeded");
        Assert.Equal(1, search.GetProperty("totalHits").GetInt32());
    }

    // ---- transport ----

    [Fact]
    public async Task PushAndDownloadOverHttp2()
    {
        using var dir = new FeedDirectory();
        await using var server = await StartAsync(dir);

        using var client = new HttpClient(new SocketsHttpHandler())
        {
            BaseAddress = new Uri($"http://127.0.0.1:{server.Port}"),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };

        var bytes = Nupkg("Over.H2", "1.0.0");
        var push = await PushRawAsync(client, bytes);
        Assert.Equal(HttpVersion.Version20, push.Version);
        Assert.Equal(HttpStatusCode.Created, push.StatusCode);

        var download = await client.GetAsync("/nuget/v3/package/over.h2/1.0.0/over.h2.1.0.0.nupkg", Token);
        Assert.Equal(HttpVersion.Version20, download.Version);
        Assert.Equal(bytes, await download.Content.ReadAsByteArrayAsync(Token));
    }

    [Fact]
    public async Task HeadOnAPackageAnswersWithoutTheBody()
    {
        using var dir = new FeedDirectory();
        await using var server = await StartAsync(dir);

        var bytes = Nupkg("Headed", "1.0.0");
        Assert.Equal(HttpStatusCode.Created, (await PushRawAsync(server.Client, bytes)).StatusCode);

        using var head = new HttpRequestMessage(HttpMethod.Head, "/nuget/v3/package/headed/1.0.0/headed.1.0.0.nupkg");
        var response = await server.Client.SendAsync(head, Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(bytes.Length, response.Content.Headers.ContentLength);
    }
}
