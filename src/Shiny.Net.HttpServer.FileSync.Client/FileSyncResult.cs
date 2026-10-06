namespace Shiny.Net.HttpServer.FileSync.Client;

/// <summary>What happened to one path during a sync.</summary>
public enum FileSyncActivityKind
{
    /// <summary>A local new or changed file was sent to the server.</summary>
    Uploaded,

    /// <summary>A remote new or changed file was written locally.</summary>
    Downloaded,

    /// <summary>A file deleted on the server was deleted here.</summary>
    DeletedLocally,

    /// <summary>A file deleted here was deleted on the server.</summary>
    DeletedRemotely,

    /// <summary>
    /// Both sides changed the same file. The server's version took the path; the local edit was kept
    /// as a conflicted copy beside it and uploaded too. Nothing is lost.
    /// </summary>
    Conflict,

    /// <summary>The path could not be synced this time; it is retried on the next pass.</summary>
    Failed
}

/// <summary>One thing a sync pass did, as it happened.</summary>
public sealed record FileSyncActivity(
    FileSyncActivityKind Kind,
    string Path,
    string? ConflictCopyPath = null,
    long BytesTransferred = 0,
    long BytesReused = 0,
    Exception? Exception = null
);

/// <summary>What one sync pass did.</summary>
public sealed record FileSyncResult(IReadOnlyList<FileSyncActivity> Activities)
{
    public int Uploaded => this.Count(FileSyncActivityKind.Uploaded);

    public int Downloaded => this.Count(FileSyncActivityKind.Downloaded);

    public int Deleted => this.Count(FileSyncActivityKind.DeletedLocally) + this.Count(FileSyncActivityKind.DeletedRemotely);

    public int Conflicts => this.Count(FileSyncActivityKind.Conflict);

    public int Failed => this.Count(FileSyncActivityKind.Failed);

    /// <summary>Chunk bytes that crossed the network.</summary>
    public long BytesTransferred => this.Activities.Sum(a => a.BytesTransferred);

    /// <summary>
    /// Bytes that did not have to: chunks the other side already held. For an edit to a large file
    /// this is most of it - the whole point.
    /// </summary>
    public long BytesReused => this.Activities.Sum(a => a.BytesReused);

    int Count(FileSyncActivityKind kind) => this.Activities.Count(a => a.Kind == kind);
}
