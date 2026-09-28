namespace Shiny.Net.HttpServer.Tus;

/// <summary>How an upload takes part in the concatenation extension.</summary>
public enum TusConcatenation
{
    /// <summary>An ordinary upload.</summary>
    None,

    /// <summary>
    /// One piece of a larger file, uploaded - often in parallel with its siblings - to be joined
    /// later. Never handed to <see cref="TusOptions.OnUploadCompleteAsync"/> on its own.
    /// </summary>
    Partial,

    /// <summary>The joined result of <see cref="TusUpload.PartialUploads"/>, in order.</summary>
    Final
}

/// <summary>What a store knows about one upload.</summary>
public sealed class TusUpload
{
    /// <summary>The store's identifier, and the last segment of the upload's URL.</summary>
    public required string Id { get; init; }

    /// <summary>The total size, or null while a client that deferred it has not said yet.</summary>
    public long? Length { get; init; }

    /// <summary>
    /// How many bytes the store holds - where the next <c>PATCH</c> must start. After an interrupted
    /// request this is how far that request got, not where it began.
    /// </summary>
    public long Offset { get; init; }

    public TusMetadata Metadata { get; init; } = TusMetadata.Empty;

    public DateTimeOffset CreatedUtc { get; init; }

    /// <summary>When an unfinished upload is thrown away, or null if it never is.</summary>
    public DateTimeOffset? ExpiresUtc { get; init; }

    public TusConcatenation Concatenation { get; init; }

    /// <summary>For a final upload, the partial uploads it was joined from, in order.</summary>
    public IReadOnlyList<string> PartialUploads { get; init; } = [];

    /// <summary>Every byte has arrived. Expiry no longer applies once this is true.</summary>
    public bool IsComplete => this.Length is { } length && this.Offset >= length;
}

/// <summary>An upload about to be created.</summary>
public sealed class TusCreateRequest
{
    public long? Length { get; init; }

    public TusMetadata Metadata { get; init; } = TusMetadata.Empty;

    public DateTimeOffset? ExpiresUtc { get; init; }

    public TusConcatenation Concatenation { get; init; }

    public IReadOnlyList<string> PartialUploads { get; init; } = [];
}

/// <summary>
/// Where uploads live. <see cref="DiskTusStore"/> is the one most apps want; implement this to put
/// them somewhere else.
/// <para>
/// The endpoint serialises every request for one upload before it calls in here, so a store never
/// sees two appends to the same upload at once and needs no locking of its own for that. It does
/// have to be durable in the one way that matters: the offset it reports after an append has to
/// survive a restart, or a client that resumes will be told to start from a byte the server no
/// longer has.
/// </para>
/// </summary>
public interface ITusStore
{
    /// <summary>Creates an empty upload and returns its identifier - URL-safe, since it becomes one.</summary>
    ValueTask<string> CreateAsync(TusCreateRequest request, CancellationToken cancellationToken);

    /// <summary>The upload, or null when there is no such upload.</summary>
    ValueTask<TusUpload?> GetAsync(string id, CancellationToken cancellationToken);

    /// <summary>
    /// Appends <paramref name="data"/> at <paramref name="offset"/> and returns the new offset.
    /// <para>
    /// The contract is built around a phone losing its connection half way through, and it has two
    /// halves. The stream simply <em>ending</em> early is normal: keep every byte that was read and
    /// report the offset they reach - that is what makes the next attempt a resume rather than a
    /// restart. The stream <em>throwing</em> means this append must not count: roll back to
    /// <paramref name="offset"/> and let the exception out. The endpoint uses the second for a
    /// checksum that did not match and for a body longer than the upload, where keeping a partial
    /// write would be keeping bytes nobody vouched for.
    /// </para>
    /// <para>
    /// Throw a <see cref="TusException"/> with 409 if <paramref name="offset"/> is not the current
    /// one.
    /// </para>
    /// </summary>
    ValueTask<long> AppendAsync(string id, long offset, Stream data, CancellationToken cancellationToken);

    /// <summary>Fixes the length of an upload created with it deferred.</summary>
    ValueTask SetLengthAsync(string id, long length, CancellationToken cancellationToken);

    /// <summary>Moves (or clears) an upload's expiry - it slides forward on every append.</summary>
    ValueTask SetExpirationAsync(string id, DateTimeOffset? expiresUtc, CancellationToken cancellationToken);

    /// <summary>
    /// Fills a final upload with its partial uploads' bytes, in order. The partials are complete when
    /// this is called; the final upload is empty and should come out complete, or not at all.
    /// </summary>
    ValueTask ConcatenateAsync(string id, IReadOnlyList<string> partialIds, CancellationToken cancellationToken);

    /// <summary>The upload's bytes so far - for a completed upload, the file.</summary>
    ValueTask<Stream> OpenReadAsync(string id, CancellationToken cancellationToken);

    /// <summary>Removes an upload and its bytes. False if there was nothing to remove.</summary>
    ValueTask<bool> DeleteAsync(string id, CancellationToken cancellationToken);

    /// <summary>The unfinished uploads whose expiry is before <paramref name="nowUtc"/>.</summary>
    ValueTask<IReadOnlyList<string>> GetExpiredAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken);
}
