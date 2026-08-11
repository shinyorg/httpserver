using System.Buffers;
using System.Globalization;
using System.IO.Pipelines;

namespace Shiny.Net.HttpServer.Tunneling;

/// <summary>
/// Forwards exactly one request body, byte for byte, and stops on the boundary where the next
/// request head begins.
/// <para>
/// This is the piece that makes per-request routing possible on a reused connection. Forwarding
/// blindly would work right up until a client sent two requests with different Host headers down
/// one socket, at which point the second would land in the first one's tunnel. Knowing where a
/// body ends is the price of not doing that.
/// </para>
/// </summary>
static class RequestBodyForwarder
{
    /// <summary>Sends the body described by <paramref name="framing"/>. False means the client hung up mid-body.</summary>
    public static ValueTask<bool> ForwardAsync(
        PipeReader reader,
        RequestHead.Framing framing,
        Func<ReadOnlySequence<byte>, CancellationToken, ValueTask> send,
        CancellationToken cancellationToken
    )
    {
        if (framing.Chunked)
            return ForwardChunkedAsync(reader, send, cancellationToken);

        return framing.ContentLength is > 0 and var length
            ? ForwardCountedAsync(reader, length, send, cancellationToken)
            : new ValueTask<bool>(true);
    }

    static async ValueTask<bool> ForwardCountedAsync(
        PipeReader reader,
        long remaining,
        Func<ReadOnlySequence<byte>, CancellationToken, ValueTask> send,
        CancellationToken cancellationToken
    )
    {
        while (remaining > 0)
        {
            var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;

            if (buffer.IsEmpty && (result.IsCompleted || result.IsCanceled))
                return false;

            var take = buffer.Slice(0, Math.Min(buffer.Length, remaining));
            await SendChunkedAsync(take, send, cancellationToken).ConfigureAwait(false);

            remaining -= take.Length;
            reader.AdvanceTo(take.End);

            if (remaining > 0 && result.IsCompleted)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Forwards chunk by chunk. The bytes go out unchanged — sizes are parsed only to know when
    /// the terminating zero-length chunk and its trailers have gone by.
    /// </summary>
    static async ValueTask<bool> ForwardChunkedAsync(
        PipeReader reader,
        Func<ReadOnlySequence<byte>, CancellationToken, ValueTask> send,
        CancellationToken cancellationToken
    )
    {
        while (true)
        {
            var sizeLine = await ReadLineAsync(reader, send, cancellationToken).ConfigureAwait(false);
            if (sizeLine is null)
                return false;

            if (!TryParseChunkSize(sizeLine, out var size))
                return false;

            if (size == 0)
                return await ForwardTrailersAsync(reader, send, cancellationToken).ConfigureAwait(false);

            // The chunk data plus its terminating CRLF.
            if (!await ForwardCountedAsync(reader, size + 2, send, cancellationToken).ConfigureAwait(false))
                return false;
        }
    }

    static async ValueTask<bool> ForwardTrailersAsync(
        PipeReader reader,
        Func<ReadOnlySequence<byte>, CancellationToken, ValueTask> send,
        CancellationToken cancellationToken
    )
    {
        while (true)
        {
            var line = await ReadLineAsync(reader, send, cancellationToken).ConfigureAwait(false);
            if (line is null)
                return false;

            // The blank line closes the trailer section and, with it, the message.
            if (line.Length == 0)
                return true;
        }
    }

    /// <summary>
    /// Reads one CRLF-terminated line, forwarding it as it goes, and returns its content without
    /// the terminator.
    /// </summary>
    static async ValueTask<string?> ReadLineAsync(
        PipeReader reader,
        Func<ReadOnlySequence<byte>, CancellationToken, ValueTask> send,
        CancellationToken cancellationToken
    )
    {
        while (true)
        {
            var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;

            // The SequenceReader work happens in a synchronous helper: a ref struct cannot survive
            // the await that follows.
            if (TryFindLine(buffer, out var end, out var text))
            {
                await SendChunkedAsync(buffer.Slice(0, end), send, cancellationToken).ConfigureAwait(false);
                reader.AdvanceTo(end);

                return text;
            }

            reader.AdvanceTo(buffer.Start, buffer.End);

            if (result.IsCompleted || result.IsCanceled)
                return null;

            // A chunk size line that never terminates is either broken or hostile.
            if (buffer.Length > 8 * 1024)
                return null;
        }
    }

    static bool TryFindLine(in ReadOnlySequence<byte> buffer, out SequencePosition end, out string text)
    {
        var reader = new SequenceReader<byte>(buffer);
        if (reader.TryReadTo(out ReadOnlySequence<byte> line, "\r\n"u8, advancePastDelimiter: true))
        {
            end = reader.Position;
            text = System.Text.Encoding.ASCII.GetString(line.ToArray());
            return true;
        }

        end = buffer.Start;
        text = string.Empty;
        return false;
    }

    static async ValueTask SendChunkedAsync(
        ReadOnlySequence<byte> payload,
        Func<ReadOnlySequence<byte>, CancellationToken, ValueTask> send,
        CancellationToken cancellationToken
    )
    {
        while (!payload.IsEmpty)
        {
            var slice = payload.Slice(0, Math.Min(payload.Length, Tunneling.TunnelProtocol.MaxPayloadLength));
            await send(slice, cancellationToken).ConfigureAwait(false);
            payload = payload.Slice(slice.End);
        }
    }

    static bool TryParseChunkSize(string line, out long size)
    {
        // Chunk extensions ("1a;name=value") are legal and irrelevant here.
        var semicolon = line.IndexOf(';');
        var digits = (semicolon < 0 ? line : line[..semicolon]).Trim();

        return long.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out size) && size >= 0;
    }
}
