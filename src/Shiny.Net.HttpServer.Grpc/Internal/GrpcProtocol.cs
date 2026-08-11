using System.Globalization;
using System.Text;

namespace Shiny.Net.HttpServer.Grpc.Internal;

/// <summary>
/// The constants and the two string formats the gRPC wire protocol defines for itself: the
/// <c>grpc-timeout</c> deadline and the percent-escaping of <c>grpc-message</c>.
/// </summary>
static class GrpcProtocol
{
    public const string HeaderTimeout = "grpc-timeout";
    public const string HeaderEncoding = "grpc-encoding";
    public const string HeaderAcceptEncoding = "grpc-accept-encoding";
    public const string HeaderStatus = "grpc-status";
    public const string HeaderMessage = "grpc-message";

    public const string ContentTypeGrpc = "application/grpc";
    public const string ContentTypeGrpcWeb = "application/grpc-web";
    public const string ContentTypeGrpcWebText = "application/grpc-web-text";

    public const string EncodingIdentity = "identity";
    public const string EncodingGzip = "gzip";
    public const string EncodingDeflate = "deflate";

    /// <summary>
    /// Decides which flavour of the protocol a content type asks for.
    /// <para>
    /// The suffix after the '+' names the message encoding — proto, json, or anything the two ends
    /// agreed on privately — and is the caller's business, not the transport's, since the
    /// marshallers already settle it. Only the framing is decided here.
    /// </para>
    /// </summary>
    public static bool TryParseContentType(string? contentType, out GrpcProtocolKind kind)
    {
        kind = GrpcProtocolKind.Grpc;

        if (contentType is null)
            return false;

        // Parameters (";charset=") are not part of the decision, and neither is case.
        var span = contentType.AsSpan();
        var semicolon = span.IndexOf(';');
        if (semicolon >= 0)
            span = span[..semicolon];

        span = span.Trim();

        // Longest first: "application/grpc" is a prefix of both of the others.
        if (Matches(span, ContentTypeGrpcWebText))
        {
            kind = GrpcProtocolKind.GrpcWebText;
            return true;
        }

        if (Matches(span, ContentTypeGrpcWeb))
        {
            kind = GrpcProtocolKind.GrpcWeb;
            return true;
        }

        if (Matches(span, ContentTypeGrpc))
        {
            kind = GrpcProtocolKind.Grpc;
            return true;
        }

        return false;

        // Either exactly the base type, or the base type with a "+format" suffix. A bare prefix
        // match would accept "application/grpc-web" as plain gRPC and frame the response wrongly.
        static bool Matches(ReadOnlySpan<char> value, string baseType)
        {
            if (!value.StartsWith(baseType, StringComparison.OrdinalIgnoreCase))
                return false;

            var rest = value[baseType.Length..];
            return rest.IsEmpty || rest[0] == '+';
        }
    }

    /// <summary>
    /// Parses <c>grpc-timeout</c>: up to eight digits followed by a unit — H, M, S, m, u or n.
    /// An unparseable value is treated as no deadline at all, because refusing the call over a
    /// malformed header would be a worse outcome than running it without a limit.
    /// </summary>
    public static TimeSpan? ParseTimeout(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length < 2 || value.Length > 9)
            return null;

        var digits = value.AsSpan(0, value.Length - 1);
        if (!long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var amount) || amount < 0)
            return null;

        return value[^1] switch
        {
            'H' => TimeSpan.FromHours(amount),
            'M' => TimeSpan.FromMinutes(amount),
            'S' => TimeSpan.FromSeconds(amount),
            'm' => TimeSpan.FromMilliseconds(amount),

            // Ticks are 100ns, so microseconds and nanoseconds are the two units that cannot be
            // expressed exactly. Rounding up keeps a 1n deadline from becoming no deadline.
            'u' => TimeSpan.FromTicks(Math.Max(1, amount * 10)),
            'n' => TimeSpan.FromTicks(Math.Max(1, (amount + 99) / 100)),
            _ => null
        };
    }

    /// <summary>
    /// Percent-escapes a status message. Only printable ASCII survives a header field intact, and
    /// an exception message can hold anything at all — including the newline that would let it
    /// forge a second header.
    /// </summary>
    public static string EscapeMessage(string message)
    {
        if (message.Length == 0)
            return message;

        var needsEscaping = false;
        foreach (var c in message)
        {
            if (c is < ' ' or > '~' || c == '%')
            {
                needsEscaping = true;
                break;
            }
        }

        if (!needsEscaping)
            return message;

        var builder = new StringBuilder(message.Length + 16);
        foreach (var b in Encoding.UTF8.GetBytes(message))
        {
            if (b is >= (byte)' ' and <= (byte)'~' && b != (byte)'%')
                builder.Append((char)b);
            else
                builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    /// <summary>True when a comma-separated encoding list names <paramref name="encoding"/>.</summary>
    public static bool Accepts(string? acceptEncoding, string encoding)
    {
        if (string.IsNullOrEmpty(acceptEncoding))
            return false;

        foreach (var range in acceptEncoding.AsSpan().Split(','))
        {
            if (acceptEncoding.AsSpan(range).Trim().Equals(encoding, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}

/// <summary>Which of the three framings a call is using. They differ only in how bytes are wrapped.</summary>
enum GrpcProtocolKind
{
    /// <summary>Native gRPC over HTTP/2, with the status in trailers.</summary>
    Grpc,

    /// <summary>gRPC-Web: the same message frames, with trailers moved into the body.</summary>
    GrpcWeb,

    /// <summary>gRPC-Web with the whole body base64 encoded, for browsers that cannot read binary.</summary>
    GrpcWebText
}
