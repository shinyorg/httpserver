using System.IO.Pipelines;

namespace Shiny.Net.HttpServer;

/// <summary>
/// The seam between <see cref="HttpResponse"/> and whatever is actually framing bytes onto the
/// wire. Implemented per protocol version, which is what lets HTTP/2 slot in later without
/// touching the public response surface.
/// <para>
/// Public so a middleware can sit in the middle of it: take the control the response is currently
/// bound to, wrap it, and <see cref="HttpResponse.Bind"/> the wrapper. That is how response
/// compression inserts itself without every writer knowing about it, and it is the same seam a
/// recorder or a logger needs to see the bytes of a body it did not write. Wrappers must forward
/// <see cref="StartAsync"/> and <see cref="CompleteAsync"/> to the control they wrapped, and must
/// flush anything they buffered before the connection completes.
/// </para>
/// </summary>
public interface IResponseBodyControl
{
    bool HasStarted { get; }
    Stream Stream { get; }
    PipeWriter Writer { get; }
    ValueTask StartAsync(CancellationToken cancellationToken);
    ValueTask CompleteAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Stand-in used while a response object is detached from any connection (pooled, or under test).
/// Fails loudly rather than silently swallowing writes.
/// </summary>
sealed class NullResponseBodyControl : IResponseBodyControl
{
    public static readonly NullResponseBodyControl Instance = new();

    public bool HasStarted => false;
    public Stream Stream => throw NotBound();
    public PipeWriter Writer => throw NotBound();
    public ValueTask StartAsync(CancellationToken cancellationToken) => throw NotBound();
    public ValueTask CompleteAsync(CancellationToken cancellationToken) => default;

    static InvalidOperationException NotBound() => new(
        "This response is not attached to a connection, so it cannot be written to."
    );
}

/// <summary>
/// Implemented by the per-protocol response controls that can put an interim (1xx) response on the
/// wire ahead of the final one.
/// <para>
/// Deliberately separate from <see cref="IResponseBodyControl"/>, and captured by
/// <see cref="HttpResponse.Bind"/> only when the bound control implements it. A middleware that wraps
/// the body — compression, a recorder — rebinds the response to its wrapper, and an informational
/// response has nothing to do with the body: it must still reach the connection underneath, not get
/// lost because the wrapper never heard of it. Internal, so the public seam does not grow a member
/// every existing wrapper would have to forward.
/// </para>
/// </summary>
interface IInformationalResponseWriter
{
    /// <summary>
    /// Frames and flushes one informational response. Returns false when the protocol in use has no
    /// way to carry it (HTTP/1.0), which the caller treats as a silent no-op.
    /// </summary>
    ValueTask<bool> WriteInformationalAsync(int statusCode, HeaderDictionary headers, CancellationToken cancellationToken);
}
