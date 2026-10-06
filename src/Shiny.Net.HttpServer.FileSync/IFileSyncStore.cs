namespace Shiny.Net.HttpServer.FileSync;

/// <summary>One chunk of a file: its SHA-256 (lower-case hex) and length.</summary>
public sealed record FileSyncChunk(string Hash, long Size);

/// <summary>
/// The current version of one path - or, when <see cref="Deleted"/>, the tombstone that says it was
/// removed, which is kept so a client that was offline learns about the delete.
/// </summary>
public sealed record FileSyncEntry
{
    /// <summary>Relative and <c>/</c>-separated. Paths compare without case, as on macOS and Windows.</summary>
    public required string Path { get; init; }

    /// <summary>
    /// Where this version sits in the store's history. Every change anywhere gets the next number,
    /// so it doubles as the cursor of the change feed.
    /// </summary>
    public required long Revision { get; init; }

    public long Size { get; init; }

    /// <summary>SHA-256 of the whole file, as the client that wrote it computed it.</summary>
    public string? Hash { get; init; }

    /// <summary>The file's modified time on the device that wrote it.</summary>
    public DateTimeOffset Modified { get; init; }

    public bool Deleted { get; init; }

    /// <summary>The file's chunks, in order. Empty for a tombstone and for an empty file.</summary>
    public IReadOnlyList<FileSyncChunk> Chunks { get; init; } = [];
}

/// <summary>A chunk as the store holds it, for garbage collection.</summary>
public sealed record FileSyncStoredChunk(string Hash, long Size, DateTimeOffset StoredUtc);

/// <summary>A change to commit: a new version of <see cref="Path"/>, or its deletion.</summary>
public sealed record FileSyncCommit
{
    public required string Path { get; init; }

    /// <summary>
    /// The revision the client based this change on. Null means it believes the path does not exist
    /// (or was deleted); anything else must be the current revision, or the commit is a conflict.
    /// </summary>
    public long? ExpectedRevision { get; init; }

    public long Size { get; init; }

    public string? Hash { get; init; }

    public DateTimeOffset Modified { get; init; }

    public bool Deleted { get; init; }

    public IReadOnlyList<FileSyncChunk> Chunks { get; init; } = [];
}

/// <summary>What a commit did: stored as <see cref="Entry"/>, or refused because <see cref="Current"/> moved on.</summary>
public sealed record FileSyncCommitResult(bool Committed, FileSyncEntry? Entry, FileSyncEntry? Current)
{
    public static FileSyncCommitResult Success(FileSyncEntry entry) => new(true, entry, entry);

    public static FileSyncCommitResult Conflict(FileSyncEntry? current) => new(false, null, current);
}

/// <summary>
/// Where a sync endpoint keeps chunks and file versions. <see cref="DiskFileSyncStore"/> is the one
/// most apps want; implement this to put them in blob storage or a database.
/// <para>
/// Two halves. Chunks are content-addressed and immutable - storing one twice is a no-op, and the
/// bytes for a hash never change. Entries are the file tree: one current version per path, each
/// stamped with a revision from a single, strictly increasing counter, so "everything after
/// revision N" is the change feed. <see cref="CommitAsync"/> must check the expected revision and
/// assign the new one atomically - it is the only place two clients can race.
/// </para>
/// </summary>
public interface IFileSyncStore
{
    ValueTask<bool> HasChunkAsync(string hash, CancellationToken cancellationToken);

    /// <summary>Stores a chunk whose hash has already been verified. Storing one that exists does nothing.</summary>
    ValueTask PutChunkAsync(string hash, ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    /// <summary>The chunk's bytes, or null when the store does not have it.</summary>
    ValueTask<Stream?> OpenChunkAsync(string hash, CancellationToken cancellationToken);

    ValueTask<bool> DeleteChunkAsync(string hash, CancellationToken cancellationToken);

    /// <summary>Every stored chunk, for garbage collection.</summary>
    IAsyncEnumerable<FileSyncStoredChunk> ListChunksAsync(CancellationToken cancellationToken);

    /// <summary>The current version of a path, tombstones included, or null when it never existed.</summary>
    ValueTask<FileSyncEntry?> GetEntryAsync(string path, CancellationToken cancellationToken);

    /// <summary>Up to <paramref name="limit"/> entries with a revision after <paramref name="afterRevision"/>, oldest first.</summary>
    ValueTask<IReadOnlyList<FileSyncEntry>> GetChangesAsync(long afterRevision, int limit, CancellationToken cancellationToken);

    /// <summary>Every current entry, tombstones included.</summary>
    ValueTask<IReadOnlyList<FileSyncEntry>> GetAllEntriesAsync(CancellationToken cancellationToken);

    /// <summary>The highest revision handed out so far; 0 for an empty store.</summary>
    ValueTask<long> GetLatestRevisionAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Applies <paramref name="commit"/> if <see cref="FileSyncCommit.ExpectedRevision"/> still holds,
    /// stamping it with the next revision; otherwise returns a conflict carrying the current entry.
    /// </summary>
    ValueTask<FileSyncCommitResult> CommitAsync(FileSyncCommit commit, CancellationToken cancellationToken);
}
