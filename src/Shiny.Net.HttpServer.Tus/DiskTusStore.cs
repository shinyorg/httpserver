using System.Buffers;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Shiny.Net.HttpServer.Tus;

/// <summary>
/// Uploads as files in one directory: <c>{id}</c> holds the bytes, <c>{id}.info</c> what the client
/// said about them.
/// <para>
/// The offset is not stored anywhere - it <em>is</em> the data file's length. That is what makes
/// resuming survive a restart, a crash, or the OS killing a backgrounded app: there is no separate
/// counter to fall out of step with the bytes, so whatever reached the file is exactly where the
/// client is told to carry on from. Each chunk is handed to the OS as it is written, so a process
/// that dies mid-request loses nothing it had already been sent; and the file is flushed to the
/// disk itself before a <c>PATCH</c> is acknowledged, so a power cut cannot take back an offset the
/// client has already been told.
/// </para>
/// <para>
/// The info file is small and rewritten whole through a temporary file, flushed to disk, then
/// renamed, with the directory flushed after where the platform allows - so a crash or a power cut
/// leaves the old one or the new one and never half of each, or an empty one. Data with no readable
/// info beside it (a crash between the two in creation, or an info file lost anyway) is deleted by
/// the expiry sweep once it is older than the endpoint's <see cref="TusOptions.Expiration"/>.
/// </para>
/// <code>
/// app.MapTus("/files", o => o.Store = new DiskTusStore(Path.Combine(FileSystem.AppDataDirectory, "uploads")));
/// </code>
/// </summary>
public sealed class DiskTusStore : ITusStore
{
    const int CopyBufferSize = 81920;
    const string InfoExtension = ".info";
    const string TempExtension = ".tmp";
    const string ConcatExtension = ".concat";

    /// <summary>Stores uploads under <paramref name="directory"/>, creating it if needed.</summary>
    public DiskTusStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        this.Directory = Path.GetFullPath(directory);
        System.IO.Directory.CreateDirectory(this.Directory);
    }

    /// <summary>The directory uploads are kept in.</summary>
    public string Directory { get; }

    /// <summary>
    /// How old a data file with no readable info beside it has to be before the sweep deletes it.
    /// Set from <see cref="TusOptions.Expiration"/> by the endpoint the store is mounted on; null
    /// (no expiration) leaves such files alone, as it leaves every upload.
    /// </summary>
    internal TimeSpan? OrphanLifetime { get; set; }

    /// <summary>
    /// Where a completed upload's bytes are. Handy in <see cref="TusOptions.OnUploadCompleteAsync"/>
    /// for moving the file somewhere permanent rather than copying it.
    /// </summary>
    public string GetFilePath(string id) => this.DataPath(id);

    public async ValueTask<string> CreateAsync(TusCreateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var id = Guid.NewGuid().ToString("N");

        // Data first, info second: the info file is what makes an upload exist, so a crash between
        // the two leaves an orphaned empty file rather than an upload with nowhere to put its bytes.
        await using (new FileStream(this.DataPath(id), FileMode.CreateNew, FileAccess.Write).ConfigureAwait(false))
        {
        }

        await this.WriteInfoAsync(id, new Info(
            request.Length,
            request.Metadata.Header,
            DateTimeOffset.UtcNow,
            request.ExpiresUtc,
            request.Concatenation,
            request.PartialUploads,
            request.Owner
        ), cancellationToken).ConfigureAwait(false);

        return id;
    }

    public async ValueTask<TusUpload?> GetAsync(string id, CancellationToken cancellationToken)
    {
        if (!IsValidId(id))
            return null;

        var info = await this.ReadInfoAsync(id, cancellationToken).ConfigureAwait(false);
        if (info is null)
            return null;

        var data = new FileInfo(this.DataPath(id));
        if (!data.Exists)
            return null;

        return new TusUpload
        {
            Id = id,
            Length = info.Length,
            Offset = data.Length,
            Metadata = TusMetadata.TryParse(info.Metadata, out var metadata) ? metadata : TusMetadata.Empty,
            CreatedUtc = info.CreatedUtc,
            ExpiresUtc = info.ExpiresUtc,
            Concatenation = info.Concatenation,
            PartialUploads = info.Parts,
            Owner = info.Owner
        };
    }

    public async ValueTask<long> AppendAsync(string id, long offset, Stream data, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);
        this.RequireValid(id);

        FileStream file;

        try
        {
            // Unbuffered, so a write is handed straight to the OS rather than sitting in a managed
            // buffer that dies with the process.
            file = new FileStream(this.DataPath(id), FileMode.Open, FileAccess.Write, FileShare.Read, bufferSize: 0, useAsync: true);
        }
        catch (FileNotFoundException)
        {
            throw new TusException(StatusCodes.Status404NotFound, "The upload does not exist.");
        }

        await using (file.ConfigureAwait(false))
        {
            if (file.Length != offset)
                throw new TusException(
                    StatusCodes.Status409Conflict,
                    $"Upload-Offset {offset} does not match the {file.Length} bytes already stored."
                );

            file.Seek(0, SeekOrigin.End);

            var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);

            try
            {
                while (true)
                {
                    var read = await data.ReadAsync(buffer.AsMemory(0, CopyBufferSize), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                        break;

                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }

                // To the disk, not just the OS: the offset returned here is what the client is told
                // it can resume from, and the OS cache does not survive a power cut. Once per PATCH,
                // never per buffer - the cost is one flush, however large the request.
                file.Flush(flushToDisk: true);
            }
            catch
            {
                // The stream threw, so this append does not count - see ITusStore.AppendAsync. Back
                // to where it started, as though it had never been sent.
                file.SetLength(offset);
                throw;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            return file.Length;
        }
    }

    public async ValueTask SetLengthAsync(string id, long length, CancellationToken cancellationToken)
    {
        var info = await this.RequireInfoAsync(id, cancellationToken).ConfigureAwait(false);
        await this.WriteInfoAsync(id, info with { Length = length }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SetExpirationAsync(string id, DateTimeOffset? expiresUtc, CancellationToken cancellationToken)
    {
        var info = await this.RequireInfoAsync(id, cancellationToken).ConfigureAwait(false);
        await this.WriteInfoAsync(id, info with { ExpiresUtc = expiresUtc }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask ConcatenateAsync(string id, IReadOnlyList<string> partialIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(partialIds);
        this.RequireValid(id);

        // Assembled beside the final upload and renamed over it, so a crash half way leaves an empty
        // final upload rather than one that looks partly done - a final upload cannot be PATCHed, so
        // the partly-done one could never be finished.
        var staging = this.DataPath(id) + ".concat";

        try
        {
            var target = new FileStream(staging, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize, useAsync: true);

            await using (target.ConfigureAwait(false))
            {
                foreach (var part in partialIds)
                {
                    this.RequireValid(part);

                    await using var source = new FileStream(this.DataPath(part), FileMode.Open, FileAccess.Read, FileShare.ReadWrite, CopyBufferSize, useAsync: true);
                    await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
                }

                target.Flush(flushToDisk: true);
            }

            File.Move(staging, this.DataPath(id), overwrite: true);
            FlushDirectory(this.Directory);
        }
        finally
        {
            File.Delete(staging);
        }
    }

    public ValueTask<Stream> OpenReadAsync(string id, CancellationToken cancellationToken)
    {
        this.RequireValid(id);

        Stream stream = new FileStream(
            this.DataPath(id),
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            CopyBufferSize,
            useAsync: true
        );

        return ValueTask.FromResult(stream);
    }

    public ValueTask<bool> DeleteAsync(string id, CancellationToken cancellationToken)
    {
        if (!IsValidId(id))
            return ValueTask.FromResult(false);

        // Info first, for the same reason it is written last: once it is gone the upload is gone,
        // whatever happens to the data file after.
        var info = new FileInfo(this.InfoPath(id));
        var data = new FileInfo(this.DataPath(id));
        var existed = info.Exists || data.Exists;

        if (info.Exists)
            info.Delete();

        data.Delete();

        // Leftovers of a write that never finished, which belong to nothing once the upload is gone.
        File.Delete(this.InfoPath(id) + TempExtension);
        File.Delete(this.DataPath(id) + ConcatExtension);

        return ValueTask.FromResult(existed);
    }

    public async ValueTask<IReadOnlyList<string>> GetExpiredAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var expired = new List<string>();

        // Every file, not just the info files: a data file whose info never made it to disk - or
        // did not survive - is still taking up space, and nothing else will ever find it.
        var ids = System.IO.Directory.EnumerateFiles(this.Directory)
            .Select(path => IdOf(Path.GetFileName(path)))
            .Where(IsValidId)
            .Distinct(StringComparer.Ordinal);

        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var info = await this.ReadInfoAsync(id, cancellationToken).ConfigureAwait(false);

            if (info is null ? this.IsAbandonedOrphan(id, nowUtc) : IsExpired(info, this.DataPath(id), nowUtc))
                expired.Add(id);
        }

        return expired;
    }

    static bool IsExpired(Info info, string dataPath, DateTimeOffset nowUtc)
    {
        if (info.ExpiresUtc is not { } expires || expires >= nowUtc)
            return false;

        // A finished upload is the application's now, and expiry was only ever about the
        // unfinished ones cluttering the disk.
        var data = new FileInfo(dataPath);

        return !(info.Length is { } length && data.Exists && data.Length >= length);
    }

    /// <summary>
    /// An upload with no readable info: its files are kept for one expiration period after they were
    /// last touched - long enough that one being created right now, data first, is never caught
    /// between its two writes - and swept after that.
    /// </summary>
    bool IsAbandonedOrphan(string id, DateTimeOffset nowUtc)
    {
        if (this.OrphanLifetime is not { } lifetime)
            return false;

        var newest = new[] { this.DataPath(id), this.InfoPath(id), this.InfoPath(id) + TempExtension, this.DataPath(id) + ConcatExtension }
            .Select(path => new FileInfo(path))
            .Where(file => file.Exists)
            .Select(file => new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero))
            .DefaultIfEmpty(DateTimeOffset.MaxValue)
            .Max();

        return newest != DateTimeOffset.MaxValue && newest + lifetime < nowUtc;
    }

    /// <summary>The upload a file in the directory belongs to: its name up to the first dot.</summary>
    static string IdOf(string fileName)
    {
        var dot = fileName.IndexOf('.');
        return dot < 0 ? fileName : fileName[..dot];
    }

    // ---- files ----

    /// <summary>
    /// Identifiers are generated here, but they come back in through a URL - so every one is checked
    /// before it is joined to a path, and nothing with a separator or a dot in it ever is.
    /// </summary>
    internal static bool IsValidId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 128)
            return false;

        foreach (var c in id)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')
                return false;
        }

        return true;
    }

    void RequireValid(string id)
    {
        if (!IsValidId(id))
            throw new TusException(StatusCodes.Status404NotFound, "The upload does not exist.");
    }

    string DataPath(string id) => Path.Combine(this.Directory, id);

    string InfoPath(string id) => Path.Combine(this.Directory, id + InfoExtension);

    sealed record Info(
        long? Length,
        string Metadata,
        DateTimeOffset CreatedUtc,
        DateTimeOffset? ExpiresUtc,
        TusConcatenation Concatenation,
        IReadOnlyList<string> Parts,
        string? Owner
    );

    async ValueTask<Info> RequireInfoAsync(string id, CancellationToken cancellationToken)
    {
        this.RequireValid(id);

        return await this.ReadInfoAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new TusException(StatusCodes.Status404NotFound, "The upload does not exist.");
    }

    async ValueTask<Info?> ReadInfoAsync(string id, CancellationToken cancellationToken)
    {
        byte[] bytes;

        try
        {
            bytes = await File.ReadAllBytesAsync(this.InfoPath(id), cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }

        // Read by hand rather than deserialised, which keeps the package free of reflection and of
        // a source-generated context for five fields.
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;

            long? length = root.TryGetProperty("length", out var l) && l.ValueKind == JsonValueKind.Number
                ? l.GetInt64()
                : null;

            DateTimeOffset? expires = root.TryGetProperty("expires", out var e) && e.ValueKind == JsonValueKind.String
                ? DateTimeOffset.Parse(e.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
                : null;

            var concat = root.TryGetProperty("concat", out var c) ? c.GetString() : null;
            var parts = new List<string>();

            if (root.TryGetProperty("parts", out var p) && p.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in p.EnumerateArray())
                    parts.Add(part.GetString()!);
            }

            return new Info(
                length,
                root.TryGetProperty("metadata", out var m) ? m.GetString() ?? string.Empty : string.Empty,
                DateTimeOffset.Parse(root.GetProperty("created").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                expires,
                concat switch
                {
                    "partial" => TusConcatenation.Partial,
                    "final" => TusConcatenation.Final,
                    _ => TusConcatenation.None
                },
                parts,
                root.TryGetProperty("owner", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString() : null
            );
        }
        catch (Exception ex) when (ex is JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
        {
            // An info file nobody can read describes an upload nobody can resume. Treated as absent,
            // so the client gets a 404 and starts over rather than a 500 on every attempt.
            return null;
        }
    }

    async ValueTask WriteInfoAsync(string id, Info info, CancellationToken cancellationToken)
    {
        var buffer = new ArrayBufferWriter<byte>(256);

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();

            if (info.Length is { } length)
                writer.WriteNumber("length", length);
            else
                writer.WriteNull("length");

            writer.WriteString("metadata", info.Metadata);
            writer.WriteString("created", info.CreatedUtc.ToString("O", CultureInfo.InvariantCulture));

            if (info.ExpiresUtc is { } expires)
                writer.WriteString("expires", expires.ToString("O", CultureInfo.InvariantCulture));

            if (info.Concatenation != TusConcatenation.None)
                writer.WriteString("concat", info.Concatenation == TusConcatenation.Partial ? "partial" : "final");

            if (info.Owner is { } owner)
                writer.WriteString("owner", owner);

            if (info.Parts.Count > 0)
            {
                writer.WriteStartArray("parts");
                foreach (var part in info.Parts)
                    writer.WriteStringValue(part);
                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        var path = this.InfoPath(id);
        var temp = path + TempExtension;

        var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 0, useAsync: true);

        await using (file.ConfigureAwait(false))
        {
            await file.WriteAsync(buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);

            // Before the rename, not after: a rename can reach the disk ahead of the data it points
            // at, and what a power cut leaves then is an info file of the right name and no content -
            // an upload that 404s, with its bytes orphaned beside it.
            file.Flush(flushToDisk: true);
        }

        File.Move(temp, path, overwrite: true);

        // And the rename itself, which lives in the directory rather than in either file.
        FlushDirectory(this.Directory);
    }

    /// <summary>
    /// Flushes a directory's entries to disk, so a rename or a new file in it survives a power cut.
    /// Best effort: only Unix-like systems can (Windows offers no handle to a directory to flush,
    /// and NTFS journals the metadata anyway), and a platform where it fails once is not asked again.
    /// </summary>
    static void FlushDirectory(string directory)
    {
        if (OperatingSystem.IsWindows() || !directoryFlushWorks)
            return;

        try
        {
            var fd = Native.Open(Encoding.UTF8.GetBytes(directory + "\0"), 0 /* O_RDONLY */);

            if (fd >= 0)
            {
                Native.Fsync(fd);
                Native.Close(fd);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            directoryFlushWorks = false;
        }
    }

    static bool directoryFlushWorks = true;

    /// <summary>
    /// The three libc calls a directory flush needs, which .NET does not wrap: it refuses to open a
    /// directory as a file. Blittable arguments only, so there is no marshalling to trim or to
    /// generate.
    /// </summary>
    static class Native
    {
        [DllImport("libc", EntryPoint = "open")]
        public static extern int Open(byte[] path, int flags);

        [DllImport("libc", EntryPoint = "fsync")]
        public static extern int Fsync(int fd);

        [DllImport("libc", EntryPoint = "close")]
        public static extern int Close(int fd);
    }
}
