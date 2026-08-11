using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shiny.Net.HttpServer.Negotiation;

namespace Shiny.Net.HttpServer;

/// <summary>
/// The MVC-shaped face of <see cref="IResult"/>. Purely a familiarity alias: anything returning
/// <c>IActionResult</c> is an <c>IResult</c>, so the two styles mix freely in one app.
/// <code>
/// [Get("/{id:int}")]
/// public async Task&lt;IActionResult&gt; GetData(int id)
///     => await db.FindAsync(id) is { } row ? new OkObjectResult(row) : new NotFoundResult();
/// </code>
/// </summary>
public interface IActionResult : IResult;

/// <summary>A status code and headers, with no body.</summary>
public class StatusCodeResult(int statusCode) : IActionResult
{
    public int StatusCode { get; } = statusCode;

    public virtual ValueTask ExecuteAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Response.StatusCode = this.StatusCode;
        context.Response.ContentLength = 0;
        return context.Response.StartAsync(context.RequestAborted);
    }
}

/// <summary>200 with no body.</summary>
public sealed class OkResult() : StatusCodeResult(StatusCodes.Status200OK);

/// <summary>204.</summary>
public sealed class NoContentResult() : StatusCodeResult(StatusCodes.Status204NoContent);

/// <summary>404 with no body.</summary>
public sealed class NotFoundResult() : StatusCodeResult(StatusCodes.Status404NotFound);

/// <summary>400 with no body.</summary>
public sealed class BadRequestResult() : StatusCodeResult(StatusCodes.Status400BadRequest);

/// <summary>401.</summary>
public sealed class UnauthorizedResult() : StatusCodeResult(StatusCodes.Status401Unauthorized);

/// <summary>403.</summary>
public sealed class ForbidResult() : StatusCodeResult(StatusCodes.Status403Forbidden);

/// <summary>409 with no body.</summary>
public sealed class ConflictResult() : StatusCodeResult(StatusCodes.Status409Conflict);

/// <summary>422 with no body.</summary>
public sealed class UnprocessableEntityResult() : StatusCodeResult(StatusCodes.Status422UnprocessableEntity);

/// <summary>
/// A value serialized into the response body.
/// <para>
/// Strings go out as <c>text/plain</c>; everything else is JSON, using metadata looked up from
/// <see cref="JsonTypeInfoRegistry"/>. The generator registers every type it sees crossing an
/// endpoint boundary, so returning a type from a generated endpoint always works; anything else
/// needs a <c>JsonSerializerContext</c> registered once at startup. That lookup is what keeps an
/// untyped <c>object</c> result AOT-safe instead of quietly reflection-bound.
/// </para>
/// </summary>
public class ObjectResult(object? value, int statusCode = StatusCodes.Status200OK) : IActionResult
{
    internal const string JsonContentType = "application/json; charset=utf-8";
    internal const string TextContentType = "text/plain; charset=utf-8";

    public object? Value { get; } = value;

    public int StatusCode { get; set; } = statusCode;

    /// <summary>Overrides the content type that would otherwise be inferred from the value.</summary>
    public string? ContentType { get; set; }

    /// <summary>When true a <c>string</c> value is serialized as a JSON string rather than sent as text.</summary>
    public bool SerializeStringsAsJson { get; init; }

    public virtual ValueTask ExecuteAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Response.StatusCode = this.StatusCode;

        if (this.Value is null)
        {
            context.Response.ContentLength = 0;
            return context.Response.StartAsync(context.RequestAborted);
        }

        if (this.Value is string text && !this.SerializeStringsAsJson)
            return context.Response.WriteTextAsync(
                text,
                this.ContentType ?? TextContentType,
                context.RequestAborted
            );

        // The IActionResult spelling of Results.Ok, so it negotiates on the same terms — two
        // spellings of the same intent must not disagree about what they send. An explicit
        // ContentType is an intent of its own and opts out.
        if (this.ContentType is null
            && context.RequestServices.GetService<ContentNegotiationOptions>() is { NegotiateByDefault: true } options)
            return new NegotiatedResult(this.Value, this.StatusCode) { Options = options }.ExecuteAsync(context);

        var typeInfo = JsonTypeInfoRegistry.GetRequired(this.Value.GetType());
        var bytes = JsonSerializer.SerializeToUtf8Bytes(this.Value, typeInfo);

        return context.Response.WriteBytesAsync(
            bytes,
            this.ContentType ?? JsonContentType,
            context.RequestAborted
        );
    }
}

/// <summary>200 with a serialized body.</summary>
public sealed class OkObjectResult(object? value) : ObjectResult(value, StatusCodes.Status200OK);

/// <summary>404 with a serialized body — a problem description, usually.</summary>
public sealed class NotFoundObjectResult(object? value) : ObjectResult(value, StatusCodes.Status404NotFound);

/// <summary>400 with a serialized body.</summary>
public sealed class BadRequestObjectResult(object? value) : ObjectResult(value, StatusCodes.Status400BadRequest);

/// <summary>409 with a serialized body.</summary>
public sealed class ConflictObjectResult(object? value) : ObjectResult(value, StatusCodes.Status409Conflict);

/// <summary>Forces JSON, including for <c>string</c> values.</summary>
public sealed class JsonResult(object? value, int statusCode = StatusCodes.Status200OK)
    : ObjectResult(value, statusCode)
{
    public override ValueTask ExecuteAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Response.StatusCode = this.StatusCode;

        if (this.Value is null)
        {
            context.Response.ContentLength = 0;
            return context.Response.StartAsync(context.RequestAborted);
        }

        var typeInfo = JsonTypeInfoRegistry.GetRequired(this.Value.GetType());
        var bytes = JsonSerializer.SerializeToUtf8Bytes(this.Value, typeInfo);

        return context.Response.WriteBytesAsync(
            bytes,
            this.ContentType ?? JsonContentType,
            context.RequestAborted
        );
    }
}

/// <summary>201 with a <c>Location</c> header and an optional body.</summary>
public sealed class CreatedResult(string location, object? value = null)
    : ObjectResult(value, StatusCodes.Status201Created)
{
    public string Location { get; } = location;

    public override ValueTask ExecuteAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Response.Headers[HeaderNames.Location] = this.Location;
        return base.ExecuteAsync(context);
    }
}

/// <summary>A literal body with an explicit content type.</summary>
public sealed class ContentResult(
    string content,
    string contentType = ObjectResult.TextContentType,
    int statusCode = StatusCodes.Status200OK
) : IActionResult
{
    public string Content { get; } = content;
    public string ContentType { get; } = contentType;
    public int StatusCode { get; } = statusCode;

    public ValueTask ExecuteAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Response.StatusCode = this.StatusCode;
        return context.Response.WriteTextAsync(this.Content, this.ContentType, context.RequestAborted);
    }
}

/// <summary>A byte body already in memory.</summary>
public sealed class FileContentResult(
    ReadOnlyMemory<byte> bytes,
    string contentType = "application/octet-stream",
    int statusCode = StatusCodes.Status200OK
) : IActionResult
{
    public ReadOnlyMemory<byte> Bytes { get; } = bytes;
    public string ContentType { get; } = contentType;
    public int StatusCode { get; } = statusCode;

    /// <summary>Set to send a <c>Content-Disposition: attachment</c> header.</summary>
    public string? FileDownloadName { get; init; }

    public ValueTask ExecuteAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Response.StatusCode = this.StatusCode;
        if (this.FileDownloadName is not null)
            context.Response.Headers[HeaderNames.ContentDisposition] =
                $"attachment; filename=\"{this.FileDownloadName}\"";

        return context.Response.WriteBytesAsync(this.Bytes, this.ContentType, context.RequestAborted);
    }
}

/// <summary>
/// A streamed body. <paramref name="stream"/> is disposed once written, so a <c>FileStream</c> can
/// be handed over without a using block.
/// </summary>
public sealed class FileStreamResult(
    Stream stream,
    string contentType = "application/octet-stream",
    int statusCode = StatusCodes.Status200OK
) : IActionResult
{
    public Stream Stream { get; } = stream;
    public string ContentType { get; } = contentType;
    public int StatusCode { get; } = statusCode;

    public string? FileDownloadName { get; init; }

    public async ValueTask ExecuteAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Response.StatusCode = this.StatusCode;
        context.Response.ContentType = this.ContentType;

        if (this.FileDownloadName is not null)
            context.Response.Headers[HeaderNames.ContentDisposition] =
                $"attachment; filename=\"{this.FileDownloadName}\"";

        await using (this.Stream.ConfigureAwait(false))
            await context.Response
                .WriteStreamAsync(this.Stream, this.ContentType, context.RequestAborted)
                .ConfigureAwait(false);
    }
}

/// <summary>A 3xx redirect.</summary>
public sealed class RedirectResult(string location, bool permanent = false, bool preserveMethod = false)
    : IActionResult
{
    public string Location { get; } = location;
    public bool Permanent { get; } = permanent;
    public bool PreserveMethod { get; } = preserveMethod;

    public ValueTask ExecuteAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Response.Redirect(this.Location, this.Permanent, this.PreserveMethod);
        context.Response.ContentLength = 0;
        return context.Response.StartAsync(context.RequestAborted);
    }
}
