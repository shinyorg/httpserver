using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Shiny.Net.HttpServer.Tus;

namespace Shiny.Net.HttpServer.FileSync.Internal;

/// <summary>An error the endpoint answers with a status code instead of a 500.</summary>
sealed class FileSyncException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>The routes behind <see cref="FileSyncExtensions.MapFileSync(HttpServer, string, FileSyncOptions)"/>.</summary>
sealed class FileSyncHandler
{
    const string JsonContentType = "application/json; charset=utf-8";
    const int MaxManifestBytes = 32 * 1024 * 1024;

    readonly FileSyncOptions options;
    readonly ConditionalWeakTable<IFileSyncStore, ChangeSignal> signals = new();

    public FileSyncHandler(FileSyncOptions options) => this.options = options;

    /// <summary>Turns a <see cref="FileSyncException"/> into the status it names, as a problem response.</summary>
    public RequestDelegate Guard(RequestDelegate verb) => async context =>
    {
        try
        {
            await verb(context).ConfigureAwait(false);
        }
        catch (FileSyncException ex) when (!context.Response.HasStarted)
        {
            await ProblemDetailsWriter.WriteResponseAsync(context, new ProblemDetails
            {
                Status = ex.StatusCode,
                Detail = ex.Message
            }).ConfigureAwait(false);
        }
    };

    public IFileSyncStore StoreFor(HttpContext context)
        => this.options.StoreSelector?.Invoke(context)
            ?? this.options.Store
            ?? throw new InvalidOperationException($"{nameof(FileSyncOptions)} needs a {nameof(FileSyncOptions.Store)} or a {nameof(FileSyncOptions.StoreSelector)}.");

    // ---- change feed ----

    /// <summary><c>GET changes?cursor=&amp;limit=</c>: what changed after the cursor, oldest first, without chunk lists.</summary>
    public async ValueTask ChangesAsync(HttpContext context)
    {
        var store = this.StoreFor(context);
        var cursor = QueryLong(context, "cursor") ?? 0;
        var limit = (int)Math.Clamp(QueryLong(context, "limit") ?? this.options.MaxChangesPerPage, 1, this.options.MaxChangesPerPage);
        var changes = await store.GetChangesAsync(cursor, limit, context.RequestAborted).ConfigureAwait(false);

        var next = changes.Count > 0 ? changes[^1].Revision : cursor;

        await WriteJsonAsync(context, new WireChanges(
            [.. changes.Select(e => DiskFileSyncStore.ToWire(e, includeChunks: false))],
            next,
            changes.Count == limit
        ), WireJson.Default.WireChanges).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>GET changes/wait?cursor=&amp;timeout=</c>: a long-poll that answers as soon as something
    /// changes after the cursor, or when the timeout runs out - Dropbox's <c>longpoll</c>. Cheap for a
    /// client to keep open; it costs no polling and hears about a change as it is committed.
    /// </summary>
    public async ValueTask WaitAsync(HttpContext context)
    {
        var store = this.StoreFor(context);
        var cursor = QueryLong(context, "cursor") ?? 0;
        var timeout = TimeSpan.FromSeconds(Math.Clamp(QueryLong(context, "timeout") ?? 30, 1, (long)this.options.MaxWaitTimeout.TotalSeconds));
        var signal = this.signals.GetValue(store, _ => new ChangeSignal());

        // Taken before the revision is read, so a commit that lands in between still wakes us.
        var changed = signal.Next;
        var latest = await store.GetLatestRevisionAsync(context.RequestAborted).ConfigureAwait(false);

        if (latest <= cursor)
        {
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            timer.CancelAfter(timeout);

            try
            {
                await changed.WaitAsync(timer.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
            {
            }

            latest = await store.GetLatestRevisionAsync(context.RequestAborted).ConfigureAwait(false);
        }

        await WriteJsonAsync(context, new WireWait(latest > cursor, latest), WireJson.Default.WireWait).ConfigureAwait(false);
    }

    // ---- files ----

    /// <summary><c>GET files/manifest?path=</c>: the current version with its chunk list.</summary>
    public async ValueTask GetManifestAsync(HttpContext context)
    {
        var path = RequirePath(context);
        var entry = await this.StoreFor(context).GetEntryAsync(path, context.RequestAborted).ConfigureAwait(false);

        if (entry is null || entry.Deleted)
            throw new FileSyncException(StatusCodes.Status404NotFound, $"There is no file '{path}'.");

        await WriteJsonAsync(context, DiskFileSyncStore.ToWire(entry), WireJson.Default.WireEntry).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>PUT files/manifest?path=</c>: commits a new version made of chunks the store already has.
    /// 409 with the current entry when the base revision is stale; 422 with the missing hashes when
    /// a chunk is not there.
    /// </summary>
    public async ValueTask CommitAsync(HttpContext context)
    {
        var path = RequirePath(context);
        var store = this.StoreFor(context);

        if (context.Request.ContentLength > MaxManifestBytes)
            throw new FileSyncException(StatusCodes.Status413PayloadTooLarge, "The manifest is too large.");

        var commit = await context.Request.ReadJsonAsync(WireJson.Default.WireCommit, context.RequestAborted).ConfigureAwait(false)
            ?? throw new FileSyncException(StatusCodes.Status400BadRequest, "The body must be a JSON commit.");

        if (commit.Chunks is null || commit.Size < 0 || !ChunkHash.IsValid(commit.Hash))
            throw new FileSyncException(StatusCodes.Status400BadRequest, "A commit needs a size, a whole-file SHA-256 and a chunk list.");

        long total = 0;

        foreach (var chunk in commit.Chunks)
        {
            if (!ChunkHash.IsValid(chunk.Hash) || chunk.Size <= 0 || chunk.Size > this.options.MaxChunkSize)
                throw new FileSyncException(StatusCodes.Status400BadRequest, $"'{chunk.Hash}' ({chunk.Size} bytes) is not a valid chunk.");

            total += chunk.Size;
        }

        if (total != commit.Size)
            throw new FileSyncException(StatusCodes.Status400BadRequest, $"The chunks add up to {total} bytes, not {commit.Size}.");

        var missing = new List<string>();

        foreach (var hash in commit.Chunks.Select(c => c.Hash).Distinct(StringComparer.Ordinal))
        {
            if (!await store.HasChunkAsync(hash, context.RequestAborted).ConfigureAwait(false))
                missing.Add(hash);
        }

        if (missing.Count > 0)
        {
            await WriteJsonAsync(context, new WireHashes(missing), WireJson.Default.WireHashes, StatusCodes.Status422UnprocessableEntity).ConfigureAwait(false);
            return;
        }

        await this.ApplyAsync(context, store, new FileSyncCommit
        {
            Path = path,
            ExpectedRevision = commit.BaseRevision,
            Size = commit.Size,
            Hash = commit.Hash,
            Modified = commit.Modified,
            Chunks = [.. commit.Chunks.Select(c => new FileSyncChunk(c.Hash, c.Size))]
        }).ConfigureAwait(false);
    }

    /// <summary><c>DELETE files/manifest?path=&amp;revision=</c>: leaves a tombstone, if the revision is still current.</summary>
    public async ValueTask DeleteAsync(HttpContext context)
    {
        var path = RequirePath(context);
        var store = this.StoreFor(context);
        var revision = QueryLong(context, "revision")
            ?? throw new FileSyncException(StatusCodes.Status400BadRequest, "revision is required: the version being deleted.");

        var current = await store.GetEntryAsync(path, context.RequestAborted).ConfigureAwait(false);

        if (current is null || current.Deleted)
            throw new FileSyncException(StatusCodes.Status404NotFound, $"There is no file '{path}'.");

        await this.ApplyAsync(context, store, new FileSyncCommit
        {
            Path = current.Path,
            ExpectedRevision = revision,
            Deleted = true,
            Modified = this.options.TimeProvider.GetUtcNow()
        }).ConfigureAwait(false);
    }

    async ValueTask ApplyAsync(HttpContext context, IFileSyncStore store, FileSyncCommit commit)
    {
        var previous = await store.GetEntryAsync(commit.Path, context.RequestAborted).ConfigureAwait(false);
        var result = await store.CommitAsync(commit, context.RequestAborted).ConfigureAwait(false);

        if (!result.Committed)
        {
            await WriteJsonAsync(
                context,
                new WireConflict(result.Current is { } current ? DiskFileSyncStore.ToWire(current, includeChunks: false) : null),
                WireJson.Default.WireConflict,
                StatusCodes.Status409Conflict
            ).ConfigureAwait(false);
            return;
        }

        this.signals.GetValue(store, _ => new ChangeSignal()).Pulse();

        if (this.options.OnChangedAsync is { } changed)
            await changed(new FileSyncChangeContext(context, store, result.Entry!, previous)).ConfigureAwait(false);

        await WriteJsonAsync(context, DiskFileSyncStore.ToWire(result.Entry!, includeChunks: false), WireJson.Default.WireEntry).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>GET files/content?path=</c>: the whole file, reassembled - for a browser, a script, or
    /// anything that is not a sync client.
    /// </summary>
    public async ValueTask ContentAsync(HttpContext context)
    {
        var path = RequirePath(context);
        var store = this.StoreFor(context);
        var entry = await store.GetEntryAsync(path, context.RequestAborted).ConfigureAwait(false);

        if (entry is null || entry.Deleted)
            throw new FileSyncException(StatusCodes.Status404NotFound, $"There is no file '{path}'.");

        var response = context.Response;
        response.ContentType = "application/octet-stream";
        response.ContentLength = entry.Size;
        response.Headers.Set(HeaderNames.ETag, $"\"{entry.Revision.ToString(CultureInfo.InvariantCulture)}\"");
        response.Headers.Set(HeaderNames.LastModified, entry.Modified.ToString("r", CultureInfo.InvariantCulture));

        if (HttpMethods.IsHead(context.Request.Method))
        {
            await response.StartAsync(context.RequestAborted).ConfigureAwait(false);
            return;
        }

        foreach (var chunk in entry.Chunks)
        {
            await using var stream = await store.OpenChunkAsync(chunk.Hash, context.RequestAborted).ConfigureAwait(false)
                ?? throw new FileSyncException(StatusCodes.Status500InternalServerError, $"Chunk {chunk.Hash} of '{path}' is missing from the store.");

            await stream.CopyToAsync(response.Body, context.RequestAborted).ConfigureAwait(false);
        }
    }

    // ---- chunks ----

    /// <summary><c>POST chunks/missing</c>: which of these hashes the store does not have - the question that makes a re-upload cost only what changed.</summary>
    public async ValueTask MissingAsync(HttpContext context)
    {
        var store = this.StoreFor(context);
        var request = await context.Request.ReadJsonAsync(WireJson.Default.WireHashes, context.RequestAborted).ConfigureAwait(false)
            ?? throw new FileSyncException(StatusCodes.Status400BadRequest, "The body must be a JSON list of hashes.");

        var missing = new List<string>();

        foreach (var hash in request.Hashes.Distinct(StringComparer.Ordinal))
        {
            if (!ChunkHash.IsValid(hash))
                throw new FileSyncException(StatusCodes.Status400BadRequest, $"'{hash}' is not a chunk hash.");

            if (!await store.HasChunkAsync(hash, context.RequestAborted).ConfigureAwait(false))
                missing.Add(hash);
        }

        await WriteJsonAsync(context, new WireHashes(missing), WireJson.Default.WireHashes).ConfigureAwait(false);
    }

    /// <summary><c>PUT chunks/pack</c>: a pack in one request, for clients that do not use tus.</summary>
    public async ValueTask UploadPackAsync(HttpContext context)
    {
        if (context.Request.ContentLength > this.options.MaxPackSize)
            throw new FileSyncException(StatusCodes.Status413PayloadTooLarge, $"Packs are limited to {this.options.MaxPackSize} bytes.");

        var stored = await this.IngestAsync(this.StoreFor(context), context.Request.Body, context.RequestAborted).ConfigureAwait(false);
        await WriteJsonAsync(context, new WirePackResult(stored), WireJson.Default.WirePackResult).ConfigureAwait(false);
    }

    public async ValueTask<int> IngestAsync(IFileSyncStore store, Stream pack, CancellationToken cancellationToken)
    {
        try
        {
            return await PackFormat.ReadAsync(
                pack,
                this.options.MaxChunkSize,
                (hash, data) => store.PutChunkAsync(hash, data, cancellationToken),
                cancellationToken
            ).ConfigureAwait(false);
        }
        catch (InvalidDataException ex)
        {
            throw new FileSyncException(StatusCodes.Status400BadRequest, ex.Message);
        }
    }

    /// <summary>
    /// <c>GET chunks/pack?h=a,b,c</c>: the named chunks as one pack. The layout is fixed by the
    /// hashes, so a <c>Range: bytes=N-</c> resume gets the same bytes it would have got - the
    /// client's background downloader picks up a dropped pack where it stopped.
    /// </summary>
    public async ValueTask DownloadPackAsync(HttpContext context)
    {
        var store = this.StoreFor(context);
        var hashes = (context.Request.Query.GetFirst("h") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (hashes.Length == 0 || hashes.Length > this.options.MaxChunksPerDownload)
            throw new FileSyncException(StatusCodes.Status400BadRequest, $"Name between 1 and {this.options.MaxChunksPerDownload} chunks in h.");

        var parts = new List<(string Hash, Stream Data)>(hashes.Length);

        try
        {
            long total = 4;

            foreach (var hash in hashes)
            {
                if (!ChunkHash.IsValid(hash))
                    throw new FileSyncException(StatusCodes.Status400BadRequest, $"'{hash}' is not a chunk hash.");

                var stream = await store.OpenChunkAsync(hash, context.RequestAborted).ConfigureAwait(false)
                    ?? throw new FileSyncException(StatusCodes.Status404NotFound, $"There is no chunk {hash}.");

                if (!stream.CanSeek)
                {
                    var buffered = new MemoryStream();
                    await using (stream)
                        await stream.CopyToAsync(buffered, context.RequestAborted).ConfigureAwait(false);

                    buffered.Position = 0;
                    stream = buffered;
                }

                parts.Add((hash, stream));
                total += PackFormat.HashLength + 4 + stream.Length;
            }

            var start = RangeStart(context, total);
            var response = context.Response;
            response.ContentType = PackFormat.ContentType;
            response.Headers.Set(HeaderNames.AcceptRanges, "bytes");
            response.Headers.Set(HeaderNames.CacheControl, "public, max-age=31536000, immutable");

            if (start > 0)
            {
                response.StatusCode = StatusCodes.Status206PartialContent;
                response.Headers.Set(HeaderNames.ContentRange, $"bytes {start}-{total - 1}/{total}");
            }

            response.ContentLength = total - start;

            if (HttpMethods.IsHead(context.Request.Method))
            {
                await response.StartAsync(context.RequestAborted).ConfigureAwait(false);
                return;
            }

            await using var body = new SkippingStream(response.Body, start);
            await PackFormat.WriteHeaderAsync(body, context.RequestAborted).ConfigureAwait(false);

            foreach (var (hash, data) in parts)
            {
                var header = new byte[PackFormat.HashLength + 4];
                Convert.FromHexString(hash).CopyTo(header, 0);
                System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(PackFormat.HashLength), (int)data.Length);

                await body.WriteAsync(header, context.RequestAborted).ConfigureAwait(false);
                await data.CopyToAsync(body, context.RequestAborted).ConfigureAwait(false);
            }
        }
        finally
        {
            foreach (var (_, data) in parts)
                await data.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary><c>GET chunks/{hash}</c>: one chunk's bytes.</summary>
    public async ValueTask DownloadChunkAsync(HttpContext context)
    {
        var hash = context.Request.RouteValues["hash"];

        if (!ChunkHash.IsValid(hash))
            throw new FileSyncException(StatusCodes.Status404NotFound, $"'{hash}' is not a chunk hash.");

        await using var stream = await this.StoreFor(context).OpenChunkAsync(hash!, context.RequestAborted).ConfigureAwait(false)
            ?? throw new FileSyncException(StatusCodes.Status404NotFound, $"There is no chunk {hash}.");

        context.Response.Headers.Set(HeaderNames.CacheControl, "public, max-age=31536000, immutable");
        await context.Response.WriteStreamAsync(stream, "application/octet-stream", context.RequestAborted).ConfigureAwait(false);
    }

    // ---- tus ----

    /// <summary>A pack finished arriving over tus: verify it, store its chunks, and drop the upload.</summary>
    public async ValueTask IngestTusAsync(TusCompleteContext context)
    {
        try
        {
            await using var pack = await context.OpenReadAsync(context.HttpContext.RequestAborted).ConfigureAwait(false);
            await this.IngestAsync(this.StoreFor(context.HttpContext), pack, context.HttpContext.RequestAborted).ConfigureAwait(false);
        }
        catch (FileSyncException ex)
        {
            throw new TusException(ex.StatusCode, ex.Message);
        }
        finally
        {
            // The chunks are in the store now (or the pack was refused); either way the staged
            // copy has done its job.
            await context.Store.DeleteAsync(context.Upload.Id, CancellationToken.None).ConfigureAwait(false);
        }
    }

    // ---- garbage collection ----

    public async ValueTask<int> CollectGarbageAsync(IFileSyncStore store, CancellationToken cancellationToken)
    {
        var referenced = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in await store.GetAllEntriesAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!entry.Deleted)
                referenced.UnionWith(entry.Chunks.Select(c => c.Hash));
        }

        var cutoff = this.options.TimeProvider.GetUtcNow() - this.options.GarbageGracePeriod;
        var removed = 0;

        await foreach (var chunk in store.ListChunksAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!referenced.Contains(chunk.Hash) && chunk.StoredUtc < cutoff && await store.DeleteChunkAsync(chunk.Hash, cancellationToken).ConfigureAwait(false))
                removed++;
        }

        return removed;
    }

    // ---- helpers ----

    static string RequirePath(HttpContext context)
    {
        if (!SyncPath.TryNormalize(context.Request.Query.GetFirst("path"), out var path))
            throw new FileSyncException(StatusCodes.Status400BadRequest, "path must be a relative, '/'-separated file path.");

        return path;
    }

    static long? QueryLong(HttpContext context, string name)
        => long.TryParse(context.Request.Query.GetFirst(name), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>Where a <c>Range: bytes=N-</c> asks to start, or 0. Any other range form is answered in full.</summary>
    static long RangeStart(HttpContext context, long total)
    {
        var range = context.Request.Headers.GetFirst(HeaderNames.Range);

        if (range is null || !range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) || !range.EndsWith('-'))
            return 0;

        return long.TryParse(range.AsSpan(6, range.Length - 7), NumberStyles.None, CultureInfo.InvariantCulture, out var start) && start < total
            ? start
            : 0;
    }

    static async ValueTask WriteJsonAsync<T>(HttpContext context, T value, JsonTypeInfo<T> typeInfo, int statusCode = StatusCodes.Status200OK)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
        context.Response.StatusCode = statusCode;
        context.Response.Headers.Set(HeaderNames.CacheControl, "no-store");
        await context.Response.WriteBytesAsync(bytes, JsonContentType, context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>Wakes every long-poll waiting on one store.</summary>
    sealed class ChangeSignal
    {
        TaskCompletionSource next = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Next => Volatile.Read(ref this.next).Task;

        public void Pulse()
            => Interlocked.Exchange(ref this.next, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
    }

    /// <summary>Drops the first <c>skip</c> bytes written through it - how a ranged pack response starts mid-way.</summary>
    sealed class SkippingStream(Stream inner, long skip) : Stream
    {
        long remaining = skip;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => this.WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (this.remaining >= buffer.Length)
            {
                this.remaining -= buffer.Length;
                return ValueTask.CompletedTask;
            }

            var from = (int)this.remaining;
            this.remaining = 0;
            return inner.WriteAsync(buffer[from..], cancellationToken);
        }

        // The response body belongs to the server, not to this view of it.
        protected override void Dispose(bool disposing)
        {
        }

        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
