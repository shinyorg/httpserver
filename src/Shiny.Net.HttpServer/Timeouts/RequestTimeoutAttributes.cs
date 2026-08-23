namespace Shiny.Net.HttpServer;

/// <summary>
/// Bounds how long a generated endpoint may take.
/// <code>
/// [Route("/api/reports")]
/// public class ReportEndpoints
/// {
///     [Get("/{id:int}")] [RequestTimeout(2_000)] public Task&lt;IActionResult&gt; Get(int id, CancellationToken ct) => ...;
///     [Get("/stream")] [DisableRequestTimeout] public Task Stream(HttpContext ctx) => ...;
/// }
/// </code>
/// <para>
/// The timeout is delivered as cancellation on the handler's <c>CancellationToken</c>, so a handler
/// that ignores its token still runs to completion — it just does so after the client has been
/// answered. Pass the token into the slow thing.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class RequestTimeoutAttribute : Attribute
{
    /// <param name="milliseconds">How long the endpoint gets.</param>
    public RequestTimeoutAttribute(int milliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(milliseconds);
        this.Milliseconds = milliseconds;
    }

    /// <param name="policy">Name of a policy registered with <c>AddRequestTimeouts</c>.</param>
    public RequestTimeoutAttribute(string policy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policy);
        this.Policy = policy;
    }

    public int? Milliseconds { get; }

    public string? Policy { get; }
}

/// <summary>
/// Exempts an endpoint from request timeouts, the default policy included — a download, an event
/// stream, a WebSocket upgrade, anything whose whole job is to stay open.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class DisableRequestTimeoutAttribute : Attribute;
