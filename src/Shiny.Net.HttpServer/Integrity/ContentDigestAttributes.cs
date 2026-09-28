namespace Shiny.Net.HttpServer;

/// <summary>
/// RFC 9530 digests for a generated endpoint class or one of its methods.
/// <code>
/// [Route("/api/sync")]
/// public class SyncEndpoints
/// {
///     [Post("/batch")] [ContentDigest(RequireRequestDigest = true)] public Task&lt;IActionResult&gt; Batch(...) => ...;
///     [Get("/snapshot")] [ContentDigest] public Snapshot Get() => ...;
/// }
/// </code>
/// <para>
/// Needs <c>app.UseContentDigest()</c>. On a method it replaces the class's settings rather than
/// adding to them.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class ContentDigestAttribute : Attribute
{
    /// <summary>Refuses requests with a body but no verifiable <c>Content-Digest</c>. Off by default.</summary>
    public bool RequireRequestDigest { get; set; }

    /// <summary>Gives every response a <c>Content-Digest</c>, asked for or not. On by default.</summary>
    public bool AlwaysEmitResponseDigest { get; set; } = true;
}

/// <summary>Exempts a generated endpoint from digests, including from a class-level <c>[ContentDigest]</c>.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class DisableContentDigestAttribute : Attribute;
