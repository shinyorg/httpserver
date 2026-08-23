using System.Globalization;

namespace Shiny.Net.HttpServer.Caching;

/// <summary>What the request's preconditions say should happen next.</summary>
public enum PreconditionResult
{
    /// <summary>Nothing was asked, or everything asked for held. Carry on.</summary>
    Proceed,

    /// <summary>The client already has this version. Answer 304 and no body.</summary>
    NotModified,

    /// <summary>An <c>If-Match</c> or <c>If-Unmodified-Since</c> did not hold. Answer 412 and do not write.</summary>
    PreconditionFailed
}

/// <summary>
/// Conditional requests for handlers that are not serving a file.
/// <para>
/// Static files, downloads and the WebDAV store have done this for themselves since day one. This
/// is the same evaluation for a JSON endpoint, which is where it is worth the most on a device: a
/// 304 is a couple of hundred bytes and no serialisation, and the client's list still refreshes.
/// </para>
/// <code>
/// var etag = EntityTag.FromContent(user.RowVersion);
/// if (ctx.CheckPreconditions(etag, user.Updated) is var result and not PreconditionResult.Proceed)
///     return ctx.CompletePreconditionAsync(result);
///
/// ctx.Response.SetETag(etag);
/// return Results.Json(user, AppJson.Default.User);
/// </code>
/// </summary>
public static class ConditionalRequestExtensions
{
    /// <summary>
    /// Evaluates <c>If-Match</c>, <c>If-None-Match</c>, <c>If-Modified-Since</c> and
    /// <c>If-Unmodified-Since</c> against the version the handler is about to serve.
    /// <para>
    /// Order is the one RFC 9110 lays down: the strong preconditions that guard a write are checked
    /// before the weak ones that save a read, because answering 304 to a caller whose
    /// <c>If-Match</c> failed would tell it the write succeeded.
    /// </para>
    /// </summary>
    public static PreconditionResult CheckPreconditions(
        this HttpContext context,
        string? etag,
        DateTimeOffset? lastModified = null
    )
    {
        ArgumentNullException.ThrowIfNull(context);

        var request = context.Request;
        var normalized = EntityTag.Normalize(etag);

        if (request.Headers.GetFirst(HeaderNames.IfMatch) is { Length: > 0 } ifMatch)
        {
            if (!Matches(ifMatch, normalized))
                return PreconditionResult.PreconditionFailed;
        }
        else if (lastModified is { } modified
            && request.Headers.GetFirst(HeaderNames.IfUnmodifiedSince) is { Length: > 0 } unmodifiedSince
            && TryParseDate(unmodifiedSince, out var limit)
            && modified.ToUnixTimeSeconds() > limit.ToUnixTimeSeconds())
        {
            return PreconditionResult.PreconditionFailed;
        }

        if (request.Headers.GetFirst(HeaderNames.IfNoneMatch) is { Length: > 0 } ifNoneMatch)
        {
            // An explicit If-None-Match settles it either way. Falling through to the date could
            // contradict the tag the client is actually holding.
            return Matches(ifNoneMatch, normalized)
                ? SafeToSkip(request) ? PreconditionResult.NotModified : PreconditionResult.PreconditionFailed
                : PreconditionResult.Proceed;
        }

        if (SafeToSkip(request)
            && lastModified is { } since
            && request.Headers.GetFirst(HeaderNames.IfModifiedSince) is { Length: > 0 } modifiedSince
            && TryParseDate(modifiedSince, out var parsed)
            // Second precision on the wire, so anything inside a second counts as unchanged.
            && since.ToUnixTimeSeconds() <= parsed.ToUnixTimeSeconds())
        {
            return PreconditionResult.NotModified;
        }

        return PreconditionResult.Proceed;
    }

    /// <summary>
    /// Writes the answer <see cref="CheckPreconditions"/> asked for. Does nothing for
    /// <see cref="PreconditionResult.Proceed"/>, so it is safe to call unconditionally.
    /// </summary>
    public static ValueTask CompletePreconditionAsync(this HttpContext context, PreconditionResult result)
    {
        ArgumentNullException.ThrowIfNull(context);

        switch (result)
        {
            case PreconditionResult.NotModified:
                context.Response.StatusCode = StatusCodes.Status304NotModified;

                // A 304 carries the validators and nothing else. Content-Length on a bodiless
                // response is what makes a client wait for bytes that never come.
                context.Response.Headers.Remove(HeaderNames.ContentLength);
                context.Response.Headers.Remove(HeaderNames.ContentType);
                return context.Response.StartAsync(context.RequestAborted);

            case PreconditionResult.PreconditionFailed:
                context.Response.StatusCode = StatusCodes.Status412PreconditionFailed;
                context.Response.ContentLength = 0;
                return context.Response.StartAsync(context.RequestAborted);

            default:
                return default;
        }
    }

    /// <summary>
    /// Evaluates the preconditions and answers them in one step, returning true when the response
    /// is finished and the handler should stop.
    /// </summary>
    public static async ValueTask<bool> TryCompleteConditionalAsync(
        this HttpContext context,
        string? etag,
        DateTimeOffset? lastModified = null
    )
    {
        var result = context.CheckPreconditions(etag, lastModified);

        // The validators go out either way: a 304 that omits the ETag leaves the client unable to
        // revalidate next time, and it has to re-download to find out nothing changed.
        if (etag is not null)
            context.Response.SetETag(etag);

        if (lastModified is { } modified)
            context.Response.SetLastModified(modified);

        if (result == PreconditionResult.Proceed)
            return false;

        await context.CompletePreconditionAsync(result).ConfigureAwait(false);
        return true;
    }

    /// <summary>Sets <c>ETag</c>, quoting the value when the caller did not.</summary>
    public static HttpResponse SetETag(this HttpResponse response, string etag, bool weak = false)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentException.ThrowIfNullOrEmpty(etag);

        var normalized = EntityTag.Normalize(etag)!;

        response.Headers.Set(
            HeaderNames.ETag,
            weak && !normalized.StartsWith("W/", StringComparison.Ordinal) ? "W/" + normalized : normalized
        );

        return response;
    }

    /// <summary>Sets <c>Last-Modified</c> in the one date format RFC 9110 allows.</summary>
    public static HttpResponse SetLastModified(this HttpResponse response, DateTimeOffset value)
    {
        ArgumentNullException.ThrowIfNull(response);

        response.Headers.Set(HeaderNames.LastModified, value.ToUniversalTime().ToString("R", CultureInfo.InvariantCulture));
        return response;
    }

    /// <summary>
    /// Sets <c>Cache-Control</c>.
    /// <para>
    /// Private by default. A server embedded in an app answers one user, and a response marked
    /// public is one a proxy between the app and its tunnel is entitled to hand to someone else.
    /// </para>
    /// </summary>
    public static HttpResponse SetCacheControl(
        this HttpResponse response,
        TimeSpan maxAge,
        bool isPrivate = true,
        bool mustRevalidate = false
    )
    {
        ArgumentNullException.ThrowIfNull(response);

        var directives = new List<string>(3)
        {
            isPrivate ? "private" : "public",
            "max-age=" + ((long)maxAge.TotalSeconds).ToString(CultureInfo.InvariantCulture)
        };

        if (mustRevalidate)
            directives.Add("must-revalidate");

        response.Headers.Set(HeaderNames.CacheControl, string.Join(", ", directives));
        return response;
    }

    /// <summary>Marks a response as never to be stored — an authentication reply, a one-time token.</summary>
    public static HttpResponse SetNoStore(this HttpResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        response.Headers.Set(HeaderNames.CacheControl, "no-store, no-cache");
        return response;
    }

    /// <summary>
    /// A 304 is only meaningful for a request that was going to read. On anything else a matched
    /// <c>If-None-Match</c> means "only if it does not exist yet", and that is a 412.
    /// </summary>
    static bool SafeToSkip(HttpRequest request) => HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method);

    static bool Matches(string header, string? etag)
    {
        var value = header.Trim();

        if (value == "*")
            return etag is not null;

        if (etag is null)
            return false;

        foreach (var candidate in header.Split(','))
        {
            var trimmed = candidate.Trim();

            // Weak comparison: W/"x" and "x" are the same entity for the purposes of a conditional
            // GET, which is what both If-None-Match and If-Match are asking here.
            if (trimmed.StartsWith("W/", StringComparison.Ordinal))
                trimmed = trimmed[2..];

            var subject = etag.StartsWith("W/", StringComparison.Ordinal) ? etag[2..] : etag;

            if (trimmed == subject)
                return true;
        }

        return false;
    }

    static bool TryParseDate(string value, out DateTimeOffset parsed)
        => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out parsed);
}

/// <summary>Making entity tags out of things a handler already has.</summary>
public static class EntityTag
{
    /// <summary>
    /// A strong tag over some bytes — a row version, a serialised payload, a file's contents.
    /// <para>
    /// SHA-256 truncated to 128 bits. A faster non-cryptographic hash would do the job — an entity
    /// tag is an equality check, not a signature — but every one of those lives in a NuGet package,
    /// and this server does not take a dependency to save a microsecond per response.
    /// </para>
    /// </summary>
    public static string FromContent(ReadOnlySpan<byte> content)
    {
        Span<byte> digest = stackalloc byte[32];
        System.Security.Cryptography.SHA256.HashData(content, digest);

        return "\"" + Convert.ToHexStringLower(digest[..16]) + "\"";
    }

    /// <summary>A strong tag over a string — a version column, a revision id.</summary>
    public static string FromContent(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return FromContent(System.Text.Encoding.UTF8.GetBytes(content));
    }

    /// <summary>The tag shape a file already uses here: last write time and length.</summary>
    public static string FromMetadata(DateTimeOffset lastModified, long length)
        => $"\"{lastModified.ToUnixTimeMilliseconds():x}-{length:x}\"";

    /// <summary>Adds the quotes callers forget, and leaves an already-quoted or weak tag alone.</summary>
    public static string? Normalize(string? etag)
    {
        if (etag is not { Length: > 0 })
            return null;

        return etag.StartsWith('"') || etag.StartsWith("W/\"", StringComparison.Ordinal) ? etag : $"\"{etag}\"";
    }
}
