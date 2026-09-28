using System.ComponentModel;

namespace Shiny.Net.HttpServer.JsonPatch;

/// <summary>
/// Reading an <c>application/json-patch+json</c> body from a raw handler, and the binding generated
/// endpoints use for a <see cref="JsonPatchDocument"/> parameter.
/// </summary>
public static class JsonPatchRequestExtensions
{
    /// <summary>True when the request declares an <c>application/json-patch+json</c> body (parameters ignored).</summary>
    public static bool IsJsonPatch(this HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var contentType = request.ContentType;
        if (string.IsNullOrEmpty(contentType))
            return false;

        var semicolon = contentType.IndexOf(';');
        var bare = (semicolon < 0 ? contentType : contentType[..semicolon]).AsSpan().Trim();

        return bare.Equals(JsonPatchDocument.MediaType, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads and parses the body as a JSON Patch.
    /// <para>
    /// Strict about the <c>Content-Type</c>: anything but <c>application/json-patch+json</c> throws
    /// <see cref="JsonPatchErrorKind.UnsupportedMediaType"/> (415). A JSON array sent as plain
    /// <c>application/json</c> is not assumed to be a JSON Patch, because on a resource that also accepts
    /// merge patch the two readings of the same body would write different things.
    /// </para>
    /// <code>
    /// app.MapPatch("/notes/{id}", async ctx =>
    /// {
    ///     try
    ///     {
    ///         var patch = await ctx.Request.ReadJsonPatchAsync();
    ///         store.Save(patch.ApplyTo(store.Get(id), AppJson.Default.Note));
    ///         return Results.NoContent();
    ///     }
    ///     catch (JsonPatchException ex)
    ///     {
    ///         return ex.ToResult();   // 400 / 409 / 415 / 422 problem naming the failed operation
    ///     }
    /// });
    /// </code>
    /// </summary>
    /// <exception cref="JsonPatchException">Wrong content type (415), no body or an invalid patch (400).</exception>
    public static async ValueTask<JsonPatchDocument> ReadJsonPatchAsync(
        this HttpRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.IsJsonPatch())
            throw new JsonPatchException(
                JsonPatchErrorKind.UnsupportedMediaType,
                $"A JSON Patch body must be sent as '{JsonPatchDocument.MediaType}' "
                    + $"(got '{(string.IsNullOrEmpty(request.ContentType) ? "no Content-Type" : request.ContentType)}')."
            );

        if (!request.HasBody)
            throw new JsonPatchException(JsonPatchErrorKind.Malformed, "A JSON Patch body is required.");

        return await JsonPatchDocument.ParseAsync(request.Body, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Binds a <see cref="JsonPatchDocument"/> parameter for a generated endpoint: the patch, or null after
    /// writing the 400/415 problem response itself.
    /// <para>
    /// Separate from the general body binder because a JSON Patch is not "JSON in some format a formatter
    /// can read" — it has one media type, it needs no registered metadata, and its failures deserve an
    /// operation index rather than "the body could not be read".
    /// </para>
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static async ValueTask<JsonPatchDocument?> TryBindJsonPatchAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            return await context.Request.ReadJsonPatchAsync(context.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonPatchException ex)
        {
            await ex.ToResult().ExecuteAsync(context).ConfigureAwait(false);
            return null;
        }
    }
}
