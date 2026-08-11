using System.Reflection;

namespace Shiny.Net.HttpServer.StaticFiles;

/// <summary>One servable file, resolved but not yet opened.</summary>
/// <param name="Name">Logical file name, used to pick a content type.</param>
/// <param name="Length">Byte length, which ranges are resolved against.</param>
/// <param name="ETag">Entity tag for conditional requests. Already quoted.</param>
/// <param name="LastModified">Modification time, or null when the source has no meaningful one.</param>
/// <param name="Open">Opens the content. Called once per response, not per resolution.</param>
/// <param name="ContentEncoding">
/// Set when the bytes are already compressed — a <c>.br</c> or <c>.gz</c> sidecar served in place
/// of the original. The content type still describes the file underneath.
/// </param>
public readonly record struct StaticFile(
    string Name,
    long Length,
    string ETag,
    DateTimeOffset? LastModified,
    Func<CancellationToken, ValueTask<Stream>> Open,
    string? ContentEncoding = null
);

/// <summary>
/// Where static content comes from.
/// <para>
/// An abstraction rather than a directory path because the interesting case on a phone is not a
/// directory: a MAUI app ships its web assets as embedded resources, and there is no folder to
/// point at.
/// </para>
/// </summary>
public interface IStaticFileSource
{
    /// <summary>
    /// Resolves a request path, relative and already URL-decoded, to a file.
    /// <para>
    /// Implementations must treat the path as hostile. It arrives from the network, and the caller
    /// has already rejected the obvious traversal attempts — but a source that maps paths onto a
    /// real file system is the last line of defence, not the first.
    /// </para>
    /// </summary>
    bool TryGetFile(string relativePath, out StaticFile file);
}

/// <summary>
/// Serves files from a directory on disk.
/// <para>
/// Every resolved path is checked to be inside the root after normalization *and* after following
/// links, because a symlink is the one way a path that looks contained can leave.
/// </para>
/// </summary>
public sealed class PhysicalFileSource : IStaticFileSource
{
    readonly string root;

    public PhysicalFileSource(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        this.root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
    }

    /// <summary>Serves dotfiles. Off by default: <c>.env</c> and <c>.git</c> live in content directories.</summary>
    public bool ServeHiddenFiles { get; init; }

    /// <summary>
    /// Codings to look for as sidecar files, best first — <c>app.js.br</c> beside <c>app.js</c>.
    /// <para>
    /// Empty by default. A build that ships precompressed assets (a Blazor publish does) has already
    /// spent the CPU at maximum effort, and serving that file beats recompressing it per request at
    /// a level chosen for speed. Set through <see cref="StaticFileOptions"/> rather than here.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> PrecompressedEncodings { get; init; } = [];

    public bool TryGetFile(string relativePath, out StaticFile file)
        => this.TryGetFile(relativePath, acceptedEncodings: null, out file);

    /// <summary>
    /// Resolves a path, preferring a precompressed sidecar the client can decode.
    /// </summary>
    public bool TryGetFile(string relativePath, IReadOnlyList<string>? acceptedEncodings, out StaticFile file)
    {
        file = default;

        if (!StaticFilePath.TryNormalize(relativePath, this.ServeHiddenFiles, out var segments))
            return false;

        if (acceptedEncodings is { Count: > 0 } && this.PrecompressedEncodings.Count > 0)
        {
            foreach (var encoding in this.PrecompressedEncodings)
            {
                if (!acceptedEncodings.Contains(encoding, StringComparer.OrdinalIgnoreCase))
                    continue;

                var suffix = encoding switch
                {
                    "br" => ".br",
                    "gzip" => ".gz",
                    _ => null
                };

                if (suffix is null || !this.TryResolve(segments + suffix, out var sidecar))
                    continue;

                // The name comes from the original file, so the content type describes what the
                // bytes decompress *to* rather than the container they arrived in.
                file = sidecar with
                {
                    Name = Path.GetFileName(segments),
                    ContentEncoding = encoding
                };

                return true;
            }
        }

        return this.TryResolve(segments, out file);
    }

    bool TryResolve(string segments, out StaticFile file)
    {
        file = default;

        var candidate = Path.GetFullPath(Path.Combine(this.root, segments));

        // Belt and braces. The segment check above should make this unreachable; it is here because
        // "should be unreachable" is how directory traversal keeps being rediscovered.
        if (!this.IsInsideRoot(candidate))
            return false;

        var info = new FileInfo(candidate);
        if (!info.Exists)
            return false;

        // A link may point anywhere, and the path that reached us said nothing about where.
        if (info.ResolveLinkTarget(returnFinalTarget: true) is { } target && !this.IsInsideRoot(target.FullName))
            return false;

        if (!this.ServeHiddenFiles && info.Attributes.HasFlag(FileAttributes.Hidden))
            return false;

        file = new StaticFile(
            info.Name,
            info.Length,

            // Length plus modification time: cheap to compute, and a file whose both are unchanged
            // is the same file for caching purposes.
            $"\"{info.LastWriteTimeUtc.Ticks:x}-{info.Length:x}\"",
            info.LastWriteTimeUtc,
            _ => new ValueTask<Stream>(new FileStream(
                info.FullName,
                new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.Read,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                }
            ))
        );

        return true;
    }

    bool IsInsideRoot(string fullPath) =>
        fullPath.StartsWith(this.root + Path.DirectorySeparatorChar, StaticFilePath.PathComparison)
        || string.Equals(fullPath, this.root, StaticFilePath.PathComparison);
}

/// <summary>
/// Serves files embedded in an assembly.
/// <para>
/// The one that matters for a packaged app: a MAUI or single-file build has no content directory to
/// point at, so the web assets travel inside the assembly. Resource names are flattened at build
/// time (<c>wwwroot/css/site.css</c> becomes <c>Namespace.wwwroot.css.site.css</c>), so the map is
/// built once by reversing that.
/// </para>
/// <code>
/// &lt;ItemGroup&gt;
///   &lt;EmbeddedResource Include="wwwroot\**" /&gt;
/// &lt;/ItemGroup&gt;
///
/// new EmbeddedFileSource(typeof(App).Assembly, "MyApp.wwwroot")
/// </code>
/// </summary>
public sealed class EmbeddedFileSource : IStaticFileSource
{
    readonly Assembly assembly;
    readonly Dictionary<string, string> resourcesByPath;
    readonly string versionTag;

    public EmbeddedFileSource(Assembly assembly, string? baseNamespace = null)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        this.assembly = assembly;

        var prefix = baseNamespace is { Length: > 0 } ns
            ? (ns.EndsWith('.') ? ns : ns + ".")
            : string.Empty;

        this.resourcesByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            var relative = name[prefix.Length..];

            // Only the separators are ambiguous: every dot but the extension's was a directory
            // separator, and a file named "site.min.css" is indistinguishable from a directory
            // named "site" containing "min.css". Registering both readings costs a dictionary
            // entry and makes either request resolve.
            foreach (var path in CandidatePaths(relative))
                this.resourcesByPath.TryAdd(path, name);
        }

        // Stable per build and cheap: the module version id changes whenever the assembly is
        // rebuilt, which is exactly when embedded content can have changed.
        this.versionTag = assembly.ManifestModule.ModuleVersionId.ToString("n")[..8];
    }

    /// <summary>The resource paths this source can serve, for diagnostics.</summary>
    public IReadOnlyCollection<string> Paths => this.resourcesByPath.Keys;

    public bool TryGetFile(string relativePath, out StaticFile file)
    {
        file = default;

        if (!StaticFilePath.TryNormalize(relativePath, serveHiddenFiles: true, out var normalized))
            return false;

        var lookup = normalized.Replace(Path.DirectorySeparatorChar, '/');

        if (!this.resourcesByPath.TryGetValue(lookup, out var resourceName))
            return false;

        // Opened once to learn the length, which the response needs before any body goes out.
        using var probe = this.assembly.GetManifestResourceStream(resourceName);
        if (probe is null)
            return false;

        var length = probe.Length;
        var name = lookup[(lookup.LastIndexOf('/') + 1)..];

        file = new StaticFile(
            name,
            length,
            $"\"{this.versionTag}-{length:x}\"",

            // No meaningful timestamp: a resource is as old as the assembly, and reporting the
            // build time as the file's modification date invites a stale cache after a rebuild
            // that happened to keep the length. The ETag covers it.
            null,
            _ => new ValueTask<Stream>(
                this.assembly.GetManifestResourceStream(resourceName)
                ?? throw new FileNotFoundException($"The embedded resource '{resourceName}' vanished.")
            )
        );

        return true;
    }

    /// <summary>Every way a flattened resource name could be read back as a path.</summary>
    static IEnumerable<string> CandidatePaths(string resourceSuffix)
    {
        var lastDot = resourceSuffix.LastIndexOf('.');
        if (lastDot <= 0)
        {
            yield return resourceSuffix;
            yield break;
        }

        var stem = resourceSuffix[..lastDot];
        var extension = resourceSuffix[lastDot..];

        // "wwwroot.css.site.css" reads as wwwroot/css/site.css, and also as css/site.css once the
        // base namespace has been stripped — plus "site.min.css" style names, where a dot is part
        // of the file name rather than a separator.
        yield return stem.Replace('.', '/') + extension;

        for (var i = stem.LastIndexOf('.'); i > 0; i = stem.LastIndexOf('.', i - 1))
            yield return stem[..i].Replace('.', '/') + "/" + stem[(i + 1)..] + extension;
    }
}

/// <summary>
/// Tries several sources in order.
/// <para>
/// The useful arrangement during development: a physical directory first so an edited file is
/// picked up without a rebuild, embedded resources behind it so the packaged app still works.
/// </para>
/// </summary>
public sealed class CompositeFileSource(params IStaticFileSource[] sources) : IStaticFileSource
{
    readonly IStaticFileSource[] sources = sources ?? throw new ArgumentNullException(nameof(sources));

    public bool TryGetFile(string relativePath, out StaticFile file)
    {
        foreach (var source in this.sources)
        {
            if (source.TryGetFile(relativePath, out file))
                return true;
        }

        file = default;
        return false;
    }
}

/// <summary>Turning a request path into something safe to look up.</summary>
static class StaticFilePath
{
    /// <summary>
    /// Path comparison follows the platform, because that is what the file system will do. Treating
    /// a case-insensitive file system as case-sensitive is how a containment check passes while the
    /// open succeeds on a different file.
    /// </summary>
    public static readonly StringComparison PathComparison = OperatingSystem.IsLinux()
        ? StringComparison.Ordinal
        : StringComparison.OrdinalIgnoreCase;

    /// <summary>
    /// Validates a decoded request path and returns it as a relative platform path.
    /// <para>
    /// The path arrives already percent-decoded, so <c>%2e%2e%2f</c> is plain <c>../</c> by now —
    /// which is the whole reason the segment check happens here and not on the raw target.
    /// </para>
    /// </summary>
    public static bool TryNormalize(string requestPath, bool serveHiddenFiles, out string relativePath)
    {
        relativePath = string.Empty;

        if (requestPath.Length == 0)
            return false;

        // A null byte truncates the name for some file APIs, making the checked path and the opened
        // path two different things.
        if (requestPath.Contains('\0'))
            return false;

        var builder = new List<string>();

        foreach (var range in requestPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var segment = range;

            if (segment == ".")
                continue;

            if (segment == "..")
                return false;

            if (!serveHiddenFiles && segment.StartsWith('.'))
                return false;

            // A backslash is a separator on Windows, so a segment containing one would smuggle a
            // directory change past the split above.
            if (segment.Contains('\\') || segment.Contains(':'))
                return false;

            if (segment.AsSpan().IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return false;

            builder.Add(segment);
        }

        if (builder.Count == 0)
            return false;

        relativePath = string.Join(Path.DirectorySeparatorChar, builder);
        return true;
    }
}
