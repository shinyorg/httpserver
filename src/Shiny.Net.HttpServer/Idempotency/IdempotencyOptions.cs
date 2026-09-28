namespace Shiny.Net.HttpServer.Idempotency;

/// <summary>
/// How the server honours <c>Idempotency-Key</c> (draft-ietf-httpapi-idempotency-key-header).
/// <para>
/// The problem this solves is a phone on a train. The client sends a POST, the request reaches the
/// server and charges the card, and then the tunnel drops before the response gets back. The client
/// cannot tell "never arrived" from "arrived and the reply was lost", so it retries — and without a
/// key, the retry charges again. With one, the retry is answered with the stored original.
/// </para>
/// </summary>
public sealed class IdempotencyOptions
{
    /// <summary>The request header the key travels in. The draft's name by default.</summary>
    public string HeaderName { get; set; } = IdempotencyHeaders.IdempotencyKey;

    /// <summary>
    /// The header added to a replayed response, so a client (and whoever is debugging it) can tell a
    /// replay from a fresh answer. <c>Idempotent-Replayed: true</c>, the name Stripe uses and most
    /// client libraries already look for. Null adds nothing.
    /// </summary>
    public string? ReplayedHeaderName { get; set; } = IdempotencyHeaders.IdempotentReplayed;

    /// <summary>
    /// How long a completed response is kept for replay. A day by default: long enough to outlast any
    /// sane retry policy, short enough that the store does not become an archive.
    /// </summary>
    public TimeSpan Expiration { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// How long a key stays locked while its first request is still running. Past this, the claim is
    /// treated as abandoned — the process that held it most likely died — and a retry may run.
    /// Only matters to a store shared between processes; in one process the claim is always released
    /// when the request ends, however it ends.
    /// </summary>
    public TimeSpan InFlightTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The methods a key is honoured on. POST and PATCH by default — the ones that are not idempotent
    /// on their own. GET, PUT and DELETE already are, so a key on them is accepted and ignored.
    /// </summary>
    public ISet<string> Methods { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        HttpMethods.Post,
        HttpMethods.Patch
    };

    /// <summary>
    /// Whether a response with this status is stored for replay. Everything below 500 by default.
    /// <para>
    /// A 5xx is the server saying it failed, which is exactly the case a retry should get to try
    /// again rather than be handed the failure forever. A 4xx is a decision about the request, and
    /// replaying it is what keeps a retry from getting a different answer to the same question.
    /// </para>
    /// </summary>
    public Func<int, bool> ShouldStoreStatusCode { get; set; } = static status => status < 500;

    /// <summary>
    /// The largest request body that is fingerprinted. The body has to be read in full to hash it,
    /// and then held so the handler can read it too, so this is also what the feature costs in
    /// memory per request. A larger body is refused with a 413.
    /// </summary>
    public int MaxRequestBodyBytes { get; set; } = 1024 * 1024;

    /// <summary>
    /// The largest response body that is stored. A larger one is served, but cannot be replayed —
    /// the key is released, and a retry runs the handler again. Keep idempotent endpoints to small
    /// confirmations; a download does not need a key.
    /// </summary>
    public int MaxResponseBodyBytes { get; set; } = 256 * 1024;

    /// <summary>The longest key accepted. The draft does not set one; a UUID is 36 characters.</summary>
    public int MaxKeyLength { get; set; } = 255;
}

/// <summary>
/// What an endpoint asked for, attached as metadata by <c>RequireIdempotencyKey</c>,
/// <c>WithIdempotency</c> or <c>[Idempotent]</c>.
/// </summary>
public sealed class IdempotencyMetadata
{
    /// <summary>
    /// True when a request without a key is refused with a 400. False honours a key when one is sent
    /// and lets a request without one through untouched.
    /// </summary>
    public bool Required { get; set; }

    /// <summary>Overrides <see cref="IdempotencyOptions.Expiration"/> for this endpoint.</summary>
    public TimeSpan? Expiration { get; set; }

    /// <summary>True when the endpoint opted out, including out of a group's convention.</summary>
    public bool Disabled { get; set; }
}

/// <summary>The header names the feature reads and writes.</summary>
public static class IdempotencyHeaders
{
    /// <summary>The request header carrying the key.</summary>
    public const string IdempotencyKey = "Idempotency-Key";

    /// <summary>The response header marking a replay.</summary>
    public const string IdempotentReplayed = "Idempotent-Replayed";
}
