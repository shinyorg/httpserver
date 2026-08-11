using System.Buffers;
using System.Globalization;
using System.IO.Pipelines;
using System.Text;

namespace Shiny.Net.HttpServer.Tunneling;

/// <summary>
/// Just enough HTTP parsing to route correctly.
/// <para>
/// The relay forwards bytes; it does not serve HTTP. But it cannot forward blindly either. It has
/// to know which tunnel each request belongs to, and that answer lives in the Host header — which
/// can change from one request to the next on a connection a client is reusing. So it reads every
/// request head, and reads exactly enough framing (Content-Length, chunked) to know where one
/// request's body ends and the next request's head begins. Nothing else is interpreted, and the
/// bytes that go down the tunnel are the bytes that arrived.
/// </para>
/// </summary>
static class RequestHead
{
    public readonly record struct Framing(long? ContentLength, bool Chunked)
    {
        public bool HasBody => this.Chunked || this.ContentLength > 0;
    }

    public readonly record struct Result(byte[] Bytes, string? Host, Framing Framing, bool Complete)
    {
        public static readonly Result Incomplete = new([], null, default, false);
    }

    /// <summary>Reads up to and including the blank line that ends the request head.</summary>
    public static async ValueTask<Result> ReadAsync(
        PipeReader reader,
        int maxSize,
        CancellationToken cancellationToken
    )
    {
        while (true)
        {
            var read = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = read.Buffer;

            var sequenceReader = new SequenceReader<byte>(buffer);
            if (sequenceReader.TryReadTo(out ReadOnlySequence<byte> _, "\r\n\r\n"u8, advancePastDelimiter: true))
            {
                var end = sequenceReader.Position;
                var head = buffer.Slice(0, end).ToArray();
                reader.AdvanceTo(end);

                return new Result(head, FindHost(head), FindFraming(head), true);
            }

            if (buffer.Length > maxSize)
            {
                reader.AdvanceTo(buffer.Start, buffer.End);
                return Result.Incomplete;
            }

            reader.AdvanceTo(buffer.Start, buffer.End);

            if (read.IsCompleted || read.IsCanceled)
                return Result.Incomplete;
        }
    }

    /// <summary>Returns the Host header value with any port stripped, lowercased.</summary>
    static string? FindHost(ReadOnlySpan<byte> head)
    {
        foreach (var line in HeaderLines(head))
        {
            if (line.Length > 5 && Ascii.EqualsIgnoreCase(line[..5], "host:"u8))
                return Normalize(Encoding.ASCII.GetString(line[5..]));
        }

        return null;
    }

    static Framing FindFraming(ReadOnlySpan<byte> head)
    {
        long? contentLength = null;
        var chunked = false;

        foreach (var line in HeaderLines(head))
        {
            if (line.Length > 15 && Ascii.EqualsIgnoreCase(line[..15], "content-length:"u8))
            {
                if (long.TryParse(
                        Encoding.ASCII.GetString(line[15..]).Trim(),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var parsed
                    ) && parsed >= 0)
                    contentLength = parsed;
            }
            else if (line.Length > 18 && Ascii.EqualsIgnoreCase(line[..18], "transfer-encoding:"u8))
            {
                chunked = Encoding.ASCII.GetString(line[18..])
                    .Contains("chunked", StringComparison.OrdinalIgnoreCase);
            }
        }

        // Chunked wins if both are present. The tunnelled server rejects that combination anyway;
        // the relay only needs a consistent answer for where the body ends.
        return new Framing(chunked ? null : contentLength, chunked);
    }

    /// <summary>Enumerates header lines, skipping the request line and the terminating blank line.</summary>
    static HeaderLineEnumerator HeaderLines(ReadOnlySpan<byte> head) => new(head);

    ref struct HeaderLineEnumerator
    {
        ReadOnlySpan<byte> remaining;

        public HeaderLineEnumerator(ReadOnlySpan<byte> head)
        {
            // Skip the request line; a Host-looking string inside it is not a header.
            var requestLineEnd = head.IndexOf("\r\n"u8);
            this.remaining = requestLineEnd < 0 ? default : head[(requestLineEnd + 2)..];
            this.Current = default;
        }

        public ReadOnlySpan<byte> Current { get; private set; }

        public readonly HeaderLineEnumerator GetEnumerator() => this;

        public bool MoveNext()
        {
            while (true)
            {
                if (this.remaining.IsEmpty)
                    return false;

                var lineEnd = this.remaining.IndexOf("\r\n"u8);
                if (lineEnd < 0)
                    return false;

                if (lineEnd == 0)
                    return false;

                this.Current = this.remaining[..lineEnd];
                this.remaining = this.remaining[(lineEnd + 2)..];
                return true;
            }
        }
    }

    static string? Normalize(string value)
    {
        var host = value.Trim();
        if (host.Length == 0)
            return null;

        // Strip the port, but not from a bracketed IPv6 literal, which is full of colons.
        if (host.StartsWith('['))
        {
            var bracket = host.IndexOf(']');
            if (bracket > 0)
                host = host[..(bracket + 1)];
        }
        else
        {
            var colon = host.IndexOf(':');
            if (colon >= 0)
                host = host[..colon];
        }

        return host.ToLowerInvariant();
    }

    /// <summary>
    /// Inserts forwarding headers immediately after the request line, so the tunnelled server can
    /// see who the original client was instead of the relay.
    /// </summary>
    public static byte[] WithForwardedHeaders(byte[] head, string? clientIp, string scheme, string host)
    {
        var requestLineEnd = head.AsSpan().IndexOf("\r\n"u8);
        if (requestLineEnd < 0)
            return head;

        var builder = new StringBuilder();
        if (clientIp is not null)
            builder.Append("X-Forwarded-For: ").Append(clientIp).Append("\r\n");

        builder.Append("X-Forwarded-Proto: ").Append(scheme).Append("\r\n");
        builder.Append("X-Forwarded-Host: ").Append(host).Append("\r\n");

        var injected = Encoding.ASCII.GetBytes(builder.ToString());
        var result = new byte[head.Length + injected.Length];

        var insertAt = requestLineEnd + 2;
        head.AsSpan(0, insertAt).CopyTo(result);
        injected.CopyTo(result, insertAt);
        head.AsSpan(insertAt).CopyTo(result.AsSpan(insertAt + injected.Length));

        return result;
    }
}
