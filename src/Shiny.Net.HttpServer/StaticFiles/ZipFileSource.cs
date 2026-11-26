using System.Buffers.Binary;
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
/// The archive is never held open. The entry index - names, sizes, and where each entry's bytes sit
/// in the archive - is read once here, and each response opens its own stream and reads just that
/// entry, never the central directory again. That is what makes one source safe to read from any
/// number of requests at once. An embedded archive costs nothing to reopen: the resource stream is a
/// window onto the already-mapped assembly image, not a copy of it.
/// </para>
/// <para>
/// A stored (uncompressed) entry is served as a seekable window onto the archive, so a range
/// request - a video scrubbed half way - starts at the byte it asked for. A deflated entry can only
/// be read from its start, so a range over one still inflates and discards everything before it;
/// store large media uncompressed (<c>zip -0</c>, or <c>CompressionLevel.NoCompression</c>) if it
/// will be seeked.
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
            _ => new ValueTask<Stream>(this.OpenEntry(entry)),
            contentEncoding
        );

    Stream OpenEntry(ZipEntryInfo entry)
        => this.TryOpenDirect(entry) ?? this.OpenThroughArchive(entry.FullName);

    /// <summary>
    /// Opens a stored or deflated entry straight from its offset, using the layout read with the
    /// index. Null when there is no usable layout - an encrypted entry, a compression method only
    /// <see cref="ZipArchive"/> understands, a stream that cannot seek, or an archive whose local
    /// header is not where the directory said - and the archive is opened the ordinary way instead.
    /// </summary>
    Stream? TryOpenDirect(ZipEntryInfo entry)
    {
        if (entry.Layout is not { Encrypted: false } layout || layout.Method is not (StoredMethod or DeflateMethod))
            return null;

        var stream = this.open();

        try
        {
            if (!stream.CanSeek)
            {
                stream.Dispose();
                return null;
            }

            var dataOffset = entry.DataOffset;

            if (dataOffset < 0)
            {
                dataOffset = ReadDataOffset(stream, layout.LocalHeaderOffset);
                entry.DataOffset = dataOffset;
            }

            if (dataOffset < 0 || dataOffset + layout.CompressedSize > stream.Length)
            {
                stream.Dispose();
                return null;
            }

            var window = new WindowStream(stream, dataOffset, layout.CompressedSize);

            return layout.Method == StoredMethod
                ? window
                : new DeflateStream(window, CompressionMode.Decompress, leaveOpen: false);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    Stream OpenThroughArchive(string fullName)
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

        // ZipArchive keeps where each entry starts to itself, so the central directory is read a
        // second time here for just that. Its records are in the same order as Entries; if the two
        // disagree on how many there are, nothing is trusted and every entry goes the slow way.
        var layouts = ReadLayouts(open);

        if (layouts is not null && layouts.Count != archive.Entries.Count)
            layouts = null;

        var servable = archive.Entries
            .Select((entry, i) => (Entry: entry, Layout: layouts?[i], Path: entry.FullName.Replace('\\', '/').TrimStart('/')))

            // A directory entry is a name ending in a separator with no content. Zips written by
            // some tools have them and some do not, and neither is a file to serve.
            .Where(x => !x.Entry.FullName.EndsWith('/') && !x.Entry.FullName.EndsWith('\\'))
            .Where(x => prefix.Length == 0 || x.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(x => x with { Path = x.Path[prefix.Length..] })
            .Where(x => x.Path.Length > 0);

        foreach (var (entry, layout, path) in servable)
        {
            // TryAdd, so a zip holding two entries whose names differ only in case keeps the first
            // rather than throwing at construction. Lookup is case-insensitive because that is what
            // a URL author will expect and what EmbeddedFileSource already does.
            index.TryAdd(path, new ZipEntryInfo(entry.FullName, entry.Length, entry.Crc32, entry.LastWriteTime, layout));
        }

        return index;
    }

    const ushort StoredMethod = 0;
    const ushort DeflateMethod = 8;

    /// <summary>Where an entry's bytes are and how they are packed, from its central directory record.</summary>
    readonly record struct ZipLayout(ushort Method, bool Encrypted, long CompressedSize, long LocalHeaderOffset);

    /// <summary>What the index keeps, so a lookup never opens the archive.</summary>
    sealed class ZipEntryInfo(string fullName, long length, uint crc32, DateTimeOffset lastWriteTime, ZipLayout? layout)
    {
        long dataOffset = -1;

        public string FullName { get; } = fullName;
        public long Length { get; } = length;
        public uint Crc32 { get; } = crc32;
        public DateTimeOffset LastWriteTime { get; } = lastWriteTime;
        public ZipLayout? Layout { get; } = layout;

        /// <summary>
        /// Where the entry's bytes start, past its local header, or -1 until the first response
        /// reads that header. Racing requests compute the same number, so a plain field will do.
        /// </summary>
        public long DataOffset
        {
            get => Volatile.Read(ref this.dataOffset);
            set => Volatile.Write(ref this.dataOffset, value);
        }
    }

    /// <summary>
    /// The central directory's records, in order: just the fields <see cref="ZipArchive"/> does not
    /// expose. ZIP64 sizes and offsets are followed. Null for anything unexpected, which only costs
    /// the fast path.
    /// </summary>
    static List<ZipLayout>? ReadLayouts(Func<Stream> open)
    {
        using var stream = open();

        if (!stream.CanSeek)
            return null;

        try
        {
            return ReadLayouts(stream);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    static List<ZipLayout>? ReadLayouts(Stream stream)
    {
        const int EndRecordSize = 22;
        const uint EndSignature = 0x06054b50;
        const uint Zip64LocatorSignature = 0x07064b50;
        const uint Zip64EndSignature = 0x06064b50;
        const uint CentralSignature = 0x02014b50;
        const int CentralRecordSize = 46;

        var length = stream.Length;

        // The end record sits in the last 22 bytes plus however long the archive comment is, up to
        // 64 KB, so it is searched for backwards.
        var tail = ReadAt(stream, Math.Max(0, length - (EndRecordSize + ushort.MaxValue)), (int)Math.Min(length, EndRecordSize + ushort.MaxValue));
        var end = -1;

        for (var i = tail.Length - EndRecordSize; i >= 0 && end < 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) == EndSignature)
                end = i;
        }

        if (end < 0)
            return null;

        long count = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(end + 10));
        long size = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(end + 12));
        long offset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(end + 16));

        if (count == ushort.MaxValue || size == uint.MaxValue || offset == uint.MaxValue)
        {
            var locator = end - 20;

            if (locator < 0 || BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(locator)) != Zip64LocatorSignature)
                return null;

            var zip64End = ReadAt(stream, checked((long)BinaryPrimitives.ReadUInt64LittleEndian(tail.AsSpan(locator + 8))), 56);

            if (BinaryPrimitives.ReadUInt32LittleEndian(zip64End) != Zip64EndSignature)
                return null;

            count = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(zip64End.AsSpan(32)));
            size = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(zip64End.AsSpan(40)));
            offset = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(zip64End.AsSpan(48)));
        }

        if (size > int.MaxValue || offset + size > length)
            return null;

        var directory = ReadAt(stream, offset, (int)size);
        var layouts = new List<ZipLayout>();
        var position = 0;

        while (position + CentralRecordSize <= directory.Length && layouts.Count < count)
        {
            var record = directory.AsSpan(position);

            if (BinaryPrimitives.ReadUInt32LittleEndian(record) != CentralSignature)
                return null;

            var flags = BinaryPrimitives.ReadUInt16LittleEndian(record[8..]);
            var method = BinaryPrimitives.ReadUInt16LittleEndian(record[10..]);
            long compressed = BinaryPrimitives.ReadUInt32LittleEndian(record[20..]);
            long uncompressed = BinaryPrimitives.ReadUInt32LittleEndian(record[24..]);
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(record[28..]);
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(record[30..]);
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(record[32..]);
            long localOffset = BinaryPrimitives.ReadUInt32LittleEndian(record[42..]);

            var extraStart = position + CentralRecordSize + nameLength;
            var extraEnd = extraStart + extraLength;

            if (extraEnd > directory.Length)
                return null;

            // The ZIP64 extra field carries, in this order, whichever of the three values did not
            // fit in 32 bits - and only those.
            var extra = extraStart;

            while (extra + 4 <= extraEnd)
            {
                var tag = BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(extra));
                var fieldSize = BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(extra + 2));
                var field = extra + 4;
                var fieldEnd = Math.Min(field + fieldSize, extraEnd);
                var next = field + fieldSize;

                if (tag == 0x0001)
                {
                    if (uncompressed == uint.MaxValue && field + 8 <= fieldEnd)
                    {
                        uncompressed = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(directory.AsSpan(field)));
                        field += 8;
                    }

                    if (compressed == uint.MaxValue && field + 8 <= fieldEnd)
                    {
                        compressed = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(directory.AsSpan(field)));
                        field += 8;
                    }

                    if (localOffset == uint.MaxValue && field + 8 <= fieldEnd)
                        localOffset = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(directory.AsSpan(field)));
                }

                extra = next;
            }

            // A stored entry is its own bytes; sizes that disagree mean something this parser did
            // not understand, and the entry is left to ZipArchive.
            var usable = method != StoredMethod || compressed == uncompressed;

            layouts.Add(new ZipLayout(usable ? method : ushort.MaxValue, (flags & 1) != 0, compressed, localOffset));
            position = extraEnd + commentLength;
        }

        return layouts.Count == count ? layouts : null;
    }

    /// <summary>
    /// Where an entry's bytes begin: after its 30-byte local header and that header's own name and
    /// extra field, which need not match the central directory's. -1 when no local header is there.
    /// </summary>
    static long ReadDataOffset(Stream stream, long localHeaderOffset)
    {
        const uint LocalSignature = 0x04034b50;

        if (localHeaderOffset < 0 || localHeaderOffset + 30 > stream.Length)
            return -1;

        var header = ReadAt(stream, localHeaderOffset, 30);

        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != LocalSignature)
            return -1;

        return localHeaderOffset + 30
            + BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(26))
            + BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28));
    }

    static byte[] ReadAt(Stream stream, long offset, int count)
    {
        var buffer = new byte[count];

        stream.Seek(offset, SeekOrigin.Begin);
        stream.ReadExactly(buffer);

        return buffer;
    }

    /// <summary>
    /// A read-only, seekable window onto part of a stream it owns: a stored entry's bytes inside the
    /// archive. Seekable is the point - the download result seeks straight to a range's start.
    /// </summary>
    sealed class WindowStream(Stream inner, long start, long length) : Stream
    {
        long position;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;

        public override long Position
        {
            get => this.position;
            set => this.Seek(value, SeekOrigin.Begin);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            var target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => this.position + offset,
                _ => length + offset
            };

            ArgumentOutOfRangeException.ThrowIfNegative(target, nameof(offset));

            this.position = target;
            return target;
        }

        /// <summary>How much of a read of <paramref name="count"/> bytes is still inside the window.</summary>
        int Take(int count)
        {
            var left = length - this.position;

            if (left <= 0)
                return 0;

            if (inner.Position != start + this.position)
                inner.Position = start + this.position;

            return (int)Math.Min(count, left);
        }

        public override int Read(byte[] buffer, int offset, int count) => this.Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var take = this.Take(buffer.Length);
            var read = take == 0 ? 0 : inner.Read(buffer[..take]);

            this.position += read;
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => this.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var take = this.Take(buffer.Length);
            var read = take == 0 ? 0 : await inner.ReadAsync(buffer[..take], cancellationToken).ConfigureAwait(false);

            this.position += read;
            return read;
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }

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
