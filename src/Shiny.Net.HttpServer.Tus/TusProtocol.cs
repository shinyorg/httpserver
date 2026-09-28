namespace Shiny.Net.HttpServer.Tus;

/// <summary>The fixed parts of the tus 1.0.0 protocol: its version, its content type, its extra status.</summary>
public static class TusProtocol
{
    /// <summary>
    /// The only protocol version this server speaks. Sent as <c>Tus-Resumable</c> on every response
    /// and required on every request but <c>OPTIONS</c>.
    /// </summary>
    public const string Version = "1.0.0";

    /// <summary>
    /// The content type of a <c>PATCH</c> body. tus insists on it so that a proxy or a framework in
    /// front of the server does not try to parse the bytes as a form or as JSON on the way through.
    /// </summary>
    public const string OffsetOctetStream = "application/offset+octet-stream";

    /// <summary>
    /// <c>460 Checksum Mismatch</c> - the checksum extension's own status, answered when the bytes
    /// that arrived do not hash to what the client said it sent. None of them are kept.
    /// </summary>
    public const int Status460ChecksumMismatch = 460;

    /// <summary>The checksum algorithms this server verifies, in the order it advertises them.</summary>
    public static IReadOnlyList<string> ChecksumAlgorithms { get; } = ["sha1", "sha256", "md5"];
}

/// <summary>The header names tus adds to HTTP.</summary>
public static class TusHeaderNames
{
    public const string TusResumable = "Tus-Resumable";
    public const string TusVersion = "Tus-Version";
    public const string TusExtension = "Tus-Extension";
    public const string TusMaxSize = "Tus-Max-Size";
    public const string TusChecksumAlgorithm = "Tus-Checksum-Algorithm";
    public const string UploadOffset = "Upload-Offset";
    public const string UploadLength = "Upload-Length";
    public const string UploadDeferLength = "Upload-Defer-Length";
    public const string UploadMetadata = "Upload-Metadata";
    public const string UploadExpires = "Upload-Expires";
    public const string UploadConcat = "Upload-Concat";
    public const string UploadChecksum = "Upload-Checksum";

    /// <summary>
    /// Lets a client tunnel <c>PATCH</c>, <c>DELETE</c> or <c>HEAD</c> through a <c>POST</c>, for
    /// the environments - old proxies, some WebViews - that only let <c>GET</c> and <c>POST</c> out.
    /// tus-js-client sends it when told <c>overridePatchMethod: true</c>.
    /// </summary>
    public const string XHttpMethodOverride = "X-HTTP-Method-Override";

    /// <summary>
    /// Every header a tus response carries that script needs to read. A browser hides anything not
    /// on this list from a cross-origin caller, and a tus client that cannot see
    /// <c>Upload-Offset</c> or <c>Location</c> cannot resume or even start.
    /// </summary>
    public static IReadOnlyList<string> Exposed { get; } =
    [
        UploadOffset,
        UploadLength,
        UploadMetadata,
        UploadDeferLength,
        UploadExpires,
        UploadConcat,
        TusResumable,
        TusVersion,
        TusExtension,
        TusMaxSize,
        TusChecksumAlgorithm,
        HeaderNames.Location
    ];

    /// <summary>Every request header a tus client sends, for a CORS policy's allowed list.</summary>
    public static IReadOnlyList<string> Request { get; } =
    [
        TusResumable,
        UploadLength,
        UploadMetadata,
        UploadDeferLength,
        UploadOffset,
        UploadConcat,
        UploadChecksum,
        XHttpMethodOverride,
        "X-Requested-With",
        HeaderNames.ContentType
    ];
}

/// <summary>
/// A tus request refused with a specific status. A store throws it to say what went wrong in terms
/// the endpoint can answer with - 404 for an upload that is gone, 409 for an offset that no longer
/// matches - and the endpoint turns it into a problem response.
/// </summary>
public class TusException(int statusCode, string message) : Exception(message)
{
    /// <summary>The status the request is answered with.</summary>
    public int StatusCode { get; } = statusCode;
}
