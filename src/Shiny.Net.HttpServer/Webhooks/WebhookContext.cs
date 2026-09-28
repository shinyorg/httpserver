using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Shiny.Net.HttpServer.Webhooks;

/// <summary>
/// A delivery that has passed verification: the raw body it was verified over, and what the sender
/// said about it.
/// <para>
/// The body is here as bytes because it had to be buffered to verify it, and reading it again from
/// the connection is not possible. <see cref="HttpRequest.Body"/> has also been replaced with a
/// stream over the same bytes, so code that reads the request the ordinary way — a generated
/// endpoint's <c>[FromBody]</c>, a form reader — still works.
/// </para>
/// </summary>
public sealed class WebhookContext
{
    internal WebhookContext(HttpContext httpContext, string verifier, ReadOnlyMemory<byte> body, WebhookVerificationResult result)
    {
        this.HttpContext = httpContext;
        this.Verifier = verifier;
        this.Body = body;
        this.DeliveryId = result.DeliveryId;
        this.Timestamp = result.Timestamp;
        this.EventType = result.EventType;
    }

    public HttpContext HttpContext { get; }

    public HttpRequest Request => this.HttpContext.Request;

    public HttpResponse Response => this.HttpContext.Response;

    /// <summary>Cancelled when the sender goes away.</summary>
    public CancellationToken RequestAborted => this.HttpContext.RequestAborted;

    /// <summary>The <see cref="IWebhookVerifier.Name"/> that accepted the delivery.</summary>
    public string Verifier { get; }

    /// <summary>The body exactly as it was verified.</summary>
    public ReadOnlyMemory<byte> Body { get; }

    /// <summary>The sender's delivery id, when it sends one.</summary>
    public string? DeliveryId { get; }

    /// <summary>When the sender signed the delivery, for senders that sign a timestamp.</summary>
    public DateTimeOffset? Timestamp { get; }

    /// <summary>
    /// The event type, where the verifier found one — <c>X-GitHub-Event</c>, or the top-level
    /// <c>type</c> of a Stripe or Standard Webhooks body.
    /// </summary>
    public string? EventType { get; }

    /// <summary>The body as UTF-8 text.</summary>
    public string ReadAsString() => Encoding.UTF8.GetString(this.Body.Span);

    /// <summary>
    /// Deserializes the body with source-generated metadata — the only kind that survives trimming.
    /// </summary>
    public T? ReadFromJson<T>(JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        return JsonSerializer.Deserialize(this.Body.Span, typeInfo);
    }

    /// <summary>Parses the body into a <see cref="JsonDocument"/>, for payloads not worth a type.</summary>
    public JsonDocument ParseJson() => JsonDocument.Parse(this.Body);
}

/// <summary>Getting at a verified delivery from a plain handler or a generated endpoint.</summary>
public static class WebhookHttpContextExtensions
{
    static readonly object Key = new();

    /// <summary>
    /// The verified delivery for this request.
    /// <para>
    /// Throws rather than returning null when there is none, because the only way to get here without
    /// one is an endpoint that asked for verification on a server that never installed
    /// <c>UseWebhookVerification()</c> — and a handler that carried on regardless would be acting on a
    /// delivery nobody checked.
    /// </para>
    /// </summary>
    public static WebhookContext GetWebhook(this HttpContext context)
        => context.TryGetWebhook(out var webhook)
            ? webhook
            : throw new InvalidOperationException(
                "This request has not been through webhook verification. Map it with app.MapWebhook(...), " +
                "or call app.UseWebhookVerification() and mark the endpoint with [RequireWebhookSignature] " +
                "or .RequireWebhookSignature(...)."
            );

    /// <summary>The verified delivery, if this request has been verified.</summary>
    public static bool TryGetWebhook(this HttpContext context, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out WebhookContext? webhook)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Items.TryGetValue(Key, out var value) && value is WebhookContext found)
        {
            webhook = found;
            return true;
        }

        webhook = null;
        return false;
    }

    internal static void SetWebhook(this HttpContext context, WebhookContext webhook)
        => context.Items[Key] = webhook;
}
