using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Shiny.Net.HttpServer;

/// <summary>
/// Reading a JSON request body from a raw handler.
/// <para>
/// Generated endpoints get this for free through parameter binding. A handler written by hand had
/// to reach for the binder's own plumbing, which is not what that type is for — so the same thing
/// is here, spelled the way a handler would want it.
/// </para>
/// </summary>
public static class JsonRequestExtensions
{
    /// <summary>
    /// Deserializes the body, or returns null when there is no body, it is not JSON, or it
    /// deserializes to null.
    /// <para>
    /// Malformed JSON is the caller's mistake, so it comes back as null for the handler to turn
    /// into a 400 — rather than an exception that would become a 500 about a server fault that did
    /// not happen.
    /// </para>
    /// <code>
    /// app.MapPost("/notes", async ctx =>
    /// {
    ///     var note = await ctx.Request.ReadJsonAsync(AppJson.Default.NewNote);
    ///
    ///     return note is null ? Results.BadRequest() : Results.Created(…);
    /// });
    /// </code>
    /// </summary>
    public static async ValueTask<T?> ReadJsonAsync<T>(
        this HttpRequest request,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(typeInfo);

        if (!request.HasBody)
            return default;

        try
        {
            return await JsonSerializer
                .DeserializeAsync(request.Body, typeInfo, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    /// <summary>
    /// The same, using metadata from <see cref="JsonTypeInfoRegistry"/> — for apps that registered a
    /// <c>JsonSerializerContext</c> once and would rather not name the type info at every call.
    /// </summary>
    public static ValueTask<T?> ReadJsonAsync<T>(this HttpRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.ReadJsonAsync(JsonTypeInfoRegistry.GetRequired<T>(), cancellationToken);
    }
}
