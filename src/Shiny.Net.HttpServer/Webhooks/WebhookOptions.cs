namespace Shiny.Net.HttpServer.Webhooks;

/// <summary>
/// Named verifiers and the settings every webhook endpoint shares.
/// <code>
/// builder.AddWebhooks(o =>
/// {
///     o.AddVerifier("github", WebhookSignature.GitHub(config["GitHub:WebhookSecret"]!));
///     o.AddVerifier("stripe", WebhookSignature.Stripe(config["Stripe:WebhookSecret"]!));
///     o.SuppressDuplicates();
/// });
/// </code>
/// </summary>
public sealed class WebhookOptions
{
    /// <summary>The body limit when none is set: 5 MiB. GitHub caps its payloads at 25 MB; nearly all are far smaller.</summary>
    public const int DefaultMaxBodySize = 5 * 1024 * 1024;

    readonly Dictionary<string, IWebhookVerifier> verifiers = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, IWebhookVerifier> Verifiers => this.verifiers;

    /// <summary>
    /// The largest body buffered for verification. Larger deliveries are refused with a 413 before
    /// any of them is held in memory past the limit.
    /// <para>
    /// There has to be one: the signature can only be checked once the whole body is in hand, so
    /// until then the sender is unauthenticated and the body is whatever it says it is.
    /// </para>
    /// </summary>
    public int MaxBodySize { get; set; } = DefaultMaxBodySize;

    /// <summary>
    /// Where handled delivery ids are remembered. Null (the default) turns duplicate suppression
    /// off. <see cref="SuppressDuplicates"/> sets an in-memory one.
    /// </summary>
    public IWebhookDeliveryStore? DeliveryStore { get; set; }

    /// <summary>
    /// The status a duplicate delivery is acknowledged with. 200, so the sender records it as
    /// delivered and stops retrying — which is the point of recognising it.
    /// </summary>
    public int DuplicateStatusCode { get; set; } = StatusCodes.Status200OK;

    /// <summary>The clock timestamps are checked against. Replace it in tests.</summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    /// <summary>
    /// Called instead of the 401 problem response when a delivery fails verification. The result
    /// carries the reason; whatever this writes is the response.
    /// </summary>
    public Func<HttpContext, WebhookVerificationResult, ValueTask>? OnRejected { get; set; }

    /// <summary>Registers a verifier under a name, for <c>[RequireWebhookSignature("name")]</c> and <c>MapWebhook(pattern, "name", …)</c>.</summary>
    public WebhookOptions AddVerifier(string name, IWebhookVerifier verifier)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(verifier);

        this.verifiers[name] = verifier;
        return this;
    }

    /// <summary>
    /// Turns on duplicate suppression with a bounded in-memory store: a delivery whose id was
    /// already handled in the last <paramref name="retention"/> (24 hours by default) is answered
    /// with <see cref="DuplicateStatusCode"/> and the handler does not run.
    /// </summary>
    public WebhookOptions SuppressDuplicates(int capacity = 10_000, TimeSpan? retention = null)
    {
        this.DeliveryStore = new InMemoryWebhookDeliveryStore(capacity, retention, this.TimeProvider);
        return this;
    }

    /// <summary>
    /// Resolves a named verifier, throwing when it was never registered — a webhook endpoint with no
    /// way to verify must not quietly accept everything.
    /// </summary>
    public IWebhookVerifier GetVerifier(string name)
        => this.verifiers.TryGetValue(name, out var verifier)
            ? verifier
            : throw new InvalidOperationException(
                $"No webhook verifier named '{name}' is registered. " +
                $"Add it with builder.AddWebhooks(o => o.AddVerifier(\"{name}\", WebhookSignature...))."
            );

    /// <summary>A copy of the shared settings for one endpoint to adjust without affecting the rest.</summary>
    internal WebhookOptions CloneSettings()
    {
        var copy = new WebhookOptions
        {
            MaxBodySize = this.MaxBodySize,
            DeliveryStore = this.DeliveryStore,
            DuplicateStatusCode = this.DuplicateStatusCode,
            TimeProvider = this.TimeProvider,
            OnRejected = this.OnRejected
        };

        foreach (var (name, verifier) in this.verifiers)
            copy.verifiers[name] = verifier;

        return copy;
    }
}

/// <summary>
/// What an endpoint asks of webhook verification, attached to it as metadata and enforced by
/// <c>UseWebhookVerification()</c>.
/// </summary>
public sealed class WebhookMetadata
{
    /// <summary>A verifier registered with <see cref="WebhookOptions.AddVerifier"/>.</summary>
    public string? VerifierName { get; set; }

    /// <summary>A verifier given directly. Wins over <see cref="VerifierName"/>.</summary>
    public IWebhookVerifier? Verifier { get; set; }
}
