using System.Security.Cryptography;

namespace Shiny.Net.HttpServer.Webhooks;

/// <summary>
/// The shape of an HMAC signature that covers the body alone and arrives in one header — which is
/// most of them. <see cref="WebhookSignature.Hmac"/> builds a verifier from it.
/// <code>
/// // Shopify: base64 HMAC-SHA256 of the body in X-Shopify-Hmac-Sha256
/// WebhookSignature.Hmac(o =>
/// {
///     o.Name = "shopify";
///     o.HeaderName = "X-Shopify-Hmac-Sha256";
///     o.Encoding = WebhookSignatureEncoding.Base64;
///     o.Secrets.Add(shopifySecret);
///     o.DeliveryIdHeader = "X-Shopify-Webhook-Id";
/// });
/// </code>
/// </summary>
public sealed class HmacWebhookOptions
{
    /// <summary>The sender's name, for logs and the duplicate store. <c>hmac</c> by default.</summary>
    public string Name { get; set; } = "hmac";

    /// <summary>The header carrying the signature. Required.</summary>
    public string HeaderName { get; set; } = "";

    public WebhookHmacAlgorithm Algorithm { get; set; } = WebhookHmacAlgorithm.Sha256;

    public WebhookSignatureEncoding Encoding { get; set; } = WebhookSignatureEncoding.Hex;

    /// <summary>
    /// Text the sender puts in front of the encoded signature, such as <c>sha256=</c>. A header
    /// without it is rejected — it is part of the format, and a missing one means something else
    /// wrote the header.
    /// </summary>
    public string? Prefix { get; set; }

    /// <summary>
    /// The shared secrets, as text (UTF-8). More than one while rotating: add the new secret, move
    /// the sender over, then remove the old one — no delivery is refused in between.
    /// </summary>
    public IList<string> Secrets { get; } = [];

    /// <summary>Header with the sender's delivery id, for duplicate suppression.</summary>
    public string? DeliveryIdHeader { get; set; }

    /// <summary>Header with the event type, surfaced as <see cref="WebhookContext.EventType"/>.</summary>
    public string? EventTypeHeader { get; set; }
}

/// <summary>
/// Verifies an HMAC of the body carried in a single header. <see cref="WebhookSignature.GitHub(string[])"/>
/// is this with GitHub's header and prefix filled in.
/// <para>
/// No timestamp is signed in this shape, so there is no replay window to enforce — a captured
/// delivery stays valid. Pair it with duplicate suppression (<see cref="WebhookOptions.SuppressDuplicates"/>)
/// when the sender provides a delivery id, which is the protection this shape does allow.
/// </para>
/// </summary>
public sealed class HmacWebhookVerifier : IWebhookVerifier
{
    readonly HmacWebhookOptions options;
    readonly HashAlgorithmName algorithm;
    readonly byte[][] keys;

    public HmacWebhookVerifier(HmacWebhookOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.HeaderName, "options.HeaderName");
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Name, "options.Name");

        // Copied rather than held, so changing the options object after the verifier is built cannot
        // quietly change what it accepts.
        this.options = new HmacWebhookOptions
        {
            Name = options.Name,
            HeaderName = options.HeaderName,
            Algorithm = options.Algorithm,
            Encoding = options.Encoding,
            Prefix = options.Prefix,
            DeliveryIdHeader = options.DeliveryIdHeader,
            EventTypeHeader = options.EventTypeHeader
        };
        this.algorithm = WebhookCrypto.ToHashName(options.Algorithm);
        this.keys = WebhookCrypto.Utf8Keys(options.Secrets, options.Name);
    }

    public string Name => this.options.Name;

    public WebhookVerificationResult Verify(HeaderDictionary headers, ReadOnlySpan<byte> body, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(headers);

        var header = headers.GetFirst(this.options.HeaderName);
        if (String.IsNullOrWhiteSpace(header))
            return WebhookVerificationResult.Fail($"the {this.options.HeaderName} header is missing");

        var signature = header.AsSpan().Trim();
        if (this.options.Prefix is { Length: > 0 } prefix)
        {
            if (!signature.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return WebhookVerificationResult.Fail($"the {this.options.HeaderName} header does not start with '{prefix}'");

            signature = signature[prefix.Length..];
        }

        foreach (var key in this.keys)
        {
            var expected = WebhookCrypto.Compute(this.algorithm, key, null, body);

            if (WebhookCrypto.Matches(expected, signature, this.options.Encoding))
            {
                return WebhookVerificationResult.Success(
                    deliveryId: this.options.DeliveryIdHeader is { } idHeader ? headers.GetFirst(idHeader) : null,
                    eventType: this.options.EventTypeHeader is { } typeHeader ? headers.GetFirst(typeHeader) : null
                );
            }
        }

        return WebhookVerificationResult.Fail("the signature does not match any configured secret");
    }
}

/// <summary>
/// Verifies Stripe's <c>Stripe-Signature</c> header: <c>t=&lt;unix&gt;,v1=&lt;hex&gt;[,v1=…]</c>, an
/// HMAC-SHA256 over <c>{t}.{body}</c> with the endpoint's <c>whsec_…</c> signing secret.
/// <para>
/// Stripe sends several <c>v1</c> values while you roll the endpoint secret, one per active secret;
/// any one matching any configured secret is enough. <c>v0</c> entries are test-mode legacy and are
/// ignored, as Stripe's own libraries do. The delivery id is the event's <c>id</c> from the body,
/// which Stripe keeps the same across retries.
/// </para>
/// </summary>
public sealed class StripeWebhookVerifier : IWebhookVerifier
{
    /// <summary>The header Stripe signs deliveries in.</summary>
    public const string SignatureHeader = "Stripe-Signature";

    readonly byte[][] keys;
    readonly TimeSpan? tolerance;

    /// <param name="secrets">The endpoint signing secrets, <c>whsec_…</c> — used as text, exactly as Stripe shows them.</param>
    /// <param name="tolerance">How old a delivery may be. Five minutes, Stripe's own default; null disables the check.</param>
    public StripeWebhookVerifier(IEnumerable<string> secrets, TimeSpan? tolerance)
    {
        this.keys = WebhookCrypto.Utf8Keys(secrets, "Stripe");
        this.tolerance = tolerance;
    }

    public string Name => "stripe";

    public WebhookVerificationResult Verify(HeaderDictionary headers, ReadOnlySpan<byte> body, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(headers);

        var header = headers.GetFirst(SignatureHeader);
        if (String.IsNullOrWhiteSpace(header))
            return WebhookVerificationResult.Fail($"the {SignatureHeader} header is missing");

        string? t = null;
        var signatures = new List<string>(2);

        foreach (var part in header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var equals = part.IndexOf('=');
            if (equals <= 0)
                continue;

            var name = part.AsSpan(0, equals);
            if (name.SequenceEqual("t"))
                t = part[(equals + 1)..];
            else if (name.SequenceEqual("v1"))
                signatures.Add(part[(equals + 1)..]);
        }

        if (signatures.Count == 0)
            return WebhookVerificationResult.Fail($"the {SignatureHeader} header has no v1 signature");

        if (!WebhookCrypto.TryCheckTimestamp(t, now, this.tolerance, out var timestamp, out var failure))
            return WebhookVerificationResult.Fail(failure!);

        foreach (var key in this.keys)
        {
            var expected = WebhookCrypto.Compute(HashAlgorithmName.SHA256, key, t + ".", body);

            foreach (var signature in signatures)
            {
                if (WebhookCrypto.Matches(expected, signature, WebhookSignatureEncoding.Hex))
                {
                    var (id, type) = WebhookCrypto.ReadIdAndType(body);
                    return WebhookVerificationResult.Success(id, timestamp, type);
                }
            }
        }

        return WebhookVerificationResult.Fail("no v1 signature matches any configured secret");
    }
}

/// <summary>
/// Verifies the <see href="https://www.standardwebhooks.com/">Standard Webhooks</see> scheme —
/// <c>webhook-id</c>, <c>webhook-timestamp</c> and <c>webhook-signature</c>, an HMAC-SHA256 over
/// <c>{id}.{timestamp}.{body}</c> — which is what Svix, Clerk, Resend, Supabase, OpenAI and a growing
/// list of senders use.
/// <para>
/// The signature header is a space-separated list of <c>v1,&lt;base64&gt;</c> entries, several while
/// the sender rotates keys; any one matching any configured secret is enough. Secrets are the
/// <c>whsec_</c>-prefixed base64 strings senders hand out; the prefix is optional. The <c>svix-</c>
/// header names are accepted too, since Svix still sends those.
/// </para>
/// </summary>
public sealed class StandardWebhookVerifier : IWebhookVerifier
{
    public const string IdHeader = "webhook-id";
    public const string TimestampHeader = "webhook-timestamp";
    public const string SignatureHeader = "webhook-signature";

    readonly byte[][] keys;
    readonly TimeSpan? tolerance;

    /// <param name="secrets">Base64 secrets, with or without the <c>whsec_</c> prefix.</param>
    /// <param name="tolerance">How old a delivery may be. Five minutes, the spec's recommendation; null disables the check.</param>
    public StandardWebhookVerifier(IEnumerable<string> secrets, TimeSpan? tolerance)
    {
        ArgumentNullException.ThrowIfNull(secrets);

        this.keys = secrets.Select(DecodeSecret).ToArray();
        if (this.keys.Length == 0)
            throw new ArgumentException("A Standard Webhooks verifier needs at least one secret.", nameof(secrets));

        this.tolerance = tolerance;
    }

    public string Name => "standard-webhooks";

    /// <summary>Decodes a <c>whsec_…</c> secret to its key bytes. Throws for anything that is not base64.</summary>
    public static byte[] DecodeSecret(string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);

        var value = secret.StartsWith("whsec_", StringComparison.Ordinal) ? secret[6..] : secret;

        try
        {
            var key = Convert.FromBase64String(value);
            return key.Length > 0 ? key : throw new FormatException();
        }
        catch (FormatException)
        {
            // Said at construction, where it is a configuration mistake with a stack trace, rather
            // than as every delivery failing verification for no reason the log can name.
            throw new ArgumentException(
                "A Standard Webhooks secret is base64, optionally prefixed with 'whsec_'. This one does not decode.",
                nameof(secret)
            );
        }
    }

    public WebhookVerificationResult Verify(HeaderDictionary headers, ReadOnlySpan<byte> body, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(headers);

        var id = headers.GetFirst(IdHeader) ?? headers.GetFirst("svix-id");
        var rawTimestamp = headers.GetFirst(TimestampHeader) ?? headers.GetFirst("svix-timestamp");
        var header = headers.GetFirst(SignatureHeader) ?? headers.GetFirst("svix-signature");

        if (String.IsNullOrWhiteSpace(id) || String.IsNullOrWhiteSpace(header))
            return WebhookVerificationResult.Fail($"the {IdHeader} or {SignatureHeader} header is missing");

        if (!WebhookCrypto.TryCheckTimestamp(rawTimestamp, now, this.tolerance, out var timestamp, out var failure))
            return WebhookVerificationResult.Fail(failure!);

        var entries = header.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var signedPrefix = $"{id}.{rawTimestamp}.";

        foreach (var key in this.keys)
        {
            var expected = WebhookCrypto.Compute(HashAlgorithmName.SHA256, key, signedPrefix, body);

            foreach (var entry in entries)
            {
                // "v1a" is the asymmetric (ed25519) variant; skipping it rather than failing lets a
                // sender that offers both still be verified by the symmetric one.
                if (!entry.StartsWith("v1,", StringComparison.Ordinal))
                    continue;

                if (WebhookCrypto.Matches(expected, entry.AsSpan(3), WebhookSignatureEncoding.Base64))
                {
                    var (_, type) = WebhookCrypto.ReadIdAndType(body);
                    return WebhookVerificationResult.Success(id, timestamp, type);
                }
            }
        }

        return WebhookVerificationResult.Fail("no v1 signature matches any configured secret");
    }
}

/// <summary>
/// Verifies Slack's request signing: <c>X-Slack-Signature: v0=&lt;hex&gt;</c>, an HMAC-SHA256 over
/// <c>v0:{X-Slack-Request-Timestamp}:{body}</c> with the app's signing secret.
/// <para>
/// Slack's bodies are usually form-encoded — slash commands, interactivity — which is exactly why
/// the verifier has to see the raw bytes: a form parsed and re-encoded is not the form Slack signed.
/// </para>
/// </summary>
public sealed class SlackWebhookVerifier : IWebhookVerifier
{
    public const string SignatureHeader = "X-Slack-Signature";
    public const string TimestampHeader = "X-Slack-Request-Timestamp";

    readonly byte[][] keys;
    readonly TimeSpan? tolerance;

    /// <param name="signingSecrets">The app's signing secrets (not the verification token, which Slack deprecated).</param>
    /// <param name="tolerance">How old a request may be. Five minutes, as Slack recommends; null disables the check.</param>
    public SlackWebhookVerifier(IEnumerable<string> signingSecrets, TimeSpan? tolerance)
    {
        this.keys = WebhookCrypto.Utf8Keys(signingSecrets, "Slack");
        this.tolerance = tolerance;
    }

    public string Name => "slack";

    public WebhookVerificationResult Verify(HeaderDictionary headers, ReadOnlySpan<byte> body, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(headers);

        var header = headers.GetFirst(SignatureHeader);
        if (String.IsNullOrWhiteSpace(header) || !header.StartsWith("v0=", StringComparison.Ordinal))
            return WebhookVerificationResult.Fail($"the {SignatureHeader} header is missing or is not a v0 signature");

        var rawTimestamp = headers.GetFirst(TimestampHeader);
        if (!WebhookCrypto.TryCheckTimestamp(rawTimestamp, now, this.tolerance, out var timestamp, out var failure))
            return WebhookVerificationResult.Fail(failure!);

        foreach (var key in this.keys)
        {
            var expected = WebhookCrypto.Compute(HashAlgorithmName.SHA256, key, $"v0:{rawTimestamp}:", body);

            if (WebhookCrypto.Matches(expected, header.AsSpan(3), WebhookSignatureEncoding.Hex))
                return WebhookVerificationResult.Success(timestamp: timestamp);
        }

        return WebhookVerificationResult.Fail("the signature does not match any configured secret");
    }
}

/// <summary>
/// The built-in webhook verifiers.
/// <code>
/// app.MapWebhook("/hooks/github", WebhookSignature.GitHub(secret), async ctx =>
/// {
///     var push = ctx.ReadFromJson(AppJson.Default.PushEvent);
///     ...
/// });
/// </code>
/// Every factory takes several secrets, for rotation: the delivery is accepted when any one of them
/// verifies it.
/// </summary>
public static class WebhookSignature
{
    /// <summary>The replay window used when none is given: five minutes, which Stripe, Slack and Standard Webhooks all recommend.</summary>
    public static readonly TimeSpan DefaultTolerance = TimeSpan.FromMinutes(5);

    /// <summary>
    /// GitHub: <c>X-Hub-Signature-256: sha256=&lt;hex&gt;</c> over the body. The delivery id is
    /// <c>X-GitHub-Delivery</c> (the same on a redelivery) and the event type <c>X-GitHub-Event</c>.
    /// <para>
    /// GitHub signs no timestamp, so there is no replay window — turn on
    /// <see cref="WebhookOptions.SuppressDuplicates"/> to refuse a delivery id seen before.
    /// </para>
    /// </summary>
    public static HmacWebhookVerifier GitHub(params string[] secrets)
    {
        var options = new HmacWebhookOptions
        {
            Name = "github",
            HeaderName = "X-Hub-Signature-256",
            Prefix = "sha256=",
            Algorithm = WebhookHmacAlgorithm.Sha256,
            Encoding = WebhookSignatureEncoding.Hex,
            DeliveryIdHeader = "X-GitHub-Delivery",
            EventTypeHeader = "X-GitHub-Event"
        };

        foreach (var secret in secrets ?? throw new ArgumentNullException(nameof(secrets)))
            options.Secrets.Add(secret);

        return new HmacWebhookVerifier(options);
    }

    /// <summary>Stripe's <c>Stripe-Signature</c>, with the default five-minute tolerance.</summary>
    public static StripeWebhookVerifier Stripe(params string[] secrets)
        => new(secrets, DefaultTolerance);

    /// <summary>Stripe's <c>Stripe-Signature</c>, with a chosen tolerance (null disables the timestamp check).</summary>
    public static StripeWebhookVerifier Stripe(TimeSpan? tolerance, params string[] secrets)
        => new(secrets, tolerance);

    /// <summary>Standard Webhooks (Svix, Clerk, Resend, …), with the default five-minute tolerance.</summary>
    public static StandardWebhookVerifier StandardWebhooks(params string[] secrets)
        => new(secrets, DefaultTolerance);

    /// <summary>Standard Webhooks with a chosen tolerance (null disables the timestamp check).</summary>
    public static StandardWebhookVerifier StandardWebhooks(TimeSpan? tolerance, params string[] secrets)
        => new(secrets, tolerance);

    /// <summary>Slack request signing, with the default five-minute tolerance.</summary>
    public static SlackWebhookVerifier Slack(params string[] signingSecrets)
        => new(signingSecrets, DefaultTolerance);

    /// <summary>Slack request signing with a chosen tolerance (null disables the timestamp check).</summary>
    public static SlackWebhookVerifier Slack(TimeSpan? tolerance, params string[] signingSecrets)
        => new(signingSecrets, tolerance);

    /// <summary>Any sender that puts an HMAC of the body in one header.</summary>
    public static HmacWebhookVerifier Hmac(Action<HmacWebhookOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var options = new HmacWebhookOptions();
        configure(options);

        return new HmacWebhookVerifier(options);
    }
}
