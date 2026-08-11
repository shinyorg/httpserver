using System.Diagnostics.CodeAnalysis;
using System.Text;
using Shiny.Net.HttpServer.Http2.Hpack;
using Shiny.Net.HttpServer.Internal;

namespace Shiny.Net.HttpServer.Http3;

/// <summary>
/// Turns a decoded HTTP/3 field section into an <see cref="HttpRequest"/>.
/// <para>
/// The same message rules as HTTP/2 — pseudo-headers first, lowercase names, no connection-specific
/// headers — because HTTP/3 inherited the semantics wholesale and only changed how the bytes get
/// there. The validation is repeated rather than shared because the two differ in exactly one
/// respect that matters: HTTP/3 has no <c>Upgrade</c> or <c>TE</c> negotiation to make exceptions
/// for.
/// </para>
/// </summary>
static class Http3RequestMapper
{
    public static bool TryApply(
        HttpContext context,
        List<HeaderField> fields,
        byte[] body,
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
                error = $"Connection-specific header '{field.Name}' is not allowed in HTTP/3.";
                return false;
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

        request.Method = HttpMethods.GetCanonical(Encoding.ASCII.GetBytes(method));
        request.Scheme = scheme;
        request.RawTarget = path;

        var query = path.IndexOf('?');
        var rawPath = query < 0 ? path : path[..query];

        request.Path = UrlDecoder.DecodePath(rawPath);
        request.QueryString = query < 0 ? null : path[query..];
        request.Query.SetRaw(query < 0 ? null : path[(query + 1)..]);

        // :authority is HTTP/3's Host. Publishing it as Host as well keeps routing and absolute-URL
        // building working unchanged across all three protocol versions.
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
        request.Body = new MemoryStream(body, writable: false);

        error = null;
        return true;
    }

    /// <summary>
    /// Headers that describe a single hop. In HTTP/3 there is no hop to describe — QUIC owns
    /// connection management — so their presence means a message that was translated carelessly
    /// from HTTP/1.1, and passing it on invites request smuggling.
    /// </summary>
    static bool IsConnectionSpecific(string name) => name is
        "connection" or "keep-alive" or "proxy-connection" or "transfer-encoding" or "upgrade" or "te";

    /// <summary>
    /// Rejoins split cookie fields.
    /// <para>
    /// Clients are encouraged to send each cookie as its own field, since that compresses better —
    /// so the header has to be reassembled before anything can parse it as a cookie string.
    /// </para>
    /// </summary>
    static string? JoinCookies(HttpRequest request)
    {
        if (!request.Headers.TryGetValue(HeaderNames.Cookie, out var values) || values.Count == 0)
            return null;

        return values.Count == 1 ? values[0] : string.Join("; ", values.ToArray());
    }
}
