using System.IO.Pipelines;

namespace Shiny.Net.HttpServer;

/// <summary>
/// The seam between <see cref="HttpResponse"/> and whatever is actually framing bytes onto the
/// wire. Implemented per protocol version, which is what lets HTTP/2 slot in later without
/// touching the public response surface.
/// </summary>
interface IResponseBodyControl
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
