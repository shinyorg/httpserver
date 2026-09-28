namespace Shiny.Net.HttpServer.Integrity;

/// <summary>When a response gets a <c>Content-Digest</c>.</summary>
public enum DigestEmission
{
    /// <summary>Never, even when the client asked with <c>Want-Content-Digest</c>.</summary>
    Never,

    /// <summary>Only when the client asked with <c>Want-Content-Digest</c> or <c>Want-Repr-Digest</c>. The default.</summary>
    WhenRequested,

    /// <summary>Always — the client asked or not.</summary>
    Always
}

/// <summary>
/// How the server checks and produces RFC 9530 integrity fields.
/// <para>
/// TLS already protects bytes in transit, so what is this for? The hops TLS does not cover. An
/// embedded server is usually reached through something — a relay, a tunnel agent, a reverse proxy —
/// and each of those terminates TLS and re-frames the message. A digest is end to end: it is computed
/// by the sender and checked by the recipient, whatever sat in the middle. On a device it also catches
/// the upload that a flaky connection truncated but a buggy client never noticed.
/// </para>
/// </summary>
public sealed class ContentDigestOptions
{
    /// <summary>
    /// Whether a request carrying <c>Content-Digest</c> is checked against its body. On by default:
    /// a client that went to the trouble of sending one expects it to mean something.
    /// </summary>
    public bool VerifyRequests { get; set; } = true;

    /// <summary>
    /// Refuses, with a 400, any request that has a body but no <c>Content-Digest</c>. Off by default;
    /// usually set per endpoint with <c>RequireContentDigest()</c> instead.
    /// </summary>
    public bool RequireRequestDigest { get; set; }

    /// <summary>
    /// What to do with a <c>Content-Digest</c> that names only algorithms this server cannot compute.
    /// RFC 9530 lets a recipient ignore those; this refuses them with a 400 and a
    /// <c>Want-Content-Digest</c> saying what it can check, because a digest the client thinks was
    /// verified and was not is worse than no digest. Set false to accept them unverified.
    /// </summary>
    public bool RejectUnsupportedAlgorithms { get; set; } = true;

    /// <summary>When responses carry a <c>Content-Digest</c>. <see cref="DigestEmission.WhenRequested"/> by default.</summary>
    public DigestEmission ResponseDigest { get; set; } = DigestEmission.WhenRequested;

    /// <summary>
    /// The algorithm used when the client did not state a preference — <c>sha-256</c> by default.
    /// Either <see cref="DigestFields.Sha256"/> or <see cref="DigestFields.Sha512"/>.
    /// </summary>
    public string DefaultAlgorithm { get; set; } = DigestFields.Sha256;

    /// <summary>
    /// Responses up to this size are held until complete, so their digest goes out as an ordinary
    /// header. Past it — or as soon as a handler flushes its headers itself — the body streams and the
    /// digest follows as a <b>trailer</b>, computed as the bytes go past.
    /// <para>
    /// On HTTP/1.1 a trailer needs a chunked response, so a streamed response that declared its
    /// <c>Content-Length</c> goes out without a digest. HTTP/2 and HTTP/3 carry trailers on anything.
    /// </para>
    /// <para>
    /// The same limit applies to the response to a request whose <c>Content-Digest</c> is being
    /// checked, digest or not: holding it is what lets a failed check replace an answer the handler
    /// wrote after catching the failure. A handler that writes more than this before the body has been
    /// read to the end has committed to its answer.
    /// </para>
    /// </summary>
    public int MaxBufferedResponseBytes { get; set; } = 64 * 1024;
}

/// <summary>
/// What an endpoint asked for, attached as metadata by <c>RequireContentDigest</c>,
/// <c>WithContentDigest</c>, <c>DisableContentDigest</c> or <c>[ContentDigest]</c>. Null properties
/// fall back to <see cref="ContentDigestOptions"/>.
/// </summary>
public sealed class ContentDigestMetadata
{
    /// <summary>Overrides <see cref="ContentDigestOptions.RequireRequestDigest"/>.</summary>
    public bool? RequireRequestDigest { get; set; }

    /// <summary>Overrides <see cref="ContentDigestOptions.ResponseDigest"/>.</summary>
    public DigestEmission? ResponseDigest { get; set; }

    /// <summary>True when the endpoint opted out of both directions.</summary>
    public bool Disabled { get; set; }
}
