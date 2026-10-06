using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NuGet.Versioning;
using Shiny.Net.HttpServer.Files;

namespace Shiny.Net.HttpServer.NuGet.Internal;

/// <summary>
/// The NuGet V3 server API behind <see cref="NuGetFeedExtensions.MapNuGetFeed(HttpServer, string, NuGetFeedOptions)"/>:
/// https://learn.microsoft.com/nuget/api/overview.
/// <para>
/// Every resource is answered straight from the store - there is no catalog and no precomputed
/// registration blobs - which is the right trade for a feed of hundreds of packages rather than
/// hundreds of thousands. Registrations come back as a single inlined page, the shape a client
/// handles with one request.
/// </para>
/// </summary>
sealed class NuGetFeedHandler
{
    public const string ApiKeyHeader = "X-NuGet-ApiKey";

    // Paths under the feed's prefix. The service index is the only one a client is told about;
    // it finds the rest from there, so these can be anything.
    public const string ServiceIndexPath = "/v3/index.json";
    public const string PackageBasePath = "/v3/package";
    public const string RegistrationPath = "/v3/registration";
    public const string SearchPath = "/v3/search";
    public const string AutocompletePath = "/v3/autocomplete";
    public const string PublishPath = "/api/v2/package";

    const string JsonContentType = "application/json; charset=utf-8";
    const int DefaultTake = 20;
    const int MaxTake = 1000;

    static readonly JsonWriterOptions WriterOptions = new() { Indented = false };

    readonly NuGetFeedOptions options;
    readonly INuGetPackageStore store;
    readonly string basePath;

    public NuGetFeedHandler(NuGetFeedOptions options, INuGetPackageStore store, string basePath)
    {
        this.options = options;
        this.store = store;
        this.basePath = basePath;
    }

    /// <summary>Turns a <see cref="NuGetFeedException"/> into the status it names, as a problem response.</summary>
    public RequestDelegate Guard(RequestDelegate verb) => async context =>
    {
        try
        {
            await verb(context).ConfigureAwait(false);
        }
        catch (NuGetFeedException ex) when (!context.Response.HasStarted)
        {
            await ProblemAsync(context, ex.StatusCode, ex.Message).ConfigureAwait(false);
        }
    };

    string BaseUrl(HttpContext context)
    {
        if (this.options.PublicBaseUrl is { } configured)
            return configured.TrimEnd('/');

        var request = context.Request;
        return $"{request.Scheme}://{request.Host}{this.basePath}";
    }

    // ---- service index ----

    public ValueTask ServiceIndexAsync(HttpContext context)
    {
        var baseUrl = this.BaseUrl(context);

        return WriteJsonAsync(context, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("version", "3.0.0");
            writer.WriteStartArray("resources");

            Resource(writer, baseUrl + PackageBasePath + "/", "PackageBaseAddress/3.0.0", "Package content: version lists, .nupkg and .nuspec downloads.");

            foreach (var type in (string[])["RegistrationsBaseUrl", "RegistrationsBaseUrl/3.0.0-rc", "RegistrationsBaseUrl/3.0.0-beta", "RegistrationsBaseUrl/3.4.0", "RegistrationsBaseUrl/3.6.0"])
                Resource(writer, baseUrl + RegistrationPath + "/", type, "Package metadata, including SemVer 2.0.0 packages.");

            foreach (var type in (string[])["SearchQueryService", "SearchQueryService/3.0.0-rc", "SearchQueryService/3.0.0-beta", "SearchQueryService/3.5.0"])
                Resource(writer, baseUrl + SearchPath, type, "Search.");

            foreach (var type in (string[])["SearchAutocompleteService", "SearchAutocompleteService/3.0.0-rc", "SearchAutocompleteService/3.0.0-beta", "SearchAutocompleteService/3.5.0"])
                Resource(writer, baseUrl + AutocompletePath, type, "Package id and version autocomplete.");

            // Advertised only where it is mapped: a client that sees PackagePublish offers to push.
            if (!this.options.ReadOnly)
                Resource(writer, baseUrl + PublishPath, "PackagePublish/2.0.0", "Push, delete (or unlist) and relist.");

            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    static void Resource(Utf8JsonWriter writer, string id, string type, string comment)
    {
        writer.WriteStartObject();
        writer.WriteString("@id", id);
        writer.WriteString("@type", type);
        writer.WriteString("comment", comment);
        writer.WriteEndObject();
    }

    // ---- package content (PackageBaseAddress) ----

    /// <summary>
    /// <c>GET {id}/index.json</c>: every version, unlisted ones included - restore asks here, and
    /// an unlisted version a project already depends on still has to restore.
    /// </summary>
    public async ValueTask VersionListAsync(HttpContext context)
    {
        var id = RequireId(context);
        var versions = await this.store.GetVersionsAsync(id, context.RequestAborted).ConfigureAwait(false);

        if (versions.Count == 0)
            throw new NuGetFeedException(StatusCodes.Status404NotFound, $"There is no package '{id}'.");

        await WriteJsonAsync(context, writer =>
        {
            writer.WriteStartObject();
            writer.WriteStartArray("versions");

            foreach (var package in versions.OrderBy(p => p.Version))
                writer.WriteStringValue(package.Version.ToNormalizedString().ToLowerInvariant());

            writer.WriteEndArray();
            writer.WriteEndObject();
        }).ConfigureAwait(false);
    }

    /// <summary><c>GET {id}/{version}/{id}.{version}.nupkg</c> and <c>GET {id}/{version}/{id}.nuspec</c>.</summary>
    public async ValueTask DownloadAsync(HttpContext context)
    {
        var id = RequireId(context);
        var version = RequireVersion(context);
        var file = context.Request.RouteValues["file"] ?? string.Empty;
        var lowerId = id.ToLowerInvariant();
        var lowerVersion = version.ToNormalizedString().ToLowerInvariant();

        Stream? stream;
        string contentType;

        if (file.Equals($"{lowerId}.{lowerVersion}.nupkg", StringComparison.OrdinalIgnoreCase))
        {
            stream = await this.store.OpenPackageAsync(id, version, context.RequestAborted).ConfigureAwait(false);
            contentType = "application/octet-stream";
        }
        else if (file.Equals($"{lowerId}.nuspec", StringComparison.OrdinalIgnoreCase))
        {
            stream = await this.store.OpenManifestAsync(id, version, context.RequestAborted).ConfigureAwait(false);
            contentType = "application/xml";
        }
        else
        {
            throw new NuGetFeedException(StatusCodes.Status404NotFound, $"'{file}' is not a file of {id} {version}.");
        }

        if (stream is null)
            throw new NuGetFeedException(StatusCodes.Status404NotFound, $"There is no package '{id}' {version}.");

        await using (stream)
        {
            // A version's bytes never change once pushed (unless AllowOverwrite says otherwise), so
            // anything in between can keep them.
            if (!this.options.AllowOverwrite)
                context.Response.Headers.Set(HeaderNames.CacheControl, "public, max-age=31536000, immutable");

            await context.Response.WriteStreamAsync(stream, contentType, context.RequestAborted).ConfigureAwait(false);
        }
    }

    // ---- registrations (package metadata) ----

    /// <summary>
    /// <c>GET {id}/index.json</c> or <c>GET {id}/{version}.json</c> - one route, since the two
    /// differ only in their last segment.
    /// </summary>
    public async ValueTask RegistrationAsync(HttpContext context)
    {
        var id = RequireId(context);
        var leaf = context.Request.RouteValues["leaf"] ?? string.Empty;

        if (!leaf.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            throw new NuGetFeedException(StatusCodes.Status404NotFound, "Not a registration document.");

        var versions = await this.store.GetVersionsAsync(id, context.RequestAborted).ConfigureAwait(false);

        if (versions.Count == 0)
            throw new NuGetFeedException(StatusCodes.Status404NotFound, $"There is no package '{id}'.");

        var baseUrl = this.BaseUrl(context);
        var ordered = versions.OrderBy(p => p.Version).ToList();

        if (leaf.Equals("index.json", StringComparison.OrdinalIgnoreCase))
        {
            await WriteJsonAsync(context, writer => this.WriteRegistrationIndex(writer, baseUrl, ordered)).ConfigureAwait(false);
            return;
        }

        if (!NuGetVersion.TryParse(leaf[..^".json".Length], out var version)
            || ordered.FirstOrDefault(p => p.Version == version) is not { } package)
            throw new NuGetFeedException(StatusCodes.Status404NotFound, $"There is no package '{id}' {leaf[..^".json".Length]}.");

        await WriteJsonAsync(context, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("@id", LeafUrl(baseUrl, package));
            writer.WriteStartArray("@type");
            writer.WriteStringValue("Package");
            writer.WriteStringValue("http://schema.nuget.org/catalog#Permalink");
            writer.WriteEndArray();
            writer.WriteBoolean("listed", package.Listed);
            writer.WriteString("packageContent", ContentUrl(baseUrl, package));
            writer.WriteString("published", PublishedValue(package));
            writer.WriteString("registration", RegistrationIndexUrl(baseUrl, package.Id));
            writer.WriteEndObject();
        }).ConfigureAwait(false);
    }

    void WriteRegistrationIndex(Utf8JsonWriter writer, string baseUrl, List<NuGetPackage> ordered)
    {
        var id = ordered[^1].Id;
        var indexUrl = RegistrationIndexUrl(baseUrl, id);

        writer.WriteStartObject();
        writer.WriteString("@id", indexUrl);
        writer.WriteNumber("count", 1);
        writer.WriteStartArray("items");

        writer.WriteStartObject();
        writer.WriteString("@id", indexUrl + "#page/" + Lower(ordered[0]) + "/" + Lower(ordered[^1]));
        writer.WriteNumber("count", ordered.Count);
        writer.WriteString("lower", Lower(ordered[0]));
        writer.WriteString("upper", Lower(ordered[^1]));
        writer.WriteString("parent", indexUrl);
        writer.WriteStartArray("items");

        foreach (var package in ordered)
        {
            writer.WriteStartObject();
            writer.WriteString("@id", LeafUrl(baseUrl, package));
            writer.WriteString("packageContent", ContentUrl(baseUrl, package));
            writer.WriteString("registration", indexUrl);
            this.WriteCatalogEntry(writer, baseUrl, package);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    void WriteCatalogEntry(Utf8JsonWriter writer, string baseUrl, NuGetPackage package)
    {
        writer.WriteStartObject("catalogEntry");
        writer.WriteString("@id", LeafUrl(baseUrl, package) + "#catalogEntry");
        writer.WriteString("@type", "PackageDetails");
        writer.WriteString("id", package.Id);
        writer.WriteString("version", package.Version.ToFullString());
        writer.WriteString("authors", package.Authors ?? string.Empty);
        writer.WriteString("description", package.Description ?? string.Empty);
        OptionalString(writer, "title", package.Title);
        OptionalString(writer, "summary", package.Summary);
        OptionalString(writer, "iconUrl", package.IconUrl);
        OptionalString(writer, "projectUrl", package.ProjectUrl);
        OptionalString(writer, "licenseUrl", package.LicenseUrl);
        OptionalString(writer, "licenseExpression", package.LicenseExpression);
        OptionalString(writer, "language", package.Language);
        OptionalString(writer, "minClientVersion", package.MinClientVersion);
        OptionalString(writer, "releaseNotes", package.ReleaseNotes);
        writer.WriteBoolean("listed", package.Listed);
        writer.WriteString("packageContent", ContentUrl(baseUrl, package));
        writer.WriteString("published", PublishedValue(package));
        writer.WriteBoolean("requireLicenseAcceptance", package.RequireLicenseAcceptance);

        writer.WriteStartArray("tags");
        foreach (var tag in package.Tags)
            writer.WriteStringValue(tag);
        writer.WriteEndArray();

        writer.WriteStartArray("dependencyGroups");

        foreach (var group in package.DependencyGroups)
        {
            writer.WriteStartObject();
            OptionalString(writer, "targetFramework", group.TargetFramework);
            writer.WriteStartArray("dependencies");

            foreach (var dependency in group.Dependencies)
            {
                writer.WriteStartObject();
                writer.WriteString("id", dependency.Id);
                writer.WriteString("range", dependency.Range.ToNormalizedString());
                writer.WriteString("registration", RegistrationIndexUrl(baseUrl, dependency.Id));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    // ---- search ----

    public async ValueTask SearchAsync(HttpContext context)
    {
        var query = context.Request.Query;
        var (skip, take) = Paging(query);
        var filter = Filter.From(query);
        var terms = Terms(query.GetFirst("q"));
        var packageType = query.GetFirst("packageType");
        var baseUrl = this.BaseUrl(context);

        var all = await this.store.GetAllAsync(context.RequestAborted).ConfigureAwait(false);
        var matches = new List<(NuGetPackage Latest, List<NuGetPackage> Versions)>();

        foreach (var group in all.GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase))
        {
            var visible = group.Where(filter.Admits).OrderBy(p => p.Version).ToList();

            if (visible.Count == 0)
                continue;

            var latest = visible[^1];

            if (!string.IsNullOrEmpty(packageType) && !IsOfType(latest, packageType))
                continue;

            if (!terms.All(term => term.Matches(latest)))
                continue;

            matches.Add((latest, visible));
        }

        var exactId = terms.Count == 1 && terms[0].Field is null ? terms[0].Value : null;
        var page = matches
            .OrderByDescending(m => exactId is not null && m.Latest.Id.Equals(exactId, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(m => exactId is not null && m.Latest.Id.StartsWith(exactId, StringComparison.OrdinalIgnoreCase))
            .ThenBy(m => m.Latest.Id, StringComparer.OrdinalIgnoreCase)
            .Skip(skip)
            .Take(take);

        await WriteJsonAsync(context, writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("totalHits", matches.Count);
            writer.WriteStartArray("data");

            foreach (var (latest, versions) in page)
            {
                var registration = RegistrationIndexUrl(baseUrl, latest.Id);

                writer.WriteStartObject();
                writer.WriteString("@id", registration);
                writer.WriteString("@type", "Package");
                writer.WriteString("registration", registration);
                writer.WriteString("id", latest.Id);
                writer.WriteString("version", latest.Version.ToFullString());
                writer.WriteString("description", latest.Description ?? string.Empty);
                writer.WriteString("summary", latest.Summary ?? string.Empty);
                writer.WriteString("title", latest.Title ?? latest.Id);
                OptionalString(writer, "iconUrl", latest.IconUrl);
                OptionalString(writer, "licenseUrl", latest.LicenseUrl);
                OptionalString(writer, "projectUrl", latest.ProjectUrl);
                StringArray(writer, "tags", latest.Tags);
                StringArray(writer, "authors", Split(latest.Authors));
                StringArray(writer, "owners", Split(latest.Owners));
                writer.WriteNumber("totalDownloads", 0);
                writer.WriteBoolean("verified", false);

                writer.WriteStartArray("packageTypes");
                foreach (var type in latest.PackageTypes.Count > 0 ? latest.PackageTypes : [new NuGetPackageType("Dependency")])
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", type.Name);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();

                writer.WriteStartArray("versions");
                foreach (var version in versions)
                {
                    writer.WriteStartObject();
                    writer.WriteString("version", version.Version.ToFullString());
                    writer.WriteNumber("downloads", 0);
                    writer.WriteString("@id", LeafUrl(baseUrl, version));
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>?q=</c> completes package ids; <c>?id=</c> lists one package's versions. The two are
    /// different questions sharing an endpoint, as the protocol has them.
    /// </summary>
    public async ValueTask AutocompleteAsync(HttpContext context)
    {
        var query = context.Request.Query;
        var filter = Filter.From(query);
        var all = await this.store.GetAllAsync(context.RequestAborted).ConfigureAwait(false);
        List<string> results;
        int total;

        if (query.GetFirst("id") is { Length: > 0 } id)
        {
            results = [.. all
                .Where(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase) && filter.Admits(p))
                .OrderBy(p => p.Version)
                .Select(p => p.Version.ToFullString())];
            total = results.Count;
        }
        else
        {
            var (skip, take) = Paging(query);
            var q = query.GetFirst("q")?.Trim() ?? string.Empty;
            var ids = all
                .Where(p => filter.Admits(p) && p.Id.Contains(q, StringComparison.OrdinalIgnoreCase))
                .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderBy(p => p.Version).Last().Id)
                .OrderByDescending(i => q.Length > 0 && i.StartsWith(q, StringComparison.OrdinalIgnoreCase))
                .ThenBy(i => i, StringComparer.OrdinalIgnoreCase)
                .ToList();

            total = ids.Count;
            results = [.. ids.Skip(skip).Take(take)];
        }

        await WriteJsonAsync(context, writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("totalHits", total);
            StringArray(writer, "data", results);
            writer.WriteEndObject();
        }).ConfigureAwait(false);
    }

    // ---- publish ----

    /// <summary>
    /// <c>PUT</c>: the package as the first file of a <c>multipart/form-data</c> body, which is how
    /// every NuGet client sends it. A bare <c>application/octet-stream</c> body is accepted too.
    /// </summary>
    public async ValueTask PushAsync(HttpContext context)
    {
        var apiKey = await this.AuthorizeAsync(context, packageId: null).ConfigureAwait(false);
        var temp = Path.Combine(Path.GetTempPath(), "shiny-nuget-" + Guid.NewGuid().ToString("n") + ".nupkg");

        try
        {
            await using var file = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.DeleteOnClose);

            await this.ReceiveAsync(context, file).ConfigureAwait(false);

            if (file.Length == 0)
                throw new NuGetFeedException(StatusCodes.Status400BadRequest, "No package was sent.");

            file.Position = 0;
            var manifest = NuspecReader.ExtractManifest(file);
            var parsed = NuspecReader.Parse(manifest);

            if (parsed.PackageTypes.Any(t => t.Name.Equals("SymbolsPackage", StringComparison.OrdinalIgnoreCase)))
                throw new NuGetFeedException(StatusCodes.Status400BadRequest, "This feed does not take symbol packages (.snupkg).");

            file.Position = 0;
            var hash = Convert.ToBase64String(await SHA512.HashDataAsync(file, context.RequestAborted).ConfigureAwait(false));

            var package = parsed with
            {
                Published = this.options.TimeProvider.GetUtcNow(),
                Size = file.Length,
                Sha512 = hash
            };

            var push = new NuGetPushContext(context, package, apiKey);

            if (this.options.OnBeforePushAsync is { } before)
            {
                await before(push).ConfigureAwait(false);

                if (push.IsRejected)
                {
                    await ProblemAsync(context, push.RejectStatusCode, push.RejectDetail).ConfigureAwait(false);
                    return;
                }
            }

            file.Position = 0;

            if (!await this.store.AddAsync(package, file, manifest, context.RequestAborted).ConfigureAwait(false))
            {
                if (!this.options.AllowOverwrite)
                    throw new NuGetFeedException(StatusCodes.Status409Conflict, $"{package.Id} {package.Version.ToNormalizedString()} already exists.");

                await this.store.DeleteAsync(package.Id, package.Version, context.RequestAborted).ConfigureAwait(false);
                file.Position = 0;

                if (!await this.store.AddAsync(package, file, manifest, context.RequestAborted).ConfigureAwait(false))
                    throw new NuGetFeedException(StatusCodes.Status409Conflict, $"{package.Id} {package.Version.ToNormalizedString()} was pushed by someone else at the same time.");
            }

            if (this.options.OnPackagePushedAsync is { } pushed)
                await pushed(push).ConfigureAwait(false);

            context.Response.StatusCode = StatusCodes.Status201Created;
            context.Response.ContentLength = 0;
            await context.Response.StartAsync(context.RequestAborted).ConfigureAwait(false);
        }
        finally
        {
            // DeleteOnClose covers the normal path; this covers a stream that never opened.
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    async ValueTask ReceiveAsync(HttpContext context, Stream destination)
    {
        var request = context.Request;

        if (request.ContentLength is { } declared && declared > this.options.MaxPackageSize + 64 * 1024)
            throw TooLarge();

        if (request.ContentType?.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase) == true)
        {
            await foreach (var section in request.ReadMultipartAsync(context.RequestAborted).ConfigureAwait(false))
            {
                // The client names the part "package" and gives it a file name; take the first
                // part that looks like a file, and drain the rest.
                if (destination.Length == 0 && (section.IsFile || section.ContentType is not null))
                    await this.CopyBoundedAsync(section.Body, destination, context.RequestAborted).ConfigureAwait(false);
                else
                    await section.Body.CopyToAsync(Stream.Null, context.RequestAborted).ConfigureAwait(false);
            }
        }
        else
        {
            await this.CopyBoundedAsync(request.Body, destination, context.RequestAborted).ConfigureAwait(false);
        }
    }

    async ValueTask CopyBoundedAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(81920);

        try
        {
            int read;

            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (destination.Length + read > this.options.MaxPackageSize)
                    throw TooLarge();

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    NuGetFeedException TooLarge() => new(
        StatusCodes.Status413PayloadTooLarge,
        $"Packages on this feed are limited to {this.options.MaxPackageSize.ToString(CultureInfo.InvariantCulture)} bytes."
    );

    /// <summary><c>DELETE {id}/{version}</c>: unlists, or deletes, as <see cref="NuGetFeedOptions.DeleteBehavior"/> says.</summary>
    public async ValueTask DeleteAsync(HttpContext context)
    {
        var id = RequireId(context);
        var version = RequireVersion(context);
        await this.AuthorizeAsync(context, id).ConfigureAwait(false);

        var found = this.options.DeleteBehavior == NuGetDeleteBehavior.Delete
            ? await this.store.DeleteAsync(id, version, context.RequestAborted).ConfigureAwait(false)
            : await this.store.SetListedAsync(id, version, listed: false, context.RequestAborted).ConfigureAwait(false);

        if (!found)
            throw new NuGetFeedException(StatusCodes.Status404NotFound, $"There is no package '{id}' {version}.");

        await StatusAsync(context, StatusCodes.Status204NoContent).ConfigureAwait(false);
    }

    /// <summary><c>POST {id}/{version}</c>: relists an unlisted version.</summary>
    public async ValueTask RelistAsync(HttpContext context)
    {
        var id = RequireId(context);
        var version = RequireVersion(context);
        await this.AuthorizeAsync(context, id).ConfigureAwait(false);

        if (!await this.store.SetListedAsync(id, version, listed: true, context.RequestAborted).ConfigureAwait(false))
            throw new NuGetFeedException(StatusCodes.Status404NotFound, $"There is no package '{id}' {version}.");

        await StatusAsync(context, StatusCodes.Status200OK).ConfigureAwait(false);
    }

    /// <summary>Checks <c>X-NuGet-ApiKey</c>. Returns the key, or null when keys are not required.</summary>
    async ValueTask<string?> AuthorizeAsync(HttpContext context, string? packageId)
    {
        if (!this.options.RequireApiKey)
            return null;

        var key = context.Request.Headers.GetFirst(ApiKeyHeader);

        // 403 rather than 401 for both missing and wrong, as nuget.org answers: a 401 sends the
        // client off to its credential providers, which have nothing to do with an API key.
        if (string.IsNullOrEmpty(key))
            throw new NuGetFeedException(StatusCodes.Status403Forbidden, $"An API key is required ({ApiKeyHeader}).");

        var presented = Encoding.UTF8.GetBytes(key);

        foreach (var known in this.options.ApiKeys)
        {
            if (CryptographicOperations.FixedTimeEquals(presented, Encoding.UTF8.GetBytes(known)))
                return key;
        }

        if (this.options.ValidateApiKeyAsync is { } validate
            && await validate(new NuGetApiKeyContext(context, key, packageId)).ConfigureAwait(false))
            return key;

        throw new NuGetFeedException(StatusCodes.Status403Forbidden, "The API key is invalid, or is not allowed to change this package.");
    }

    // ---- helpers ----

    static string RequireId(HttpContext context)
    {
        var id = context.Request.RouteValues["id"];

        // Validated before it reaches the store, which may well build a file path out of it.
        if (!NuspecReader.IsValidId(id))
            throw new NuGetFeedException(StatusCodes.Status404NotFound, $"'{id}' is not a package id.");

        return id!;
    }

    static NuGetVersion RequireVersion(HttpContext context)
    {
        var raw = context.Request.RouteValues["version"];

        if (!NuGetVersion.TryParse(raw, out var version))
            throw new NuGetFeedException(StatusCodes.Status404NotFound, $"'{raw}' is not a package version.");

        return version;
    }

    static (int Skip, int Take) Paging(QueryCollection query)
    {
        var skip = int.TryParse(query.GetFirst("skip"), NumberStyles.None, CultureInfo.InvariantCulture, out var s) ? s : 0;
        var take = int.TryParse(query.GetFirst("take"), NumberStyles.None, CultureInfo.InvariantCulture, out var t) ? t : DefaultTake;

        return (skip, Math.Clamp(take, 0, MaxTake));
    }

    /// <summary>What a query may see: listed versions, prerelease only when asked, SemVer 2.0.0 only when the client says it understands it.</summary>
    readonly record struct Filter(bool Prerelease, bool SemVer2)
    {
        public static Filter From(QueryCollection query) => new(
            bool.TryParse(query.GetFirst("prerelease"), out var prerelease) && prerelease,
            NuGetVersion.TryParse(query.GetFirst("semVerLevel"), out var level) && level.Major >= 2
        );

        public bool Admits(NuGetPackage package)
            => package.Listed
                && (this.Prerelease || !package.Version.IsPrerelease)
                && (this.SemVer2 || !package.IsSemVer2);
    }

    /// <summary>A search term: bare text, or <c>field:value</c> for <c>id</c>, <c>packageid</c>, <c>tags</c>, <c>author</c>, <c>title</c>, <c>description</c>.</summary>
    sealed record Term(string? Field, string Value)
    {
        public bool Matches(NuGetPackage p) => this.Field switch
        {
            "packageid" => p.Id.Equals(this.Value, StringComparison.OrdinalIgnoreCase),
            "id" => Has(p.Id),
            "tag" or "tags" => p.Tags.Any(t => t.Equals(this.Value, StringComparison.OrdinalIgnoreCase)),
            "author" or "authors" => Has(p.Authors),
            "owner" or "owners" => Has(p.Owners),
            "title" => Has(p.Title),
            "description" => Has(p.Description),
            "summary" => Has(p.Summary),
            _ => Has(p.Id) || Has(p.Title) || Has(p.Description) || Has(p.Summary) || Has(p.Authors)
                || p.Tags.Any(t => t.Contains(this.Value, StringComparison.OrdinalIgnoreCase))
        };

        bool Has(string? text) => text?.Contains(this.Value, StringComparison.OrdinalIgnoreCase) == true;
    }

    static List<Term> Terms(string? q)
    {
        var terms = new List<Term>();

        if (string.IsNullOrWhiteSpace(q))
            return terms;

        foreach (var token in q.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = token.IndexOf(':');

            if (colon > 0 && colon < token.Length - 1)
                terms.Add(new Term(token[..colon].ToLowerInvariant(), token[(colon + 1)..].Trim('"')));
            else
                terms.Add(new Term(null, token.Trim('"')));
        }

        return terms;
    }

    static bool IsOfType(NuGetPackage package, string type)
        => package.PackageTypes.Count == 0
            ? type.Equals("Dependency", StringComparison.OrdinalIgnoreCase)
            : package.PackageTypes.Any(t => t.Name.Equals(type, StringComparison.OrdinalIgnoreCase));

    static string[] Split(string? list)
        => list?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];

    static string Lower(NuGetPackage package) => package.Version.ToNormalizedString().ToLowerInvariant();

    static string RegistrationIndexUrl(string baseUrl, string id)
        => $"{baseUrl}{RegistrationPath}/{id.ToLowerInvariant()}/index.json";

    static string LeafUrl(string baseUrl, NuGetPackage package)
        => $"{baseUrl}{RegistrationPath}/{package.Id.ToLowerInvariant()}/{Lower(package)}.json";

    static string ContentUrl(string baseUrl, NuGetPackage package)
    {
        var id = package.Id.ToLowerInvariant();
        var version = Lower(package);
        return $"{baseUrl}{PackageBasePath}/{id}/{version}/{id}.{version}.nupkg";
    }

    /// <summary>
    /// When the version was pushed - or, unlisted, the year 1900, which is how nuget.org has always
    /// marked it and what older clients look for instead of <c>listed</c>.
    /// </summary>
    static string PublishedValue(NuGetPackage package)
        => (package.Listed ? package.Published : new DateTimeOffset(1900, 1, 1, 0, 0, 0, TimeSpan.Zero))
            .UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    static void OptionalString(Utf8JsonWriter writer, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
            writer.WriteString(name, value);
    }

    static void StringArray(Utf8JsonWriter writer, string name, IEnumerable<string> values)
    {
        writer.WriteStartArray(name);

        foreach (var value in values)
            writer.WriteStringValue(value);

        writer.WriteEndArray();
    }

    static ValueTask WriteJsonAsync(HttpContext context, Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>(4096);

        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
            write(writer);

        return context.Response.WriteBytesAsync(buffer.WrittenMemory, JsonContentType, context.RequestAborted);
    }

    static ValueTask StatusAsync(HttpContext context, int statusCode)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentLength = 0;
        return context.Response.StartAsync(context.RequestAborted);
    }

    static ValueTask ProblemAsync(HttpContext context, int statusCode, string? detail)
    {
        if (HttpMethods.IsHead(context.Request.Method))
            return StatusAsync(context, statusCode);

        return ProblemDetailsWriter.WriteResponseAsync(context, new ProblemDetails
        {
            Status = statusCode,
            Title = statusCode switch
            {
                StatusCodes.Status400BadRequest => "Bad Request",
                StatusCodes.Status403Forbidden => "Forbidden",
                StatusCodes.Status404NotFound => "Not Found",
                StatusCodes.Status409Conflict => "Conflict",
                StatusCodes.Status413PayloadTooLarge => "Payload Too Large",
                _ => null
            },
            Detail = detail
        });
    }
}
