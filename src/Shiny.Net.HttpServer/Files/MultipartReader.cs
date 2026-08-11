using System.Buffers;
using System.IO.Pipelines;
using System.Text;

namespace Shiny.Net.HttpServer.Files;

/// <summary>
/// One part of a <c>multipart/form-data</c> body.
/// <para>
/// <see cref="Body"/> is a forward-only stream over the request, not a buffer. A 2 GB upload can be
/// copied straight to disk without the server ever holding it — which is the whole reason to parse
/// multipart incrementally rather than reading the body first and splitting it up.
/// </para>
/// </summary>
public sealed class MultipartSection
{
    internal MultipartSection(HeaderDictionary headers, Stream body)
    {
        this.Headers = headers;
        this.Body = body;
    }

    public HeaderDictionary Headers { get; }

    /// <summary>This part's content, readable until you advance to the next part.</summary>
    public Stream Body { get; }

    public string? ContentType => this.Headers.ContentType;

    /// <summary>The form field name from <c>Content-Disposition</c>.</summary>
    public string? Name => ContentDisposition.Parse(this.Headers.GetFirst(HeaderNames.ContentDisposition)).Name;

    /// <summary>The uploaded file's name, when this part is a file.</summary>
    public string? FileName => ContentDisposition.Parse(this.Headers.GetFirst(HeaderNames.ContentDisposition)).FileName;

    /// <summary>True when this part carries a file rather than a plain form value.</summary>
    public bool IsFile => this.FileName is not null;

    /// <summary>Reads this part as text. Only for form values — a file belongs on <see cref="Body"/>.</summary>
    public async ValueTask<string> ReadAsStringAsync(int maxLength = 64 * 1024, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        var rented = ArrayPool<byte>.Shared.Rent(4096);

        try
        {
            int read;
            while ((read = await this.Body.ReadAsync(rented, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > maxLength)
                    throw new BadHttpRequestException(
                        $"A form value exceeds the {maxLength} byte limit.",
                        StatusCodes.Status413PayloadTooLarge
                    );

                buffer.Write(rented, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Copies this part straight to a destination stream, never buffering it in memory.</summary>
    public Task CopyToAsync(Stream destination, CancellationToken cancellationToken = default)
        => this.Body.CopyToAsync(destination, cancellationToken);

    /// <summary>Saves this part to a file, never buffering it in memory.</summary>
    public async Task SaveToAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var file = new FileStream(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            }
        );

        await this.Body.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Streaming <c>multipart/form-data</c> parser (RFC 7578).
/// <para>
/// Reads one part at a time from the request pipe. Each part's body is a stream that stops at the
/// boundary, so nothing is buffered beyond what it takes to find the next delimiter.
/// </para>
/// </summary>
public sealed class MultipartReader
{
    readonly PipeReader reader;
    readonly byte[] boundary;

    MultipartBodyStream? current;
    bool finished;
    bool started;

    /// <summary>Maximum bytes for one part's headers.</summary>
    public int HeadersLengthLimit { get; set; } = 16 * 1024;

    /// <summary>Maximum number of parts, so a body of a million empty parts is refused.</summary>
    public int PartCountLimit { get; set; } = 256;

    int partsRead;

    public MultipartReader(PipeReader reader, string boundary)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentException.ThrowIfNullOrEmpty(boundary);

        this.reader = reader;

        // Every delimiter on the wire is CRLF + "--" + boundary. Building it once means the search
        // is a plain byte-sequence scan rather than a state machine.
        this.boundary = Encoding.ASCII.GetBytes("\r\n--" + boundary);
    }

    /// <summary>
    /// Reads the boundary out of a <c>Content-Type</c> header, or null when the request is not
    /// multipart.
    /// </summary>
    public static string? GetBoundary(string? contentType)
    {
        if (contentType is null)
            return null;

        var marker = contentType.IndexOf("boundary=", StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
            return null;

        var value = contentType[(marker + "boundary=".Length)..].Trim();

        if (value.StartsWith('"'))
        {
            var end = value.IndexOf('"', 1);
            value = end > 0 ? value[1..end] : value[1..];
        }
        else
        {
            var end = value.IndexOf(';');
            if (end >= 0)
                value = value[..end];
        }

        value = value.Trim();

        // RFC 2046 caps a boundary at 70 characters; anything longer is malformed, and treating it
        // as valid would let a client make every scan arbitrarily expensive.
        return value.Length is > 0 and <= 70 ? value : null;
    }

    /// <summary>
    /// Advances to the next part, or returns null at the end of the body. The previous part's
    /// stream is drained first, so skipping a part you do not want is safe.
    /// </summary>
    public async ValueTask<MultipartSection?> ReadNextSectionAsync(CancellationToken cancellationToken = default)
    {
        if (this.finished)
            return null;

        if (this.current is { } previous)
        {
            await previous.DrainAsync(cancellationToken).ConfigureAwait(false);
            this.current = null;
        }

        if (!this.started)
        {
            // The first delimiter has no leading CRLF. Consuming it here lets every later search
            // use the same CRLF-prefixed pattern.
            if (!await this.ReadFirstBoundaryAsync(cancellationToken).ConfigureAwait(false))
            {
                this.finished = true;
                return null;
            }

            this.started = true;
        }

        if (await this.ReadBoundarySuffixAsync(cancellationToken).ConfigureAwait(false))
        {
            this.finished = true;
            return null;
        }

        if (++this.partsRead > this.PartCountLimit)
            throw new BadHttpRequestException($"The request has more than {this.PartCountLimit} parts.");

        var headers = await this.ReadHeadersAsync(cancellationToken).ConfigureAwait(false);
        this.current = new MultipartBodyStream(this.reader, this.boundary);

        return new MultipartSection(headers, this.current);
    }

    async ValueTask<bool> ReadFirstBoundaryAsync(CancellationToken cancellationToken)
    {
        // "--boundary" — the same pattern without its leading CRLF.
        var opening = this.boundary.AsMemory(2);

        while (true)
        {
            var result = await this.reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;

            if (buffer.Length >= opening.Length)
            {
                var head = buffer.Slice(0, opening.Length).ToArray();
                if (head.AsSpan().SequenceEqual(opening.Span))
                {
                    this.reader.AdvanceTo(buffer.GetPosition(opening.Length));
                    return true;
                }

                this.reader.AdvanceTo(buffer.Start, buffer.End);
                return false;
            }

            this.reader.AdvanceTo(buffer.Start, buffer.End);

            if (result.IsCompleted || result.IsCanceled)
                return false;
        }
    }

    /// <summary>
    /// Reads what follows a delimiter: <c>--</c> for the final one, or CRLF before the next part's
    /// headers. Returns true at the end of the body.
    /// </summary>
    async ValueTask<bool> ReadBoundarySuffixAsync(CancellationToken cancellationToken)
    {
        // Two bytes, reused across iterations rather than stack-allocated inside the loop.
        var suffix = new byte[2];

        while (true)
        {
            var result = await this.reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;

            if (buffer.Length >= 2)
            {
                buffer.Slice(0, 2).CopyTo(suffix);

                if (suffix[0] == '-' && suffix[1] == '-')
                {
                    this.reader.AdvanceTo(buffer.GetPosition(2));
                    return true;
                }

                if (suffix[0] == '\r' && suffix[1] == '\n')
                {
                    this.reader.AdvanceTo(buffer.GetPosition(2));
                    return false;
                }

                // Transport padding (whitespace) is allowed between the boundary and CRLF; skip it
                // rather than rejecting a technically legal body.
                if (suffix[0] is (byte)' ' or (byte)'\t')
                {
                    this.reader.AdvanceTo(buffer.GetPosition(1));
                    continue;
                }

                throw new BadHttpRequestException("Malformed multipart boundary.");
            }

            this.reader.AdvanceTo(buffer.Start, buffer.End);

            if (result.IsCompleted || result.IsCanceled)
                return true;
        }
    }

    async ValueTask<HeaderDictionary> ReadHeadersAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var result = await this.reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;

            if (TryFindHeaderEnd(buffer, out var end))
            {
                var headers = ParseHeaders(buffer.Slice(0, end));
                this.reader.AdvanceTo(end);

                return headers;
            }

            if (buffer.Length > this.HeadersLengthLimit)
                throw new BadHttpRequestException("A multipart section's headers are too large.");

            this.reader.AdvanceTo(buffer.Start, buffer.End);

            if (result.IsCompleted || result.IsCanceled)
                throw new BadHttpRequestException("The multipart body ended inside a section's headers.");
        }
    }

    static bool TryFindHeaderEnd(in ReadOnlySequence<byte> buffer, out SequencePosition end)
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

    static HeaderDictionary ParseHeaders(ReadOnlySequence<byte> head)
    {
        var headers = new HeaderDictionary(4);
        var text = Encoding.UTF8.GetString(head.ToArray());

        foreach (var line in text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue;

            headers.Append(line[..colon].Trim(), line[(colon + 1)..].Trim());
        }

        return headers;
    }

}
