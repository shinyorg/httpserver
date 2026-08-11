using System.IO.Pipelines;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using Shiny.Net.HttpServer.Transports;

namespace Shiny.Net.HttpServer.Ssh;

/// <summary>
/// A connection that arrived through the SSH forward.
/// <para>
/// Everything is delegated to the loopback socket except what the socket cannot know: that this is
/// tunnelled traffic. <see cref="IConnection.RemoteEndPoint"/> is the loopback end of the forward,
/// not the caller — SSH does not carry the original address, so a caller's IP is only knowable if
/// the endpoint sets <c>X-Forwarded-For</c> and the server is configured to believe it.
/// </para>
/// </summary>
sealed class ForwardedConnection(IConnection inner) : IConnection, IConnectionInitializer
{
    /// <summary>
    /// Forwards initialization to the socket underneath.
    /// <para>
    /// Without this the wrapper would hide the inner connection's <see cref="IConnectionInitializer"/>
    /// from the server, which decides whether to initialize by testing for that interface — and a
    /// socket whose pipes were never opened throws the moment anything reads it.
    /// </para>
    /// </summary>
    public ValueTask InitializeAsync(CancellationToken cancellationToken) =>
        inner is IConnectionInitializer initializer
            ? initializer.InitializeAsync(cancellationToken)
            : default;

    public string ConnectionId => inner.ConnectionId;

    public PipeReader Input => inner.Input;

    public PipeWriter Output => inner.Output;

    public EndPoint? RemoteEndPoint => inner.RemoteEndPoint;

    public EndPoint? LocalEndPoint => inner.LocalEndPoint;

    /// <summary>
    /// False. The SSH hop is encrypted, but the leg between the caller and the SSH server is not
    /// this connection's to vouch for — that depends on whether a proxy terminates TLS out there.
    /// Overstating it would let a handler believe a plaintext request was secure.
    /// </summary>
    public bool IsEncrypted => false;

    public X509Certificate2? ClientCertificate => null;

    public bool IsTunneled => true;

    public string? ApplicationProtocol => null;

    public void Abort() => inner.Abort();

    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
