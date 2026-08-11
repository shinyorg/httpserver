using System.Buffers;
using System.Text;

namespace Shiny.Net.HttpServer.Http1;

/// <summary>
/// Parses one materialised line. A dedicated delegate type rather than <c>Action&lt;,&gt;</c>
/// because a <c>ReadOnlySpan&lt;byte&gt;</c> cannot be a generic type argument.
/// </summary>
delegate void LineParser(ReadOnlySpan<byte> line, HttpRequest request);

/// <summary>
/// Incremental HTTP/1.1 request parser.
/// <para>
/// State lives on the instance rather than in locals because a request's head can arrive across any
/// number of reads. Each call consumes whole lines and leaves the remainder for next time, so header
/// lines are never parsed twice and the size limits accumulate correctly across reads. Malformed
/// input throws <see cref="BadHttpRequestException"/> so the connection can answer with a specific
/// 4xx rather than a blanket failure.
/// </para>
/// </summary>
sealed class Http1RequestParser
{
    const byte CR = (byte)'\r';
    const byte LF = (byte)'\n';
    const byte Space = (byte)' ';
    const byte Colon = (byte)':';
    const byte Tab = (byte)'\t';

    enum Step
    {
        RequestLine,
        Headers,
        Complete
    }

    Step step;
    int requestLineBytes;
    int headerBytes;
    int headerCount;

    /// <summary>True once the request line has been parsed. Used to distinguish an idle connection
    /// (client politely went away between requests) from one that died mid-request.</summary>
    public bool HasStartedRequest => this.step != Step.RequestLine || this.requestLineBytes > 0;

    public void Reset()
    {
        this.step = Step.RequestLine;
        this.requestLineBytes = 0;
        this.headerBytes = 0;
        this.headerCount = 0;
    }

    /// <summary>
    /// Consumes as much of the request head as is available. Returns true once the terminating blank
    /// line has been seen, meaning the request line and all headers are populated.
    /// </summary>
    public bool TryParseRequestHead(
        ref SequenceReader<byte> reader,
        HttpRequest request,
        HttpServerLimits limits
    )
    {
        if (this.step == Step.RequestLine)
        {
            if (!this.TryParseRequestLine(ref reader, request, limits))
                return false;

            this.step = Step.Headers;
        }

        if (this.step == Step.Headers)
        {
            if (!this.TryParseHeaders(ref reader, request, limits))
                return false;

            this.step = Step.Complete;
        }

        return true;
    }

    bool TryParseRequestLine(ref SequenceReader<byte> reader, HttpRequest request, HttpServerLimits limits)
    {
        if (!reader.TryReadTo(out ReadOnlySequence<byte> lineSequence, LF, advancePastDelimiter: true))
        {
            // Guard before a full line exists, otherwise a client that never sends LF makes us
            // buffer without bound.
            if (reader.Remaining > limits.MaxRequestLineSize)
                throw new BadHttpRequestException(
                    $"Request line exceeds the {limits.MaxRequestLineSize} byte limit.",
                    StatusCodes.Status414UriTooLong
                );

            return false;
        }

        var length = (int)lineSequence.Length;
        this.requestLineBytes = length + 1;
        if (this.requestLineBytes > limits.MaxRequestLineSize)
            throw new BadHttpRequestException(
                $"Request line exceeds the {limits.MaxRequestLineSize} byte limit.",
                StatusCodes.Status414UriTooLong
            );

        WithLineBytes(lineSequence, length, request, static (line, req) => ParseRequestLine(line, req));
        return true;
    }

    bool TryParseHeaders(ref SequenceReader<byte> reader, HttpRequest request, HttpServerLimits limits)
    {
        while (true)
        {
            if (!reader.TryReadTo(out ReadOnlySequence<byte> lineSequence, LF, advancePastDelimiter: true))
            {
                if (this.headerBytes + reader.Remaining > limits.MaxRequestHeadersTotalSize)
                    throw new BadHttpRequestException(
                        $"Request headers exceed the {limits.MaxRequestHeadersTotalSize} byte limit.",
                        StatusCodes.Status431RequestHeaderFieldsTooLarge
                    );

                return false;
            }

            var length = (int)lineSequence.Length;
            this.headerBytes += length + 1;
            if (this.headerBytes > limits.MaxRequestHeadersTotalSize)
                throw new BadHttpRequestException(
                    $"Request headers exceed the {limits.MaxRequestHeadersTotalSize} byte limit.",
                    StatusCodes.Status431RequestHeaderFieldsTooLarge
                );

            // A blank line terminates the header block.
            if (IsBlankLine(lineSequence, length))
                return true;

            if (++this.headerCount > limits.MaxRequestHeaderCount)
                throw new BadHttpRequestException(
                    $"Request has more than {limits.MaxRequestHeaderCount} headers.",
                    StatusCodes.Status431RequestHeaderFieldsTooLarge
                );

            WithLineBytes(lineSequence, length, request, static (line, req) => ParseHeaderLine(line, req));
        }
    }

    static bool IsBlankLine(in ReadOnlySequence<byte> lineSequence, int length)
        => length == 0 || (length == 1 && FirstByte(lineSequence) == CR);

    static byte FirstByte(in ReadOnlySequence<byte> sequence)
    {
        var span = sequence.FirstSpan;
        return span.Length > 0 ? span[0] : (byte)0;
    }

    /// <summary>
    /// Materialises a line as a contiguous span. Single-segment lines (the common case) are handed
    /// straight through with no copy; multi-segment lines are flattened into a pooled buffer.
    /// </summary>
    static void WithLineBytes(
        in ReadOnlySequence<byte> lineSequence,
        int length,
        HttpRequest request,
        LineParser parse
    )
    {
        if (lineSequence.IsSingleSegment)
        {
            parse(TrimCarriageReturn(lineSequence.FirstSpan), request);
            return;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(length, 1));
        try
        {
            lineSequence.CopyTo(buffer);
            parse(TrimCarriageReturn(buffer.AsSpan(0, length)), request);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    static void ParseRequestLine(ReadOnlySpan<byte> line, HttpRequest request)
    {
        var firstSpace = line.IndexOf(Space);
        if (firstSpace <= 0)
            throw new BadHttpRequestException("Malformed request line: no method.");

        var method = line[..firstSpace];
        ValidateToken(method, "method");

        var rest = line[(firstSpace + 1)..];
        var secondSpace = rest.IndexOf(Space);
        if (secondSpace <= 0)
            throw new BadHttpRequestException("Malformed request line: no request target or version.");

        var target = rest[..secondSpace];
        var version = rest[(secondSpace + 1)..];

        request.Method = HttpMethods.GetCanonical(method);
        if (version.SequenceEqual("HTTP/1.1"u8))
            request.Protocol = HttpProtocols.Http11;
        else if (version.SequenceEqual("HTTP/1.0"u8))
            request.Protocol = HttpProtocols.Http10;
        else
            throw new BadHttpRequestException(
                $"Unsupported HTTP version '{Encoding.ASCII.GetString(version)}'.",
                StatusCodes.Status505HttpVersionNotSupported
            );

        ParseTarget(target, request);
    }

    static void ParseTarget(ReadOnlySpan<byte> target, HttpRequest request)
    {
        if (target.IsEmpty)
            throw new BadHttpRequestException("Malformed request line: empty request target.");

        if (target[0] != (byte)'/')
        {
            // Origin-form is what browsers send, but proxies (including our own relay) may use
            // absolute-form. Strip scheme and authority so downstream code always sees a path.
            var schemeEnd = target.IndexOf("://"u8);
            if (schemeEnd > 0)
            {
                var afterScheme = target[(schemeEnd + 3)..];
                var pathStart = afterScheme.IndexOf((byte)'/');
                target = pathStart < 0 ? "/"u8 : afterScheme[pathStart..];
            }
            else if (target.SequenceEqual("*"u8))
            {
                // Asterisk-form, legal only for OPTIONS.
                request.RawTarget = "*";
                request.Path = "*";
                request.QueryString = null;
                request.Query.SetRaw(null);
                return;
            }
            else
            {
                throw new BadHttpRequestException("Malformed request target.");
            }
        }

        var queryStart = target.IndexOf((byte)'?');
        var pathBytes = queryStart < 0 ? target : target[..queryStart];
        var queryBytes = queryStart < 0 ? default : target[(queryStart + 1)..];

        request.RawTarget = Encoding.ASCII.GetString(target);

        // Percent escapes are decoded here so route matching and route values see real text.
        request.Path = UrlDecoder.DecodePath(Encoding.UTF8.GetString(pathBytes));

        if (queryStart < 0)
        {
            request.QueryString = null;
            request.Query.SetRaw(null);
        }
        else
        {
            var query = Encoding.UTF8.GetString(queryBytes);
            request.QueryString = "?" + query;
            request.Query.SetRaw(query);
        }
    }

    static void ParseHeaderLine(ReadOnlySpan<byte> line, HttpRequest request)
    {
        if (line.IsEmpty)
            return;

        // Obsolete line folding: rejected rather than supported. It is a well-known request
        // smuggling vector and no current client needs it.
        if (line[0] == Space || line[0] == Tab)
            throw new BadHttpRequestException("Obsolete header line folding is not supported.");

        var colon = line.IndexOf(Colon);
        if (colon <= 0)
            throw new BadHttpRequestException("Malformed header: missing colon.");

        var nameBytes = line[..colon];

        // Whitespace between name and colon is illegal, and another smuggling vector.
        if (nameBytes[^1] == Space || nameBytes[^1] == Tab)
            throw new BadHttpRequestException("Malformed header: whitespace before colon.");

        ValidateToken(nameBytes, "header name");

        var valueBytes = TrimOptionalWhitespace(line[(colon + 1)..]);
        ValidateHeaderValue(valueBytes);

        request.Headers.Append(KnownHeaders.GetName(nameBytes), Encoding.UTF8.GetString(valueBytes));
    }

    static ReadOnlySpan<byte> TrimCarriageReturn(ReadOnlySpan<byte> line)
        => line.Length > 0 && line[^1] == CR ? line[..^1] : line;

    static ReadOnlySpan<byte> TrimOptionalWhitespace(ReadOnlySpan<byte> value)
    {
        var start = 0;
        while (start < value.Length && (value[start] == Space || value[start] == Tab))
            start++;

        var end = value.Length;
        while (end > start && (value[end - 1] == Space || value[end - 1] == Tab))
            end--;

        return value[start..end];
    }

    /// <summary>
    /// Validates an RFC 9110 token. Anything outside the token set — control characters, spaces,
    /// separators — is rejected outright rather than normalised.
    /// </summary>
    static void ValidateToken(ReadOnlySpan<byte> token, string what)
    {
        if (token.IsEmpty)
            throw new BadHttpRequestException($"Malformed request: empty {what}.");

        foreach (var b in token)
        {
            var isTokenChar =
                (b >= 'a' && b <= 'z') ||
                (b >= 'A' && b <= 'Z') ||
                (b >= '0' && b <= '9') ||
                b is (byte)'!' or (byte)'#' or (byte)'$' or (byte)'%' or (byte)'&' or (byte)'\'' or
                     (byte)'*' or (byte)'+' or (byte)'-' or (byte)'.' or (byte)'^' or (byte)'_' or
                     (byte)'`' or (byte)'|' or (byte)'~';

            if (!isTokenChar)
                throw new BadHttpRequestException($"Malformed request: invalid character in {what}.");
        }
    }

    static void ValidateHeaderValue(ReadOnlySpan<byte> value)
    {
        foreach (var b in value)
        {
            // Bare CR or LF inside a value would let an attacker inject extra headers.
            if (b is CR or LF or 0)
                throw new BadHttpRequestException("Malformed header: control character in value.");
        }
    }
}
