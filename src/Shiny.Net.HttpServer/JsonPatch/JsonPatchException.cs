namespace Shiny.Net.HttpServer.JsonPatch;

/// <summary>
/// Why a JSON Patch could not be applied. Each kind maps to the status RFC 5789 §2.2 gives it, so the
/// status a client sees says whether to fix the patch, re-read the resource, or fix the result.
/// </summary>
public enum JsonPatchErrorKind
{
    /// <summary>
    /// The patch document itself is invalid — not a JSON array, an unknown <c>op</c>, a missing
    /// <c>path</c>/<c>from</c>/<c>value</c>, an unparseable pointer. <b>400</b>: no resource state would
    /// make it apply.
    /// </summary>
    Malformed,

    /// <summary>
    /// A location the operation needs does not exist in this document — a missing member, an index past
    /// the end, a pointer that runs through a string. <b>409</b>: RFC 5789's "conflicting state", where
    /// the patch assumed structure the resource does not have.
    /// </summary>
    PathNotFound,

    /// <summary>
    /// A <c>test</c> operation compared unequal. <b>409</b>: the resource is not in the state the client
    /// asserted, which is exactly what <c>test</c> exists to detect.
    /// </summary>
    TestFailed,

    /// <summary>
    /// The patch applied, but the result is not a valid representation of the resource — it no longer
    /// deserializes as the target type, or a document that must be an object became something else.
    /// <b>422</b>: RFC 5789's "unprocessable request".
    /// </summary>
    InvalidResult,

    /// <summary>
    /// The body was not <c>application/json-patch+json</c>. <b>415</b>, answered with an
    /// <c>Accept-Patch</c> header naming the format that is accepted, as RFC 5789 §2.2 asks.
    /// </summary>
    UnsupportedMediaType
}

/// <summary>
/// A JSON Patch that could not be parsed or applied — carrying which operation failed and why.
/// <para>
/// The operation index is the useful part. "Patch failed" tells a client nothing it can act on;
/// "operation 3, a <c>test</c> of <c>/version</c>, failed" tells it the resource changed underneath
/// it, and which assertion caught it. <see cref="ToProblemDetails"/> puts both in the RFC 9457 body.
/// </para>
/// <para>
/// Thrown out of a handler unhandled, the problem-details middleware maps it to <see cref="StatusCode"/>
/// rather than a 500. Catching it and returning <see cref="ToResult"/> also keeps the operation index.
/// </para>
/// </summary>
public sealed class JsonPatchException : Exception
{
    public JsonPatchException(JsonPatchErrorKind kind, string message, int operationIndex = -1, JsonPatchOperation? operation = null)
        : base(message)
    {
        this.Kind = kind;
        this.OperationIndex = operationIndex;
        this.Operation = operation;
    }

    public JsonPatchException(JsonPatchErrorKind kind, string message, int operationIndex, JsonPatchOperation? operation, Exception innerException)
        : base(message, innerException)
    {
        this.Kind = kind;
        this.OperationIndex = operationIndex;
        this.Operation = operation;
    }

    public JsonPatchErrorKind Kind { get; }

    /// <summary>Zero-based index of the failing operation, or -1 when the failure was not in one operation.</summary>
    public int OperationIndex { get; }

    /// <summary>The failing operation, when it got far enough to be parsed.</summary>
    public JsonPatchOperation? Operation { get; }

    /// <summary>The HTTP status for <see cref="Kind"/>: 400, 409, 409, 422 or 415.</summary>
    public int StatusCode => StatusFor(this.Kind);

    internal static int StatusFor(JsonPatchErrorKind kind) => kind switch
    {
        JsonPatchErrorKind.Malformed => StatusCodes.Status400BadRequest,
        JsonPatchErrorKind.PathNotFound or JsonPatchErrorKind.TestFailed => StatusCodes.Status409Conflict,
        JsonPatchErrorKind.InvalidResult => StatusCodes.Status422UnprocessableEntity,
        _ => StatusCodes.Status415UnsupportedMediaType
    };

    /// <summary>
    /// An RFC 9457 problem naming the failure. Extensions: <c>patchError</c> (the kind, camel-cased — the
    /// stable thing to branch on), and when an operation failed <c>operationIndex</c>, <c>op</c> and
    /// <c>path</c>.
    /// </summary>
    public ProblemDetails ToProblemDetails()
    {
        var problem = new ProblemDetails
        {
            Status = this.StatusCode,
            Detail = this.Message
        };

        problem.Extensions["patchError"] = this.Kind switch
        {
            JsonPatchErrorKind.Malformed => "malformed",
            JsonPatchErrorKind.PathNotFound => "pathNotFound",
            JsonPatchErrorKind.TestFailed => "testFailed",
            JsonPatchErrorKind.InvalidResult => "invalidResult",
            _ => "unsupportedMediaType"
        };

        if (this.OperationIndex >= 0)
            problem.Extensions["operationIndex"] = this.OperationIndex;

        if (this.Operation is { } operation)
        {
            problem.Extensions["op"] = operation.OpName;
            problem.Extensions["path"] = operation.Path.ToString();
        }

        return problem;
    }

    /// <summary>
    /// The problem as a result, with an <c>Accept-Patch</c> header on a 415 so the client learns which
    /// patch format to send instead.
    /// </summary>
    public IResult ToResult() => new JsonPatchProblemResult(this);

    sealed class JsonPatchProblemResult(JsonPatchException exception) : IResult
    {
        public ValueTask ExecuteAsync(HttpContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            if (exception.Kind == JsonPatchErrorKind.UnsupportedMediaType)
                context.Response.Headers[JsonPatchDocument.AcceptPatchHeader] = JsonPatchDocument.MediaType;

            return Results.Problem(exception.ToProblemDetails()).ExecuteAsync(context);
        }
    }
}
