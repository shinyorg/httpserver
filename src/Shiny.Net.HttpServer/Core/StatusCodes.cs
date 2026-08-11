namespace Shiny.Net.HttpServer;

/// <summary>HTTP status codes and their standard reason phrases.</summary>
public static class StatusCodes
{
    public const int Status100Continue = 100;
    public const int Status101SwitchingProtocols = 101;
    public const int Status200OK = 200;
    public const int Status201Created = 201;
    public const int Status202Accepted = 202;
    public const int Status204NoContent = 204;
    public const int Status206PartialContent = 206;

    /// <summary>
    /// One status per resource rather than one for the request. RFC 4918 — what a WebDAV
    /// <c>PROPFIND</c>, <c>PROPPATCH</c>, <c>COPY</c>, <c>MOVE</c> or <c>DELETE</c> answers when the
    /// operation spanned more than one resource and they did not all end the same way.
    /// </summary>
    public const int Status207MultiStatus = 207;

    public const int Status301MovedPermanently = 301;
    public const int Status302Found = 302;
    public const int Status303SeeOther = 303;
    public const int Status304NotModified = 304;
    public const int Status307TemporaryRedirect = 307;
    public const int Status308PermanentRedirect = 308;
    public const int Status400BadRequest = 400;
    public const int Status401Unauthorized = 401;
    public const int Status403Forbidden = 403;
    public const int Status404NotFound = 404;
    public const int Status405MethodNotAllowed = 405;
    public const int Status406NotAcceptable = 406;
    public const int Status408RequestTimeout = 408;
    public const int Status409Conflict = 409;
    public const int Status411LengthRequired = 411;

    /// <summary>The <c>If-Match</c> / <c>If-None-Match</c> the caller supplied did not hold.</summary>
    public const int Status412PreconditionFailed = 412;

    public const int Status413PayloadTooLarge = 413;
    public const int Status414UriTooLong = 414;
    public const int Status415UnsupportedMediaType = 415;
    public const int Status416RangeNotSatisfiable = 416;
    public const int Status422UnprocessableEntity = 422;

    /// <summary>
    /// The resource is write-locked and the request did not present the token. RFC 4918.
    /// </summary>
    public const int Status423Locked = 423;

    /// <summary>
    /// The request failed because a different one it depended on did. RFC 4918 — the status a
    /// multi-resource operation reports for the members it never got to.
    /// </summary>
    public const int Status424FailedDependency = 424;

    public const int Status426UpgradeRequired = 426;

    /// <summary>
    /// The request must be conditional. RFC 6585 — what a resource answers when it refuses a blind
    /// overwrite and wants an <c>If-Match</c> carrying the version the caller read.
    /// </summary>
    public const int Status428PreconditionRequired = 428;

    public const int Status429TooManyRequests = 429;
    public const int Status431RequestHeaderFieldsTooLarge = 431;
    public const int Status500InternalServerError = 500;
    public const int Status501NotImplemented = 501;
    public const int Status502BadGateway = 502;
    public const int Status503ServiceUnavailable = 503;
    public const int Status504GatewayTimeout = 504;
    public const int Status505HttpVersionNotSupported = 505;

    /// <summary>
    /// There is no room to store what the request carried. RFC 4918 — the honest answer from a
    /// server whose storage is a phone's.
    /// </summary>
    public const int Status507InsufficientStorage = 507;

    /// <summary>
    /// Returns the standard reason phrase, or "Unknown" for codes we do not recognise. Callers can
    /// always override via <see cref="HttpResponse.ReasonPhrase"/>.
    /// </summary>
    public static string GetReasonPhrase(int statusCode) => statusCode switch
    {
        100 => "Continue",
        101 => "Switching Protocols",
        200 => "OK",
        201 => "Created",
        202 => "Accepted",
        203 => "Non-Authoritative Information",
        204 => "No Content",
        205 => "Reset Content",
        206 => "Partial Content",
        207 => "Multi-Status",
        300 => "Multiple Choices",
        301 => "Moved Permanently",
        302 => "Found",
        303 => "See Other",
        304 => "Not Modified",
        307 => "Temporary Redirect",
        308 => "Permanent Redirect",
        400 => "Bad Request",
        401 => "Unauthorized",
        402 => "Payment Required",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        406 => "Not Acceptable",
        407 => "Proxy Authentication Required",
        408 => "Request Timeout",
        409 => "Conflict",
        410 => "Gone",
        411 => "Length Required",
        412 => "Precondition Failed",
        413 => "Content Too Large",
        414 => "URI Too Long",
        415 => "Unsupported Media Type",
        416 => "Range Not Satisfiable",
        417 => "Expectation Failed",
        418 => "I'm a teapot",
        421 => "Misdirected Request",
        422 => "Unprocessable Content",
        423 => "Locked",
        424 => "Failed Dependency",
        426 => "Upgrade Required",
        428 => "Precondition Required",
        429 => "Too Many Requests",
        431 => "Request Header Fields Too Large",
        451 => "Unavailable For Legal Reasons",
        500 => "Internal Server Error",
        501 => "Not Implemented",
        502 => "Bad Gateway",
        503 => "Service Unavailable",
        504 => "Gateway Timeout",
        505 => "HTTP Version Not Supported",
        507 => "Insufficient Storage",
        508 => "Loop Detected",
        _ => "Unknown"
    };
}
