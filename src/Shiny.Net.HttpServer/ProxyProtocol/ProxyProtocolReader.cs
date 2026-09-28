namespace Shiny.Net.HttpServer.Transports;

/// <summary>
/// Reads a PROXY header off the front of a raw transport, before TLS and before HTTP detection.
/// <para>
/// Reads in chunks rather than a byte at a time, so it usually over-reads: the v1 form has no
/// length prefix, and the balancer's next bytes (a ClientHello, a request line, the HTTP/2 preface)
/// commonly arrive in the same segment. Those bytes are not lost — they come back as a
/// <see cref="PrefixedStream"/> that replays them ahead of the socket, so SslStream and the pipe
/// reader downstream see the connection exactly as if the header had never been there.
/// </para>
/// </summary>
static class ProxyProtocolReader
{
    /// <summary>
    /// Returns the parsed header (null when none was sent and none was required) and the stream the
    /// rest of the connection should be read from.
    /// </summary>
    public static async ValueTask<(ProxyProtocolInfo? Info, Stream Transport)> ReadAsync(
        Stream transport,
        ProxyProtocolOptions options,
        CancellationToken cancellationToken
    )
    {
        var required = options.Mode == ProxyProtocolMode.Required;

        // Sized for the common case in one read: a v1 line, or a v2 header with an IPv6 or unix
        // address block and a few TLVs. Grown only when a v2 length says so, and only up to the cap.
        var buffer = new byte[Math.Min(512, options.MaxHeaderSize)];
        var filled = 0;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.HeaderTimeout);

        while (true)
        {
            var status = ProxyProtocolParser.TryParse(
                buffer.AsSpan(0, filled),
                options.MaxHeaderSize,
                out var info,
                out var consumed,
                out var needed
            );

            switch (status)
            {
                case ProxyProtocolParseStatus.Complete:
                    return (info, Wrap(transport, buffer, consumed, filled));

                case ProxyProtocolParseStatus.NotProxyProtocol:
                    if (required)
                        throw new ProxyProtocolException("Connection did not open with a PROXY protocol header.");

                    return (null, Wrap(transport, buffer, 0, filled));
            }

            if (needed > buffer.Length)
                Array.Resize(ref buffer, needed);

            int read;
            try
            {
                read = await transport.ReadAsync(buffer.AsMemory(filled), timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new ProxyProtocolException($"No complete PROXY protocol header within {options.HeaderTimeout}.");
            }

            if (read == 0)
                throw new ProxyProtocolException("Connection closed before the PROXY protocol header was complete.");

            filled += read;
        }
    }

    static Stream Wrap(Stream transport, byte[] buffer, int start, int end)
        => start == end
            ? transport
            : new PrefixedStream(transport, buffer.AsMemory(start, end - start));
}

/// <summary>
/// A stream that yields some already-read bytes before reading on from the one it wraps. Writes,
/// flushes and disposal go straight through.
/// </summary>
sealed class PrefixedStream(Stream inner, ReadOnlyMemory<byte> prefix) : Stream
{
    ReadOnlyMemory<byte> prefix = prefix;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => this.Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (this.TryTakePrefix(buffer, out var taken))
            return taken;

        return inner.Read(buffer);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => this.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (this.TryTakePrefix(buffer.Span, out var taken))
            return new ValueTask<int>(taken);

        return inner.ReadAsync(buffer, cancellationToken);
    }

    bool TryTakePrefix(Span<byte> destination, out int taken)
    {
        taken = 0;
        if (this.prefix.IsEmpty)
            return false;

        // A zero-byte read is a "tell me when data is available" probe — SslStream opens its
        // handshake with one. Data is available right here, so it must complete now; passing it to
        // the socket would wait for bytes the client already sent and we already hold.
        if (destination.IsEmpty)
            return true;

        taken = Math.Min(destination.Length, this.prefix.Length);
        this.prefix.Span[..taken].CopyTo(destination);
        this.prefix = this.prefix[taken..];
        return true;
    }

    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => inner.WriteAsync(buffer, offset, count, cancellationToken);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => inner.WriteAsync(buffer, cancellationToken);

    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            inner.Dispose();

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
