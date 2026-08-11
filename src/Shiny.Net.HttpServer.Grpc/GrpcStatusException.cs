namespace Shiny.Net.HttpServer.Grpc;

/// <summary>
/// Throw this from a method to end the call with a specific <see cref="GrpcStatusCode"/> and
/// message. It is the gRPC equivalent of returning a 4xx: an outcome the caller is meant to see,
/// as opposed to an unhandled exception, which reports <see cref="GrpcStatusCode.Unknown"/> and
/// deliberately says nothing about what went wrong inside the server.
/// <code>
/// if (order is null)
///     throw new GrpcStatusException(GrpcStatusCode.NotFound, $"No order {request.Id}.");
/// </code>
/// </summary>
public sealed class GrpcStatusException : Exception
{
    public GrpcStatusException(GrpcStatusCode statusCode, string? message = null)
        : base(message ?? statusCode.ToString())
    {
        this.StatusCode = statusCode;
        this.StatusMessage = message;
    }

    public GrpcStatusException(GrpcStatusCode statusCode, string? message, Exception? innerException)
        : base(message ?? statusCode.ToString(), innerException)
    {
        this.StatusCode = statusCode;
        this.StatusMessage = message;
    }

    public GrpcStatusCode StatusCode { get; }

    /// <summary>
    /// The message sent to the caller in the <c>grpc-message</c> trailer, which is null when the
    /// status code is the whole story.
    /// </summary>
    public string? StatusMessage { get; }
}
