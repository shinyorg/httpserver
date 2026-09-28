using System.Security.Cryptography;

namespace Shiny.Net.HttpServer.Webhooks;

/// <summary>
/// Signs outgoing webhooks with the <see href="https://www.standardwebhooks.com/">Standard Webhooks</see>
/// scheme — the sending half of <see cref="StandardWebhookVerifier"/>.
/// <para>
/// For an app that is the sender: a device that notifies a backend when something happens on it,
/// or one embedded server calling another. Any Standard Webhooks library on the receiving end —
/// Svix's, or this server's own verifier — can check what it produces.
/// </para>
/// <code>
/// var signer = WebhookSigner.StandardWebhooks(secret);
/// var body = JsonSerializer.SerializeToUtf8Bytes(evt, AppJson.Default.DeviceEvent);
///
/// using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(body) };
/// signer.Apply(request, messageId: evt.Id, body);
/// await http.SendAsync(request);
/// </code>
/// </summary>
public sealed class StandardWebhookSigner
{
    readonly byte[] key;
    readonly TimeProvider timeProvider;

    /// <param name="secret">A base64 secret, with or without the <c>whsec_</c> prefix. <see cref="WebhookSigner.GenerateSecret"/> makes one.</param>
    /// <param name="timeProvider">The clock the timestamp comes from, when <see cref="Apply"/> is not given one.</param>
    public StandardWebhookSigner(string secret, TimeProvider? timeProvider = null)
    {
        this.key = StandardWebhookVerifier.DecodeSecret(secret);
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// The <c>webhook-signature</c> value for one message: <c>v1,&lt;base64&gt;</c>.
    /// </summary>
    /// <param name="messageId">Unique per event and the same across retries of it — receivers deduplicate on it.</param>
    /// <param name="timestamp">When the message is sent; receivers refuse one too far from their own clock.</param>
    /// <param name="body">The exact bytes that will be sent as the request body.</param>
    public string Sign(string messageId, DateTimeOffset timestamp, ReadOnlySpan<byte> body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);

        var hash = WebhookCrypto.Compute(HashAlgorithmName.SHA256, this.key, $"{messageId}.{timestamp.ToUnixTimeSeconds()}.", body);
        return "v1," + Convert.ToBase64String(hash);
    }

    /// <summary>
    /// Adds <c>webhook-id</c>, <c>webhook-timestamp</c> and <c>webhook-signature</c> to a request.
    /// <paramref name="body"/> must be exactly the bytes the request's content sends.
    /// </summary>
    public void Apply(HttpRequestMessage request, string messageId, ReadOnlySpan<byte> body, DateTimeOffset? timestamp = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        var at = timestamp ?? this.timeProvider.GetUtcNow();
        var signature = this.Sign(messageId, at, body);

        request.Headers.Remove(StandardWebhookVerifier.IdHeader);
        request.Headers.Remove(StandardWebhookVerifier.TimestampHeader);
        request.Headers.Remove(StandardWebhookVerifier.SignatureHeader);

        request.Headers.TryAddWithoutValidation(StandardWebhookVerifier.IdHeader, messageId);
        request.Headers.TryAddWithoutValidation(StandardWebhookVerifier.TimestampHeader, at.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation(StandardWebhookVerifier.SignatureHeader, signature);
    }
}

/// <summary>Signing webhooks this app sends.</summary>
public static class WebhookSigner
{
    /// <summary>A Standard Webhooks signer for the given secret.</summary>
    public static StandardWebhookSigner StandardWebhooks(string secret, TimeProvider? timeProvider = null)
        => new(secret, timeProvider);

    /// <summary>
    /// A fresh <c>whsec_</c> secret: 32 random bytes, base64. Hand it to the receiver once, over a
    /// channel you trust, and keep it out of source control.
    /// </summary>
    public static string GenerateSecret()
        => "whsec_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
}
