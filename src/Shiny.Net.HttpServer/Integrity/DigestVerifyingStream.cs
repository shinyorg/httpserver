using System.Security.Cryptography;

namespace Shiny.Net.HttpServer.Integrity;

/// <summary>
/// A request body that hashes itself as it is read and refuses to end if the hash is wrong.
/// <para>
/// Streaming rather than buffered: an upload headed for disk is never held in memory just to be
/// checked. The price is that the verdict only exists at the end — the last read throws instead of
/// returning zero, so a handler that deserializes the body fails before it acts, and one that streams
/// to disk sees the failure in place of a clean end and should discard what it wrote.
/// </para>
/// </summary>
sealed class DigestVerifyingStream : Stream
{
    readonly Stream inner;
    readonly (string Algorithm, byte[] Expected, IncrementalHash Hash)[] checks;
    readonly long? length;
    long read;
    bool verified;

    /// <param name="inner">The body as it arrives.</param>
    /// <param name="expected">The digests to hold it to.</param>
    /// <param name="length">
    /// The declared <c>Content-Length</c>, when there is one. A reader that knows the length stops
    /// once it has that many bytes and never makes the read that would return zero — so the verdict
    /// is given on the read that completes the body instead, and that read throws rather than handing
    /// over the last of it.
    /// </param>
    public DigestVerifyingStream(Stream inner, IReadOnlyList<KeyValuePair<string, byte[]>> expected, long? length)
    {
        this.inner = inner;
        this.length = length;
        this.checks = new (string, byte[], IncrementalHash)[expected.Count];

        for (var i = 0; i < expected.Count; i++)
            this.checks[i] = (expected[i].Key, expected[i].Value, DigestFields.CreateHash(expected[i].Key));
    }

    /// <summary>The failure thrown at the end of the body, so the middleware can recognise its own.</summary>
    public BadHttpRequestException? Failure { get; private set; }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => this.Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        this.ThrowIfFailed();
        return this.Observe(buffer, this.inner.Read(buffer));
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        this.ThrowIfFailed();

        var read = await this.inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        return this.Observe(buffer.Span, read);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => this.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    int Observe(ReadOnlySpan<byte> buffer, int read)
    {
        if (read > 0)
        {
            foreach (var check in this.checks)
                check.Hash.AppendData(buffer[..read]);

            this.read += read;

            if (this.read == this.length && !this.verified)
            {
                this.verified = true;
                this.Verify();
            }

            return read;
        }

        if (!this.verified)
        {
            this.verified = true;
            this.Verify();
        }

        return 0;
    }

    void Verify()
    {
        foreach (var (algorithm, expected, hash) in this.checks)
        {
            if (CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), expected))
                continue;

            this.Failure = new BadHttpRequestException(
                $"The request body does not match its Content-Digest ({algorithm}). It was altered or truncated on the way."
            );

            throw this.Failure;
        }
    }

    void ThrowIfFailed()
    {
        if (this.Failure is { } failure)
            throw failure;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var check in this.checks)
                check.Hash.Dispose();
        }

        base.Dispose(disposing);
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
