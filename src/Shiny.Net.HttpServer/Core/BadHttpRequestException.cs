namespace Shiny.Net.HttpServer;

/// <summary>
/// Thrown when a request is malformed or violates a configured limit. The server turns this into the
/// carried <see cref="StatusCode"/> rather than a blanket 500, since these are client errors.
/// </summary>
public sealed class BadHttpRequestException : Exception
{
    public BadHttpRequestException(string message, int statusCode = StatusCodes.Status400BadRequest)
        : base(message)
        => this.StatusCode = statusCode;

    public BadHttpRequestException(string message, int statusCode, Exception innerException)
        : base(message, innerException)
        => this.StatusCode = statusCode;

    public int StatusCode { get; }
}
