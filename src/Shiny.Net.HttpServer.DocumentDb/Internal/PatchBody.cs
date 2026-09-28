using System.Text.Json;
using System.Text.Json.Nodes;
using Shiny.Net.HttpServer.JsonPatch;

namespace Shiny.Net.HttpServer.DocumentDb.Internal;

/// <summary>
/// The PATCH body, in whichever of the two patch formats the caller declared, applied to the document the
/// endpoint already read.
/// <para>
/// <c>application/json-patch+json</c> is RFC 6902; everything else — <c>application/merge-patch+json</c>,
/// plain <c>application/json</c>, or no header at all — stays RFC 7396 merge patch, exactly as these
/// endpoints have always read it, so no existing client changes behaviour. Both formats produce the same
/// thing, a whole replacement document, and it goes through one write path: the same If-Match check before,
/// the same full-replace write, the same scope check after. The format changes what the caller can say,
/// never what the endpoint enforces.
/// </para>
/// </summary>
static class PatchBody
{
    /// <summary>The RFC 5789 <c>Accept-Patch</c> value for a document resource: both formats it reads.</summary>
    public const string AcceptPatch = "application/merge-patch+json, " + JsonPatchDocument.MediaType;

    /// <summary>
    /// Applies the request's patch to <paramref name="document"/> and returns the patched object. A JSON Patch
    /// is applied all-or-nothing to a copy; a failed <c>test</c> or a missing path surfaces as
    /// <see cref="JsonPatchException"/> for the endpoint's guard to turn into a 409.
    /// </summary>
    public static async Task<JsonObject> ApplyAsync(HttpContext http, JsonObject document)
    {
        if (http.Request.IsJsonPatch())
        {
            var patch = await http.Request.ReadJsonPatchAsync(http.RequestAborted).ConfigureAwait(false);

            // A document is an object. A patch that replaced the root with an array or a string produced a
            // valid JSON value but not a valid document — RFC 5789's "unprocessable", not a malformed patch.
            return patch.ApplyTo(document) as JsonObject
                ?? throw new JsonPatchException(
                    JsonPatchErrorKind.InvalidResult,
                    "The patched document must still be a JSON object."
                );
        }

        var merge = await JsonSerializer
            .DeserializeAsync(http.Request.Body, DocumentDbJson.Default.JsonObject, http.RequestAborted)
            .ConfigureAwait(false)
            ?? throw new BadRequestException("A PATCH body must be a JSON object.");

        MergePatch.Apply(document, merge);
        return document;
    }
}
