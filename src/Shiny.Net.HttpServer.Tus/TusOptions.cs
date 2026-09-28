namespace Shiny.Net.HttpServer.Tus;

/// <summary>What a tus endpoint accepts and where it puts it.</summary>
public sealed class TusOptions
{
    /// <summary>
    /// Where uploads are kept. Required - normally a <see cref="DiskTusStore"/> under the app's data
    /// directory.
    /// </summary>
    public ITusStore Store { get; set; } = null!;

    /// <summary>
    /// Largest upload this endpoint accepts, advertised as <c>Tus-Max-Size</c>. Null is no limit,
    /// which on a device with finite storage is rarely what you want.
    /// <para>
    /// This is the whole file. Each <c>PATCH</c> is separately bounded by the server's
    /// <see cref="HttpServerLimits.MaxRequestBodySize"/>, so a client that sends a big file in one
    /// request needs a chunk size below that - tus-js-client's <c>chunkSize</c>, for example.
    /// </para>
    /// </summary>
    public long? MaxSize { get; set; }

    /// <summary>
    /// How long an unfinished upload is kept after it was last written to. Null keeps them forever.
    /// <para>
    /// Advertised as the expiration extension, sent as <c>Upload-Expires</c>, and enforced by
    /// a sweep that deletes expired uploads - see <see cref="CleanupInterval"/>. A client that comes
    /// back after it gets 410 and starts again. Completed uploads never expire.
    /// </para>
    /// </summary>
    public TimeSpan? Expiration { get; set; }

    /// <summary>
    /// How often expired uploads are swept away, when <see cref="Expiration"/> is set. The sweep
    /// runs on a timer while the server is running and is also nudged by request traffic, so a
    /// mount inside a group gets it too. Default 15 minutes.
    /// </summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How long a request waits for another request on the same upload to let go of it before
    /// answering <c>423 Locked</c>. Default 3 seconds.
    /// <para>
    /// Only one request touches an upload at a time; that is what stops two <c>PATCH</c>es
    /// interleaving their bytes. But the usual reason there is a second request is that the phone
    /// lost its connection and the first is still sitting on a dead socket, waiting for bytes that
    /// will never come. So a newcomer does not just wait - it asks the holder to stop, the holder
    /// keeps what it had received and steps aside, and the newcomer carries on from there. Set to
    /// <see cref="TimeSpan.Zero"/> to refuse immediately instead, never interrupting anything.
    /// </para>
    /// </summary>
    public TimeSpan LockReleaseTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Accepts <c>Upload-Defer-Length</c> - an upload whose size is not known when it starts, such as
    /// a recording still in progress. On by default.
    /// </summary>
    public bool AllowDeferredLength { get; set; } = true;

    /// <summary>Accepts <c>DELETE</c> on an upload (the termination extension). On by default.</summary>
    public bool AllowTermination { get; set; } = true;

    /// <summary>
    /// Accepts <c>Upload-Concat</c> - partial uploads sent in parallel and joined into one. On by
    /// default.
    /// </summary>
    public bool AllowConcatenation { get; set; } = true;

    /// <summary>
    /// Runs before an upload is created, to authorize it or check its metadata. Call
    /// <see cref="TusCreateContext.Reject"/> to refuse it; nothing is stored.
    /// <code>
    /// o.OnBeforeCreateAsync = ctx =>
    /// {
    ///     if (!ctx.Metadata.ContainsKey("filename"))
    ///         ctx.Reject(StatusCodes.Status400BadRequest, "A filename is required.");
    ///
    ///     return ValueTask.CompletedTask;
    /// };
    /// </code>
    /// </summary>
    public Func<TusCreateContext, ValueTask>? OnBeforeCreateAsync { get; set; }

    /// <summary>
    /// Runs once, when the last byte of an upload has been stored - inside the request that sent it,
    /// before the client hears back. The place to move the file somewhere permanent, record it, or
    /// hand it to whatever processes it. Partial uploads do not raise it; the final upload they are
    /// joined into does.
    /// <para>
    /// The upload stays in the store afterwards. Delete it from here if the file has been moved on
    /// and a client has no reason to ask about it again.
    /// </para>
    /// </summary>
    public Func<TusCompleteContext, ValueTask>? OnUploadCompleteAsync { get; set; }

    /// <summary>The clock used for expiry. Replace it in tests.</summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    internal ITusStore ResolveStore()
        => this.Store ?? throw new InvalidOperationException($"{nameof(TusOptions)}.{nameof(this.Store)} is required.");
}

/// <summary>An upload about to be created, for <see cref="TusOptions.OnBeforeCreateAsync"/>.</summary>
public sealed class TusCreateContext
{
    internal TusCreateContext(HttpContext httpContext, long? uploadLength, TusMetadata metadata, TusConcatenation concatenation)
    {
        this.HttpContext = httpContext;
        this.UploadLength = uploadLength;
        this.Metadata = metadata;
        this.Concatenation = concatenation;
    }

    public HttpContext HttpContext { get; }

    /// <summary>The declared size, or null when the client deferred it.</summary>
    public long? UploadLength { get; }

    public TusMetadata Metadata { get; }

    public TusConcatenation Concatenation { get; }

    /// <summary>True once <see cref="Reject"/> has been called.</summary>
    public bool IsRejected { get; private set; }

    internal int RejectStatusCode { get; private set; }

    internal string? RejectDetail { get; private set; }

    /// <summary>Refuses the upload with <paramref name="statusCode"/>, as a problem response.</summary>
    public void Reject(int statusCode = StatusCodes.Status400BadRequest, string? detail = null)
    {
        this.IsRejected = true;
        this.RejectStatusCode = statusCode;
        this.RejectDetail = detail;
    }
}

/// <summary>A finished upload, for <see cref="TusOptions.OnUploadCompleteAsync"/>.</summary>
public sealed class TusCompleteContext
{
    internal TusCompleteContext(HttpContext httpContext, TusUpload upload, ITusStore store)
    {
        this.HttpContext = httpContext;
        this.Upload = upload;
        this.Store = store;
    }

    /// <summary>The request that delivered the last byte.</summary>
    public HttpContext HttpContext { get; }

    public TusUpload Upload { get; }

    public ITusStore Store { get; }

    /// <summary>The finished file.</summary>
    public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
        => this.Store.OpenReadAsync(this.Upload.Id, cancellationToken);
}
