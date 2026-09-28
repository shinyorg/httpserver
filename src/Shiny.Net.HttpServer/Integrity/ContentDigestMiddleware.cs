using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shiny.Net.HttpServer.Internal;
using Shiny.Net.HttpServer.Routing;

namespace Shiny.Net.HttpServer.Integrity;

/// <summary>
/// Checks <c>Content-Digest</c> on request bodies and adds it — and <c>Repr-Digest</c> — to responses
/// (RFC 9530).
/// <para>
/// Runs <b>before</b> routing, and must be registered ahead of <c>UseRequestDecompression</c> and
/// <c>UseResponseCompression</c>: a <c>Content-Digest</c> is over the bytes on the wire, content
/// coding included, so it has to see them before decompression and after compression. It looks the
/// endpoint up itself for per-route settings, the same way CORS and rate limiting do, which also
/// means static files and fallbacks are covered by the server-wide options.
/// </para>
/// </summary>
public sealed class ContentDigestMiddleware(
    Router router,
    ContentDigestOptions options,
    ILogger<ContentDigestMiddleware>? logger = null
) : IHttpMiddleware
{
    readonly Router router = router ?? throw new ArgumentNullException(nameof(router));
    readonly ContentDigestOptions options = options ?? throw new ArgumentNullException(nameof(options));
    readonly ILogger logger = logger ?? NullLogger<ContentDigestMiddleware>.Instance;

    public async ValueTask InvokeAsync(HttpContext context, RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var metadata = EndpointResolver.Resolve(this.router, context)?.GetMetadata<ContentDigestMetadata>();

        if (metadata is { Disabled: true })
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var request = context.Request;
        DigestVerifyingStream? verifier = null;

        if (this.options.VerifyRequests)
        {
            var header = DigestFields.Join(request.Headers[DigestFields.ContentDigest]);

            if (header.Length > 0)
            {
                var (rejection, stream) = this.PrepareVerification(request, header);

                if (rejection is not null)
                {
                    await this.RejectAsync(context, rejection).ConfigureAwait(false);
                    return;
                }

                verifier = stream;
            }
            else if (request.HasBody && (metadata?.RequireRequestDigest ?? this.options.RequireRequestDigest))
            {
                await this.RejectAsync(
                    context,
                    $"This endpoint requires a '{DigestFields.ContentDigest}' header on the request body."
                ).ConfigureAwait(false);
                return;
            }
        }

        if (verifier is not null)
            request.Body = verifier;

        var response = context.Response;
        var original = response.BodyControl;
        var control = this.CreateBodyControl(context, metadata, holdForVerdict: verifier is not null);

        if (control is not null)
            response.Bind(control);

        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (Exception ex) when (!response.HasStarted)
        {
            // Nothing reached the wire, so whatever the handler had written is dropped and the error
            // goes straight to the connection rather than into a body that is being held.
            if (control is not null)
                response.Bind(original);

            if (verifier?.Failure is { } failure && ReferenceEquals(ex, failure))
            {
                await this.RejectAsync(context, failure.Message).ConfigureAwait(false);
                return;
            }

            throw;
        }

        // A handler that caught the mismatch and answered anyway has answered a request whose body is
        // not what the client sent. While nothing has reached the wire, that answer is replaced.
        if (verifier?.Failure is { } swallowed && !response.HasStarted)
        {
            if (control is not null)
                response.Bind(original);

            await this.RejectAsync(context, swallowed.Message).ConfigureAwait(false);
            return;
        }

        if (control is not null)
            await control.FinishAsync(context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>
    /// Parses the request's digest and either returns why it is refused or a stream that checks the
    /// body against it.
    /// </summary>
    (string? Rejection, DigestVerifyingStream? Stream) PrepareVerification(HttpRequest request, string header)
    {
        if (!DigestFields.TryParse(header, out var digests))
            return ($"The '{DigestFields.ContentDigest}' header is not a valid structured-field dictionary of byte sequences.", null);

        var supported = new List<KeyValuePair<string, byte[]>>(digests.Count);

        foreach (var digest in digests)
        {
            if (DigestFields.IsSupported(digest.Key))
                supported.Add(digest);
        }

        if (supported.Count == 0)
        {
            if (!this.options.RejectUnsupportedAlgorithms)
                return (null, null);

            return ($"None of the algorithms in '{DigestFields.ContentDigest}' are supported. Use '{DigestFields.Sha256}' or '{DigestFields.Sha512}'.", null);
        }

        // No body: the digest is of empty content, and can be checked right now.
        if (!request.HasBody)
        {
            foreach (var (algorithm, expected) in supported)
            {
                using var hash = DigestFields.CreateHash(algorithm);

                if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), expected))
                    return ($"The request body does not match its Content-Digest ({algorithm}).", null);
            }

            return (null, null);
        }

        return (null, new DigestVerifyingStream(request.Body, supported, request.ContentLength));
    }

    /// <summary>
    /// Decides which digests this response gets, and wraps the body to compute them. Null when it gets
    /// none, so a response nobody asked a digest of pays nothing.
    /// <para>
    /// A request whose body is being verified has its response held too, digest or not
    /// (<paramref name="holdForVerdict"/>). The verdict only exists once the body has been read to the
    /// end, and a handler that catches the failure and answers anyway would otherwise have its 200
    /// on the wire before anything could replace it.
    /// </para>
    /// </summary>
    DigestingBodyControl? CreateBodyControl(HttpContext context, ContentDigestMetadata? metadata, bool holdForVerdict)
    {
        var mode = metadata?.ResponseDigest ?? this.options.ResponseDigest;
        var request = context.Request;

        // HEAD has no content to digest, and an upgrade's body belongs to another protocol.
        if (HttpMethods.IsHead(request.Method)
            || HttpMethods.IsConnect(request.Method)
            || request.Headers.ContainsKey(HeaderNames.Upgrade))
        {
            return null;
        }

        if (mode == DigestEmission.Never)
        {
            return holdForVerdict
                ? new DigestingBodyControl(context.Response, this.options.MaxBufferedResponseBytes, null, null)
                : null;
        }

        var content = DigestFields.SelectPreferred(DigestFields.Join(request.Headers[DigestFields.WantContentDigest]));
        var repr = DigestFields.SelectPreferred(DigestFields.Join(request.Headers[DigestFields.WantReprDigest]));

        if (content is null && mode == DigestEmission.Always)
            content = this.options.DefaultAlgorithm;

        if (content is null && repr is null && !holdForVerdict)
            return null;

        return new DigestingBodyControl(context.Response, this.options.MaxBufferedResponseBytes, content, repr);
    }

    async ValueTask RejectAsync(HttpContext context, string detail)
    {
        this.logger.LogWarning(
            "Refused {Method} {Path}: {Detail}",
            context.Request.Method,
            context.Request.Path,
            detail
        );

        // Says what would have been accepted, which is how RFC 9530 has a server ask for a digest.
        context.Response.Headers.Set(DigestFields.WantContentDigest, DigestFields.WantValue(this.options.DefaultAlgorithm));

        await Results
            .Problem(StatusCodes.Status400BadRequest, detail, "Content digest check failed")
            .ExecuteAsync(context)
            .ConfigureAwait(false);
    }
}
