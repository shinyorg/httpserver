using System.Buffers;
using System.IO.Compression;

namespace Shiny.Net.HttpServer.Grpc.Internal;

/// <summary>
/// Per-message compression. gRPC compresses each message individually and flags it in the frame
/// header, rather than compressing the stream — which is what lets a single response mix compressed
/// and uncompressed messages, and what stops a compressed stream from stalling a long-lived one.
/// </summary>
static class GrpcCompression
{
    public static bool IsSupported(string encoding)
        => encoding.Equals(GrpcProtocol.EncodingGzip, StringComparison.OrdinalIgnoreCase)
        || encoding.Equals(GrpcProtocol.EncodingDeflate, StringComparison.OrdinalIgnoreCase)
        || encoding.Equals(GrpcProtocol.EncodingIdentity, StringComparison.OrdinalIgnoreCase);

    public static void Compress(string encoding, ReadOnlySpan<byte> source, IBufferWriter<byte> destination)
    {
        using var buffer = new MemoryStream(source.Length);

        using (var compressor = Create(encoding, buffer, compress: true))
            compressor.Write(source);

        destination.Write(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
    }

    /// <summary>
    /// Decompresses one message, refusing to produce more than <paramref name="maxLength"/> bytes.
    /// <para>
    /// The limit is not a nicety. A few compressed kilobytes can expand to gigabytes, and a decoder
    /// that only checks the size it received has already allocated the size it did not.
    /// </para>
    /// </summary>
    public static void Decompress(
        string encoding,
        ReadOnlySequence<byte> source,
        IBufferWriter<byte> destination,
        int? maxLength
    )
    {
        using var input = new MemoryStream(source.ToArray(), writable: false);
        using var decompressor = Create(encoding, input, compress: false);

        var remaining = maxLength ?? int.MaxValue;

        while (true)
        {
            // One byte past the limit, so an oversized message is caught by a read that overshoots
            // rather than by a read that stops exactly on the boundary and looks complete.
            var span = destination.GetSpan((int)Math.Min((long)remaining + 1, 16 * 1024));
            var read = decompressor.Read(span);

            if (read == 0)
                return;

            if (read > remaining)
                throw new GrpcStatusException(
                    GrpcStatusCode.ResourceExhausted,
                    $"A compressed message expanded past the {maxLength} byte limit."
                );

            destination.Advance(read);
            remaining -= read;
        }
    }

    static Stream Create(string encoding, Stream inner, bool compress)
    {
        var mode = compress ? CompressionMode.Compress : CompressionMode.Decompress;

        if (encoding.Equals(GrpcProtocol.EncodingGzip, StringComparison.OrdinalIgnoreCase))
            return new GZipStream(inner, mode, leaveOpen: true);

        if (encoding.Equals(GrpcProtocol.EncodingDeflate, StringComparison.OrdinalIgnoreCase))
            return new DeflateStream(inner, mode, leaveOpen: true);

        throw new GrpcStatusException(
            GrpcStatusCode.Unimplemented,
            $"The message encoding '{encoding}' is not supported by this server."
        );
    }
}
