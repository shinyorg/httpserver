using System.Buffers;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.IO.Pipelines;
using System.Text;

namespace Shiny.Net.HttpServer.Grpc.Internal;

/// <summary>
/// Writes gRPC message frames to the response body, flushing each one as it goes — a server-streaming
/// call whose messages sat in a buffer waiting for the next one would not be streaming at all.
/// </summary>
sealed class GrpcFrameWriter(
    PipeWriter writer,
    GrpcProtocolKind kind,
    string responseEncoding,
    int? maxMessageSize
)
{
    const byte FlagUncompressed = 0;
    const byte FlagCompressed = 1;

    /// <summary>gRPC-Web moves the trailers into the body, in a frame of their own (bit 7 set).</summary>
    const byte FlagTrailers = 0x80;

    readonly ArrayBufferWriter<byte> payload = new(4096);
    readonly ArrayBufferWriter<byte> compressed = new(1024);

    readonly bool compressResponses = !responseEncoding.Equals(
        GrpcProtocol.EncodingIdentity,
        StringComparison.OrdinalIgnoreCase
    );

    public async ValueTask WriteMessageAsync<T>(
        GrpcMarshaller<T> marshaller,
        T message,
        CancellationToken cancellationToken
    )
    {
        this.payload.Clear();
        marshaller.Write(message, this.payload);

        var body = this.payload.WrittenSpan;
        var flag = FlagUncompressed;

        if (this.compressResponses && body.Length > 0)
        {
            this.compressed.Clear();
            GrpcCompression.Compress(responseEncoding, body, this.compressed);

            // Compressing is only worth the caller's CPU if the result is actually smaller. Small
            // payloads routinely come back larger, and a message flagged compressed is one the
            // client must inflate whether it gained anything or not.
            if (this.compressed.WrittenCount < body.Length)
            {
                body = this.compressed.WrittenSpan;
                flag = FlagCompressed;
            }
        }

        if (maxMessageSize is { } max && body.Length > max)
            throw new GrpcStatusException(
                GrpcStatusCode.ResourceExhausted,
                $"Tried to send a message of {body.Length} bytes, which exceeds the {max} byte limit."
            );

        this.WriteFrame(flag, body);

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the gRPC-Web trailer frame. Over HTTP/1.1 there is nowhere else for the status to go,
    /// so it travels as the last frame of the body, formatted like a header block.
    /// </summary>
    public async ValueTask WriteTrailerFrameAsync(HeaderDictionary trailers, CancellationToken cancellationToken)
    {
        var text = new StringBuilder(64);

        foreach (var (name, values) in trailers)
        {
            foreach (var value in values)
            {
                if (value is not null)
                    text.Append(name.ToLowerInvariant()).Append(": ").Append(value).Append("\r\n");
            }
        }

        var bytes = Encoding.ASCII.GetBytes(text.ToString());
        this.WriteFrame(FlagTrailers, bytes);

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    void WriteFrame(byte flag, ReadOnlySpan<byte> body)
    {
        if (kind != GrpcProtocolKind.GrpcWebText)
        {
            var header = writer.GetSpan(5);
            header[0] = flag;
            BinaryPrimitives.WriteUInt32BigEndian(header[1..], (uint)body.Length);
            writer.Advance(5);

            writer.Write(body);
            return;
        }

        // Text mode: the body as a whole is one base64 document, so the frames are encoded as a
        // stream rather than one at a time. Only whole three-byte groups can be encoded — the
        // remainder is carried to the next frame, and padded at the end by FinishAsync.
        var frameLength = 5 + body.Length;
        var frame = frameLength <= 512 ? stackalloc byte[frameLength] : new byte[frameLength];

        frame[0] = flag;
        BinaryPrimitives.WriteUInt32BigEndian(frame[1..], (uint)body.Length);
        body.CopyTo(frame[5..]);

        if (this.carried == 0)
        {
            this.EncodeGroups(frame);
            return;
        }

        var combined = new byte[this.carried + frameLength];
        this.carry.AsSpan(0, this.carried).CopyTo(combined);
        frame.CopyTo(combined.AsSpan(this.carried));
        this.carried = 0;

        this.EncodeGroups(combined);
    }

    void EncodeGroups(ReadOnlySpan<byte> source)
    {
        var destination = writer.GetSpan(Base64.GetMaxEncodedToUtf8Length(source.Length));

        Base64.EncodeToUtf8(source, destination, out var consumed, out var written, isFinalBlock: false);
        writer.Advance(written);

        var remainder = source[consumed..];
        remainder.CopyTo(this.carry);
        this.carried = remainder.Length;
    }

    /// <summary>
    /// Closes the body. Only text mode has anything to do here: up to two bytes may be waiting for
    /// the padding that ends the base64 document.
    /// </summary>
    public async ValueTask FinishAsync(CancellationToken cancellationToken)
    {
        if (this.carried == 0)
            return;

        var destination = writer.GetSpan(4);
        Base64.EncodeToUtf8(this.carry.AsSpan(0, this.carried), destination, out _, out var written, isFinalBlock: true);
        writer.Advance(written);
        this.carried = 0;

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    readonly byte[] carry = new byte[3];
    int carried;
}
