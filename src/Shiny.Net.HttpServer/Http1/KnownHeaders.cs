using System.Text;

namespace Shiny.Net.HttpServer.Http1;

/// <summary>
/// Maps header-name bytes onto interned constants. Worth the switch: every request carries a
/// handful of these, and returning a cached string turns a per-header allocation into a compare.
/// </summary>
static class KnownHeaders
{
    public static string GetName(ReadOnlySpan<byte> name)
    {
        switch (name.Length)
        {
            case 4:
                if (Matches(name, "Host"u8)) return HeaderNames.Host;
                if (Matches(name, "Date"u8)) return HeaderNames.Date;
                if (Matches(name, "ETag"u8)) return HeaderNames.ETag;
                break;
            case 5:
                if (Matches(name, "Range"u8)) return HeaderNames.Range;
                break;
            case 6:
                if (Matches(name, "Accept"u8)) return HeaderNames.Accept;
                if (Matches(name, "Cookie"u8)) return HeaderNames.Cookie;
                if (Matches(name, "Expect"u8)) return HeaderNames.Expect;
                if (Matches(name, "Server"u8)) return HeaderNames.Server;
                break;
            case 7:
                if (Matches(name, "Upgrade"u8)) return HeaderNames.Upgrade;
                break;
            case 10:
                if (Matches(name, "Connection"u8)) return HeaderNames.Connection;
                if (Matches(name, "User-Agent"u8)) return HeaderNames.UserAgent;
                if (Matches(name, "Keep-Alive"u8)) return HeaderNames.KeepAlive;
                if (Matches(name, "Set-Cookie"u8)) return HeaderNames.SetCookie;
                break;
            case 12:
                if (Matches(name, "Content-Type"u8)) return HeaderNames.ContentType;
                break;
            case 13:
                if (Matches(name, "Authorization"u8)) return HeaderNames.Authorization;
                if (Matches(name, "Cache-Control"u8)) return HeaderNames.CacheControl;
                if (Matches(name, "Last-Modified"u8)) return HeaderNames.LastModified;
                break;
            case 14:
                if (Matches(name, "Content-Length"u8)) return HeaderNames.ContentLength;
                break;
            case 15:
                if (Matches(name, "Accept-Encoding"u8)) return HeaderNames.AcceptEncoding;
                if (Matches(name, "X-Forwarded-For"u8)) return HeaderNames.XForwardedFor;
                break;
            case 16:
                if (Matches(name, "Content-Encoding"u8)) return HeaderNames.ContentEncoding;
                if (Matches(name, "X-Forwarded-Host"u8)) return HeaderNames.XForwardedHost;
                break;
            case 17:
                if (Matches(name, "Transfer-Encoding"u8)) return HeaderNames.TransferEncoding;
                if (Matches(name, "X-Forwarded-Proto"u8)) return HeaderNames.XForwardedProto;
                if (Matches(name, "Sec-WebSocket-Key"u8)) return HeaderNames.SecWebSocketKey;
                break;
            case 19:
                if (Matches(name, "Content-Disposition"u8)) return HeaderNames.ContentDisposition;
                break;
            case 21:
                if (Matches(name, "Sec-WebSocket-Version"u8)) return HeaderNames.SecWebSocketVersion;
                break;
        }
        return Encoding.ASCII.GetString(name);
    }

    /// <summary>
    /// Case-insensitive compare against an ASCII literal. Header names are case-insensitive per
    /// spec, and real clients disagree on casing constantly.
    /// </summary>
    static bool Matches(ReadOnlySpan<byte> actual, ReadOnlySpan<byte> expected)
    {
        for (var i = 0; i < expected.Length; i++)
        {
            // OR 0x20 lowercases ASCII letters and leaves '-' and digits alone.
            if ((actual[i] | 0x20) != (expected[i] | 0x20))
                return false;
        }
        return true;
    }
}
