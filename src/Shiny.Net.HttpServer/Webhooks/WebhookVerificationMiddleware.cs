using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Shiny.Net.HttpServer.Webhooks;

/// <summary>
/// Verifies deliveries to endpoints that carry <see cref="WebhookMetadata"/> — from
/// <c>[RequireWebhookSignature]</c> on a generated endpoint, or <c>.RequireWebhookSignature(…)</c> on a
/// mapped route. Installed with <c>app.UseWebhookVerification()</c>; <c>MapWebhook</c> does the same
/// work inline and does not need it.
/// <para>
/// It runs after routing, because which verifier applies is a property of the endpoint. Endpoints
/// without the metadata pass straight through, so installing it costs nothing elsewhere.
/// </para>
/// </summary>
public sealed class WebhookVerificationMiddleware(WebhookOptions options, ILogger<WebhookVerificationMiddleware>? logger = null) : IHttpMiddleware
{
    readonly WebhookOptions options = options ?? throw new ArgumentNullException(nameof(options));
    readonly ILogger logger = logger ?? NullLogger<WebhookVerificationMiddleware>.Instance;

    public ValueTask InvokeAsync(HttpContext context, RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var metadata = context.Endpoint?.GetMetadata<WebhookMetadata>();
        if (metadata is null)
            return next(context);

        var verifier = metadata.Verifier
            ?? (metadata.VerifierName is { } name
                ? this.options.GetVerifier(name)
                : throw new InvalidOperationException(
                    $"Endpoint '{context.Endpoint!.DisplayName}' requires a webhook signature but names no verifier."
                ));

        return WebhookPipeline.RunAsync(context, verifier, this.options, this.logger, next);
    }
}

/// <summary>
/// Buffer, verify, deduplicate, invoke — the one sequence both <c>MapWebhook</c> and the middleware run.
/// </summary>
static class WebhookPipeline
{
    const string RejectedDetail = "The webhook signature could not be verified.";

    public static async ValueTask RunAsync(
        HttpContext context,
        IWebhookVerifier verifier,
        WebhookOptions options,
        ILogger logger,
        RequestDelegate next
    )
    {
        // Already verified — the middleware ran ahead of a MapWebhook route. Verifying twice would be
        // harmless but recording the delivery id twice would make it a duplicate of itself.
        if (context.TryGetWebhook(out _))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var request = context.Request;
        byte[] body;

        try
        {
            body = await request.ReadBodyAsBytesAsync(options.MaxBodySize, context.RequestAborted).ConfigureAwait(false);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            logger.LogWarning(
                "Rejected {Verifier} webhook {Method} {Path}: body exceeds {Limit} bytes",
                verifier.Name,
                request.Method,
                request.Path,
                options.MaxBodySize
            );

            await WriteProblemAsync(context, StatusCodes.Status413PayloadTooLarge, $"The webhook body exceeds the {options.MaxBodySize} byte limit.")
                .ConfigureAwait(false);
            return;
        }

        // Whatever runs next reads the same bytes that were verified, through the ordinary API.
        request.Body = new MemoryStream(body, writable: false);

        var result = verifier.Verify(request.Headers, body, options.TimeProvider.GetUtcNow());

        if (!result.Succeeded)
        {
            // The reason goes to the log and nowhere else. A forger learns only that it failed.
            logger.LogWarning(
                "Rejected {Verifier} webhook {Method} {Path} from {Address}: {Reason}",
                verifier.Name,
                request.Method,
                request.Path,
                context.Connection.RemoteIpAddress?.ToString() ?? "an unknown address",
                result.FailureReason
            );

            if (options.OnRejected is { } onRejected)
            {
                await onRejected(context, result).ConfigureAwait(false);
                return;
            }

            await WriteProblemAsync(context, StatusCodes.Status401Unauthorized, RejectedDetail).ConfigureAwait(false);
            return;
        }

        var webhook = new WebhookContext(context, verifier.Name, body, result);
        context.SetWebhook(webhook);

        string? deliveryKey = null;

        if (options.DeliveryStore is { } store && result.DeliveryId is { Length: > 0 } deliveryId)
        {
            // Keyed by sender as well as id: two senders' ids are two namespaces, and a collision
            // between them would silently drop a real delivery.
            deliveryKey = verifier.Name + ":" + deliveryId;

            if (!await store.TryAddAsync(deliveryKey, context.RequestAborted).ConfigureAwait(false))
            {
                logger.LogInformation(
                    "Acknowledged duplicate {Verifier} webhook delivery {DeliveryId} without handling it",
                    verifier.Name,
                    deliveryId
                );

                context.Response.StatusCode = options.DuplicateStatusCode;
                context.Response.ContentLength = 0;
                return;
            }
        }

        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch
        {
            await ForgetAsync(options, deliveryKey).ConfigureAwait(false);
            throw;
        }

        // A 5xx tells the sender to retry. Keeping the id would turn that retry into a "duplicate"
        // of a delivery that never succeeded, and the event would be lost.
        if (context.Response.StatusCode >= 500)
            await ForgetAsync(options, deliveryKey).ConfigureAwait(false);
    }

    static ValueTask ForgetAsync(WebhookOptions options, string? deliveryKey)
        => deliveryKey is not null && options.DeliveryStore is { } store
            ? store.RemoveAsync(deliveryKey, CancellationToken.None)
            : ValueTask.CompletedTask;

    static ValueTask WriteProblemAsync(HttpContext context, int status, string detail)
    {
        var problem = new ProblemDetails { Detail = detail };
        ProblemDetailsDefaults.ApplyDefaults(problem, context, status);

        return ProblemDetailsWriter.WriteResponseAsync(context, problem, context.RequestAborted);
    }
}
