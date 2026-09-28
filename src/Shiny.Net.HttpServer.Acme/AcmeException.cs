using System.Net;

namespace Shiny.Net.HttpServer.Acme;

/// <summary>
/// The CA refused something, or an order did not reach a certificate.
/// <para>
/// When the CA said why, its problem document (RFC 8555 §6.7, RFC 7807) is carried here: the
/// <see cref="ProblemType"/> URN is the machine-readable reason — <c>…:rateLimited</c>,
/// <c>…:unauthorized</c>, <c>…:connection</c> — and <see cref="Detail"/> is the CA's own explanation,
/// which for a failed validation usually names the exact URL or address it could not reach.
/// </para>
/// </summary>
public sealed class AcmeException : Exception
{
    public AcmeException(string message)
        : base(message)
    {
    }

    public AcmeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    internal AcmeException(
        string message,
        string? problemType,
        string? detail,
        HttpStatusCode? statusCode,
        TimeSpan? retryAfter,
        IReadOnlyList<string>? subproblems = null
    )
        : base(message)
    {
        this.ProblemType = problemType;
        this.Detail = detail;
        this.StatusCode = statusCode;
        this.RetryAfter = retryAfter;
        this.Subproblems = subproblems ?? [];
    }

    /// <summary>The problem type URN, e.g. <c>urn:ietf:params:acme:error:rateLimited</c>.</summary>
    public string? ProblemType { get; }

    /// <summary>The CA's human-readable explanation.</summary>
    public string? Detail { get; }

    /// <summary>The HTTP status the CA answered with, when the failure came from a response.</summary>
    public HttpStatusCode? StatusCode { get; }

    /// <summary>How long the CA asked to be left alone, when it said. Renewal retries honour it.</summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>Per-identifier problems, one line each, when the CA reported several.</summary>
    public IReadOnlyList<string> Subproblems { get; } = [];
}
