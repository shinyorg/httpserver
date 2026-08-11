using System.IO.Pipelines;
using System.Net;
using System.Security.Cryptography.X509Certificates;

namespace Shiny.Net.HttpServer.Transports;

/// <summary>
/// A bidirectional byte stream carrying one or more HTTP exchanges. Deliberately knows nothing
/// about HTTP — that separation is what lets the tunnel client hand the server an in-memory
/// connection that is otherwise indistinguishable from a socket.
/// </summary>
public interface IConnection : IAsyncDisposable
{
    /// <summary>Stable identifier, surfaced as <see cref="ConnectionInfo.ConnectionId"/>.</summary>
    string ConnectionId { get; }

    PipeReader Input { get; }
    PipeWriter Output { get; }

    EndPoint? RemoteEndPoint { get; }
    EndPoint? LocalEndPoint { get; }

    /// <summary>True when this transport itself negotiated TLS.</summary>
    bool IsEncrypted { get; }

    /// <summary>Present only when TLS client certificates were requested and supplied.</summary>
    X509Certificate2? ClientCertificate { get; }

    /// <summary>True when the connection was delivered through a tunnel rather than a local socket.</summary>
    bool IsTunneled { get; }

    /// <summary>
    /// The protocol ALPN agreed, when TLS negotiated one. Null on cleartext, where the protocol is
    /// decided by what the client sends first rather than by negotiation.
    /// </summary>
    string? ApplicationProtocol { get; }

    /// <summary>
    /// Tears the connection down immediately, without draining. Used for protocol errors and for
    /// shutdown, where waiting on a peer that may never speak again is not an option.
    /// </summary>
    void Abort();
}

/// <summary>
/// Accepts inbound connections. One implementation per listening strategy: sockets today,
/// with the tunnel and any future transports plugging in behind the same contract.
/// </summary>
public interface IConnectionListener : IAsyncDisposable
{
    /// <summary>Human-readable description of what is being listened on, for logging.</summary>
    string ListenDescription { get; }

    /// <summary>Starts listening. Throws if the endpoint cannot be bound.</summary>
    ValueTask BindAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the next connection, or null once the listener has been unbound — which is the
    /// signal for the accept loop to exit.
    /// </summary>
    ValueTask<IConnection?> AcceptAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops accepting new connections. Existing connections are unaffected.</summary>
    ValueTask UnbindAsync(CancellationToken cancellationToken = default);
}
