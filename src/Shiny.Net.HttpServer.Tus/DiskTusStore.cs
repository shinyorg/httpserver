using System.Buffers;
using System.Globalization;
using System.Text.Json;

namespace Shiny.Net.HttpServer.Tus;

/// <summary>
/// Uploads as files in one directory: <c>{id}</c> holds the bytes, <c>{id}.info</c> what the client
/// said about them.
/// <para>
/// The offset is not stored anywhere - it <em>is</em> the data file's length. That is what makes
/// resuming survive a restart, a crash, or the OS killing a backgrounded app: there is no separate
/// counter to fall out of step with the bytes, so whatever reached the file is exactly where the
/// client is told to carry on from. Each chunk is flushed to the OS as it is written, so a process
/// that dies mid-request loses nothing it had already been sent.
/// </para>
/// <para>
/// The info file is small and rewritten whole through a temporary file and a rename, so a crash
/// leaves the old one or the new one and never half of each.
/// </para>
/// <code>
/// app.MapTus("/files", o => o.Store = new DiskTusStore(Path.Combine(FileSystem.AppDataDirectory, "uploads")));
/// </code>
/// </summary>
public sealed class DiskTusStore : ITusStore
{
    const int CopyBufferSize = 81920;
    const string InfoExtension = ".info";

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
            request.PartialUploads
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
            PartialUploads = info.Parts
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

                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
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

                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(staging, this.DataPath(id), overwrite: true);
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
        var existed = info.Exists;

        if (existed)
            info.Delete();

        File.Delete(this.DataPath(id));

        return ValueTask.FromResult(existed);
    }

    public async ValueTask<IReadOnlyList<string>> GetExpiredAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var expired = new List<string>();

        foreach (var path in System.IO.Directory.EnumerateFiles(this.Directory, "*" + InfoExtension))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var id = Path.GetFileNameWithoutExtension(path);
            if (!IsValidId(id))
                continue;

            var info = await this.ReadInfoAsync(id, cancellationToken).ConfigureAwait(false);
            if (info?.ExpiresUtc is not { } expires || expires >= nowUtc)
                continue;

            // A finished upload is the application's now, and expiry was only ever about the
            // unfinished ones cluttering the disk.
            var data = new FileInfo(this.DataPath(id));
            if (info.Length is { } length && data.Exists && data.Length >= length)
                continue;

            expired.Add(id);
        }

        return expired;
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
        IReadOnlyList<string> Parts
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
                parts
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
        var temp = path + ".tmp";

        await File.WriteAllBytesAsync(temp, buffer.WrittenMemory.ToArray(), cancellationToken).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }
}
