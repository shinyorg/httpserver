using System.Buffers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shiny.Net.HttpServer.Caching;

namespace Shiny.Net.HttpServer.Idempotency;

/// <summary>
/// Answers a retried request with the response its first attempt got, instead of running the
/// handler twice.
/// <para>
/// Runs after routing because whether a key matters is a property of the endpoint, and after
/// authorization because the caller is part of the key: a replay must never hand one user the
/// response to another user's request just because their clients picked the same key.
/// </para>
/// <para>
/// A request carrying a key goes one of four ways:
/// <list type="bullet">
/// <item>The key is new — the handler runs, and its response is stored.</item>
/// <item>The key's first request finished — the stored response is replayed, marked as such.</item>
/// <item>The key's first request is still running — <b>409</b>. Running a second copy alongside is
/// the double charge this exists to prevent; waiting for it would hold a connection open for as long
/// as the first one takes.</item>
/// <item>The key was used with a different body — <b>422</b>. That is a client bug, and replaying
/// the first answer would hide it.</item>
/// </list>
/// </para>
/// </summary>
public sealed class IdempotencyMiddleware(
    IdempotencyOptions options,
    IIdempotencyStore store,
    ILogger<IdempotencyMiddleware>? logger = null
) : IHttpMiddleware
{
    /// <summary>Separates the parts of a store key. Not a character any of them can contain.</summary>
    const char KeySeparator = '\u001f';

    readonly IdempotencyOptions options = options ?? throw new ArgumentNullException(nameof(options));
    readonly IIdempotencyStore store = store ?? throw new ArgumentNullException(nameof(store));
    readonly ILogger logger = logger ?? NullLogger<IdempotencyMiddleware>.Instance;

    public async ValueTask InvokeAsync(HttpContext context, RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var metadata = context.Endpoint?.GetMetadata<IdempotencyMetadata>();

        if (metadata is null or { Disabled: true } || !this.options.Methods.Contains(context.Request.Method))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var rawKey = context.Request.Headers.GetFirst(this.options.HeaderName);

        if (rawKey is null)
        {
            if (metadata.Required)
            {
                await ProblemAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    "Idempotency key required",
                    $"This endpoint requires an '{this.options.HeaderName}' header, so a retry of the request cannot be applied twice."
                ).ConfigureAwait(false);
                return;
            }

            await next(context).ConfigureAwait(false);
            return;
        }

        if (!TryParseKey(rawKey, this.options.MaxKeyLength, out var clientKey))
        {
            await ProblemAsync(
                context,
                StatusCodes.Status400BadRequest,
                "Invalid idempotency key",
                $"The '{this.options.HeaderName}' header must be a non-empty string of at most {this.options.MaxKeyLength} characters."
            ).ConfigureAwait(false);
            return;
        }

        // The body is read before anything is reserved: a request that turns out to be too large, or
        // whose Content-Digest does not verify, must not leave a claim behind that locks out the
        // client's corrected retry.
        var body = await ReadBodyAsync(context, this.options.MaxRequestBodyBytes).ConfigureAwait(false);

        if (body is null)
        {
            await ProblemAsync(
                context,
                StatusCodes.Status413PayloadTooLarge,
                "Request body too large for an idempotent request",
                $"A request carrying '{this.options.HeaderName}' is limited to {this.options.MaxRequestBodyBytes} bytes, because its body has to be held to fingerprint it."
            ).ConfigureAwait(false);
            return;
        }

        var fingerprint = Convert.ToBase64String(SHA256.HashData(body));
        var key = BuildKey(context, clientKey);
        var abort = context.RequestAborted;

        var existing = await this.store.TryReserveAsync(key, fingerprint, this.options.InFlightTimeout, abort).ConfigureAwait(false);

        if (existing is not null)
        {
            await this.AnswerFromExistingAsync(context, existing, fingerprint).ConfigureAwait(false);
            return;
        }

        // Handed on rewound, so the handler reads exactly what was fingerprinted.
        context.Request.Body = new MemoryStream(body, writable: false);

        var response = context.Response;
        var original = response.BodyControl;
        var buffer = new BufferingBodyControl(response, this.options.MaxResponseBodyBytes);
        response.Bind(buffer);

        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch
        {
            // A failure is not an answer. The claim goes, so the client's retry gets to run.
            await this.store.ReleaseAsync(key, CancellationToken.None).ConfigureAwait(false);

            // Nothing has reached the wire, so the error response is written to the connection
            // directly rather than into a buffer that is about to be thrown away.
            if (!response.HasStarted)
                response.Bind(original);

            throw;
        }

        // Flushed here rather than through CompleteAsync: the connection completes its own producer,
        // not whatever the response ended up bound to.
        await buffer.FinishAsync(abort).ConfigureAwait(false);

        if (buffer.Captured is { } captured && this.options.ShouldStoreStatusCode(response.StatusCode))
        {
            var now = DateTimeOffset.UtcNow;
            var stored = new IdempotentResponse(
                response.StatusCode,
                OutputCacheMiddleware.Storable(response.Headers),
                captured,
                now
            );

            var record = new IdempotencyRecord(fingerprint, stored, now + (metadata.Expiration ?? this.options.Expiration));
            await this.store.CompleteAsync(key, record, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        if (buffer.Captured is null)
        {
            this.logger.LogWarning(
                "The response to {Method} {Path} was streamed or larger than {Limit} bytes, so it cannot be replayed; a retry with the same key will run the handler again",
                context.Request.Method,
                context.Request.Path,
                this.options.MaxResponseBodyBytes
            );
        }

        await this.store.ReleaseAsync(key, CancellationToken.None).ConfigureAwait(false);
    }

    async ValueTask AnswerFromExistingAsync(HttpContext context, IdempotencyRecord existing, string fingerprint)
    {
        // Compared before anything else: a different payload under a key that is still running is
        // still a misused key, and saying "conflict, retry later" would send the client round again
        // to be told the same thing.
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(existing.Fingerprint), Encoding.ASCII.GetBytes(fingerprint)))
        {
            await ProblemAsync(
                context,
                StatusCodes.Status422UnprocessableEntity,
                "Idempotency key reused",
                $"The '{this.options.HeaderName}' was already used for a request with a different body. Use a new key for a new request."
            ).ConfigureAwait(false);
            return;
        }

        if (existing.Response is not { } stored)
        {
            context.Response.Headers.Set(HeaderNames.RetryAfter, "1");

            await ProblemAsync(
                context,
                StatusCodes.Status409Conflict,
                "Request already in progress",
                $"A request with this '{this.options.HeaderName}' is still being processed. Retry once it has finished to receive its response."
            ).ConfigureAwait(false);
            return;
        }

        await this.ReplayAsync(context, stored).ConfigureAwait(false);
    }

    async ValueTask ReplayAsync(HttpContext context, IdempotentResponse stored)
    {
        var response = context.Response;

        response.StatusCode = stored.StatusCode;

        foreach (var header in stored.Headers)
            response.Headers.Append(header.Key, header.Value);

        if (this.options.ReplayedHeaderName is { Length: > 0 } marker)
            response.Headers.Set(marker, "true");

        if (stored.Body.Length == 0)
        {
            response.ContentLength = 0;
            await response.StartAsync(context.RequestAborted).ConfigureAwait(false);
            return;
        }

        await response.WriteBytesAsync(stored.Body, cancellationToken: context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>
    /// Accepts the draft's form — a structured-field string, <c>"8e03978e-…"</c> — and the unquoted
    /// form most clients actually send. Either way the key is opaque; nothing about its shape is
    /// checked beyond being printable and bounded.
    /// </summary>
    internal static bool TryParseKey(string raw, int maxLength, out string key)
    {
        var value = raw.Trim();

        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            value = value[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);

        key = value;

        if (value.Length == 0 || value.Length > maxLength)
            return false;

        foreach (var c in value)
        {
            if (c < 0x20 || c > 0x7e)
                return false;
        }

        return true;
    }

    /// <summary>
    /// The client's key, scoped by method, path and caller.
    /// <para>
    /// Scoped by path because the draft makes a key unique per resource, not per server — two
    /// endpoints are allowed to see the same key. Scoped by caller because a key is chosen by a
    /// client, and two clients choosing the same one must not be able to read each other's replies.
    /// </para>
    /// </summary>
    static string BuildKey(HttpContext context, string clientKey)
    {
        var request = context.Request;
        var builder = new StringBuilder(request.Method.Length + request.Path.Length + clientKey.Length + 32)
            .Append(request.Method)
            .Append(KeySeparator)
            .Append(request.Path)
            .Append(KeySeparator);

        if (context.User.Identity is { IsAuthenticated: true } identity)
        {
            builder
                .Append(identity.AuthenticationType)
                .Append(':')
                .Append(context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? identity.Name);
        }

        return builder.Append(KeySeparator).Append(clientKey).ToString();
    }

    /// <summary>Reads the whole body, or returns null once it passes <paramref name="limit"/>.</summary>
    static async ValueTask<byte[]?> ReadBodyAsync(HttpContext context, int limit)
    {
        var request = context.Request;

        if (!request.HasBody)
            return [];

        if (request.ContentLength > limit)
            return null;

        var buffer = new ArrayBufferWriter<byte>((int)Math.Min(request.ContentLength ?? 4096, limit) + 1);

        while (true)
        {
            var memory = buffer.GetMemory(4096);
            var read = await request.Body.ReadAsync(memory, context.RequestAborted).ConfigureAwait(false);

            if (read == 0)
                return buffer.WrittenSpan.ToArray();

            buffer.Advance(read);

            if (buffer.WrittenCount > limit)
                return null;
        }
    }

    static ValueTask ProblemAsync(HttpContext context, int status, string title, string detail)
        => Results.Problem(status, detail, title).ExecuteAsync(context);
}
