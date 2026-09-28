namespace Shiny.Net.HttpServer.Webhooks;

/// <summary>
/// Decides whether a webhook delivery really came from the sender it claims to — by checking the
/// signature the sender computed over the body with a secret only the two of you share.
/// <para>
/// A verifier sees the headers and the exact bytes that arrived, never a parsed or re-serialized
/// form. That is not a detail: every provider signs the bytes it sent, and a body that has been
/// through a JSON parser and back — reordered keys, different whitespace, a normalised number — no
/// longer matches its own signature. The pipeline buffers the raw body before anything else can
/// touch it and hands that buffer here.
/// </para>
/// <para>
/// Built-in verifiers come from <see cref="WebhookSignature"/>. Implement this for a provider it
/// does not cover; compare with <see cref="System.Security.Cryptography.CryptographicOperations.FixedTimeEquals"/>,
/// never <c>==</c> or <c>SequenceEqual</c>, which return as soon as a byte differs and so tell an
/// attacker how much of a forged signature was right.
/// </para>
/// </summary>
public interface IWebhookVerifier
{
    /// <summary>
    /// Short name for the sender, used in logs and to keep delivery ids from different senders
    /// apart in the duplicate store — <c>github</c>, <c>stripe</c>.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Checks one delivery. Must not throw for a bad or missing signature — that is an ordinary
    /// rejection, reported through <see cref="WebhookVerificationResult.Fail"/>.
    /// </summary>
    /// <param name="headers">The request headers, case-insensitive.</param>
    /// <param name="body">The raw request body, exactly as it arrived.</param>
    /// <param name="now">The current time, for replay protection. Comes from <see cref="WebhookOptions.TimeProvider"/>.</param>
    WebhookVerificationResult Verify(HeaderDictionary headers, ReadOnlySpan<byte> body, DateTimeOffset now);
}

/// <summary>
/// The outcome of <see cref="IWebhookVerifier.Verify"/>: either a verified delivery and what the
/// sender said about it, or the reason it was refused.
/// <para>
/// The reason is for the log, not the caller. The response to a rejected delivery is the same 401
/// whether the signature was wrong, missing or stale, because telling a forger which check they
/// failed is telling them which part to fix.
/// </para>
/// </summary>
public sealed class WebhookVerificationResult
{
    WebhookVerificationResult(bool succeeded, string? failureReason, string? deliveryId, DateTimeOffset? timestamp, string? eventType)
    {
        this.Succeeded = succeeded;
        this.FailureReason = failureReason;
        this.DeliveryId = deliveryId;
        this.Timestamp = timestamp;
        this.EventType = eventType;
    }

    /// <summary>True when the signature checked out and the delivery is fresh.</summary>
    public bool Succeeded { get; }

    /// <summary>Why the delivery was refused, for the log. Null on success.</summary>
    public string? FailureReason { get; }

    /// <summary>
    /// The sender's id for this delivery — the same across its retries, which is what makes
    /// duplicate suppression possible. Null when the sender does not provide one.
    /// </summary>
    public string? DeliveryId { get; }

    /// <summary>When the sender says it signed the delivery, for senders that sign a timestamp.</summary>
    public DateTimeOffset? Timestamp { get; }

    /// <summary>The event type, when the sender puts it somewhere the verifier already reads.</summary>
    public string? EventType { get; }

    /// <summary>A verified delivery.</summary>
    public static WebhookVerificationResult Success(string? deliveryId = null, DateTimeOffset? timestamp = null, string? eventType = null)
        => new(true, null, deliveryId, timestamp, eventType);

    /// <summary>A refused delivery, with the reason to log.</summary>
    public static WebhookVerificationResult Fail(string reason)
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);
        return new(false, reason, null, null, null);
    }
}
