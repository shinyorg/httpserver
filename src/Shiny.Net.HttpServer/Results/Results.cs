using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Shiny.Net.HttpServer;

/// <summary>
/// Factory for the responses a handler can return. The functional face of the same objects
/// <see cref="IActionResult"/> exposes as types — <c>Results.NotFound()</c> and
/// <c>new NotFoundResult()</c> are the same response written two ways.
/// <para>
/// There are two ways to say "serialize this as JSON" and the difference matters. Passing a
/// <see cref="JsonTypeInfo{T}"/> is direct: the compiler sees the metadata, nothing is discovered
/// at runtime. Omitting it goes through <see cref="JsonTypeInfoRegistry"/>, which is still
/// reflection-free but needs the type to have been registered — automatic for anything crossing a
/// generated endpoint, one line of setup otherwise.
/// </para>
/// </summary>
public static class Results
{
    const string JsonContentType = ObjectResult.JsonContentType;
    const string TextContentType = ObjectResult.TextContentType;

    /// <summary>200 with no body.</summary>
    public static IResult Ok() => new OkResult();

    /// <summary>200 with a JSON body, serialized from compile-time metadata.</summary>
    public static IResult Ok<T>(T value, JsonTypeInfo<T> typeInfo)
        => new JsonResult<T>(value, typeInfo, JsonContentType, StatusCodes.Status200OK);

    /// <summary>
    /// 200 with the value serialized from metadata in <see cref="JsonTypeInfoRegistry"/>.
    /// <para>
    /// JSON, unless the app set <see cref="Negotiation.ContentNegotiationOptions.NegotiateByDefault"/>
    /// — this overload names no format, so it is the one that can honour an <c>Accept</c> header.
    /// </para>
    /// </summary>
    public static IResult Ok<T>(T value)
        => new RegistryJsonResult<T>(value, JsonContentType, StatusCodes.Status200OK, negotiable: true);

    public static IResult Json<T>(
        T value,
        JsonTypeInfo<T> typeInfo,
        int statusCode = StatusCodes.Status200OK,
        string contentType = JsonContentType
    ) => new JsonResult<T>(value, typeInfo, contentType, statusCode);

    /// <summary>JSON at an explicit status code, using metadata from <see cref="JsonTypeInfoRegistry"/>.</summary>
    public static IResult Json<T>(T value, int statusCode, string contentType = JsonContentType)
        => new RegistryJsonResult<T>(value, contentType, statusCode);

    /// <summary>
    /// JSON without any registered metadata, using runtime reflection.
    /// <para>
    /// Only for apps that are not published trimmed or AOT. Everything else should pass type info
    /// or register a context — this overload is annotated precisely so the analyzer complains if
    /// you reach for it in an AOT build.
    /// </para>
    /// </summary>
    [RequiresUnreferencedCode("JSON serialization without JsonTypeInfo<T> requires unreferenced code. Pass a JsonTypeInfo<T> from a JsonSerializerContext instead.")]
    [RequiresDynamicCode("JSON serialization without JsonTypeInfo<T> requires dynamic code. Pass a JsonTypeInfo<T> from a JsonSerializerContext instead.")]
    public static IResult Json<T>(T value, JsonSerializerOptions? options = null, int statusCode = StatusCodes.Status200OK)
        => new JsonResult<T>(
            value,
            (JsonTypeInfo<T>)(options ?? JsonSerializerOptions.Default).GetTypeInfo(typeof(T)),
            JsonContentType,
            statusCode
        );

    public static IResult Text(string content, string contentType = TextContentType, int statusCode = StatusCodes.Status200OK)
        => new ContentResult(content, contentType, statusCode);

    public static IResult Content(string content, string contentType, int statusCode = StatusCodes.Status200OK)
        => new ContentResult(content, contentType, statusCode);

    public static IResult Bytes(
        ReadOnlyMemory<byte> bytes,
        string contentType = "application/octet-stream",
        int statusCode = StatusCodes.Status200OK
    ) => new FileContentResult(bytes, contentType, statusCode);

    /// <summary>
    /// Streams a response body. <paramref name="stream"/> is disposed once written, so a
    /// <c>FileStream</c> can be handed over without a using block.
    /// </summary>
    public static IResult Stream(
        Stream stream,
        string contentType = "application/octet-stream",
        string? fileName = null,
        int statusCode = StatusCodes.Status200OK
    ) => new FileStreamResult(stream, contentType, statusCode) { FileDownloadName = fileName };

    /// <summary>Streams a file from disk, opened with sequential-read and async hints.</summary>
    public static IResult File(string path, string contentType = "application/octet-stream", string? fileName = null)
        => new FileStreamResult(
            new FileStream(
                path,
                new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.Read,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                }
            ),
            contentType
        )
        {
            FileDownloadName = fileName ?? Path.GetFileName(path)
        };

    public static IResult NoContent() => new NoContentResult();

    public static IResult NotFound() => new NotFoundResult();

    public static IResult NotFound<T>(T value) => new RegistryJsonResult<T>(value, JsonContentType, StatusCodes.Status404NotFound);

    public static IResult BadRequest() => new BadRequestResult();

    public static IResult BadRequest(string message)
        => new ContentResult(message, TextContentType, StatusCodes.Status400BadRequest);

    public static IResult Unauthorized() => new UnauthorizedResult();

    public static IResult Forbidden() => new ForbidResult();

    public static IResult Conflict() => new ConflictResult();

    public static IResult UnprocessableEntity() => new UnprocessableEntityResult();

    public static IResult StatusCode(int statusCode) => new StatusCodeResult(statusCode);

    public static IResult Created(string location) => new CreatedResult(location);

    public static IResult Created<T>(string location, T value, JsonTypeInfo<T> typeInfo)
        => new CreatedJsonResult<T>(location, value, typeInfo, JsonContentType);

    /// <summary>201 with a JSON body, using metadata from <see cref="JsonTypeInfoRegistry"/>.</summary>
    public static IResult Created<T>(string location, T value)
        => new CreatedResult(location, value);

    public static IResult Redirect(string location, bool permanent = false, bool preserveMethod = false)
        => new RedirectResult(location, permanent, preserveMethod);

    /// <summary>
    /// A response whose format is chosen from the request's <c>Accept</c> header.
    /// <para>
    /// Where <see cref="Ok{T}(T)"/> always writes JSON, this asks the caller — worth it when the
    /// same endpoint serves a browser, a shell script and an API client. Answers 406 when the
    /// client accepts nothing the server can produce, rather than sending a format it said it
    /// could not read.
    /// </para>
    /// </summary>
    public static IResult Negotiate<T>(T value, int statusCode = StatusCodes.Status200OK)
        => new Negotiation.NegotiatedResult(value, statusCode);

    /// <summary>
    /// An RFC 9457 <c>application/problem+json</c> error body.
    /// <para>
    /// What separates this from <see cref="BadRequest(string)"/> is that a client can branch on it:
    /// <paramref name="type"/> is a stable identifier for the *kind* of failure, while the status
    /// code alone rarely is.
    /// </para>
    /// <code>
    /// return Results.Problem(
    ///     StatusCodes.Status409Conflict,
    ///     detail: "The device is already paired to another account.",
    ///     type: "https://example.com/problems/already-paired"
    /// );
    /// </code>
    /// </summary>
    public static IResult Problem(
        int statusCode = StatusCodes.Status500InternalServerError,
        string? detail = null,
        string? title = null,
        string? type = null,
        string? instance = null
    ) => new ProblemResult(new ProblemDetails
    {
        Status = statusCode,
        Detail = detail,
        Title = title,
        Type = type,
        Instance = instance
    });

    /// <summary>A problem built up by the caller, extensions and all.</summary>
    public static IResult Problem(ProblemDetails problem) => new ProblemResult(problem);

    /// <summary>
    /// A 400 carrying per-field errors, in the <c>errors</c> shape ASP.NET Core produces — so a
    /// client that already parses that keeps working.
    /// </summary>
    public static IResult ValidationProblem(
        IDictionary<string, string[]> errors,
        string? detail = null,
        int statusCode = StatusCodes.Status400BadRequest
    ) => new ProblemResult(new ValidationProblemDetails(errors)
    {
        Status = statusCode,
        Detail = detail
    });

    /// <summary>
    /// A <c>text/event-stream</c> response that stays open while <paramref name="produce"/> pushes
    /// events to it.
    /// </summary>
    public static IResult ServerSentEvents(
        Func<Sse.ServerSentEventStream, Task> produce,
        TimeSpan? retry = null
    ) => new Sse.ServerSentEventResult(produce, retry);
}
