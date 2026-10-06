namespace Shiny.Net.HttpServer.FileSync.Client;

/// <summary>What to sync with what, and how.</summary>
public sealed class FileSyncClientOptions
{
    /// <summary>
    /// The sync endpoint: the server's URL plus the prefix it was mapped at, such as
    /// <c>https://files.example.com/sync</c>. Required.
    /// </summary>
    public Uri ServerUri { get; set; } = null!;

    /// <summary>The local folder kept in sync with the server. Required.</summary>
    public string LocalPath { get; set; } = null!;

    /// <summary>
    /// Where the client keeps what it last agreed with the server, and transfers in flight. Default
    /// <c>{LocalPath}/.shinysync</c>, which is never synced itself.
    /// </summary>
    public string? StatePath { get; set; }

    /// <summary>
    /// Sent with every request, transfers included - normally <c>Authorization</c>. Background
    /// transfers carry a copy of these, so a short-lived token should be refreshed through
    /// <see cref="HeadersProvider"/> instead.
    /// </summary>
    public IDictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Called before each request and transfer for headers that change, such as a bearer token
    /// refreshed from an identity provider. Merged over <see cref="Headers"/>.
    /// </summary>
    public Func<CancellationToken, ValueTask<IDictionary<string, string>>>? HeadersProvider { get; set; }

    /// <summary>Named in conflicted copies: <c>report (conflicted copy {DeviceName} 2026-10-06 141500).docx</c>.</summary>
    public string DeviceName { get; set; } = Environment.MachineName;

    /// <summary>
    /// The smallest, typical and largest chunk. Defaults 64 KB / 256 KB / 1 MB. Smaller chunks
    /// find more of a file unchanged but cost more metadata. Changing them on a client that has
    /// already synced is safe - a file just chunks differently next time it changes - but every
    /// client should use the same values, or their uploads of the same file will not deduplicate.
    /// </summary>
    public int MinChunkSize { get; set; } = 64 * 1024;

    /// <inheritdoc cref="MinChunkSize"/>
    public int AverageChunkSize { get; set; } = 256 * 1024;

    /// <inheritdoc cref="MinChunkSize"/>
    public int MaxChunkSize { get; set; } = 1024 * 1024;

    /// <summary>
    /// The largest pack of chunks moved in one transfer. Default 8 MB: big enough that a large
    /// change is a few transfers, not hundreds; small enough that a dropped one costs little.
    /// </summary>
    public long MaxPackSize { get; set; } = 8 * 1024 * 1024;

    /// <summary>
    /// Runs chunk transfers through Shiny.Net.Http's <c>IHttpTransferManager</c> - background
    /// sessions on iOS, a foreground service on Android, resumable downloads everywhere. On by
    /// default. Off, or when no transfer manager is available, they go straight through
    /// <see cref="HttpClient"/> and only while the app is running.
    /// </summary>
    public bool UseBackgroundTransfers { get; set; } = true;

    /// <summary>
    /// Uploads packs over tus, so an upload interrupted part way resumes. On by default; needs the
    /// server's <c>EnableTusUploads</c>, which is also on by default.
    /// </summary>
    public bool UseTusUploads { get; set; } = true;

    /// <summary>Whether background transfers may use a metered (cellular) connection. Default true.</summary>
    public bool UseMeteredConnection { get; set; } = true;

    /// <summary>
    /// Skips a path (relative, <c>/</c>-separated) in both directions. Applied on top of the built-in
    /// list: <c>.DS_Store</c>, <c>Thumbs.db</c>, <c>desktop.ini</c>, Office lock files (<c>~$…</c>),
    /// <c>*.tmp</c>, <c>*.partial</c>, and the state folder.
    /// </summary>
    public Func<string, bool>? Ignore { get; set; }

    /// <summary>How long <see cref="FileSyncClient.RunAsync"/> holds a long-poll for remote changes open. Default 60 seconds.</summary>
    public TimeSpan LongPollTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How often <see cref="FileSyncClient.RunAsync"/> rescans the local folder when nothing else
    /// prompted a sync. Default 30 seconds. Local edits are normally picked up sooner, by a file
    /// system watcher where the platform has one.
    /// </summary>
    public TimeSpan ScanInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Watches the local folder for changes in <see cref="FileSyncClient.RunAsync"/>. On by default where supported.</summary>
    public bool WatchLocalChanges { get; set; } = true;

    internal string ResolvedStatePath => this.StatePath ?? Path.Combine(this.LocalPath, ".shinysync");

    internal void Validate()
    {
        if (this.ServerUri is null)
            throw new InvalidOperationException($"{nameof(FileSyncClientOptions)}.{nameof(this.ServerUri)} is required.");

        if (string.IsNullOrWhiteSpace(this.LocalPath))
            throw new InvalidOperationException($"{nameof(FileSyncClientOptions)}.{nameof(this.LocalPath)} is required.");

        if (this.MinChunkSize < 1024 || this.AverageChunkSize <= this.MinChunkSize || this.MaxChunkSize <= this.AverageChunkSize)
            throw new InvalidOperationException("Chunk sizes must satisfy 1 KB <= Min < Average < Max.");

        if (!uint.IsPow2((uint)this.AverageChunkSize))
            throw new InvalidOperationException($"{nameof(this.AverageChunkSize)} must be a power of two.");

        if (this.MaxPackSize < this.MaxChunkSize)
            throw new InvalidOperationException($"{nameof(this.MaxPackSize)} must hold at least one chunk of {nameof(this.MaxChunkSize)}.");
    }
}
