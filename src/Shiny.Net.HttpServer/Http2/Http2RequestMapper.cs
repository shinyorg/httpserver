using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using Shiny.Net.HttpServer.Http2.Hpack;

namespace Shiny.Net.HttpServer.Http2;

/// <summary>
/// Turns a decoded HTTP/2 header list into an <see cref="HttpContext"/>.
/// <para>
/// HTTP/2 replaces the request line with pseudo-headers, so this is where <c>:method</c>,
/// <c>:path</c>, <c>:scheme</c> and <c>:authority</c> become the request the rest of the server
/// already understands. Everything above the transport then works unchanged — the same routes, the
/// same binders, the same handlers.
/// </para>
/// </summary>
static class Http2RequestMapper
{
    public static bool TryApply(
        HttpContext context,
        List<HeaderField> fields,
        Http2Stream stream,
        [NotNullWhen(false)] out string? error
    )
    {
        string? method = null;
        string? path = null;
        string? scheme = null;
        string? authority = null;

        var seenRegular = false;
        var request = context.Request;

        foreach (var field in fields)
        {
            if (field.Name.Length == 0)
            {
                error = "An empty header name is not valid.";
                return false;
            }

            if (field.Name[0] == ':')
            {
                // All pseudo-headers must precede the regular ones; a peer that interleaves them is
                // either broken or trying to confuse a downstream parser.
                if (seenRegular)
                {
                    error = "A pseudo-header followed a regular header.";
                    return false;
                }

                switch (field.Name)
                {
                    case ":method" when method is null:
                        method = field.Value;
                        break;

                    case ":path" when path is null:
                        path = field.Value;
                        break;

                    case ":scheme" when scheme is null:
                        scheme = field.Value;
                        break;

                    case ":authority" when authority is null:
                        authority = field.Value;
                        break;

                    default:
                        error = $"Unexpected or duplicated pseudo-header '{field.Name}'.";
                        return false;
                }

                continue;
            }

            // Header names are lowercase on the wire in HTTP/2; an uppercase one is malformed.
            foreach (var c in field.Name)
            {
                if (c is >= 'A' and <= 'Z')
                {
                    error = $"Header name '{field.Name}' is not lowercase.";
                    return false;
                }
            }

            if (IsConnectionSpecific(field.Name))
            {
                // TE is allowed, but only with the value "trailers".
                if (!field.Name.Equals("te", StringComparison.Ordinal) || field.Value != "trailers")
                {
                    error = $"Connection-specific header '{field.Name}' is not allowed in HTTP/2.";
                    return false;
                }
            }

            seenRegular = true;
            request.Headers.Append(field.Name, field.Value);
        }

        if (method is null || path is null || scheme is null)
        {
            error = "A request is missing :method, :path or :scheme.";
            return false;
        }

        if (path.Length == 0)
        {
            error = ":path must not be empty.";
            return false;
        }

        request.Method = HttpMethods.GetCanonical(System.Text.Encoding.ASCII.GetBytes(method));
        request.Scheme = scheme;
        request.RawTarget = path;

        var query = path.IndexOf('?');
        var rawPath = query < 0 ? path : path[..query];

        request.Path = UrlDecoder.DecodePath(rawPath);
        request.QueryString = query < 0 ? null : path[query..];
        request.Query.SetRaw(query < 0 ? null : path[(query + 1)..]);

        // :authority is HTTP/2's Host. Publishing it as Host too means routing, absolute-URL
        // building and anything else that reads Host keeps working across both protocols.
        if (authority is { Length: > 0 })
        {
            request.Host = authority;

            if (!request.Headers.ContainsKey(HeaderNames.Host))
                request.Headers.Set(HeaderNames.Host, authority);
        }
        else
        {
            request.Host = request.Headers.GetFirst(HeaderNames.Host);
        }

        request.Cookies.SetRaw(JoinCookies(request));
        request.Body = new Http2RequestBodyStream(stream.RequestBodyReader);

        error = null;
        return true;
    }

    /// <summary>
    /// HTTP/2 lets a client split Cookie into several fields for better compression; they have to
    /// be rejoined with "; " before anything can parse them (RFC 9113 §8.2.3).
    /// </summary>
    static string? JoinCookies(HttpRequest request)
    {
        var values = request.Headers[HeaderNames.Cookie];

        return values.Count switch
        {
            0 => null,
            1 => values[0],
            _ => string.Join("; ", values.ToArray())
        };
    }

    static bool IsConnectionSpecific(string name) => name is "connection" or "transfer-encoding"
        or "keep-alive" or "upgrade" or "proxy-connection" or "te";
}

/// <summary>The request body, read from the pipe the connection's read loop fills.</summary>
sealed class Http2RequestBodyStream(PipeReader reader) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
            return 0;

        while (true)
        {
            var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var available = result.Buffer;

            if (!available.IsEmpty)
            {
                var take = (int)Math.Min(available.Length, buffer.Length);
                available.Slice(0, take).CopyTo(buffer.Span);
                reader.AdvanceTo(available.GetPosition(take));

                return take;
            }

            reader.AdvanceTo(available.Start, available.End);

            if (result.IsCompleted || result.IsCanceled)
                return 0;
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => this.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count)
        => this.ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
