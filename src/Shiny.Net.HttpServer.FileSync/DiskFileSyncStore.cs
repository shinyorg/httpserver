using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Shiny.Net.HttpServer.FileSync.Internal;

namespace Shiny.Net.HttpServer.FileSync;

/// <summary>
/// Keeps a sync tree in a directory:
/// <code>
/// {root}/chunks/{first two hex}/{sha256}   one file per chunk, written once
/// {root}/journal.jsonl                      one line per committed change
/// </code>
/// The journal is append-only and flushed to disk before a commit is acknowledged, so a crash loses
/// nothing a client was told succeeded; a torn last line from a crash mid-write is ignored. It is
/// replayed into memory on first use and compacted then if it has grown well past the live tree.
/// </summary>
public sealed class DiskFileSyncStore : IFileSyncStore
{
    readonly string root;
    readonly string chunks;
    readonly string journalPath;
    readonly SemaphoreSlim gate = new(1, 1);

    Dictionary<string, FileSyncEntry>? entries;
    long latest;

    public DiskFileSyncStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        this.root = Path.GetFullPath(root);
        this.chunks = Path.Combine(this.root, "chunks");
        this.journalPath = Path.Combine(this.root, "journal.jsonl");
        Directory.CreateDirectory(this.chunks);
    }

    /// <summary>The directory everything is kept in.</summary>
    public string RootPath => this.root;

    // ---- chunks ----

    public ValueTask<bool> HasChunkAsync(string hash, CancellationToken cancellationToken)
        => ValueTask.FromResult(File.Exists(this.ChunkPath(hash)));

    public async ValueTask PutChunkAsync(string hash, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        var path = this.ChunkPath(hash);

        if (File.Exists(path))
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("n") + ".tmp";

        try
        {
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await file.WriteAsync(data, cancellationToken).ConfigureAwait(false);
                file.Flush(flushToDisk: true);
            }

            // Two uploads of the same chunk at once both land here; whichever moves second finds
            // the file there, and the bytes are the same either way.
            try
            {
                File.Move(temp, path);
            }
            catch (IOException) when (File.Exists(path))
            {
            }
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    public ValueTask<Stream?> OpenChunkAsync(string hash, CancellationToken cancellationToken)
    {
        try
        {
            return ValueTask.FromResult<Stream?>(new FileStream(this.ChunkPath(hash), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, useAsync: true));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return ValueTask.FromResult<Stream?>(null);
        }
    }

    public ValueTask<bool> DeleteChunkAsync(string hash, CancellationToken cancellationToken)
    {
        var path = this.ChunkPath(hash);

        if (!File.Exists(path))
            return ValueTask.FromResult(false);

        File.Delete(path);
        return ValueTask.FromResult(true);
    }

    public async IAsyncEnumerable<FileSyncStoredChunk> ListChunksAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var file in Directory.EnumerateFiles(this.chunks, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(file);

            if (!ChunkHash.IsValid(name))
                continue;

            var info = new FileInfo(file);
            yield return new FileSyncStoredChunk(name, info.Length, info.LastWriteTimeUtc);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    string ChunkPath(string hash)
    {
        // The hash is a path segment here, so nothing but a well-formed one gets this far.
        if (!ChunkHash.IsValid(hash))
            throw new ArgumentException($"'{hash}' is not a chunk hash.", nameof(hash));

        return Path.Combine(this.chunks, hash[..2], hash);
    }

    // ---- entries ----

    public async ValueTask<FileSyncEntry?> GetEntryAsync(string path, CancellationToken cancellationToken)
    {
        var all = await this.LoadAsync(cancellationToken).ConfigureAwait(false);

        lock (all)
            return all.GetValueOrDefault(path);
    }

    public async ValueTask<IReadOnlyList<FileSyncEntry>> GetChangesAsync(long afterRevision, int limit, CancellationToken cancellationToken)
    {
        var all = await this.LoadAsync(cancellationToken).ConfigureAwait(false);

        lock (all)
            return [.. all.Values.Where(e => e.Revision > afterRevision).OrderBy(e => e.Revision).Take(limit)];
    }

    public async ValueTask<IReadOnlyList<FileSyncEntry>> GetAllEntriesAsync(CancellationToken cancellationToken)
    {
        var all = await this.LoadAsync(cancellationToken).ConfigureAwait(false);

        lock (all)
            return [.. all.Values];
    }

    public async ValueTask<long> GetLatestRevisionAsync(CancellationToken cancellationToken)
    {
        await this.LoadAsync(cancellationToken).ConfigureAwait(false);
        return Volatile.Read(ref this.latest);
    }

    public async ValueTask<FileSyncCommitResult> CommitAsync(FileSyncCommit commit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commit);

        var all = await this.LoadAsync(cancellationToken).ConfigureAwait(false);
        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            FileSyncEntry? current;

            lock (all)
                current = all.GetValueOrDefault(commit.Path);

            var holds = commit.ExpectedRevision is { } expected
                ? current?.Revision == expected
                : current is null || current.Deleted;

            if (!holds)
                return FileSyncCommitResult.Conflict(current);

            var entry = new FileSyncEntry
            {
                Path = commit.Path,
                Revision = this.latest + 1,
                Size = commit.Deleted ? 0 : commit.Size,
                Hash = commit.Deleted ? null : commit.Hash,
                Modified = commit.Modified,
                Deleted = commit.Deleted,
                Chunks = commit.Deleted ? [] : commit.Chunks
            };

            // On disk before anyone hears about it: the revision a client is handed has to survive
            // a restart, or the next one handed out collides with it.
            await this.AppendAsync(entry, cancellationToken).ConfigureAwait(false);

            lock (all)
            {
                // A path whose case changed replaces the old spelling rather than sitting beside it.
                all.Remove(commit.Path);
                all[entry.Path] = entry;
            }

            Volatile.Write(ref this.latest, entry.Revision);
            return FileSyncCommitResult.Success(entry);
        }
        finally
        {
            this.gate.Release();
        }
    }

    async ValueTask AppendAsync(FileSyncEntry entry, CancellationToken cancellationToken)
    {
        var line = JsonSerializer.SerializeToUtf8Bytes(ToWire(entry), WireJson.Default.WireEntry);

        await using var file = new FileStream(this.journalPath, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, useAsync: true);
        await file.WriteAsync(line, cancellationToken).ConfigureAwait(false);
        await file.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        file.Flush(flushToDisk: true);
    }

    async ValueTask<Dictionary<string, FileSyncEntry>> LoadAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref this.entries) is { } loaded)
            return loaded;

        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (this.entries is { } raced)
                return raced;

            var all = new Dictionary<string, FileSyncEntry>(StringComparer.OrdinalIgnoreCase);
            var lines = 0;
            long latest = 0;

            if (File.Exists(this.journalPath))
            {
                foreach (var line in await File.ReadAllLinesAsync(this.journalPath, Encoding.UTF8, cancellationToken).ConfigureAwait(false))
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    WireEntry? wire;

                    try
                    {
                        wire = JsonSerializer.Deserialize(line, WireJson.Default.WireEntry);
                    }
                    catch (JsonException)
                    {
                        // A line torn by a crash while it was being written. It was never
                        // acknowledged, so dropping it loses nothing anyone was promised.
                        continue;
                    }

                    if (wire is null)
                        continue;

                    var entry = FromWire(wire);
                    all.Remove(entry.Path);
                    all[entry.Path] = entry;
                    latest = Math.Max(latest, entry.Revision);
                    lines++;
                }
            }

            this.latest = latest;

            if (lines > all.Count * 2 + 1024)
                await this.CompactAsync(all.Values, cancellationToken).ConfigureAwait(false);

            Volatile.Write(ref this.entries, all);
            return all;
        }
        finally
        {
            this.gate.Release();
        }
    }

    async ValueTask CompactAsync(IEnumerable<FileSyncEntry> live, CancellationToken cancellationToken)
    {
        var temp = this.journalPath + ".compact";

        await using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            foreach (var entry in live.OrderBy(e => e.Revision))
            {
                await file.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(ToWire(entry), WireJson.Default.WireEntry), cancellationToken).ConfigureAwait(false);
                await file.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
            }

            file.Flush(flushToDisk: true);
        }

        File.Move(temp, this.journalPath, overwrite: true);
    }

    internal static WireEntry ToWire(FileSyncEntry entry, bool includeChunks = true) => new(
        entry.Path,
        entry.Revision,
        entry.Size,
        entry.Hash,
        entry.Modified,
        entry.Deleted,
        includeChunks ? [.. entry.Chunks.Select(c => new WireChunk(c.Hash, c.Size))] : null
    );

    internal static FileSyncEntry FromWire(WireEntry wire) => new()
    {
        Path = wire.Path,
        Revision = wire.Revision,
        Size = wire.Size,
        Hash = wire.Hash,
        Modified = wire.Modified,
        Deleted = wire.Deleted,
        Chunks = wire.Chunks?.Select(c => new FileSyncChunk(c.Hash, c.Size)).ToList() ?? []
    };
}
