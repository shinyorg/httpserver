using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;

namespace Shiny.Net.HttpServer.Grpc.Internal;

/// <summary>
/// Reads length-prefixed gRPC message frames off the request body.
/// <para>
/// A frame is one flag byte — 1 means the payload is compressed — then a four-byte big-endian
/// length, then the payload. That is the entire framing: message boundaries are explicit, so a
/// stream of them needs no delimiter and no end marker beyond the end of the body itself.
/// </para>
/// </summary>
sealed class GrpcFrameReader<T>(
    PipeReader reader,
    GrpcMarshaller<T> marshaller,
    string requestEncoding,
    int? maxMessageSize
)
{
    /// <summary>
    /// Reads the next message, or reports false at the end of the stream.
    /// <para>
    /// The payload is deserialized here rather than handed back, because it points into the pipe's
    /// own buffer and stops being valid the moment the reader advances past it.
    /// </para>
    /// </summary>
    public async ValueTask<(bool HasValue, T Value)> TryReadAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;

            if (this.TryParse(ref buffer, out var message))
            {
                // Consumed up to the end of the message; whatever follows stays for the next read.
                reader.AdvanceTo(buffer.Start);
                return (true, message!);
            }

            if (result.IsCompleted)
            {
                reader.AdvanceTo(buffer.Start, buffer.End);

                if (buffer.Length > 0)
                    throw new GrpcStatusException(
                        GrpcStatusCode.Internal,
                        "The request body ended in the middle of a message frame."
                    );

                return (false, default!);
            }

            if (result.IsCanceled)
                throw new OperationCanceledException(cancellationToken);

            // Nothing complete yet: everything was examined, nothing consumed.
            reader.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    /// <summary>Reads the one message a unary or server-streaming call is defined to carry.</summary>
    public async ValueTask<T> ReadSingleAsync(CancellationToken cancellationToken)
    {
        var (hasValue, message) = await this.TryReadAsync(cancellationToken).ConfigureAwait(false);
        if (!hasValue)
            throw new GrpcStatusException(GrpcStatusCode.Internal, "The request carried no message.");

        var (extra, _) = await this.TryReadAsync(cancellationToken).ConfigureAwait(false);
        if (extra)
            throw new GrpcStatusException(
                GrpcStatusCode.Internal,
                "The request carried more than one message, but this method takes exactly one."
            );

        return message;
    }

    /// <summary>The request messages as a stream, for client-streaming and duplex methods.</summary>
    public async IAsyncEnumerable<T> ReadAllAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        while (true)
        {
            var (hasValue, message) = await this.TryReadAsync(cancellationToken).ConfigureAwait(false);
            if (!hasValue)
                yield break;

            yield return message;
        }
    }

    bool TryParse(ref ReadOnlySequence<byte> buffer, out T? message)
    {
        message = default;

        if (buffer.Length < 5)
            return false;

        Span<byte> header = stackalloc byte[5];
        buffer.Slice(0, 5).CopyTo(header);

        var compressed = header[0] switch
        {
            0 => false,
            1 => true,
            var other => throw new GrpcStatusException(
                GrpcStatusCode.Internal,
                $"Unrecognised gRPC message flag 0x{other:X2}."
            )
        };

        var length = BinaryPrimitives.ReadUInt32BigEndian(header[1..]);

        // Checked against the prefix, before waiting for the bytes: the declared length is the only
        // warning we get, and buffering a 3GB message to discover it is too big defeats the limit.
        if (maxMessageSize is { } max && length > (uint)max)
            throw new GrpcStatusException(
                GrpcStatusCode.ResourceExhausted,
                $"Received a message of {length} bytes, which exceeds the {max} byte limit."
            );

        if (buffer.Length < 5 + length)
            return false;

        var payload = buffer.Slice(5, length);

        if (compressed)
        {
            if (requestEncoding.Equals(GrpcProtocol.EncodingIdentity, StringComparison.OrdinalIgnoreCase))
                throw new GrpcStatusException(
                    GrpcStatusCode.Internal,
                    "A message is flagged as compressed but the request declared no grpc-encoding."
                );

            var decompressed = new ArrayBufferWriter<byte>((int)Math.Min(length * 4 + 64, 64 * 1024));
            GrpcCompression.Decompress(requestEncoding, payload, decompressed, maxMessageSize);

            message = marshaller.Read(new ReadOnlySequence<byte>(decompressed.WrittenMemory));
        }
        else
        {
            message = marshaller.Read(payload);
        }

        buffer = buffer.Slice(5 + length);
        return true;
    }
}
