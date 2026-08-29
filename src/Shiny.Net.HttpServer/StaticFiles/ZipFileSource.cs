using System.IO.Compression;
using System.Reflection;

namespace Shiny.Net.HttpServer.StaticFiles;

/// <summary>
/// Serves files out of a zip archive, either on disk or embedded in an assembly.
/// <para>
/// The case this exists for is a packaged app whose web assets are a published Blazor folder: a few
/// thousand files, most of them small. As loose embedded resources that is a few thousand entries in
/// the manifest and a name-mangling scheme to reverse (see <see cref="EmbeddedFileSource"/>); as one
/// zipped resource it is a single entry, the paths survive intact, and the assets stay compressed
/// in the binary instead of being inflated into it.
/// </para>
/// <code>
/// &lt;ItemGroup&gt;
///   &lt;EmbeddedResource Include="wwwroot.zip" LogicalName="MyApp.wwwroot.zip" /&gt;
/// &lt;/ItemGroup&gt;
///
/// new ZipFileSource(typeof(App).Assembly, "MyApp.wwwroot.zip")
/// new ZipFileSource("./content/site.zip")
/// </code>
/// <para>
/// The archive is never held open. The entry index is read once here, and each response opens its
/// own <see cref="ZipArchive"/> over its own stream — which is what makes one source safe to read
/// from any number of requests at once. An embedded archive costs nothing to reopen: the resource
/// stream is a window onto the already-mapped assembly image, not a copy of it.
/// </para>
/// </summary>
public sealed class ZipFileSource : IStaticFileSource, IPrecompressedFileSource
{
    readonly Func<Stream> open;
    readonly Dictionary<string, ZipEntryInfo> entries;

    /// <summary>
    /// Serves an archive on disk.
    /// </summary>
    /// <param name="zipPath">Path to the <c>.zip</c>. Read on every response, so it has to stay put.</param>
    /// <param name="basePath">
    /// Directory inside the archive to serve from, for an archive zipped with its parent folder —
    /// <c>"wwwroot"</c> serves <c>wwwroot/css/site.css</c> as <c>/css/site.css</c>. Null serves from
    /// the archive root.
    /// </param>
    public ZipFileSource(string zipPath, string? basePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);

        var fullPath = Path.GetFullPath(zipPath);

        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"No zip archive at '{fullPath}'.", fullPath);

        // No FileOptions.Asynchronous: ZipArchive reads its central directory synchronously, and an
        // async handle makes every one of those reads a thread-pool round trip. RandomAccess is the
        // honest hint - the directory is at the end of the file and the entries are wherever.
        this.open = () => new FileStream(
            fullPath,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read | FileShare.Delete,
                Options = FileOptions.RandomAccess
            }
        );

        this.entries = BuildIndex(this.open, basePath);
    }

    /// <summary>
    /// Serves an archive embedded in an assembly.
    /// </summary>
    /// <param name="assembly">The assembly holding the resource.</param>
    /// <param name="resourceName">
    /// The resource's manifest name. That is the <c>LogicalName</c> when the item declares one, and
    /// otherwise the default namespace plus the path with separators turned into dots.
    /// </param>
    /// <param name="basePath">Directory inside the archive to serve from. Null serves from its root.</param>
    public ZipFileSource(Assembly assembly, string resourceName, string? basePath = null)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);

        this.open = () => assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException(
                $"No embedded resource '{resourceName}' in {assembly.GetName().Name}. "
                + $"It has: {string.Join(", ", assembly.GetManifestResourceNames())}"
            );

        this.entries = BuildIndex(this.open, basePath);
    }

    /// <summary>
    /// Codings to look for as sidecar entries, best first — <c>app.js.br</c> beside <c>app.js</c>.
    /// <para>
    /// Empty by default, and set for the same reason as on <see cref="PhysicalFileSource"/>: a
    /// zipped Blazor publish carries the precompressed variants of every asset, already compressed
    /// at maximum effort. Note that they are stored, not deflated — compressing an already
    /// compressed file only makes it bigger — so serving one is a straight copy out of the archive.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> PrecompressedEncodings { get; init; } = [];

    /// <summary>The paths this archive can serve, for diagnostics.</summary>
    public IReadOnlyCollection<string> Paths => this.entries.Keys;

    public bool TryGetFile(string relativePath, out StaticFile file)
        => this.TryGetFile(relativePath, acceptedEncodings: null, out file);

    /// <summary>
    /// Resolves a path, preferring a precompressed sidecar the client can decode.
    /// </summary>
    public bool TryGetFile(string relativePath, IReadOnlyList<string>? acceptedEncodings, out StaticFile file)
    {
        file = default;

        // Hidden files are served: an archive is a curated artifact rather than a directory that
        // happens to be on the machine, so there is no .env or .git in it to protect. Same reading
        // as EmbeddedFileSource, and for the same reason.
        if (!StaticFilePath.TryNormalize(relativePath, serveHiddenFiles: true, out var normalized))
            return false;

        var lookup = normalized.Replace(Path.DirectorySeparatorChar, '/');

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

                if (suffix is null || !this.entries.TryGetValue(lookup + suffix, out var sidecar))
                    continue;

                // Named after the original, so the content type describes what the bytes
                // decompress to rather than the container they arrived in.
                file = this.ToStaticFile(sidecar, NameOf(lookup), encoding);
                return true;
            }
        }

        if (!this.entries.TryGetValue(lookup, out var entry))
            return false;

        file = this.ToStaticFile(entry, NameOf(lookup), contentEncoding: null);
        return true;
    }

    StaticFile ToStaticFile(ZipEntryInfo entry, string name, string? contentEncoding) =>
        new(
            name,
            entry.Length,

            // The archive already computed a checksum of the content, so the ETag is derived from
            // the bytes rather than from when they were written. That survives a rebuild that
            // produces identical content, which a timestamp does not.
            $"\"{entry.Crc32:x8}-{entry.Length:x}\"",
            entry.LastWriteTime,
            _ => new ValueTask<Stream>(this.OpenEntry(entry.FullName)),
            contentEncoding
        );

    Stream OpenEntry(string fullName)
    {
        var archive = OpenArchive(this.open);

        try
        {
            var entry = archive.GetEntry(fullName)
                ?? throw new FileNotFoundException($"The entry '{fullName}' is no longer in the archive.", fullName);

            // The archive owns the entry stream, so it has to outlive it - and nothing else is
            // holding either, so disposing what the response read disposes the whole chain.
            return new ArchiveEntryStream(entry.Open(), archive);
        }
        catch
        {
            archive.Dispose();
            throw;
        }
    }

    static string NameOf(string path) => path[(path.LastIndexOf('/') + 1)..];

    /// <summary>
    /// Opens the archive, closing the stream if it turns out not to be one. The constructor reads
    /// the central directory and throws on anything it cannot make sense of - and it does not take
    /// ownership of the stream until it has succeeded, so without this a corrupt or truncated
    /// archive leaks a file handle per attempt.
    /// </summary>
    static ZipArchive OpenArchive(Func<Stream> open)
    {
        var stream = open();

        try
        {
            return new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    static Dictionary<string, ZipEntryInfo> BuildIndex(Func<Stream> open, string? basePath)
    {
        var prefix = basePath is { Length: > 0 }
            ? basePath.Replace('\\', '/').Trim('/') + "/"
            : string.Empty;

        var index = new Dictionary<string, ZipEntryInfo>(StringComparer.OrdinalIgnoreCase);

        using var archive = OpenArchive(open);

        foreach (var entry in archive.Entries)
        {
            // A directory entry is a name ending in a separator with no content. Zips written by
            // some tools have them and some do not, and neither is a file to serve.
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                continue;

            var path = entry.FullName.Replace('\\', '/').TrimStart('/');

            if (prefix.Length > 0)
            {
                if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                path = path[prefix.Length..];
            }

            if (path.Length == 0)
                continue;

            // TryAdd, so a zip holding two entries whose names differ only in case keeps the first
            // rather than throwing at construction. Lookup is case-insensitive because that is what
            // a URL author will expect and what EmbeddedFileSource already does.
            index.TryAdd(path, new ZipEntryInfo(entry.FullName, entry.Length, entry.Crc32, entry.LastWriteTime));
        }

        return index;
    }

    /// <summary>What the index keeps, so a lookup never opens the archive.</summary>
    readonly record struct ZipEntryInfo(string FullName, long Length, uint Crc32, DateTimeOffset LastWriteTime);

    /// <summary>
    /// A zip entry's content, holding its archive open behind it.
    /// <para>
    /// Not seekable, because a deflated entry genuinely is not: the bytes only exist as they are
    /// read. A range request over one is served by reading and discarding up to the start, which is
    /// what <c>FileDownloadResult</c> already does for any stream that says it cannot seek.
    /// </para>
    /// </summary>
    sealed class ArchiveEntryStream(Stream inner, IDisposable archive) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                archive.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            archive.Dispose();

            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}
