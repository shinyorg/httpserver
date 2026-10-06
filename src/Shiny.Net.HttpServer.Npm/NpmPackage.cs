using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Shiny.Net.HttpServer.Npm;

/// <summary>
/// Everything the registry knows about one package: its versions, its dist-tags and its owners -
/// what npm calls the packument, minus the absolute URLs, which are added when it is served.
/// </summary>
public sealed class NpmPackageDocument
{
    /// <summary><c>name</c> or <c>@scope/name</c>, lower case.</summary>
    public required string Name { get; set; }

    /// <summary>
    /// Changes on every write. npm sends it back on updates (<c>/-rev/{rev}</c>), and an update based
    /// on an older one is refused, so two people editing the same package cannot overwrite each other.
    /// </summary>
    public string Rev { get; set; } = string.Empty;

    /// <summary><c>latest</c>, <c>next</c>, <c>beta</c>... → version.</summary>
    public Dictionary<string, string> DistTags { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Version → what was published as it.</summary>
    public Dictionary<string, NpmPackageVersion> Versions { get; set; } = new(StringComparer.Ordinal);

    /// <summary>As npm keeps them: <c>[{ "name": "...", "email": "..." }]</c>.</summary>
    public JsonArray? Maintainers { get; set; }

    public DateTimeOffset Created { get; set; }

    public DateTimeOffset Modified { get; set; }
}

/// <summary>One published version.</summary>
public sealed class NpmPackageVersion
{
    public required string Version { get; set; }

    /// <summary>
    /// The version's <c>package.json</c> as it was published - dependencies, <c>bin</c>,
    /// <c>engines</c> and all - with <c>dist</c> filled in by the registry. Kept as JSON so nothing
    /// the client sent is lost.
    /// </summary>
    public required JsonObject Manifest { get; set; }

    /// <summary>The tarball's file name in the store: <c>{unscoped-name}-{version}.tgz</c>.</summary>
    public required string TarballFile { get; set; }

    public long Size { get; set; }

    /// <summary>Subresource integrity of the tarball: <c>sha512-…</c>.</summary>
    public string? Integrity { get; set; }

    /// <summary>SHA-1 of the tarball, hex - what older clients check.</summary>
    public string? Shasum { get; set; }

    public DateTimeOffset Published { get; set; }

    /// <summary>The user whose token published it.</summary>
    public string? PublishedBy { get; set; }

    /// <summary>The deprecation message, if <c>npm deprecate</c> set one.</summary>
    [JsonIgnore]
    public string? Deprecated => this.Manifest["deprecated"] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0 ? text : null;
}
