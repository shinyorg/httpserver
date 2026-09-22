namespace Shiny.Net.HttpServer.WebDav.Internal;

/// <summary>
/// A request body that refuses to be read past <see cref="WebDavOptions.MaxUploadBytes"/>.
/// <para>
/// The limit used to be enforced by the loop that copied the body to disk. With the copy now the
/// file system's, the body is what has to enforce it, and throwing is the only way a read can say
/// "stop" to a copy it does not own: a short read would be taken for the end of the file, and the
/// truncated upload would be moved into place as though it were whole.
/// </para>
/// </summary>
sealed class UploadLimitStream(Stream inner, long limit) : Stream
{
    long total;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => this.total;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
        => this.Count(inner.Read(buffer, offset, count));

    public override int Read(Span<byte> buffer)
        => this.Count(inner.Read(buffer));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => this.Count(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => this.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    int Count(int read)
    {
        this.total += read;

        if (this.total > limit)
            throw new WebDavException(StatusCodes.Status413PayloadTooLarge, "The upload is larger than this mount accepts.");

        return read;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
