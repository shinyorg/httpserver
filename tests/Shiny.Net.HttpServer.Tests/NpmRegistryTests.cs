using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Shiny.Net.HttpServer.Npm;
using Shiny.Net.HttpServer.Npm.Internal;

namespace Shiny.Net.HttpServer.Tests;

/// <summary>
/// The registry driven with the requests npm 11 sends - the shapes were taken from the CLI's own
/// traffic against this server (publish, install, view, search, dist-tag, deprecate, unpublish,
/// owner, login, whoami), so these pin the protocol rather than a guess at it.
/// </summary>
public class NpmRegistryTests
{
    const string Token = "npm-token";
    const string OtherToken = "other-token";

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    sealed class Scratch : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "shiny-npm-" + Guid.NewGuid().ToString("n")[..8]);

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

    static Task<TestServer> StartAsync(Scratch scratch, Action<NpmRegistryOptions>? configure = null)
        => TestServer.StartAsync(
            app => app.MapNpmRegistry("/npm", o =>
            {
                o.Store = new DiskNpmPackageStore(scratch.Path);
                o.Tokens[Token] = "allan";
                o.Tokens[OtherToken] = "bob";
                configure?.Invoke(o);
            }),
            b => b.Options.Http2.AllowCleartext = true
        );

    static string Encoded(string name) => name.Replace("/", "%2f");

    static byte[] Tarball(string name, string version) => Encoding.UTF8.GetBytes($"pretend tarball of {name}@{version}");

    /// <summary>What <c>npm publish</c> PUTs: the manifest, its dist hashes, and the tarball in base64.</summary>
    static JsonObject PublishBody(string name, string version, string tag = "latest", JsonObject? extra = null, byte[]? tarball = null, string? integrityOverride = null)
    {
        tarball ??= Tarball(name, version);
        var file = $"{(name.StartsWith('@') ? name[(name.IndexOf('/') + 1)..] : name)}-{version}.tgz";

        var manifest = new JsonObject
        {
            ["name"] = name,
            ["version"] = version,
            ["description"] = $"{name} for tests",
            ["main"] = "index.js",
            ["keywords"] = new JsonArray("shiny", "test"),
            ["readme"] = "# readme",
            ["_id"] = $"{name}@{version}",
            ["dist"] = new JsonObject
            {
                ["integrity"] = integrityOverride ?? "sha512-" + Convert.ToBase64String(SHA512.HashData(tarball)),
                ["shasum"] = Convert.ToHexStringLower(SHA1.HashData(tarball)),
                ["tarball"] = $"http://localhost/npm/{name}/-/{file}"
            }
        };

        foreach (var (key, value) in extra ?? [])
            manifest[key] = value?.DeepClone();

        return new JsonObject
        {
            ["_id"] = name,
            ["name"] = name,
            ["description"] = $"{name} for tests",
            ["dist-tags"] = new JsonObject { [tag] = version },
            ["versions"] = new JsonObject { [version] = manifest },
            ["access"] = null,
            ["_attachments"] = new JsonObject
            {
                [file] = new JsonObject
                {
                    ["content_type"] = "application/octet-stream",
                    ["data"] = Convert.ToBase64String(tarball),
                    ["length"] = tarball.Length
                }
            }
        };
    }

    static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string url, JsonNode? body = null, string? token = Token, string? accept = null)
    {
        using var request = new HttpRequestMessage(method, url)
        {
            Version = client.DefaultRequestVersion,
            VersionPolicy = client.DefaultVersionPolicy
        };

        if (body is not null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        if (accept is not null)
            request.Headers.Accept.ParseAdd(accept);

        return await client.SendAsync(request, Ct);
    }

    static Task<HttpResponseMessage> PublishAsync(HttpClient client, string name, string version, string tag = "latest", string? token = Token, JsonObject? extra = null)
        => SendAsync(client, HttpMethod.Put, "/npm/" + Encoded(name), PublishBody(name, version, tag, extra), token);

    static async Task<JsonObject> GetJsonAsync(HttpClient client, string url, string? accept = null, string? token = null)
    {
        var response = await SendAsync(client, HttpMethod.Get, url, token: token, accept: accept);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (JsonObject)JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;
    }

    // ---- publish and install ----

    [Fact]
    public async Task PublishThenServesPackumentAndTarball()
    {
        using var scratch = new Scratch();
        await using var server = await StartAsync(scratch);

        var published = await PublishAsync(server.Client, "shiny-lib", "1.0.0", extra: new JsonObject { ["dependencies"] = new JsonObject { ["left-pad"] = "^1.3.0" } });
        Assert.Equal(HttpStatusCode.Created, published.StatusCode);

        var doc = await GetJsonAsync(server.Client, "/npm/shiny-lib");
        Assert.Equal("shiny-lib", (string?)doc["name"]);
        Assert.Equal("1.0.0", (string?)doc["dist-tags"]!["latest"]);
        Assert.Equal("# readme", (string?)doc["readme"]);
        Assert.NotNull(doc["time"]!["1.0.0"]);
        Assert.Equal("allan", (string?)doc["maintainers"]![0]!["name"]);

        var version = doc["versions"]!["1.0.0"]!;
        Assert.Equal("^1.3.0", (string?)version["dependencies"]!["left-pad"]);
        Assert.Equal("allan", (string?)version["_npmUser"]!["name"]);

        var tarballUrl = (string)version["dist"]!["tarball"]!;
        Assert.Equal($"http://127.0.0.1:{server.Port}/npm/shiny-lib/-/shiny-lib-1.0.0.tgz", tarballUrl);

        var bytes = await server.Client.GetByteArrayAsync(tarballUrl, Ct);
        Assert.Equal(Tarball("shiny-lib", "1.0.0"), bytes);
        Assert.Equal("sha512-" + Convert.ToBase64String(SHA512.HashData(bytes)), (string?)version["dist"]!["integrity"]);
    }

    [Fact]
    public async Task AbbreviatedMetadataAndRevalidation()
    {
        using var scratch = new Scratch();
        await using var server = await StartAsync(scratch);
        await PublishAsync(server.Client, "corgi", "1.0.0", extra: new JsonObject { ["engines"] = new JsonObject { ["node"] = ">=20" } });

        var response = await SendAsync(server.Client, HttpMethod.Get, "/npm/corgi", token: null, accept: "application/vnd.npm.install-v1+json; q=1.0, application/json; q=0.8");
        Assert.Equal("application/vnd.npm.install-v1+json", response.Content.Headers.ContentType?.MediaType);

        var doc = (JsonObject)JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;
        var version = (JsonObject)doc["versions"]!["1.0.0"]!;
        Assert.Equal(">=20", (string?)version["engines"]!["node"]);
        Assert.False(version.ContainsKey("readme"));
        Assert.False(doc.ContainsKey("readme"));

        using var again = new HttpRequestMessage(HttpMethod.Get, "/npm/corgi");
        again.Headers.Accept.ParseAdd("application/vnd.npm.install-v1+json");
        again.Headers.IfNoneMatch.Add(response.Headers.ETag!);
        Assert.Equal(HttpStatusCode.NotModified, (await server.Client.SendAsync(again, Ct)).StatusCode);
    }

    [Fact]
    public async Task ScopedPackagesUseBothUrlForms()
    {
        using var scratch = new Scratch();
        await using var server = await StartAsync(scratch);

        Assert.Equal(HttpStatusCode.Created, (await PublishAsync(server.Client, "@acme/widgets", "2.0.0-beta.1", tag: "beta")).StatusCode);

        // npm encodes the slash for documents and does not for tarballs.
        var doc = await GetJsonAsync(server.Client, "/npm/@acme%2fwidgets");
        Assert.Equal("2.0.0-beta.1", (string?)doc["dist-tags"]!["beta"]);
        Assert.Equal("2.0.0-beta.1", (string?)doc["dist-tags"]!["latest"]);   // the first version always gets latest

        var tarball = await server.Client.GetByteArrayAsync("/npm/@acme/widgets/-/widgets-2.0.0-beta.1.tgz", Ct);
        Assert.Equal(Tarball("@acme/widgets", "2.0.0-beta.1"), tarball);

        Assert.Equal("2.0.0-beta.1", (string?)(await GetJsonAsync(server.Client, "/npm/@acme%2fwidgets/beta"))["version"]);
        Assert.True(Directory.Exists(Path.Combine(scratch.Path, "@acme", "widgets")));
    }

    [Fact]
    public async Task VersionOrTagManifest()
    {
        using var scratch = new Scratch();
        await using var server = await StartAsync(scratch);
        await PublishAsync(server.Client, "tagged", "1.0.0");
        await PublishAsync(server.Client, "tagged", "1.1.0");

        Assert.Equal("1.1.0", (string?)(await GetJsonAsync(server.Client, "/npm/tagged/latest"))["version"]);
        Assert.Equal("1.0.0", (string?)(await GetJsonAsync(server.Client, "/npm/tagged/1.0.0"))["version"]);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(server.Client, HttpMethod.Get, "/npm/tagged/9.9.9")).StatusCode);
    }

    // ---- publish rules ----

    [Fact]
    public async Task PublishRules()
    {
        using var scratch = new Scratch();
        await using var server = await StartAsync(scratch);

        Assert.Equal(HttpStatusCode.Unauthorized, (await PublishAsync(server.Client, "rules", "1.0.0", token: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PublishAsync(server.Client, "rules", "1.0.0", token: "nope")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PublishAsync(server.Client, "rules", "1.0.0")).StatusCode);

        var again = await PublishAsync(server.Client, "rules", "1.0.0");
        Assert.Equal(HttpStatusCode.Forbidden, again.StatusCode);
        Assert.Contains("previously published", (string?)JsonNode.Parse(await again.Content.ReadAsStringAsync(Ct))!["error"]);

        var tampered = PublishBody("rules", "1.0.1", integrityOverride: "sha512-AAAA");
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(server.Client, HttpMethod.Put, "/npm/rules", tampered)).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await PublishAsync(server.Client, "rules", "not.a.version")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PublishAsync(server.Client, "Upper-Case", "1.0.0")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PublishAsync(server.Client, "..", "1.0.0")).StatusCode);

        // Another user cannot publish to a package they do not maintain.
        Assert.Equal(HttpStatusCode.Forbidden, (await PublishAsync(server.Client, "rules", "2.0.0", token: OtherToken)).StatusCode);
    }

    [Fact]
    public async Task OnBeforePublishCanReject()
    {
        using var scratch = new Scratch();
        await using var server = await StartAsync(scratch, o => o.OnBeforePublishAsync = ctx =>
        {
            if (!ctx.PackageName.StartsWith("@acme/", StringComparison.Ordinal))
                ctx.Reject(detail: "Only @acme packages.");

            return ValueTask.CompletedTask;
        });

        var refused = await PublishAsync(server.Client, "loose", "1.0.0");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.False(Directory.Exists(Path.Combine(scratch.Path, "loose")));
        Assert.Equal(HttpStatusCode.Created, (await PublishAsync(server.Client, "@acme/ok", "1.0.0")).StatusCode);
    }

    [Fact]
    public void MappingAWritableRegistryWithoutTokensFails()
    {
        var server = HttpServer.CreateBuilder().Build();

        Assert.Throws<InvalidOperationException>(() =>
            server.MapNpmRegistry("/npm", o => o.Store = new DiskNpmPackageStore(Path.Combine(Path.GetTempPath(), "shiny-npm-unused"))));
    }

    // ---- changing a package ----

    [Fact]
    public async Task DeprecateAndUndeprecateThroughTheDocument()
    {
        using var scratch = new Scratch();
        await using var server = await StartAsync(scratch);
        await PublishAsync(server.Client, "old", "1.0.0");

        // npm deprecate: GET ?write=true, mark the versions, PUT the document back with its _rev.
        var doc = await GetJsonAsync(server.Client, "/npm/old?write=true", token: Token);
        doc["versions"]!["1.0.0"]!["deprecated"] = "use something else";
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(server.Client, HttpMethod.Put, "/npm/old", doc)).StatusCode);

        var deprecated = await GetJsonAsync(server.Client, "/npm/old", accept: "application/vnd.npm.install-v1+json");
        Assert.Equal("use something else", (string?)deprecated["versions"]!["1.0.0"]!["deprecated"]);

        // The _rev it was based on is stale now.
        doc["versions"]!["1.0.0"]!["deprecated"] = "";
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(server.Client, HttpMethod.Put, "/npm/old", doc)).StatusCode);

        var fresh = await GetJsonAsync(server.Client, "/npm/old?write=true", token: Token);
        fresh["versions"]!["1.0.0"]!["deprecated"] = "";
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(server.Client, HttpMethod.Put, $"/npm/old/-rev/{fresh["_rev"]}", fresh)).StatusCode);
        Assert.Null((await GetJsonAsync(server.Client, "/npm/old"))["versions"]!["1.0.0"]!["deprecated"]);
    }

    [Fact]
    public async Task UnpublishingAVersionMovesLatest()
    {
        using var scratch = new Scratch();
        await using var server = await StartAsync(scratch);
        await PublishAsync(server.Client, "shrinking", "1.0.0");
        await PublishAsync(server.Client, "shrinking", "2.0.0");

        // npm unpublish name@2.0.0: PUT the document without the version, then DELETE its tarball.
        var doc = await GetJsonAsync(server.Client, "/npm/shrinking?write=true", token: Token);
        ((JsonObject)doc["versions"]!).Remove("2.0.0");
        ((JsonObject)doc["dist-tags"]!).Remove("latest");
        var updated = await SendAsync(server.Client, HttpMethod.Put, $"/npm/shrinking/-rev/{doc["_rev"]}", doc);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var rev = (string?)JsonNode.Parse(await updated.Content.ReadAsStringAsync(Ct))!["rev"];

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(server.Client, HttpMethod.Delete, $"/npm/shrinking/-/shrinking-2.0.0.tgz/-rev/{rev}")).StatusCode);

        var after = await GetJsonAsync(server.Client, "/npm/shrinking");
        Assert.Equal("1.0.0", (string?)after["dist-tags"]!["latest"]);
        Assert.Null(after["versions"]!["2.0.0"]);
        Assert.Equal(HttpStatusCode.NotFound, (await server.Client.GetAsync("/npm/shrinking/-/shrinking-2.0.0.tgz", Ct)).StatusCode);

        // An unpublished number is not reused on the same bytes' behalf: a new publish of it is a new version.
        Assert.Equal(HttpStatusCode.Created, (await PublishAsync(server.Client, "shrinking", "2.0.1")).StatusCode);
    }

    [Fact]
    public async Task UnpublishingTheWholePackage()
    {
        using var scratch = new Scratch();
        await using var server = await StartAsync(scratch);
        await PublishAsync(server.Client, "@acme/gone", "1.0.0");

        var doc = await GetJsonAsync(server.Client, "/npm/@acme%2fgone?write=true", token: Token);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(server.Client, HttpMethod.Delete, "/npm/@acme%2fgone/-rev/1-stale")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(server.Client, HttpMethod.Delete, $"/npm/@acme%2fgone/-rev/{doc["_rev"]}")).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(server.Client, HttpMethod.Get, "/npm/@acme%2fgone")).StatusCode);
        Assert.False(Directory.Exists(Path.Combine(scratch.Path, "@acme")));
    }

    [Fact]
    public async Task UnpublishCanBeTurnedOff()
    {
        using var scratch = new Scratch();
        await using var server = await StartAsync(scratch, o => o.AllowUnpublish = false);
        await PublishAsync(server.Client, "kept", "1.0.0");

        var doc = await GetJsonAsync(server.Client, "/npm/kept?write=true", token: Token);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(server.Client, HttpMethod.Delete, $"/npm/kept/-rev/{doc["_rev"]}")).StatusCode);
    }

    [Fact]
    public async Task DistTags()
    {
        using var scratch = new Scratch();
        await using var server = await StartAsync(scratch);
        await PublishAsync(server.Client, "tags", "1.0.0");
        await PublishAsync(server.Client, "tags", "2.0.0-rc.1", tag: "next");

        var add = await SendAsync(server.Client, HttpMethod.Put, "/npm/-/package/tags/dist-tags/stable", JsonValue.Create("1.0.0"));
        Assert.Equal(HttpStatusCode.Created, add.StatusCode);

        var tags = await GetJsonAsync(server.Client, "/npm/-/package/tags/dist-tags");
        Assert.Equal("1.0.0", (string?)tags["stable"]);
        Assert.Equal("2.0.0-rc.1", (string?)tags["next"]);

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(server.Client, HttpMethod.Delete, "/npm/-/package/tags/dist-tags/next")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(server.Client, HttpMethod.Delete, "/npm/-/package/tags/dist-tags/latest")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(server.Client, HttpMethod.Put, "/npm/-/package/tags/dist-tags/x", JsonValue.Create("9.9.9"))).StatusCode);
    }

    [Fact]
    public async Task OwnersCanBeAdded()
    {
        using var scratch = new Scratch();
        await using var server = await StartAsync(scratch);
        await PublishAsync(server.Client, "shared", "1.0.0");
        Assert.Equal(HttpStatusCode.Forbidden, (await PublishAsync(server.Client, "shared", "1.1.0", token: OtherToken)).StatusCode);

        // npm owner add bob shared
        var doc = await GetJsonAsync(server.Client, "/npm/shared?write=true", token: Token);
        ((JsonArray)doc["maintainers"]!).Add((JsonNode)new JsonObject { ["name"] = "bob" });
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(server.Client, HttpMethod.Put, $"/npm/shared/-rev/{doc["_rev"]}", doc)).StatusCode);

        Assert.Equal(HttpStatusCode.Created, (await PublishAsync(server.Client, "shared", "1.1.0", token: OtherToken)).StatusCode);
    }

    // ---- everything else ----

    [Fact]
    public async Task SearchWhoamiPing()
    {
        using var scratch = new Scratch();
        await using var server = await StartAsync(scratch);
        await PublishAsync(server.Client, "shiny-alpha", "1.0.0", extra: new JsonObject { ["keywords"] = new JsonArray("mobile") });
        await PublishAsync(server.Client, "@acme/shiny-beta", "1.0.0");
        await PublishAsync(server.Client, "unrelated", "1.0.0", extra: new JsonObject { ["description"] = "nothing", ["keywords"] = new JsonArray() });

        var search = await GetJsonAsync(server.Client, "/npm/-/v1/search?text=shiny&size=20&from=0");
        Assert.Equal(2, (int?)search["total"]);
        var first = search["objects"]![0]!["package"]!;
        Assert.Equal("@acme/shiny-beta", (string?)first["name"]);
        Assert.Equal("acme", (string?)first["scope"]);
        Assert.Equal("allan", (string?)first["maintainers"]![0]!["username"]);

        Assert.Equal(1, (int?)(await GetJsonAsync(server.Client, "/npm/-/v1/search?text=keywords:mobile"))["total"]);
        Assert.Equal(1, (int?)(await GetJsonAsync(server.Client, "/npm/-/v1/search?text=scope:acme"))["total"]);
        Assert.Equal(1, (int?)(await GetJsonAsync(server.Client, "/npm/-/v1/search?text=&size=1&from=2"))["objects"]!.AsArray().Count);

        Assert.Equal("allan", (string?)(await GetJsonAsync(server.Client, "/npm/-/whoami", token: Token))["username"]);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(server.Client, HttpMethod.Get, "/npm/-/whoami", token: null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(server.Client, HttpMethod.Get, "/npm/-/ping", token: null)).StatusCode);
    }

    [Fact]
    public async Task LegacyLoginHandsOutAToken()
    {
        using var scratch = new Scratch();
        await using var server = await StartAsync(scratch, o => o.LoginAsync = ctx =>
            ValueTask.FromResult<string?>(ctx.UserName == "allan" && ctx.Password == "pw" ? Token : null));

        var body = new JsonObject { ["_id"] = "org.couchdb.user:allan", ["name"] = "allan", ["password"] = "pw", ["type"] = "user" };
        var login = await SendAsync(server.Client, HttpMethod.Put, "/npm/-/user/org.couchdb.user:allan", body, token: null);
        Assert.Equal(HttpStatusCode.Created, login.StatusCode);
        Assert.Equal(Token, (string?)JsonNode.Parse(await login.Content.ReadAsStringAsync(Ct))!["token"]);

        body["password"] = "wrong";
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(server.Client, HttpMethod.Put, "/npm/-/user/org.couchdb.user:allan", body, token: null)).StatusCode);
    }

    [Fact]
    public async Task ReadsCanRequireAToken()
    {
        using var scratch = new Scratch();
        await using var server = await StartAsync(scratch, o => o.RequireTokenForReads = true);
        await PublishAsync(server.Client, "private-thing", "1.0.0");

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(server.Client, HttpMethod.Get, "/npm/private-thing", token: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(server.Client, HttpMethod.Get, "/npm/private-thing/-/private-thing-1.0.0.tgz", token: null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(server.Client, HttpMethod.Get, "/npm/private-thing", token: Token)).StatusCode);
    }

    [Fact]
    public async Task UnknownPackagesFallThroughToUpstream()
    {
        using var scratch = new Scratch();

        // A stand-in for registry.npmjs.org.
        await using var upstream = await TestServer.StartAsync(app =>
        {
            app.MapGet("/left-pad", ctx => ctx.Response.WriteAsync("""{"name":"left-pad","from":"upstream"}""", "application/json"));
            app.MapGet("/left-pad/-/left-pad-1.3.0.tgz", ctx => ctx.Response.WriteAsync("upstream tarball"));
            app.MapGet("/shiny-lib", ctx => ctx.Response.WriteAsync("""{"name":"shiny-lib","from":"upstream"}""", "application/json"));
        });

        await using var server = await StartAsync(scratch, o => o.Upstream = new Uri($"http://127.0.0.1:{upstream.Port}"));
        await PublishAsync(server.Client, "shiny-lib", "1.0.0");

        Assert.Equal("upstream", (string?)(await GetJsonAsync(server.Client, "/npm/left-pad"))["from"]);
        Assert.Equal("upstream tarball", await server.Client.GetStringAsync("/npm/left-pad/-/left-pad-1.3.0.tgz", Ct));

        // A package published here always wins over the same name upstream.
        Assert.Null((await GetJsonAsync(server.Client, "/npm/shiny-lib"))["from"]);
    }

    [Fact]
    public async Task PublishAndInstallOverHttp2()
    {
        using var scratch = new Scratch();
        await using var server = await StartAsync(scratch);

        using var client = new HttpClient(new SocketsHttpHandler())
        {
            BaseAddress = new Uri($"http://127.0.0.1:{server.Port}"),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };

        var published = await PublishAsync(client, "@acme/h2", "1.0.0");
        Assert.Equal(HttpVersion.Version20, published.Version);
        Assert.Equal(HttpStatusCode.Created, published.StatusCode);

        var tarball = await client.GetAsync("/npm/@acme/h2/-/h2-1.0.0.tgz", Ct);
        Assert.Equal(HttpVersion.Version20, tarball.Version);
        Assert.Equal(Tarball("@acme/h2", "1.0.0"), await tarball.Content.ReadAsByteArrayAsync(Ct));
    }

    [Fact]
    public void SemVerPrecedence()
    {
        string[] ordered = ["1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0", "1.2.0", "1.10.0", "2.0.0"];

        Assert.Equal(ordered, ordered.Reverse().OrderBy(v => v, NpmRules.VersionComparer).ToArray());
        Assert.Equal(0, NpmRules.Compare("1.0.0+build.1", "1.0.0+build.2"));
        Assert.False(NpmRules.IsValidVersion("01.0.0"));
        Assert.False(NpmRules.IsValidVersion("v1.0.0"));
        Assert.True(NpmRules.IsValidName("@acme/widgets"));
        Assert.False(NpmRules.IsValidName("@acme/../x"));
        Assert.False(NpmRules.IsValidName(".hidden"));
    }
}
