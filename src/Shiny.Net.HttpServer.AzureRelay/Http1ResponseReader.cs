using System.Buffers;
using System.Globalization;
using System.IO.Pipelines;
using System.Text;

namespace Shiny.Net.HttpServer.AzureRelay;

/// <summary>One parsed HTTP/1.1 response.</summary>
sealed class Http1Response
{
    public int StatusCode { get; init; } = 200;

    public string ReasonPhrase { get; init; } = "OK";

    public List<KeyValuePair<string, string>> Headers { get; } = [];

    /// <summary>Null when the response carries no body.</summary>
    public byte[]? Body { get; set; }
}

/// <summary>
/// Reads an HTTP/1.1 response off the wire.
/// <para>
/// Needed because Azure Relay's HTTP mode is not a byte pipe: it wants a status code, a header
/// collection and a body stream, while the server produces framed HTTP. Rather than teach the
/// server a second output shape, the response is written as normal and parsed back here — the whole
/// existing pipeline, including every result type, works unchanged.
/// </para>
/// </summary>
static class Http1ResponseReader
{
    public static async ValueTask<Http1Response> ReadAsync(
        PipeReader reader,
        int maxHeadSize,
        CancellationToken cancellationToken
    )
    {
        var response = await ReadHeadAsync(reader, maxHeadSize, cancellationToken).ConfigureAwait(false);

        var chunked = false;
        long? contentLength = null;

        foreach (var (name, value) in response.Headers)
        {
            if (name.Equals(HeaderNames.TransferEncoding, StringComparison.OrdinalIgnoreCase))
                chunked = value.Contains("chunked", StringComparison.OrdinalIgnoreCase);
            else if (name.Equals(HeaderNames.ContentLength, StringComparison.OrdinalIgnoreCase))
                contentLength = long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : null;
        }

        response.Body = chunked
            ? await ReadChunkedAsync(reader, cancellationToken).ConfigureAwait(false)
            : await ReadCountedAsync(reader, contentLength, cancellationToken).ConfigureAwait(false);

        return response;
    }

    static async ValueTask<Http1Response> ReadHeadAsync(
        PipeReader reader,
        int maxHeadSize,
        CancellationToken cancellationToken
    )
    {
        while (true)
        {
            var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;

            if (TryFindHeadEnd(buffer, out var end))
            {
                var head = Encoding.Latin1.GetString(buffer.Slice(0, end).ToArray());
                reader.AdvanceTo(end);

                return Parse(head);
            }

            if (buffer.Length > maxHeadSize)
                throw new InvalidOperationException("The response head is larger than the configured limit.");

            reader.AdvanceTo(buffer.Start, buffer.End);

            if (result.IsCompleted || result.IsCanceled)
                throw new InvalidOperationException("The response ended before its headers were complete.");
        }
    }

    static bool TryFindHeadEnd(in ReadOnlySequence<byte> buffer, out SequencePosition end)
    {
        var reader = new SequenceReader<byte>(buffer);
        if (reader.TryReadTo(out ReadOnlySequence<byte> _, "\r\n\r\n"u8, advancePastDelimiter: true))
        {
            end = reader.Position;
            return true;
        }

        end = buffer.Start;
        return false;
    }

    static Http1Response Parse(string head)
    {
        var lines = head.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
            throw new InvalidOperationException("The response has no status line.");

        // "HTTP/1.1 200 OK"
        var statusLine = lines[0].Split(' ', 3);
        if (statusLine.Length < 2 || !int.TryParse(statusLine[1], out var statusCode))
            throw new InvalidOperationException($"Malformed status line '{lines[0]}'.");

        var response = new Http1Response
        {
            StatusCode = statusCode,
            ReasonPhrase = statusLine.Length > 2 ? statusLine[2] : StatusCodes.GetReasonPhrase(statusCode)
        };

        for (var i = 1; i < lines.Length; i++)
        {
            var colon = lines[i].IndexOf(':');
            if (colon <= 0)
                continue;

            response.Headers.Add(new KeyValuePair<string, string>(
                lines[i][..colon].Trim(),
                lines[i][(colon + 1)..].Trim()
            ));
        }

        return response;
    }

    static async ValueTask<byte[]?> ReadCountedAsync(PipeReader reader, long? length, CancellationToken cancellationToken)
    {
        if (length == 0)
            return null;

        var body = new ArrayBufferWriter<byte>(length is { } known and < 64 * 1024 ? (int)known : 4096);
        var remaining = length ?? long.MaxValue;

        while (remaining > 0)
        {
            var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;

            if (!buffer.IsEmpty)
            {
                var take = (int)Math.Min(buffer.Length, remaining);
                var chunk = buffer.Slice(0, take);

                foreach (var segment in chunk)
                    body.Write(segment.Span);

                remaining -= take;
                reader.AdvanceTo(chunk.End);
            }
            else
            {
                reader.AdvanceTo(buffer.Start, buffer.End);
            }

            if (result.IsCompleted || result.IsCanceled)
                break;
        }

        return body.WrittenCount == 0 ? null : body.WrittenSpan.ToArray();
    }

    static async ValueTask<byte[]?> ReadChunkedAsync(PipeReader reader, CancellationToken cancellationToken)
    {
        var body = new ArrayBufferWriter<byte>(4096);

        while (true)
        {
            var sizeLine = await ReadLineAsync(reader, cancellationToken).ConfigureAwait(false);
            if (sizeLine is null)
                break;

            // Chunk extensions ("1a;name=value") are legal and irrelevant here.
            var semicolon = sizeLine.IndexOf(';');
            var digits = (semicolon < 0 ? sizeLine : sizeLine[..semicolon]).Trim();

            if (!int.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var size))
                throw new InvalidOperationException($"Malformed chunk size '{sizeLine}'.");

            if (size == 0)
            {
                // Trailers, then the blank line that ends the message.
                while (await ReadLineAsync(reader, cancellationToken).ConfigureAwait(false) is { Length: > 0 })
                {
                }

                break;
            }

            await ReadExactAsync(reader, size, body, cancellationToken).ConfigureAwait(false);

            // The CRLF that terminates the chunk.
            await ReadLineAsync(reader, cancellationToken).ConfigureAwait(false);
        }

        return body.WrittenCount == 0 ? null : body.WrittenSpan.ToArray();
    }

    static async ValueTask ReadExactAsync(
        PipeReader reader,
        int count,
        IBufferWriter<byte> destination,
        CancellationToken cancellationToken
    )
    {
        while (count > 0)
        {
            var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;

            if (buffer.IsEmpty && (result.IsCompleted || result.IsCanceled))
                throw new InvalidOperationException("The response body ended mid-chunk.");

            var take = (int)Math.Min(buffer.Length, count);
            var chunk = buffer.Slice(0, take);

            foreach (var segment in chunk)
                destination.Write(segment.Span);

            count -= take;
            reader.AdvanceTo(chunk.End);
        }
    }

    static async ValueTask<string?> ReadLineAsync(PipeReader reader, CancellationToken cancellationToken)
    {
        while (true)
        {
            var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;

            if (TryReadLine(buffer, out var end, out var line))
            {
                reader.AdvanceTo(end);
                return line;
            }

            reader.AdvanceTo(buffer.Start, buffer.End);

            if (result.IsCompleted || result.IsCanceled)
                return null;
        }
    }

    static bool TryReadLine(in ReadOnlySequence<byte> buffer, out SequencePosition end, out string line)
    {
        var reader = new SequenceReader<byte>(buffer);
        if (reader.TryReadTo(out ReadOnlySequence<byte> content, "\r\n"u8, advancePastDelimiter: true))
        {
            end = reader.Position;
            line = Encoding.Latin1.GetString(content.ToArray());

            return true;
        }

        end = buffer.Start;
        line = string.Empty;

        return false;
    }
}
