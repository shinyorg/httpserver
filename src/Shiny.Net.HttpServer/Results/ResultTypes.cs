using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Shiny.Net.HttpServer.Negotiation;

namespace Shiny.Net.HttpServer;

/// <summary>
/// Serializes with a <see cref="JsonTypeInfo{T}"/> rather than reflection, which is what keeps
/// JSON responses trim- and AOT-safe. The generator supplies the type info; callers writing
/// results by hand pass one from their own <c>JsonSerializerContext</c>.
/// </summary>
sealed class JsonResult<T>(T value, JsonTypeInfo<T> typeInfo, string contentType, int statusCode) : IActionResult
{
    public async ValueTask ExecuteAsync(HttpContext context)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = contentType;

        // Serialized to a pooled buffer first so Content-Length can be set. Streaming straight to
        // the body would force chunked encoding on responses that are usually a few hundred bytes.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);

        await context.Response.WriteBytesAsync(bytes, contentType, context.RequestAborted).ConfigureAwait(false);
    }
}

/// <summary>
/// The registry-backed sibling of <see cref="JsonResult{T}"/>: the static type is known, so the
/// lookup happens once per closed generic rather than once per response.
/// </summary>
/// <param name="negotiable">
/// Whether this result may hand off to content negotiation when the app turned
/// <see cref="ContentNegotiationOptions.NegotiateByDefault"/> on. True for <c>Results.Ok(value)</c>,
/// which says nothing about a format; false for <c>Results.Json(value, 201)</c>, which does.
/// </param>
sealed class RegistryJsonResult<T>(T value, string contentType, int statusCode, bool negotiable = false)
    : IActionResult
{
    public async ValueTask ExecuteAsync(HttpContext context)
    {
        if (negotiable
            && context.RequestServices.GetService<ContentNegotiationOptions>() is { NegotiateByDefault: true } options)
        {
            await new NegotiatedResult(value, statusCode) { Options = options }
                .ExecuteAsync(context)
                .ConfigureAwait(false);

            return;
        }

        context.Response.StatusCode = statusCode;

        if (value is null)
        {
            context.Response.ContentLength = 0;
            await context.Response.StartAsync(context.RequestAborted).ConfigureAwait(false);
            return;
        }

        // The runtime type first, so a derived instance returned as its base serializes with all of
        // its own properties; the declared type is the fallback for the (common) exact match.
        var runtimeType = value.GetType();
        var bytes = runtimeType != typeof(T) && JsonTypeInfoRegistry.TryGet(runtimeType, out var runtimeInfo)
            ? JsonSerializer.SerializeToUtf8Bytes(value, runtimeInfo)
            : JsonSerializer.SerializeToUtf8Bytes(value, JsonTypeInfoRegistry.GetRequired<T>());

        await context.Response.WriteBytesAsync(bytes, contentType, context.RequestAborted).ConfigureAwait(false);
    }
}

/// <summary>
/// 201 with a Location header and a strongly typed JSON body. Separate from
/// <see cref="CreatedResult"/> so the AOT-safe path does not have to go through the registry.
/// </summary>
sealed class CreatedJsonResult<T>(string location, T value, JsonTypeInfo<T> typeInfo, string contentType) : IActionResult
{
    public async ValueTask ExecuteAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status201Created;
        context.Response.Headers[HeaderNames.Location] = location;
        context.Response.ContentType = contentType;

        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);

        await context.Response.WriteBytesAsync(bytes, contentType, context.RequestAborted).ConfigureAwait(false);
    }
}
