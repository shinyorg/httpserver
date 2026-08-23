namespace Shiny.Net.HttpServer;

/// <summary>
/// Canonical HTTP method strings plus interning helpers. Interning matters here: the parser
/// sees the method on every single request, and returning a cached constant avoids a
/// per-request string allocation.
/// </summary>
public static class HttpMethods
{
    public const string Connect = "CONNECT";
    public const string Delete = "DELETE";
    public const string Get = "GET";
    public const string Head = "HEAD";
    public const string Options = "OPTIONS";
    public const string Patch = "PATCH";
    public const string Post = "POST";
    public const string Put = "PUT";
    public const string Trace = "TRACE";

    public static bool IsGet(string method) => Equals(Get, method);
    public static bool IsHead(string method) => Equals(Head, method);
    public static bool IsPost(string method) => Equals(Post, method);
    public static bool IsPut(string method) => Equals(Put, method);
    public static bool IsPatch(string method) => Equals(Patch, method);
    public static bool IsDelete(string method) => Equals(Delete, method);
    public static bool IsOptions(string method) => Equals(Options, method);
    public static bool IsConnect(string method) => Equals(Connect, method);

    /// <summary>
    /// True for the nine methods defined in RFC 9110. Extension methods — WebDAV's PROPFIND, a
    /// private verb — are not "unknown" in any interesting sense, but telemetry has to bound what
    /// it reports on a caller-controlled value, and this is the line the semantic conventions draw.
    /// </summary>
    public static bool IsKnown(string method)
        => IsGet(method)
            || IsHead(method)
            || IsPost(method)
            || IsPut(method)
            || IsPatch(method)
            || IsDelete(method)
            || IsOptions(method)
            || IsConnect(method)
            || Equals(Trace, method);

    public static bool Equals(string a, string b)
        => ReferenceEquals(a, b) || string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the method conventionally carries a request body.</summary>
    public static bool CanHaveBody(string method)
        => IsPost(method) || IsPut(method) || IsPatch(method) || IsDelete(method);

    /// <summary>
    /// Maps a method span onto the cached constant, avoiding an allocation on the hot path.
    /// Falls back to allocating for exotic/extension methods.
    /// </summary>
    public static string GetCanonical(ReadOnlySpan<byte> method)
    {
        // Full-span compares, not first-byte guesses: "PRI" (the HTTP/2 preface) is three
        // bytes starting with 'P' and must not be mistaken for PUT.
        switch (method.Length)
        {
            case 3:
                if (method.SequenceEqual("GET"u8)) return Get;
                if (method.SequenceEqual("PUT"u8)) return Put;
                break;
            case 4:
                if (method.SequenceEqual("POST"u8)) return Post;
                if (method.SequenceEqual("HEAD"u8)) return Head;
                break;
            case 5:
                if (method.SequenceEqual("PATCH"u8)) return Patch;
                if (method.SequenceEqual("TRACE"u8)) return Trace;
                break;
            case 6:
                if (method.SequenceEqual("DELETE"u8)) return Delete;
                break;
            case 7:
                if (method.SequenceEqual("OPTIONS"u8)) return Options;
                if (method.SequenceEqual("CONNECT"u8)) return Connect;
                break;
        }
        return System.Text.Encoding.ASCII.GetString(method);
    }
}
