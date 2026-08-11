using Shiny.Net.HttpServer.Http2.Hpack;

namespace Shiny.Net.HttpServer.Http3.Qpack;

/// <summary>
/// The 99 predefined entries (RFC 9204 Appendix A).
/// <para>
/// Not the same table as HPACK's, and not a superset of it — the indices differ entirely, which is
/// the single easiest way to produce headers that decode into something else. It is reproduced in
/// full and in order for that reason.
/// </para>
/// </summary>
static class QpackStaticTable
{
    public static readonly HeaderField[] Entries =
    [
        new(":authority", ""),                                  // 0
        new(":path", "/"),                                      // 1
        new("age", "0"),                                        // 2
        new("content-disposition", ""),                         // 3
        new("content-length", "0"),                             // 4
        new("cookie", ""),                                      // 5
        new("date", ""),                                        // 6
        new("etag", ""),                                        // 7
        new("if-modified-since", ""),                           // 8
        new("if-none-match", ""),                               // 9
        new("last-modified", ""),                               // 10
        new("link", ""),                                        // 11
        new("location", ""),                                    // 12
        new("referer", ""),                                     // 13
        new("set-cookie", ""),                                  // 14
        new(":method", "CONNECT"),                              // 15
        new(":method", "DELETE"),                               // 16
        new(":method", "GET"),                                  // 17
        new(":method", "HEAD"),                                 // 18
        new(":method", "OPTIONS"),                              // 19
        new(":method", "POST"),                                 // 20
        new(":method", "PUT"),                                  // 21
        new(":scheme", "http"),                                 // 22
        new(":scheme", "https"),                                // 23
        new(":status", "103"),                                  // 24
        new(":status", "200"),                                  // 25
        new(":status", "304"),                                  // 26
        new(":status", "404"),                                  // 27
        new(":status", "503"),                                  // 28
        new("accept", "*/*"),                                   // 29
        new("accept", "application/dns-message"),               // 30
        new("accept-encoding", "gzip, deflate, br"),            // 31
        new("accept-ranges", "bytes"),                          // 32
        new("access-control-allow-headers", "cache-control"),   // 33
        new("access-control-allow-headers", "content-type"),    // 34
        new("access-control-allow-origin", "*"),                // 35
        new("cache-control", "max-age=0"),                      // 36
        new("cache-control", "max-age=2592000"),                // 37
        new("cache-control", "max-age=604800"),                 // 38
        new("cache-control", "no-cache"),                       // 39
        new("cache-control", "no-store"),                       // 40
        new("cache-control", "public, max-age=31536000"),       // 41
        new("content-encoding", "br"),                          // 42
        new("content-encoding", "gzip"),                        // 43
        new("content-type", "application/dns-message"),         // 44
        new("content-type", "application/javascript"),          // 45
        new("content-type", "application/json"),                // 46
        new("content-type", "application/x-www-form-urlencoded"), // 47
        new("content-type", "image/gif"),                       // 48
        new("content-type", "image/jpeg"),                      // 49
        new("content-type", "image/png"),                       // 50
        new("content-type", "text/css"),                        // 51
        new("content-type", "text/html; charset=utf-8"),        // 52
        new("content-type", "text/plain"),                      // 53
        new("content-type", "text/plain;charset=utf-8"),        // 54
        new("range", "bytes=0-"),                               // 55
        new("strict-transport-security", "max-age=31536000"),   // 56
        new("strict-transport-security", "max-age=31536000; includesubdomains"),          // 57
        new("strict-transport-security", "max-age=31536000; includesubdomains; preload"), // 58
        new("vary", "accept-encoding"),                         // 59
        new("vary", "origin"),                                  // 60
        new("x-content-type-options", "nosniff"),               // 61
        new("x-xss-protection", "1; mode=block"),               // 62
        new(":status", "100"),                                  // 63
        new(":status", "204"),                                  // 64
        new(":status", "206"),                                  // 65
        new(":status", "302"),                                  // 66
        new(":status", "400"),                                  // 67
        new(":status", "403"),                                  // 68
        new(":status", "421"),                                  // 69
        new(":status", "425"),                                  // 70
        new(":status", "500"),                                  // 71
        new("accept-language", ""),                             // 72
        new("access-control-allow-credentials", "FALSE"),       // 73
        new("access-control-allow-credentials", "TRUE"),        // 74
        new("access-control-allow-headers", "*"),               // 75
        new("access-control-allow-methods", "get"),             // 76
        new("access-control-allow-methods", "get, post, options"), // 77
        new("access-control-allow-methods", "options"),         // 78
        new("access-control-expose-headers", "content-length"), // 79
        new("access-control-request-headers", "content-type"),  // 80
        new("access-control-request-method", "get"),            // 81
        new("access-control-request-method", "post"),           // 82
        new("alt-svc", "clear"),                                // 83
        new("authorization", ""),                               // 84
        new("content-security-policy", "script-src 'none'; object-src 'none'; base-uri 'none'"), // 85
        new("early-data", "1"),                                 // 86
        new("expect-ct", ""),                                   // 87
        new("forwarded", ""),                                   // 88
        new("if-range", ""),                                    // 89
        new("origin", ""),                                      // 90
        new("purpose", "prefetch"),                             // 91
        new("server", ""),                                      // 92
        new("timing-allow-origin", "*"),                        // 93
        new("upgrade-insecure-requests", "1"),                  // 94
        new("user-agent", ""),                                  // 95
        new("x-forwarded-for", ""),                             // 96
        new("x-frame-options", "deny"),                         // 97
        new("x-frame-options", "sameorigin")                    // 98
    ];

    static readonly Dictionary<HeaderField, int> ByPair = BuildPairIndex();
    static readonly Dictionary<string, int> ByName = BuildNameIndex();

    public static int Count => Entries.Length;

    public static bool TryGet(long index, out HeaderField field)
    {
        if (index < 0 || index >= Entries.Length)
        {
            field = default;
            return false;
        }

        field = Entries[(int)index];
        return true;
    }

    /// <summary>The index of an exact name/value match, or -1.</summary>
    public static int FindExact(string name, string value)
        => ByPair.TryGetValue(new HeaderField(name, value), out var index) ? index : -1;

    /// <summary>The index of the first entry with this name, or -1.</summary>
    public static int FindName(string name) => ByName.TryGetValue(name, out var index) ? index : -1;

    static Dictionary<HeaderField, int> BuildPairIndex()
    {
        var map = new Dictionary<HeaderField, int>(Entries.Length);

        for (var i = 0; i < Entries.Length; i++)
            map.TryAdd(Entries[i], i);

        return map;
    }

    static Dictionary<string, int> BuildNameIndex()
    {
        var map = new Dictionary<string, int>(Entries.Length, StringComparer.Ordinal);

        // First wins: the lowest index for a name is the one to reference, and later duplicates
        // only differ by value.
        for (var i = 0; i < Entries.Length; i++)
            map.TryAdd(Entries[i].Name, i);

        return map;
    }
}
