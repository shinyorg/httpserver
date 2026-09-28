using System.Security.Cryptography;

namespace Shiny.Net.HttpServer.Tus.Internal;

/// <summary>
/// A <c>PATCH</c> body as a store sees it: ends early instead of failing when the client goes away,
/// and throws when what arrived cannot be trusted.
/// <para>
/// This is how the two halves of <see cref="ITusStore.AppendAsync"/>'s contract are produced, so no
/// store has to know about either. A connection that drops mid-body is turned into an ordinary end
/// of stream, and the store keeps what it wrote - the resume. A checksum that does not match, a body
/// that runs past the upload's length, or an interruption the checksum can then no longer vouch for
/// are turned into exceptions, and the store rolls back.
/// </para>
/// <para>
/// Reads are bound to the request's own token plus the lock's release token, never to the token the
/// store passes in. The store's writes are not what should be interrupted; only the waiting for
/// bytes is.
/// </para>
/// </summary>
sealed class TusBodyStream : Stream
{
    readonly Stream inner;
    readonly long limit;
    readonly IncrementalHash? hash;
    readonly byte[]? expected;
    readonly CancellationToken token;
    bool ended;

    public TusBodyStream(Stream inner, long limit, TusChecksum? checksum, CancellationToken token)
    {
        this.inner = inner;
        this.limit = limit;
        this.token = token;

        if (checksum is not null)
        {
            this.hash = IncrementalHash.CreateHash(checksum.Algorithm);
            this.expected = checksum.Expected;
        }
    }

    /// <summary>The client stopped sending before the body was complete.</summary>
    public bool Interrupted { get; private set; }

    public long BytesRead { get; private set; }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (this.ended || buffer.IsEmpty)
            return 0;

        int read;

        try
        {
            read = await this.inner.ReadAsync(buffer, this.token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not TusException)
        {
            this.ended = true;
            this.Interrupted = true;

            // Without a checksum the bytes so far are as good as any: keep them. With one, they are
            // a prefix of something that was hashed whole, and there is nothing to check them against.
            if (this.hash is not null)
                throw new TusException(
                    TusProtocol.Status460ChecksumMismatch,
                    "The request ended before its body did, so its checksum cannot be verified."
                );

            return 0;
        }

        if (read == 0)
        {
            this.ended = true;

            if (this.hash is not null && !this.hash.GetHashAndReset().AsSpan().SequenceEqual(this.expected))
                throw new TusException(
                    TusProtocol.Status460ChecksumMismatch,
                    "The body does not match its Upload-Checksum. None of it was kept."
                );

            return 0;
        }

        this.BytesRead += read;

        if (this.BytesRead > this.limit)
        {
            this.ended = true;
            throw new TusException(
                StatusCodes.Status413PayloadTooLarge,
                "The body runs past the upload's length. None of it was kept."
            );
        }

        this.hash?.AppendData(buffer.Span[..read]);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => this.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count)
        => throw new NotSupportedException("Request bodies must be read asynchronously.");

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => this.BytesRead;
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            this.hash?.Dispose();

        base.Dispose(disposing);
    }
}

/// <summary>A parsed <c>Upload-Checksum</c>: <c>sha1 base64digest</c>.</summary>
sealed record TusChecksum(HashAlgorithmName Algorithm, byte[] Expected)
{
    /// <summary>
    /// False for a header tus calls a bad request: no space, an algorithm this server does not
    /// advertise, or a digest that is not base64 of the right length.
    /// </summary>
    public static bool TryParse(string header, out TusChecksum? checksum)
    {
        checksum = null;

        var space = header.IndexOf(' ');
        if (space <= 0)
            return false;

        var name = header[..space].Trim().ToLowerInvariant();
        var encoded = header[(space + 1)..].Trim();

        (HashAlgorithmName algorithm, int size) = name switch
        {
            "sha1" => (HashAlgorithmName.SHA1, 20),
            "sha256" => (HashAlgorithmName.SHA256, 32),
            "md5" => (HashAlgorithmName.MD5, 16),
            _ => (default, 0)
        };

        if (size == 0)
            return false;

        var digest = new byte[Math.Max(encoded.Length, size)];

        if (!Convert.TryFromBase64String(encoded, digest, out var written) || written != size)
            return false;

        checksum = new TusChecksum(algorithm, digest[..size]);
        return true;
    }
}
