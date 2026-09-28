namespace Shiny.Net.HttpServer;

/// <summary>
/// Makes a generated endpoint honour <c>Idempotency-Key</c>, so a retried POST gets the stored
/// original response instead of running twice.
/// <code>
/// [Route("/api/payments")]
/// [Idempotent]
/// public class PaymentEndpoints
/// {
///     [Post("/")] public Task&lt;IActionResult&gt; Charge([FromBody] ChargeRequest request) => ...;
///     [Post("/quote")] [DisableIdempotency] public QuoteResponse Quote([FromBody] QuoteRequest request) => ...;
/// }
/// </code>
/// <para>
/// Needs <c>builder.AddIdempotency()</c> and <c>app.UseIdempotency()</c>. On a method it replaces the
/// class's settings rather than adding to them.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class IdempotentAttribute : Attribute
{
    /// <summary>
    /// Whether a request without a key is refused with a 400. On by default — an endpoint that is
    /// worth protecting is usually one where an unprotected call is the bug.
    /// </summary>
    public bool Required { get; set; } = true;

    /// <summary>How long the response is kept for replay, overriding the server-wide expiration. Zero keeps the default.</summary>
    public int ExpirationSeconds { get; set; }
}

/// <summary>Exempts a generated endpoint from idempotency, including from a class-level <c>[Idempotent]</c>.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class DisableIdempotencyAttribute : Attribute;
