using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shiny.Net.Http;
using Shiny.Net.HttpServer.FileSync.Client.Internal;

namespace Shiny.Net.HttpServer.FileSync.Client;

/// <summary>
/// Keeps <see cref="FileSyncClientOptions.LocalPath"/> and a <c>MapFileSync</c> endpoint the same,
/// both ways - Dropbox's model.
/// <code>
/// var sync = new FileSyncClient(new FileSyncClientOptions
/// {
///     ServerUri = new("https://files.example.com/sync"),
///     LocalPath = Path.Combine(FileSystem.AppDataDirectory, "Documents")
/// }, transferManager);
///
/// var result = await sync.SyncAsync();          // one pass
/// await sync.RunAsync(cancellationToken);       // or keep it in sync until cancelled
/// </code>
/// <para>
/// A pass pulls what changed on the server since the last one, scans the folder for what changed
/// here, and reconciles each path against the version both sides last agreed on. A changed file is
/// split into content-defined chunks and only the chunks the server lacks are uploaded; a file
/// coming down is assembled from chunks already on the device - its own old version, or any other
/// file - and only the rest is downloaded. Both sides changing a file is a conflict, settled the
/// way Dropbox settles it: the server's version keeps the name and the local edit becomes a
/// conflicted copy next to it.
/// </para>
/// <para>
/// A pass is safe to interrupt at any point. Everything is content-addressed and every commit
/// names the revision it was based on, so the next pass picks up where the last one stopped, and a
/// background transfer that finished while the app was not running is used rather than repeated.
/// </para>
/// </summary>
public sealed class FileSyncClient : IDisposable
{
    const string ConflictMarker = " (conflicted copy ";

    static readonly string[] IgnoredNames = [".DS_Store", "Thumbs.db", "desktop.ini", ".shinysync"];

    readonly FileSyncClientOptions options;
    readonly HttpClient http;
    readonly bool ownsHttp;
    readonly IPackTransport transport;
    readonly ILogger logger;
    readonly SemaphoreSlim gate = new(1, 1);
    readonly string root;
    readonly string statePath;
    readonly string stateFile;
    readonly string staging;

    /// <summary>
    /// Creates a client. <paramref name="transfers"/> is Shiny.Net.Http's transfer manager, used
    /// for chunk transfers when <see cref="FileSyncClientOptions.UseBackgroundTransfers"/> is on;
    /// without one, transfers go straight through <see cref="HttpClient"/>.
    /// </summary>
    public FileSyncClient(
        FileSyncClientOptions options,
        IHttpTransferManager? transfers = null,
        HttpClient? httpClient = null,
        ILogger<FileSyncClient>? logger = null
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        this.options = options;
        this.http = httpClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        this.ownsHttp = httpClient is null;
        this.logger = (ILogger?)logger ?? NullLogger.Instance;
        this.transport = options.UseBackgroundTransfers && transfers is not null
            ? new ShinyPackTransport(transfers, options)
            : new DirectPackTransport(this.http, options);

        this.root = Path.GetFullPath(options.LocalPath);
        this.statePath = Path.GetFullPath(options.ResolvedStatePath);
        this.stateFile = Path.Combine(this.statePath, "state.json");
        this.staging = Path.Combine(this.statePath, "staging");
    }

    /// <summary>Raised for each path a pass touched, as it happens - for a progress list or a log.</summary>
    public event EventHandler<FileSyncActivity>? Activity;

    /// <summary>Raised when a pass finishes.</summary>
    public event EventHandler<FileSyncResult>? SyncCompleted;

    public FileSyncClientOptions Options => this.options;

    // ---- the loop ----

    /// <summary>
    /// Keeps the folder in sync until <paramref name="cancellationToken"/> is cancelled: a pass now,
    /// then another whenever the server reports a change (long-poll), the folder changes (a file
    /// system watcher, where the platform has one), or <see cref="FileSyncClientOptions.ScanInterval"/>
    /// passes. A failed pass backs off, up to five minutes.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var watcher = this.CreateWatcher(out var localChanged);
        var backoff = TimeSpan.Zero;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var result = await this.SyncAsync(cancellationToken).ConfigureAwait(false);
                backoff = result.Failed > 0 ? Backoff(backoff) : TimeSpan.Zero;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                this.logger.LogWarning(ex, "Sync pass failed");
                backoff = Backoff(backoff);
            }

            if (backoff > TimeSpan.Zero)
            {
                await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
                continue;
            }

            using var wake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var cursor = SyncState.Load(this.stateFile).Cursor;
            var reasons = new List<Task>
            {
                this.WaitForRemoteAsync(cursor, wake.Token),
                Task.Delay(this.options.ScanInterval, wake.Token)
            };

            if (localChanged is not null)
                reasons.Add(localChanged.Next(wake.Token));

            await Task.WhenAny(reasons).ConfigureAwait(false);
            await wake.CancelAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            // Let a burst of local writes (a save is often several) settle into one pass.
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }

        static TimeSpan Backoff(TimeSpan current)
            => current == TimeSpan.Zero ? TimeSpan.FromSeconds(5) : TimeSpan.FromTicks(Math.Min(current.Ticks * 2, TimeSpan.FromMinutes(5).Ticks));
    }

    async Task WaitForRemoteAsync(long cursor, CancellationToken cancellationToken)
    {
        try
        {
            var timeout = (int)this.options.LongPollTimeout.TotalSeconds;
            using var response = await this.SendAsync(HttpMethod.Get, $"changes/wait?cursor={cursor}&timeout={timeout}", null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The server being unreachable is the scan interval's problem, not a reason to spin.
            await Task.Delay(this.options.ScanInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    FileSystemWatcher? CreateWatcher(out LocalChangeSignal? signal)
    {
        signal = null;

        if (!this.options.WatchLocalChanges)
            return null;

        try
        {
            Directory.CreateDirectory(this.root);
            var watcher = new FileSystemWatcher(this.root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
            };

            var local = new LocalChangeSignal();
            FileSystemEventHandler changed = (_, e) =>
            {
                if (!Path.GetFullPath(e.FullPath).StartsWith(this.statePath, StringComparison.OrdinalIgnoreCase))
                    local.Pulse();
            };

            watcher.Changed += changed;
            watcher.Created += changed;
            watcher.Deleted += changed;
            watcher.Renamed += (_, e) => local.Pulse();
            watcher.EnableRaisingEvents = true;

            signal = local;
            return watcher;
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or IOException or ArgumentException)
        {
            // iOS has no file system watcher; the scan interval covers it.
            return null;
        }
    }

    // ---- one pass ----

    /// <summary>Runs one sync pass. Passes never overlap; a second call waits for the first.</summary>
    public async Task<FileSyncResult> SyncAsync(CancellationToken cancellationToken = default)
    {
        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            Directory.CreateDirectory(this.root);
            Directory.CreateDirectory(this.staging);

            var pass = new Pass(this, SyncState.Load(this.stateFile), cancellationToken);
            await pass.RunAsync().ConfigureAwait(false);

            var result = new FileSyncResult(pass.Activities);
            this.SyncCompleted?.Invoke(this, result);
            return result;
        }
        finally
        {
            this.gate.Release();
        }
    }

    /// <summary>
    /// Everything one pass knows: the remote changes it pulled, the local scan, and the chunks this
    /// device can already supply.
    /// </summary>
    sealed class Pass(FileSyncClient client, SyncState state, CancellationToken cancellationToken)
    {
        readonly Dictionary<string, WireEntry> remote = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, FileInfo> local = new(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> downloadedChunks = new(StringComparer.Ordinal);
        Dictionary<string, (string File, long Offset, int Length)>? chunkIndex;

        public List<FileSyncActivity> Activities { get; } = [];

        public async Task RunAsync()
        {
            var cursor = await this.PullAsync().ConfigureAwait(false);
            this.ScanLocal();

            var remoteFailed = false;
            var paths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            paths.UnionWith(this.remote.Keys);
            paths.UnionWith(this.local.Keys);
            paths.UnionWith(state.Entries.Keys);

            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    await this.ReconcileAsync(path).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    client.logger.LogWarning(ex, "Could not sync {Path}", path);
                    this.Report(new FileSyncActivity(FileSyncActivityKind.Failed, path, Exception: ex));

                    // A remote change that was not applied must be pulled again next time, so the
                    // cursor stays put; local changes are rediscovered by the scan regardless.
                    if (this.remote.ContainsKey(path))
                        remoteFailed = true;
                }
                finally
                {
                    state.Save(client.stateFile);
                }
            }

            if (!remoteFailed)
                state.Cursor = cursor;

            state.Save(client.stateFile);
            this.CleanStaging();
        }

        // ---- gathering ----

        async Task<long> PullAsync()
        {
            var cursor = state.Cursor;

            while (true)
            {
                var page = await client.GetJsonAsync($"changes?cursor={cursor}", WireJson.Default.WireChanges, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The server returned no change feed.");

                foreach (var entry in page.Entries)
                {
                    if (SyncPath.TryNormalize(entry.Path, out _))
                        this.remote[entry.Path] = entry;
                }

                cursor = page.Cursor;

                if (!page.HasMore)
                    return cursor;
            }
        }

        void ScanLocal()
        {
            var enumeration = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.System
            };

            foreach (var full in Directory.EnumerateFiles(client.root, "*", enumeration))
            {
                var relative = Path.GetRelativePath(client.root, full).Replace('\\', '/');

                if (client.IsIgnored(relative) || !SyncPath.TryNormalize(relative, out var normalized))
                    continue;

                // Two names differing only by case, on a case-sensitive disk: the server keeps one
                // file per name-ignoring-case, so only the first is synced.
                this.local.TryAdd(normalized, new FileInfo(full));
            }
        }

        // ---- deciding ----

        async Task ReconcileAsync(string path)
        {
            state.Entries.TryGetValue(path, out var known);
            this.local.TryGetValue(path, out var file);
            this.remote.TryGetValue(path, out var theirs);

            var remoteChanged = theirs is not null && (known is null ? !theirs.Deleted : theirs.Revision > known.Revision);
            var localDeleted = file is null && known is not null;
            var localNew = file is not null && known is null;
            var localTouched = file is not null && known is not null
                && (file.Length != known.Size || file.LastWriteTimeUtc.Ticks != known.LocalModifiedTicks);

            if (!remoteChanged)
            {
                if (localDeleted)
                    await this.DeleteRemoteAsync(known!).ConfigureAwait(false);
                else if (localNew || localTouched)
                    await this.PushAsync(path, file!, known?.Revision).ConfigureAwait(false);
                else if (file is null && known is null && theirs is { Deleted: true })
                    state.Entries.Remove(path);

                return;
            }

            if (theirs!.Deleted)
            {
                if (file is null)
                    state.Entries.Remove(path);
                else if (localNew || localTouched)
                    await this.PushAsync(path, file, null).ConfigureAwait(false);   // edited here, deleted there: the edit wins
                else
                    this.DeleteLocal(path, file);

                return;
            }

            if (file is null || !(localNew || localTouched))
            {
                // Unchanged here (or deleted here and changed there - the change wins).
                await this.DownloadAsync(path, theirs).ConfigureAwait(false);
                return;
            }

            // Changed on both sides. The same bytes is no conflict at all - the typical first sync
            // of a folder that was copied to both ends.
            var chunked = await client.ChunkAsync(file.FullName, cancellationToken).ConfigureAwait(false);

            if (chunked.Hash == theirs.Hash)
            {
                this.Adopt(path, theirs, chunked);
                return;
            }

            await this.ConflictAsync(path, file, theirs).ConfigureAwait(false);
        }

        // ---- uploading ----

        async Task PushAsync(string path, FileInfo file, long? baseRevision, bool retried = false)
        {
            var chunked = await client.ChunkAsync(file.FullName, cancellationToken).ConfigureAwait(false);

            if (state.Entries.TryGetValue(path, out var known) && known.Hash == chunked.Hash && known.Revision == baseRevision)
            {
                // Saved without changing anything - a new modified time and the same bytes.
                known.LocalModifiedTicks = chunked.ModifiedUtc.Ticks;
                return;
            }

            var (sent, reused) = await this.UploadMissingAsync(file.FullName, chunked).ConfigureAwait(false);

            var commit = new WireCommit(
                baseRevision,
                chunked.Size,
                chunked.Hash,
                new DateTimeOffset(chunked.ModifiedUtc, TimeSpan.Zero),
                [.. chunked.Chunks.Select(c => new WireChunk(c.Hash, c.Length))]
            );

            using var response = await client.SendJsonAsync(HttpMethod.Put, "files/manifest?path=" + Uri.EscapeDataString(path), commit, WireJson.Default.WireCommit, cancellationToken).ConfigureAwait(false);

            switch (response.StatusCode)
            {
                case HttpStatusCode.OK:
                    var entry = await response.Content.ReadFromJsonAsync(WireJson.Default.WireEntry, cancellationToken).ConfigureAwait(false);
                    this.Remember(path, entry!.Revision, chunked);
                    this.Report(new FileSyncActivity(FileSyncActivityKind.Uploaded, path, BytesTransferred: sent, BytesReused: reused));
                    return;

                case HttpStatusCode.UnprocessableEntity when !retried:
                    // A chunk the server said it had was collected before the commit landed.
                    await this.PushAsync(path, file, baseRevision, retried: true).ConfigureAwait(false);
                    return;

                case HttpStatusCode.Conflict:
                    var conflict = await response.Content.ReadFromJsonAsync(WireJson.Default.WireConflict, cancellationToken).ConfigureAwait(false);
                    var current = conflict?.Current;

                    if (current is null || current.Deleted)
                    {
                        // Deleted on the server since: committing as a new file brings it back.
                        if (retried)
                            throw new InvalidOperationException($"'{path}' kept changing on the server.");

                        await this.PushAsync(path, file, null, retried: true).ConfigureAwait(false);
                        return;
                    }

                    if (current.Hash == chunked.Hash)
                    {
                        this.Adopt(path, current, chunked);
                        return;
                    }

                    // Changed on the server after this pass pulled: the same conflict, found late.
                    await this.ConflictAsync(path, file, current).ConfigureAwait(false);
                    return;

                default:
                    response.EnsureSuccessStatusCode();
                    return;
            }
        }

        async Task<(long Sent, long Reused)> UploadMissingAsync(string file, ChunkedFile chunked)
        {
            var distinct = chunked.Chunks.DistinctBy(c => c.Hash).ToList();
            var missing = new HashSet<string>(StringComparer.Ordinal);

            foreach (var batch in distinct.Chunk(5000))
            {
                using var response = await client.SendJsonAsync(HttpMethod.Post, "chunks/missing", new WireHashes([.. batch.Select(c => c.Hash)]), WireJson.Default.WireHashes, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var answer = await response.Content.ReadFromJsonAsync(WireJson.Default.WireHashes, cancellationToken).ConfigureAwait(false);
                missing.UnionWith(answer?.Hashes ?? []);
            }

            var toSend = distinct.Where(c => missing.Contains(c.Hash)).ToList();
            long sent = 0;

            foreach (var pack in Pack(toSend, c => c.Length, client.options.MaxPackSize))
            {
                var id = "shinysync-up-" + PackId(pack.Select(c => c.Hash));
                var packFile = Path.Combine(client.staging, id + ".pack");

                await using (var output = new FileStream(packFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true))
                {
                    await PackFormat.WriteHeaderAsync(output, cancellationToken).ConfigureAwait(false);

                    foreach (var chunk in pack)
                    {
                        var data = await ReadVerifiedAsync(input, chunk.Offset, chunk.Length, chunk.Hash, cancellationToken).ConfigureAwait(false)
                            ?? throw new FileChangedException(file);

                        await PackFormat.WriteChunkAsync(output, chunk.Hash, data, cancellationToken).ConfigureAwait(false);
                    }
                }

                await client.transport.UploadAsync(id, packFile, await client.HeadersAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
                TryDelete(packFile);
                sent += pack.Sum(c => (long)c.Length);
            }

            return (sent, chunked.Size - chunked.Chunks.Where(c => missing.Contains(c.Hash)).Sum(c => (long)c.Length));
        }

        async Task DeleteRemoteAsync(SyncStateEntry known)
        {
            using var response = await client.SendAsync(
                HttpMethod.Delete,
                $"files/manifest?path={Uri.EscapeDataString(known.Path)}&revision={known.Revision}",
                null,
                cancellationToken
            ).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                // Changed on the server since: the change wins over a delete, and the next pass
                // pulls it down again.
                return;
            }

            if (response.StatusCode != HttpStatusCode.NotFound)
                response.EnsureSuccessStatusCode();

            state.Entries.Remove(known.Path);
            this.Report(new FileSyncActivity(FileSyncActivityKind.DeletedRemotely, known.Path));
        }

        // ---- downloading ----

        async Task DownloadAsync(string path, WireEntry theirs)
        {
            using var response = await client.SendAsync(HttpMethod.Get, "files/manifest?path=" + Uri.EscapeDataString(path), null, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
                return;   // deleted since the pull; the next pass sees the tombstone

            response.EnsureSuccessStatusCode();
            var manifest = await response.Content.ReadFromJsonAsync(WireJson.Default.WireEntry, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"The server returned no manifest for '{path}'.");

            var target = client.FullPath(manifest.Path);
            var index = this.ChunkIndex();
            var needed = (manifest.Chunks ?? []).DistinctBy(c => c.Hash).Where(c => !index.ContainsKey(c.Hash) && !this.downloadedChunks.Contains(c.Hash)).ToList();
            long fetched = await this.FetchAsync(needed).ConfigureAwait(false);

            var temp = Path.Combine(client.staging, "assemble-" + Guid.NewGuid().ToString("n"));

            try
            {
                long reused = 0;

                await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    foreach (var chunk in manifest.Chunks ?? [])
                    {
                        var data = await this.LocalChunkAsync(chunk.Hash).ConfigureAwait(false);

                        if (data is not null)
                        {
                            reused += data.Length;
                        }
                        else
                        {
                            // Not where the index said (that file changed since), or not fetched
                            // yet: download it now.
                            fetched += await this.FetchAsync([chunk]).ConfigureAwait(false);
                            data = await this.LocalChunkAsync(chunk.Hash).ConfigureAwait(false)
                                ?? throw new InvalidDataException($"Chunk {chunk.Hash} of '{path}' could not be fetched.");
                        }

                        await output.WriteAsync(data, cancellationToken).ConfigureAwait(false);
                    }
                }

                // The local file may have changed while this one was being built. Replacing it now
                // would lose that edit; leave it for the next pass, which will see a conflict.
                if (File.Exists(target) && state.Entries.TryGetValue(path, out var known))
                {
                    var now = new FileInfo(target);

                    if (now.Length != known.Size || now.LastWriteTimeUtc.Ticks != known.LocalModifiedTicks)
                        throw new FileChangedException(target);
                }
                else if (File.Exists(target) && !state.Entries.ContainsKey(path))
                {
                    // Created here after the scan: not ours to overwrite.
                    throw new FileChangedException(target);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.SetLastWriteTimeUtc(temp, manifest.Modified.UtcDateTime);
                File.Move(temp, target, overwrite: true);

                var written = new FileInfo(target);
                state.Entries.Remove(path);
                state.Entries[manifest.Path] = new SyncStateEntry
                {
                    Path = manifest.Path,
                    Revision = manifest.Revision,
                    Size = manifest.Size,
                    Hash = manifest.Hash,
                    LocalModifiedTicks = written.LastWriteTimeUtc.Ticks,
                    Chunks = manifest.Chunks ?? []
                };

                // The file just written is now somewhere chunks can come from for the next one.
                this.chunkIndex = null;
                this.Report(new FileSyncActivity(FileSyncActivityKind.Downloaded, manifest.Path, BytesTransferred: fetched, BytesReused: reused));
            }
            finally
            {
                TryDelete(temp);
            }
        }

        /// <summary>Downloads chunks in packs into the staging area. Returns the bytes fetched.</summary>
        async Task<long> FetchAsync(IReadOnlyList<WireChunk> chunks)
        {
            long fetched = 0;
            var chunkDir = Directory.CreateDirectory(Path.Combine(client.staging, "chunks")).FullName;

            foreach (var pack in Pack(chunks, c => c.Size, client.options.MaxPackSize, maxCount: 200))
            {
                var hashes = pack.Select(c => c.Hash).ToList();
                var id = "shinysync-down-" + PackId(hashes);
                var packFile = Path.Combine(client.staging, id + ".pack");
                var uri = Endpoints.Combine(client.options.ServerUri, "chunks/pack?h=" + string.Join(',', hashes));

                if (!await this.TryUnpackAsync(packFile, chunkDir).ConfigureAwait(false))
                {
                    await client.transport.DownloadAsync(id, uri, packFile, await client.HeadersAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

                    if (!await this.TryUnpackAsync(packFile, chunkDir).ConfigureAwait(false))
                        throw new InvalidDataException($"Pack {id} did not verify.");
                }

                fetched += pack.Sum(c => c.Size);
            }

            return fetched;
        }

        /// <summary>
        /// Unpacks a downloaded pack, verifying every chunk. False when there is no pack yet or it is
        /// incomplete - including one a background download finished while the app was not running,
        /// which is used here instead of being fetched again.
        /// </summary>
        async Task<bool> TryUnpackAsync(string packFile, string chunkDir)
        {
            if (!File.Exists(packFile))
                return false;

            try
            {
                await using var input = File.OpenRead(packFile);
                await PackFormat.ReadAsync(input, int.MaxValue, async (hash, data) =>
                {
                    await File.WriteAllBytesAsync(Path.Combine(chunkDir, hash), data.ToArray(), cancellationToken).ConfigureAwait(false);
                    this.downloadedChunks.Add(hash);
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                TryDelete(packFile);
                return false;
            }

            TryDelete(packFile);
            return true;
        }

        /// <summary>A chunk this device already holds - downloaded this pass, or inside a synced local file - verified, or null.</summary>
        async Task<byte[]?> LocalChunkAsync(string hash)
        {
            var staged = Path.Combine(client.staging, "chunks", hash);

            if (File.Exists(staged))
            {
                var bytes = await File.ReadAllBytesAsync(staged, cancellationToken).ConfigureAwait(false);

                if (Convert.ToHexStringLower(SHA256.HashData(bytes)) == hash)
                    return bytes;
            }

            if (this.ChunkIndex().TryGetValue(hash, out var source))
            {
                try
                {
                    await using var input = new FileStream(source.File, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true);
                    return await ReadVerifiedAsync(input, source.Offset, source.Length, hash, cancellationToken).ConfigureAwait(false);
                }
                catch (IOException)
                {
                }
            }

            return null;
        }

        /// <summary>
        /// Where on this device each chunk can be read from: every synced file that is still as it
        /// was synced. A changed file is left out, since its chunk list no longer describes it.
        /// </summary>
        Dictionary<string, (string File, long Offset, int Length)> ChunkIndex()
        {
            if (this.chunkIndex is { } built)
                return built;

            var index = new Dictionary<string, (string, long, int)>(StringComparer.Ordinal);

            foreach (var entry in state.Entries.Values)
            {
                var full = client.FullPath(entry.Path);
                var info = new FileInfo(full);

                if (!info.Exists || info.Length != entry.Size || info.LastWriteTimeUtc.Ticks != entry.LocalModifiedTicks)
                    continue;

                long offset = 0;

                foreach (var chunk in entry.Chunks)
                {
                    index.TryAdd(chunk.Hash, (full, offset, (int)chunk.Size));
                    offset += chunk.Size;
                }
            }

            return this.chunkIndex = index;
        }

        // ---- conflicts and deletes ----

        /// <summary>
        /// Both sides changed <paramref name="path"/>. The local edit moves aside to a conflicted
        /// copy and is uploaded as a file of its own; the server's version takes the name.
        /// </summary>
        async Task ConflictAsync(string path, FileInfo file, WireEntry theirs)
        {
            var copy = client.ConflictCopyPath(path);
            var copyFull = client.FullPath(copy);

            File.Move(file.FullName, copyFull);
            state.Entries.Remove(path);
            this.chunkIndex = null;

            await this.DownloadAsync(path, theirs).ConfigureAwait(false);
            await this.PushAsync(copy, new FileInfo(copyFull), null).ConfigureAwait(false);

            this.Report(new FileSyncActivity(FileSyncActivityKind.Conflict, path, ConflictCopyPath: copy));
        }

        void DeleteLocal(string path, FileInfo file)
        {
            File.Delete(file.FullName);
            state.Entries.Remove(path);
            this.chunkIndex = null;
            client.RemoveEmptyParents(file.DirectoryName);
            this.Report(new FileSyncActivity(FileSyncActivityKind.DeletedLocally, path));
        }

        void Adopt(string path, WireEntry theirs, ChunkedFile chunked)
        {
            this.Remember(path, theirs.Revision, chunked);
        }

        void Remember(string path, long revision, ChunkedFile chunked)
        {
            state.Entries[path] = new SyncStateEntry
            {
                Path = path,
                Revision = revision,
                Size = chunked.Size,
                Hash = chunked.Hash,
                LocalModifiedTicks = chunked.ModifiedUtc.Ticks,
                Chunks = [.. chunked.Chunks.Select(c => new WireChunk(c.Hash, c.Length))]
            };

            this.chunkIndex = null;
        }

        void Report(FileSyncActivity activity)
        {
            this.Activities.Add(activity);
            client.Activity?.Invoke(client, activity);
        }

        void CleanStaging()
        {
            try
            {
                var chunks = Path.Combine(client.staging, "chunks");

                if (Directory.Exists(chunks))
                    Directory.Delete(chunks, recursive: true);

                foreach (var leftover in Directory.EnumerateFiles(client.staging, "assemble-*"))
                    TryDelete(leftover);
            }
            catch (IOException)
            {
            }
        }
    }

    // ---- shared helpers ----

    /// <summary>Groups chunks into packs of at most <paramref name="maxPackSize"/> bytes and <paramref name="maxCount"/> chunks.</summary>
    static IEnumerable<List<T>> Pack<T>(IEnumerable<T> chunks, Func<T, long> length, long maxPackSize, int maxCount = int.MaxValue)
    {
        var current = new List<T>();
        long size = 0;

        foreach (var chunk in chunks)
        {
            var bytes = length(chunk);

            if (current.Count > 0 && (size + bytes > maxPackSize || current.Count >= maxCount))
            {
                yield return current;
                current = [];
                size = 0;
            }

            current.Add(chunk);
            size += bytes;
        }

        if (current.Count > 0)
            yield return current;
    }

    /// <summary>A stable id for a set of chunks, so the same pack is the same transfer across restarts.</summary>
    static string PackId(IEnumerable<string> hashes)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(string.Join(',', hashes))))[..32];

    static async Task<byte[]?> ReadVerifiedAsync(Stream input, long offset, int length, string hash, CancellationToken cancellationToken)
    {
        if (input.Length < offset + length)
            return null;

        var data = new byte[length];
        input.Position = offset;
        await input.ReadExactlyAsync(data, cancellationToken).ConfigureAwait(false);

        return Convert.ToHexStringLower(SHA256.HashData(data)) == hash ? data : null;
    }

    Task<ChunkedFile> ChunkAsync(string file, CancellationToken cancellationToken)
        => ContentChunker.ChunkFileAsync(file, this.options.MinChunkSize, this.options.AverageChunkSize, this.options.MaxChunkSize, cancellationToken);

    bool IsIgnored(string relative)
    {
        var segments = relative.Split('/');
        var name = segments[^1];

        if (segments.Any(s => IgnoredNames.Contains(s, StringComparer.OrdinalIgnoreCase)))
            return true;

        if (name.StartsWith("~$", StringComparison.Ordinal)
            || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase))
            return true;

        var full = Path.GetFullPath(Path.Combine(this.root, relative));

        if (full.StartsWith(this.statePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return true;

        return this.options.Ignore?.Invoke(relative) == true;
    }

    string FullPath(string relative)
    {
        var full = Path.GetFullPath(Path.Combine(this.root, relative.Replace('/', Path.DirectorySeparatorChar)));

        // Paths come from the server; one that resolves outside the folder is refused, whatever
        // the server thinks it is doing.
        if (!full.StartsWith(this.root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException($"'{relative}' is outside the sync folder.");

        return full;
    }

    string ConflictCopyPath(string path)
    {
        var directory = Path.GetDirectoryName(path.Replace('/', Path.DirectorySeparatorChar))?.Replace(Path.DirectorySeparatorChar, '/');
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        var stamp = DateTime.Now.ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture);
        var device = string.Concat(this.options.DeviceName.Where(c => !Path.GetInvalidFileNameChars().Contains(c)));

        for (var attempt = 1; ; attempt++)
        {
            var suffix = attempt == 1 ? string.Empty : $" {attempt}";
            var candidate = $"{name}{ConflictMarker}{device} {stamp}{suffix}){extension}";
            var relative = string.IsNullOrEmpty(directory) ? candidate : directory + "/" + candidate;

            if (!File.Exists(this.FullPath(relative)))
                return relative;
        }
    }

    void RemoveEmptyParents(string? directory)
    {
        while (directory is not null
            && directory.StartsWith(this.root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && Directory.Exists(directory)
            && !Directory.EnumerateFileSystemEntries(directory).Any())
        {
            Directory.Delete(directory);
            directory = Path.GetDirectoryName(directory);
        }
    }

    static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    // ---- HTTP ----

    async Task<IDictionary<string, string>> HeadersAsync(CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>(this.options.Headers, StringComparer.OrdinalIgnoreCase);

        if (this.options.HeadersProvider is { } provider)
        {
            foreach (var (key, value) in await provider(cancellationToken).ConfigureAwait(false))
                headers[key] = value;
        }

        return headers;
    }

    async Task<HttpResponseMessage> SendAsync(HttpMethod method, string relative, HttpContent? content, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, Endpoints.Combine(this.options.ServerUri, relative)) { Content = content };

        foreach (var (key, value) in await this.HeadersAsync(cancellationToken).ConfigureAwait(false))
            request.Headers.TryAddWithoutValidation(key, value);

        return await this.http.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    Task<HttpResponseMessage> SendJsonAsync<T>(HttpMethod method, string relative, T body, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
        => this.SendAsync(method, relative, JsonContent.Create(body, typeInfo), cancellationToken);

    async Task<T?> GetJsonAsync<T>(string relative, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        using var response = await this.SendAsync(HttpMethod.Get, relative, null, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync(typeInfo, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (this.ownsHttp)
            this.http.Dispose();
    }

    /// <summary>Wakes the loop when the folder changes.</summary>
    sealed class LocalChangeSignal
    {
        TaskCompletionSource next = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Next(CancellationToken cancellationToken) => Volatile.Read(ref this.next).Task.WaitAsync(cancellationToken);

        public void Pulse()
            => Interlocked.Exchange(ref this.next, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
    }
}
