using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Shiny.Net.HttpServer.Npm.Internal;

/// <summary>
/// The npm registry API behind <see cref="NpmRegistryExtensions.MapNpmRegistry(HttpServer, string, NpmRegistryOptions)"/>.
/// <para>
/// One catch-all route per method, parsed here, because npm's URLs do not fit route templates: a
/// scoped name is one segment when npm percent-encodes its slash (<c>@acme%2fwidgets</c>) and two
/// when it does not (<c>@acme/widgets/-/widgets-1.0.0.tgz</c>), and the server decodes both to the
/// same path before routing.
/// </para>
/// </summary>
sealed class NpmRegistryHandler
{
    const string JsonContentType = "application/json";
    const string AbbreviatedContentType = "application/vnd.npm.install-v1+json";

    // What the abbreviated ("corgi") document keeps of each version: what an installer needs to
    // resolve and fetch it, and nothing it does not, like the readme.
    static readonly string[] AbbreviatedFields =
    [
        "name", "version", "deprecated", "dependencies", "optionalDependencies", "devDependencies",
        "bundleDependencies", "bundledDependencies", "peerDependencies", "peerDependenciesMeta", "bin",
        "directories", "dist", "engines", "cpu", "os", "libc", "funding", "_hasShrinkwrap", "hasInstallScript"
    ];

    // Taken from the latest version into the top of the full document, as the public registry does.
    static readonly string[] TopLevelFields = ["description", "readme", "readmeFilename", "homepage", "keywords", "repository", "author", "bugs", "license"];

    readonly NpmRegistryOptions options;
    readonly INpmPackageStore store;
    readonly string basePath;
    readonly ConcurrentDictionary<string, SemaphoreSlim> locks = new(StringComparer.Ordinal);
    readonly Lazy<HttpClient> upstream = new(() => new HttpClient { Timeout = TimeSpan.FromSeconds(60) });

    public NpmRegistryHandler(NpmRegistryOptions options, INpmPackageStore store, string basePath)
    {
        this.options = options;
        this.store = store;
        this.basePath = basePath;
    }

    // ---- dispatch ----

    public async ValueTask HandleAsync(HttpContext context)
    {
        try
        {
            await this.DispatchAsync(context).ConfigureAwait(false);
        }
        catch (NpmException ex) when (!context.Response.HasStarted)
        {
            await ErrorAsync(context, ex.StatusCode, ex.Message).ConfigureAwait(false);
        }
    }

    ValueTask DispatchAsync(HttpContext context)
    {
        var method = context.Request.Method;
        var segments = (context.Request.RouteValues["path"] ?? string.Empty)
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 0)
            return HttpMethods.IsGet(method) || HttpMethods.IsHead(method)
                ? WriteJsonAsync(context, new JsonObject { ["db_name"] = "registry" })
                : throw NotFound();

        if (segments[0] == "-")
            return this.SpecialAsync(context, method, segments[1..]);

        var (name, rest) = SplitName(segments);

        return (method, rest) switch
        {
            (_, []) when IsRead(method) => this.PackumentAsync(context, name),
            ("PUT", []) => this.PublishAsync(context, name),
            ("PUT", ["-rev", var rev]) => this.UpdateAsync(context, name, rev),
            ("DELETE", ["-rev", var rev]) => this.UnpublishAsync(context, name, rev),
            (_, ["-", var file]) when IsRead(method) => this.TarballAsync(context, name, file),
            ("DELETE", ["-", var file, "-rev", var rev]) => this.DeleteTarballAsync(context, name, file, rev),
            (_, [var version]) when IsRead(method) => this.VersionAsync(context, name, version),
            _ => throw NotFound()
        };
    }

    ValueTask SpecialAsync(HttpContext context, string method, string[] rest) => (method, rest) switch
    {
        (_, ["ping"]) => WriteJsonAsync(context, new JsonObject()),
        (_, ["whoami"]) when IsRead(method) => this.WhoAmIAsync(context),
        (_, ["v1", "search"]) when IsRead(method) => this.SearchAsync(context),
        ("PUT", ["user", var id]) when id.StartsWith("org.couchdb.user:", StringComparison.Ordinal) => this.LoginAsync(context, id["org.couchdb.user:".Length..]),
        (_, ["user", var id]) when IsRead(method) && id.StartsWith("org.couchdb.user:", StringComparison.Ordinal)
            => WriteJsonAsync(context, new JsonObject { ["name"] = id["org.couchdb.user:".Length..] }),
        ("DELETE", ["user", "token", _]) => WriteJsonAsync(context, new JsonObject { ["ok"] = true }),
        (_, ["package", .. var tail]) when tail.Length >= 2 => this.DistTagsAsync(context, method, tail),
        ("POST", ["npm", "v1", "security", ..]) => this.ProxyAsync(context, string.Join('/', ["-", .. rest])),
        _ => throw NotFound()
    };

    /// <summary><c>["@acme", "widgets", "-", "x.tgz"]</c> → <c>("@acme/widgets", ["-", "x.tgz"])</c>.</summary>
    static (string Name, string[] Remaining) SplitName(string[] segments)
    {
        var scoped = segments[0].StartsWith('@');

        if (scoped && segments.Length < 2)
            throw NotFound();

        var name = scoped ? segments[0] + "/" + segments[1] : segments[0];

        if (!NpmRules.IsValidName(name))
            throw NotFound();

        return (name, segments[(scoped ? 2 : 1)..]);
    }

    static bool IsRead(string method) => HttpMethods.IsGet(method) || HttpMethods.IsHead(method);

    // ---- reads ----

    async ValueTask PackumentAsync(HttpContext context, string name)
    {
        await this.AuthorizeReadAsync(context, name).ConfigureAwait(false);
        var document = await this.store.GetAsync(name, context.RequestAborted).ConfigureAwait(false);

        if (document is null)
        {
            await this.ProxyAsync(context, EncodeName(name)).ConfigureAwait(false);
            return;
        }

        var abbreviated = context.Request.Headers.GetFirst(HeaderNames.Accept)?.Contains(AbbreviatedContentType, StringComparison.OrdinalIgnoreCase) == true;
        var etag = $"\"{document.Rev}{(abbreviated ? "-a" : string.Empty)}\"";

        context.Response.Headers.Set(HeaderNames.ETag, etag);
        context.Response.Headers.Set(HeaderNames.CacheControl, "no-cache");

        if (context.Request.Headers.GetFirst(HeaderNames.IfNoneMatch) == etag)
        {
            context.Response.StatusCode = StatusCodes.Status304NotModified;
            await context.Response.StartAsync(context.RequestAborted).ConfigureAwait(false);
            return;
        }

        var baseUrl = this.BaseUrl(context);
        await WriteJsonAsync(
            context,
            abbreviated ? Abbreviated(document, baseUrl) : Full(document, baseUrl),
            abbreviated ? AbbreviatedContentType : JsonContentType
        ).ConfigureAwait(false);
    }

    /// <summary><c>GET {name}/{version}</c> or <c>GET {name}/{tag}</c>: one version's manifest.</summary>
    async ValueTask VersionAsync(HttpContext context, string name, string versionOrTag)
    {
        await this.AuthorizeReadAsync(context, name).ConfigureAwait(false);
        var document = await this.store.GetAsync(name, context.RequestAborted).ConfigureAwait(false);

        if (document is null)
        {
            await this.ProxyAsync(context, EncodeName(name) + "/" + Uri.EscapeDataString(versionOrTag)).ConfigureAwait(false);
            return;
        }

        var version = document.DistTags.GetValueOrDefault(versionOrTag) ?? versionOrTag;

        if (!document.Versions.TryGetValue(version, out var found))
            throw new NpmException(StatusCodes.Status404NotFound, $"version not found: {versionOrTag}");

        await WriteJsonAsync(context, Served(document.Name, found, this.BaseUrl(context))).ConfigureAwait(false);
    }

    async ValueTask TarballAsync(HttpContext context, string name, string file)
    {
        await this.AuthorizeReadAsync(context, name).ConfigureAwait(false);
        var document = await this.store.GetAsync(name, context.RequestAborted).ConfigureAwait(false);

        if (document is null)
        {
            // npm rewrites registry.npmjs.org tarball links to the configured registry
            // (replace-registry-host), so an upstream package's tarball is asked for here too.
            await this.ProxyAsync(context, $"{name}/-/{Uri.EscapeDataString(file)}").ConfigureAwait(false);
            return;
        }

        // Only a tarball a current version points at: an unpublished version's file is gone even
        // if a copy of it survived somewhere.
        if (!document.Versions.Values.Any(v => v.TarballFile == file))
            throw NotFound();

        await using var stream = await this.store.OpenTarballAsync(name, file, context.RequestAborted).ConfigureAwait(false)
            ?? throw NotFound();

        context.Response.Headers.Set(HeaderNames.CacheControl, "public, max-age=31536000, immutable");
        await context.Response.WriteStreamAsync(stream, "application/octet-stream", context.RequestAborted).ConfigureAwait(false);
    }

    async ValueTask WhoAmIAsync(HttpContext context)
    {
        var user = await this.RequireUserAsync(context, null).ConfigureAwait(false);
        await WriteJsonAsync(context, new JsonObject { ["username"] = user }).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>GET -/v1/search?text=&amp;size=&amp;from=</c>. Terms match names, descriptions and keywords;
    /// <c>keywords:</c>, <c>author:</c>, <c>maintainer:</c> and <c>scope:</c> narrow one.
    /// </summary>
    async ValueTask SearchAsync(HttpContext context)
    {
        await this.AuthorizeReadAsync(context, null).ConfigureAwait(false);

        var query = context.Request.Query;
        var size = Math.Clamp(int.TryParse(query.GetFirst("size"), out var s) ? s : 20, 0, 250);
        var from = Math.Max(int.TryParse(query.GetFirst("from"), out var f) ? f : 0, 0);
        var terms = (query.GetFirst("text") ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var matches = new List<(NpmPackageDocument Document, NpmPackageVersion Latest)>();

        foreach (var document in await this.store.GetAllAsync(context.RequestAborted).ConfigureAwait(false))
        {
            if (Latest(document) is { } latest && terms.All(t => Matches(document, latest, t)))
                matches.Add((document, latest));
        }

        var exact = terms.Length == 1 && !terms[0].Contains(':') ? terms[0] : null;
        var page = matches
            .OrderByDescending(m => exact is not null && m.Document.Name == exact)
            .ThenBy(m => m.Document.Name, StringComparer.Ordinal)
            .Skip(from)
            .Take(size);

        var objects = new JsonArray();

        foreach (var (document, latest) in page)
        {
            var manifest = latest.Manifest;
            var package = new JsonObject
            {
                ["name"] = document.Name,
                ["scope"] = document.Name.StartsWith('@') ? document.Name[1..document.Name.IndexOf('/')] : "unscoped",
                ["version"] = latest.Version,
                ["description"] = Text(manifest["description"]),
                ["date"] = NpmRules.Iso(document.Modified),
                ["links"] = new JsonObject(),
                ["publisher"] = new JsonObject { ["username"] = latest.PublishedBy },
                ["maintainers"] = SearchMaintainers(document)
            };

            if (manifest["keywords"] is JsonArray keywords)
                package["keywords"] = keywords.DeepClone();

            // As a JsonNode: the generic Add<T> would go through reflection-based serialization.
            objects.Add((JsonNode)new JsonObject
            {
                ["package"] = package,
                ["score"] = new JsonObject
                {
                    ["final"] = 1.0,
                    ["detail"] = new JsonObject { ["quality"] = 1.0, ["popularity"] = 1.0, ["maintenance"] = 1.0 }
                },
                ["searchScore"] = 1.0
            });
        }

        await WriteJsonAsync(context, new JsonObject
        {
            ["objects"] = objects,
            ["total"] = matches.Count,
            ["time"] = NpmRules.Iso(this.options.TimeProvider.GetUtcNow())
        }).ConfigureAwait(false);
    }

    /// <summary>Search results name maintainers <c>username</c>, where package documents say <c>name</c>.</summary>
    static JsonArray SearchMaintainers(NpmPackageDocument document)
    {
        var list = new JsonArray();

        foreach (var maintainer in document.Maintainers ?? [])
        {
            var entry = new JsonObject { ["username"] = Text(maintainer?["name"]) ?? string.Empty };

            if (Text(maintainer?["email"]) is { } email)
                entry["email"] = email;

            list.Add((JsonNode)entry);
        }

        return list;
    }

    static bool Matches(NpmPackageDocument document, NpmPackageVersion latest, string term)
    {
        var colon = term.IndexOf(':');
        var field = colon > 0 ? term[..colon] : null;
        var value = colon > 0 ? term[(colon + 1)..] : term;
        var keywords = latest.Manifest["keywords"] is JsonArray array ? array.Select(Text).OfType<string>().ToList() : [];

        return field switch
        {
            "keywords" => value.Split(',').Any(k => keywords.Contains(k, StringComparer.OrdinalIgnoreCase)),
            "author" => Has(latest.Manifest["author"]?.ToJsonString(), value),
            "maintainer" => document.Maintainers?.Any(m => Has(Text(m?["name"]), value)) == true,
            "scope" => document.Name.StartsWith("@" + value.TrimStart('@') + "/", StringComparison.Ordinal),
            _ => Has(document.Name, value) || Has(Text(latest.Manifest["description"]), value) || keywords.Any(k => Has(k, value))
        };

        static bool Has(string? text, string value) => text?.Contains(value, StringComparison.OrdinalIgnoreCase) == true;
    }

    // ---- publish ----

    /// <summary>
    /// <c>PUT {name}</c> - <c>npm publish</c>: the new version's manifest and its tarball, base64
    /// in <c>_attachments</c>, in one JSON body.
    /// </summary>
    async ValueTask PublishAsync(HttpContext context, string name)
    {
        this.EnsureWritable();
        var user = await this.RequireUserAsync(context, name).ConfigureAwait(false);
        var body = await this.ReadBodyAsync(context, this.options.MaxTarballSize / 3 * 4 + 4 * 1024 * 1024).ConfigureAwait(false);

        if (Text(body["name"]) != name)
            throw new NpmException(StatusCodes.Status400BadRequest, $"The document's name does not match the URL ('{name}').");

        // No tarball: the whole document sent back changed, with its _rev in the body - how npm
        // 11 sends deprecate. The same update as PUT {name}/-rev/{rev}.
        if (body["_attachments"] is not JsonObject { Count: > 0 } attachments)
        {
            await this.ApplyUpdateAsync(context, name, Text(body["_rev"]), body, user).ConfigureAwait(false);
            return;
        }

        if (body["versions"] is not JsonObject { Count: > 0 } versions)
            throw new NpmException(StatusCodes.Status400BadRequest, "Nothing to publish: a publish carries a version and its tarball.");

        using var _ = await this.LockAsync(name, context.RequestAborted).ConfigureAwait(false);

        var now = this.options.TimeProvider.GetUtcNow();
        var document = await this.store.GetAsync(name, context.RequestAborted).ConfigureAwait(false);

        if (document is not null)
            this.EnsureOwner(document, user);

        document ??= new NpmPackageDocument
        {
            Name = name,
            Created = now,
            Maintainers = [new JsonObject { ["name"] = user }]
        };

        // Everything is checked, and every hook has had its say, before anything is stored.
        var staged = new List<(NpmPackageVersion Version, byte[] Tarball)>();

        foreach (var (version, node) in versions)
        {
            if (!NpmRules.IsValidVersion(version))
                throw new NpmException(StatusCodes.Status400BadRequest, $"'{version}' is not a valid semver version.");

            if (document.Versions.ContainsKey(version))
                throw new NpmException(StatusCodes.Status403Forbidden, $"You cannot publish over the previously published versions: {version}.");

            if (node is not JsonObject submitted)
                throw new NpmException(StatusCodes.Status400BadRequest, $"Version {version} has no manifest.");

            var file = NpmRules.TarballFile(name, version);
            var attachment = attachments[file] as JsonObject
                ?? (attachments.Count == 1 ? attachments.First().Value as JsonObject : null)
                ?? throw new NpmException(StatusCodes.Status400BadRequest, $"No tarball was attached for {version}.");

            byte[] tarball;

            try
            {
                tarball = Convert.FromBase64String(Text(attachment["data"]) ?? string.Empty);
            }
            catch (FormatException)
            {
                throw new NpmException(StatusCodes.Status400BadRequest, "The attached tarball is not valid base64.");
            }

            if (tarball.Length == 0)
                throw new NpmException(StatusCodes.Status400BadRequest, "The attached tarball is empty.");

            if (tarball.Length > this.options.MaxTarballSize)
                throw new NpmException(StatusCodes.Status413PayloadTooLarge, $"Tarballs on this registry are limited to {this.options.MaxTarballSize} bytes.");

            var integrity = "sha512-" + Convert.ToBase64String(SHA512.HashData(tarball));
            var shasum = Convert.ToHexStringLower(SHA1.HashData(tarball));
            var claimed = submitted["dist"] as JsonObject;

            // The client computed these over the tarball it meant to send; if they disagree, what
            // arrived is not that tarball.
            if (Text(claimed?["integrity"]) is { } claimedIntegrity && claimedIntegrity != integrity)
                throw new NpmException(StatusCodes.Status400BadRequest, "The tarball does not match its integrity.");

            if (Text(claimed?["shasum"]) is { } claimedShasum && !claimedShasum.Equals(shasum, StringComparison.OrdinalIgnoreCase))
                throw new NpmException(StatusCodes.Status400BadRequest, "The tarball does not match its shasum.");

            var manifest = (JsonObject)submitted.DeepClone();
            var dist = new JsonObject
            {
                ["integrity"] = integrity,
                ["shasum"] = shasum,
                ["tarball"] = string.Empty   // filled in per request, from the registry's own URL
            };

            foreach (var keep in (string[])["fileCount", "unpackedSize"])
            {
                if (claimed?[keep] is { } value)
                    dist[keep] = value.DeepClone();
            }

            manifest["name"] = name;
            manifest["version"] = version;
            manifest["_id"] = $"{name}@{version}";
            manifest["dist"] = dist;
            manifest["_npmUser"] = new JsonObject { ["name"] = user };

            var stored = new NpmPackageVersion
            {
                Version = version,
                Manifest = manifest,
                TarballFile = file,
                Size = tarball.Length,
                Integrity = integrity,
                Shasum = shasum,
                Published = now,
                PublishedBy = user
            };

            if (this.options.OnBeforePublishAsync is { } before)
            {
                var publish = new NpmPublishContext(context, name, stored, user);
                await before(publish).ConfigureAwait(false);

                if (publish.IsRejected)
                    throw new NpmException(publish.RejectStatusCode, publish.RejectDetail ?? "The publish was refused.");
            }

            staged.Add((stored, tarball));
        }

        foreach (var (version, tarball) in staged)
        {
            await this.store.SaveTarballAsync(name, version.TarballFile, tarball, context.RequestAborted).ConfigureAwait(false);
            document.Versions[version.Version] = version;
        }

        if (body["dist-tags"] is JsonObject tags)
        {
            foreach (var (tag, value) in tags)
            {
                if (Text(value) is { } version && document.Versions.ContainsKey(version))
                    document.DistTags[tag] = version;
            }
        }

        FixLatest(document);
        document.Modified = now;
        document.Rev = NextRev(document.Rev);
        await this.store.SaveAsync(document, context.RequestAborted).ConfigureAwait(false);

        if (this.options.OnPublishedAsync is { } published)
        {
            foreach (var (version, _) in staged)
                await published(new NpmPublishContext(context, name, version, user)).ConfigureAwait(false);
        }

        await WriteJsonAsync(context, new JsonObject { ["ok"] = true, ["id"] = name, ["rev"] = document.Rev }, statusCode: StatusCodes.Status201Created).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>PUT {name}/-rev/{rev}</c>: the whole document sent back changed - how <c>npm unpublish
    /// name@version</c>, <c>npm deprecate</c> and <c>npm owner</c> all work. Only those changes are
    /// taken: versions removed, deprecation messages, dist-tags and maintainers. A version cannot be
    /// added or rewritten this way.
    /// </summary>
    async ValueTask UpdateAsync(HttpContext context, string name, string rev)
    {
        this.EnsureWritable();
        var user = await this.RequireUserAsync(context, name).ConfigureAwait(false);
        var body = await this.ReadBodyAsync(context, 64L * 1024 * 1024).ConfigureAwait(false);

        await this.ApplyUpdateAsync(context, name, rev, body, user).ConfigureAwait(false);
    }

    /// <summary>
    /// The update itself. <paramref name="rev"/> is checked when given; a client that sends none
    /// gets last-writer-wins, which is what it asked for.
    /// </summary>
    async ValueTask ApplyUpdateAsync(HttpContext context, string name, string? rev, JsonObject body, string user)
    {
        using var _ = await this.LockAsync(name, context.RequestAborted).ConfigureAwait(false);
        var document = await this.RequireCurrentAsync(context, name, rev).ConfigureAwait(false);
        this.EnsureOwner(document, user);

        if (body["versions"] is JsonObject versions)
        {
            var removed = document.Versions.Keys.Where(v => !versions.ContainsKey(v)).ToList();

            if (removed.Count > 0 && !this.options.AllowUnpublish)
                throw new NpmException(StatusCodes.Status403Forbidden, "Unpublishing is not allowed on this registry.");

            foreach (var version in removed)
            {
                await this.store.DeleteTarballAsync(name, document.Versions[version].TarballFile, context.RequestAborted).ConfigureAwait(false);
                document.Versions.Remove(version);
            }

            foreach (var (version, node) in versions)
            {
                if (!document.Versions.TryGetValue(version, out var stored))
                    continue;

                if (Text(node?["deprecated"]) is { Length: > 0 } message)
                    stored.Manifest["deprecated"] = message;
                else
                    stored.Manifest.Remove("deprecated");
            }
        }

        if (body["dist-tags"] is JsonObject tags)
        {
            document.DistTags.Clear();

            foreach (var (tag, value) in tags)
            {
                if (Text(value) is { } version && document.Versions.ContainsKey(version))
                    document.DistTags[tag] = version;
            }
        }

        if (body["maintainers"] is JsonArray maintainers && maintainers.Count > 0)
            document.Maintainers = (JsonArray)maintainers.DeepClone();

        if (document.Versions.Count == 0)
        {
            await this.store.DeleteAsync(name, context.RequestAborted).ConfigureAwait(false);
            await WriteJsonAsync(context, new JsonObject { ["ok"] = true, ["id"] = name }).ConfigureAwait(false);
            return;
        }

        FixLatest(document);
        document.Modified = this.options.TimeProvider.GetUtcNow();
        document.Rev = NextRev(document.Rev);
        await this.store.SaveAsync(document, context.RequestAborted).ConfigureAwait(false);

        await WriteJsonAsync(context, new JsonObject { ["ok"] = true, ["id"] = name, ["rev"] = document.Rev }).ConfigureAwait(false);
    }

    /// <summary><c>DELETE {name}/-rev/{rev}</c> - <c>npm unpublish name --force</c>: the whole package.</summary>
    async ValueTask UnpublishAsync(HttpContext context, string name, string rev)
    {
        this.EnsureWritable();
        var user = await this.RequireUserAsync(context, name).ConfigureAwait(false);

        if (!this.options.AllowUnpublish)
            throw new NpmException(StatusCodes.Status403Forbidden, "Unpublishing is not allowed on this registry.");

        using var _ = await this.LockAsync(name, context.RequestAborted).ConfigureAwait(false);
        var document = await this.RequireCurrentAsync(context, name, rev).ConfigureAwait(false);
        this.EnsureOwner(document, user);

        await this.store.DeleteAsync(name, context.RequestAborted).ConfigureAwait(false);
        await WriteJsonAsync(context, new JsonObject { ["ok"] = true, ["id"] = name }).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>DELETE {name}/-/{file}/-rev/{rev}</c>: the last step of unpublishing one version. The
    /// document update before it already removed the tarball, so this only confirms it is gone.
    /// </summary>
    async ValueTask DeleteTarballAsync(HttpContext context, string name, string file, string rev)
    {
        this.EnsureWritable();
        var user = await this.RequireUserAsync(context, name).ConfigureAwait(false);

        using var _ = await this.LockAsync(name, context.RequestAborted).ConfigureAwait(false);
        var document = await this.store.GetAsync(name, context.RequestAborted).ConfigureAwait(false);

        if (document is not null)
        {
            this.EnsureOwner(document, user);

            if (document.Versions.Values.Any(v => v.TarballFile == file))
                throw new NpmException(StatusCodes.Status403Forbidden, $"{file} belongs to a published version; unpublish the version first.");

            await this.store.DeleteTarballAsync(name, file, context.RequestAborted).ConfigureAwait(false);
        }

        await WriteJsonAsync(context, new JsonObject { ["ok"] = true }).ConfigureAwait(false);
    }

    /// <summary><c>-/package/{name}/dist-tags[/{tag}]</c> - <c>npm dist-tag ls|add|rm</c>.</summary>
    async ValueTask DistTagsAsync(HttpContext context, string method, string[] tail)
    {
        var (name, rest) = SplitName(tail);

        if (rest is not ["dist-tags", ..] || rest.Length > 2)
            throw NotFound();

        if (rest.Length == 1)
        {
            if (!IsRead(method))
                throw NotFound();

            await this.AuthorizeReadAsync(context, name).ConfigureAwait(false);
            var current = await this.store.GetAsync(name, context.RequestAborted).ConfigureAwait(false) ?? throw NotFound();
            await WriteJsonAsync(context, Tags(current)).ConfigureAwait(false);
            return;
        }

        var tag = rest[1];
        this.EnsureWritable();
        var user = await this.RequireUserAsync(context, name).ConfigureAwait(false);

        using var _ = await this.LockAsync(name, context.RequestAborted).ConfigureAwait(false);
        var document = await this.store.GetAsync(name, context.RequestAborted).ConfigureAwait(false) ?? throw NotFound();
        this.EnsureOwner(document, user);

        if (HttpMethods.IsPut(method) || HttpMethods.IsPost(method))
        {
            // npm sends the version as a bare JSON string: "1.2.0".
            var version = Text(await this.ReadBodyNodeAsync(context, 4096).ConfigureAwait(false));

            if (version is null || !document.Versions.ContainsKey(version))
                throw new NpmException(StatusCodes.Status404NotFound, $"version not found: {version}");

            document.DistTags[tag] = version;
        }
        else if (HttpMethods.IsDelete(method))
        {
            if (tag == "latest")
                throw new NpmException(StatusCodes.Status400BadRequest, "The latest tag cannot be removed.");

            document.DistTags.Remove(tag);
        }
        else
        {
            throw NotFound();
        }

        document.Modified = this.options.TimeProvider.GetUtcNow();
        document.Rev = NextRev(document.Rev);
        await this.store.SaveAsync(document, context.RequestAborted).ConfigureAwait(false);

        await WriteJsonAsync(context, Tags(document), statusCode: HttpMethods.IsDelete(method) ? StatusCodes.Status200OK : StatusCodes.Status201Created).ConfigureAwait(false);
    }

    /// <summary><c>PUT -/user/org.couchdb.user:{name}</c> - <c>npm login</c>: credentials in, a token out.</summary>
    async ValueTask LoginAsync(HttpContext context, string userName)
    {
        if (this.options.LoginAsync is not { } login)
            throw new NpmException(StatusCodes.Status404NotFound, "This registry does not support npm login; ask for a token.");

        var body = await this.ReadBodyAsync(context, 64 * 1024).ConfigureAwait(false);
        var name = Text(body["name"]) ?? userName;
        var password = Text(body["password"]);

        if (string.IsNullOrEmpty(password))
            throw new NpmException(StatusCodes.Status400BadRequest, "A password is required.");

        var token = await login(new NpmLoginContext(context, name, password, Text(body["email"]))).ConfigureAwait(false)
            ?? throw new NpmException(StatusCodes.Status401Unauthorized, "Incorrect user name or password.");

        await WriteJsonAsync(context, new JsonObject
        {
            ["ok"] = true,
            ["id"] = "org.couchdb.user:" + name,
            ["token"] = token
        }, statusCode: StatusCodes.Status201Created).ConfigureAwait(false);
    }

    // ---- upstream ----

    /// <summary>
    /// Answers from <see cref="NpmRegistryOptions.Upstream"/> when there is one, as it answered -
    /// tarball links and all, so the client downloads public packages from where they live.
    /// </summary>
    async ValueTask ProxyAsync(HttpContext context, string relative)
    {
        if (this.options.Upstream is not { } upstream)
            throw NotFound();

        using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), new Uri(upstream.ToString().TrimEnd('/') + "/" + relative));

        foreach (var header in (string[])[HeaderNames.Accept, HeaderNames.IfNoneMatch])
        {
            if (context.Request.Headers.GetFirst(header) is { } value)
                request.Headers.TryAddWithoutValidation(header, value);
        }

        if (HttpMethods.IsPost(context.Request.Method))
        {
            var buffer = new MemoryStream();
            await context.Request.Body.CopyToAsync(buffer, context.RequestAborted).ConfigureAwait(false);
            request.Content = new ByteArrayContent(buffer.ToArray());

            if (context.Request.ContentType is { } contentType)
                request.Content.Headers.TryAddWithoutValidation(HeaderNames.ContentType, contentType);

            if (context.Request.Headers.GetFirst(HeaderNames.ContentEncoding) is { } encoding)
                request.Content.Headers.TryAddWithoutValidation(HeaderNames.ContentEncoding, encoding);
        }

        using var response = await this.upstream.Value.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted).ConfigureAwait(false);

        context.Response.StatusCode = (int)response.StatusCode;

        if (response.Headers.ETag is { } etag)
            context.Response.Headers.Set(HeaderNames.ETag, etag.ToString());

        var type = response.Content.Headers.ContentType?.ToString() ?? JsonContentType;
        await using var body = await response.Content.ReadAsStreamAsync(context.RequestAborted).ConfigureAwait(false);
        await context.Response.WriteStreamAsync(body, type, context.RequestAborted).ConfigureAwait(false);
    }

    static string EncodeName(string name) => name.Replace("/", "%2f", StringComparison.Ordinal);

    // ---- documents ----

    JsonObject Full(NpmPackageDocument document, string baseUrl)
    {
        var versions = new JsonObject();
        var time = new JsonObject
        {
            ["created"] = NpmRules.Iso(document.Created),
            ["modified"] = NpmRules.Iso(document.Modified)
        };

        foreach (var version in document.Versions.Values.OrderBy(v => v.Version, NpmRules.VersionComparer))
        {
            versions[version.Version] = Served(document.Name, version, baseUrl);
            time[version.Version] = NpmRules.Iso(version.Published);
        }

        var root = new JsonObject
        {
            ["_id"] = document.Name,
            ["_rev"] = document.Rev,
            ["name"] = document.Name,
            ["dist-tags"] = Tags(document),
            ["versions"] = versions,
            ["time"] = time,
            ["maintainers"] = document.Maintainers?.DeepClone() ?? new JsonArray()
        };

        if (Latest(document) is { } latest)
        {
            foreach (var field in TopLevelFields)
            {
                if (latest.Manifest[field] is { } value)
                    root[field] = value.DeepClone();
            }
        }

        return root;
    }

    JsonObject Abbreviated(NpmPackageDocument document, string baseUrl)
    {
        var versions = new JsonObject();

        foreach (var version in document.Versions.Values.OrderBy(v => v.Version, NpmRules.VersionComparer))
        {
            var served = Served(document.Name, version, baseUrl);
            var small = new JsonObject();

            foreach (var field in AbbreviatedFields)
            {
                if (served[field] is { } value)
                    small[field] = value.DeepClone();
            }

            versions[version.Version] = small;
        }

        return new JsonObject
        {
            ["name"] = document.Name,
            ["modified"] = NpmRules.Iso(document.Modified),
            ["dist-tags"] = Tags(document),
            ["versions"] = versions
        };
    }

    /// <summary>A version's manifest with its tarball URL pointing at this registry, as this request reached it.</summary>
    static JsonObject Served(string name, NpmPackageVersion version, string baseUrl)
    {
        var manifest = (JsonObject)version.Manifest.DeepClone();
        var dist = manifest["dist"] as JsonObject ?? [];

        dist["tarball"] = $"{baseUrl}/{name}/-/{version.TarballFile}";
        dist["integrity"] ??= version.Integrity;
        dist["shasum"] ??= version.Shasum;
        manifest["dist"] = dist;

        return manifest;
    }

    static JsonObject Tags(NpmPackageDocument document)
    {
        var tags = new JsonObject();

        foreach (var (tag, version) in document.DistTags)
            tags[tag] = version;

        return tags;
    }

    static NpmPackageVersion? Latest(NpmPackageDocument document)
        => document.DistTags.TryGetValue("latest", out var latest) && document.Versions.TryGetValue(latest, out var version)
            ? version
            : document.Versions.Values.OrderBy(v => v.Version, NpmRules.VersionComparer).LastOrDefault();

    /// <summary>
    /// <c>latest</c> must name a version that exists. After an unpublish took it away, it moves to the
    /// highest remaining release, or the highest prerelease when there is no release.
    /// </summary>
    static void FixLatest(NpmPackageDocument document)
    {
        foreach (var stale in document.DistTags.Where(t => !document.Versions.ContainsKey(t.Value)).Select(t => t.Key).ToList())
            document.DistTags.Remove(stale);

        if (document.DistTags.ContainsKey("latest") || document.Versions.Count == 0)
            return;

        var ordered = document.Versions.Keys.OrderBy(v => v, NpmRules.VersionComparer).ToList();
        document.DistTags["latest"] = ordered.LastOrDefault(v => !NpmRules.IsPrerelease(v)) ?? ordered[^1];
    }

    static string NextRev(string current)
    {
        var number = current.Split('-')[0] is var head && int.TryParse(head, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
        return $"{number + 1}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8))}";
    }

    // ---- authorization ----

    async ValueTask AuthorizeReadAsync(HttpContext context, string? name)
    {
        if (this.options.RequireTokenForReads)
            await this.RequireUserAsync(context, name).ConfigureAwait(false);
    }

    async ValueTask<string> RequireUserAsync(HttpContext context, string? name)
    {
        var header = context.Request.Headers.GetFirst(HeaderNames.Authorization);

        if (header is null || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            throw new NpmException(StatusCodes.Status401Unauthorized, "This request needs a token: set //host/path/:_authToken in .npmrc, or run npm login.");

        var token = header["Bearer ".Length..].Trim();
        var presented = Encoding.UTF8.GetBytes(token);

        foreach (var (known, user) in this.options.Tokens)
        {
            if (CryptographicOperations.FixedTimeEquals(presented, Encoding.UTF8.GetBytes(known)))
                return user;
        }

        if (this.options.ValidateTokenAsync is { } validate
            && await validate(new NpmTokenContext(context, token, name)).ConfigureAwait(false) is { Length: > 0 } validated)
            return validated;

        throw new NpmException(StatusCodes.Status401Unauthorized, "The token is invalid or has been revoked.");
    }

    /// <summary>Only a maintainer may change a package, as on the public registry; <c>npm owner add</c> makes more.</summary>
    void EnsureOwner(NpmPackageDocument document, string user)
    {
        if (document.Maintainers is not { Count: > 0 } maintainers)
            return;

        if (!maintainers.Any(m => Text(m?["name"]) == user))
            throw new NpmException(StatusCodes.Status403Forbidden, $"{user} is not a maintainer of {document.Name}.");
    }

    void EnsureWritable()
    {
        if (this.options.ReadOnly)
            throw new NpmException(StatusCodes.Status405MethodNotAllowed, "This registry is read-only.");
    }

    async ValueTask<NpmPackageDocument> RequireCurrentAsync(HttpContext context, string name, string? rev)
    {
        var document = await this.store.GetAsync(name, context.RequestAborted).ConfigureAwait(false) ?? throw NotFound();

        if (rev is not null && document.Rev != rev)
            throw new NpmException(StatusCodes.Status409Conflict, "Document update conflict: the package changed since it was read. Try again.");

        return document;
    }

    async ValueTask<IDisposable> LockAsync(string name, CancellationToken cancellationToken)
    {
        var gate = this.locks.GetOrAdd(name, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(gate);
    }

    sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }

    // ---- plumbing ----

    string BaseUrl(HttpContext context)
        => this.options.PublicBaseUrl?.TrimEnd('/') ?? $"{context.Request.Scheme}://{context.Request.Host}{this.basePath}";

    async ValueTask<JsonObject> ReadBodyAsync(HttpContext context, long limit)
        => await this.ReadBodyNodeAsync(context, limit).ConfigureAwait(false) as JsonObject
            ?? throw new NpmException(StatusCodes.Status400BadRequest, "The body must be a JSON object.");

    async ValueTask<JsonNode?> ReadBodyNodeAsync(HttpContext context, long limit)
    {
        if (context.Request.ContentLength > limit)
            throw new NpmException(StatusCodes.Status413PayloadTooLarge, "The request is too large.");

        using var buffer = new MemoryStream();
        var chunk = ArrayPool<byte>.Shared.Rent(81920);

        try
        {
            int read;

            while ((read = await context.Request.Body.ReadAsync(chunk, context.RequestAborted).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > limit)
                    throw new NpmException(StatusCodes.Status413PayloadTooLarge, "The request is too large.");

                buffer.Write(chunk, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        try
        {
            return JsonNode.Parse(buffer.ToArray());
        }
        catch (JsonException)
        {
            throw new NpmException(StatusCodes.Status400BadRequest, "The body is not valid JSON.");
        }
    }

    static string? Text(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    static NpmException NotFound() => new(StatusCodes.Status404NotFound, "Not found");

    static ValueTask WriteJsonAsync(HttpContext context, JsonNode node, string contentType = JsonContentType, int statusCode = StatusCodes.Status200OK)
    {
        var buffer = new ArrayBufferWriter<byte>(1024);

        using (var writer = new Utf8JsonWriter(buffer))
            node.WriteTo(writer);

        context.Response.StatusCode = statusCode;
        return context.Response.WriteBytesAsync(buffer.WrittenMemory, contentType, context.RequestAborted);
    }

    static ValueTask ErrorAsync(HttpContext context, int statusCode, string message)
    {
        if (HttpMethods.IsHead(context.Request.Method))
        {
            context.Response.StatusCode = statusCode;
            context.Response.ContentLength = 0;
            return context.Response.StartAsync(context.RequestAborted);
        }

        // npm prints the "error" member of a failed response; problem details would be lost on it.
        return WriteJsonAsync(context, new JsonObject { ["error"] = message }, statusCode: statusCode);
    }
}
