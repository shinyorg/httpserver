using System.IO.Pipelines;
using System.Net;
using System.Security.Cryptography.X509Certificates;

namespace Shiny.Net.HttpServer.Transports;

/// <summary>
/// An <see cref="IConnection"/> backed by a pair of in-memory pipes rather than a socket.
/// <para>
/// This is the seam the whole tunnel design rests on. The HTTP core only ever sees an
/// <see cref="IConnection"/>, so bytes arriving over a multiplexed tunnel stream — or handed over
/// by a test — are indistinguishable from bytes off a TCP socket. Nothing above the transport
/// needs to know the difference.
/// </para>
/// <para>
/// The application side is <see cref="Input"/> and <see cref="Output"/>. Whoever owns the transport
/// writes inbound bytes to <see cref="TransportWriter"/> and reads outbound bytes from
/// <see cref="TransportReader"/>.
/// </para>
/// </summary>
public sealed class DuplexPipeConnection : IConnection
{
    readonly Pipe inbound;
    readonly Pipe outbound;
    readonly CancellationTokenSource aborted = new();

    // Captured up front: a CancellationToken stays queryable after its source is disposed, but the
    // source's Token property does not. Callers ask "was this aborted?" precisely when it has been.
    readonly CancellationToken abortedToken;

    int disposed;

    public DuplexPipeConnection(
        string connectionId,
        EndPoint? remoteEndPoint = null,
        EndPoint? localEndPoint = null,
        bool isTunneled = true,
        bool isEncrypted = false,
        PipeOptions? pipeOptions = null
    )
    {
        var options = pipeOptions ?? new PipeOptions(useSynchronizationContext: false);

        this.inbound = new Pipe(options);
        this.outbound = new Pipe(options);
        this.abortedToken = this.aborted.Token;

        this.ConnectionId = connectionId;
        this.RemoteEndPoint = remoteEndPoint;
        this.LocalEndPoint = localEndPoint;
        this.IsTunneled = isTunneled;
        this.IsEncrypted = isEncrypted;
    }

    public string ConnectionId { get; }

    public PipeReader Input => this.inbound.Reader;

    public PipeWriter Output => this.outbound.Writer;

    /// <summary>Where the transport pushes bytes that arrived for this connection.</summary>
    public PipeWriter TransportWriter => this.inbound.Writer;

    /// <summary>Where the transport picks up bytes the application has written.</summary>
    public PipeReader TransportReader => this.outbound.Reader;

    public EndPoint? RemoteEndPoint { get; }

    public EndPoint? LocalEndPoint { get; }

    public bool IsEncrypted { get; }

    public X509Certificate2? ClientCertificate => null;

    public bool IsTunneled { get; }

    /// <summary>
    /// Set by whoever created the connection. A tunnel that negotiated h2 with the public client
    /// passes it through here, since there is no TLS handshake on this side to read it from.
    /// </summary>
    public string? ApplicationProtocol { get; init; }

    /// <summary>Fires when the connection is aborted, so a transport pump can stop promptly.</summary>
    public CancellationToken Aborted => this.abortedToken;

    public void Abort()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) != 0)
            return;

        this.Cancel();

        // An abort tears everything down, including whatever the transport was mid-read on. That
        // is the difference from Dispose: nobody is going to finish reading this.
        this.inbound.Writer.Complete(AbortReason);
        this.inbound.Reader.Complete(AbortReason);
        this.outbound.Writer.Complete(AbortReason);
        this.outbound.Reader.Complete(AbortReason);

        this.aborted.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) != 0)
            return ValueTask.CompletedTask;

        this.Cancel();

        // Only the application's own ends are completed. Completing the transport's ends here
        // would break a transport still draining the last of the response — it would see its
        // reader vanish mid-flush rather than a clean end of stream. Dispose means "the
        // application is finished", not "stop reading immediately"; Abort means the latter.
        this.inbound.Reader.Complete();
        this.outbound.Writer.Complete();

        this.aborted.Dispose();
        return ValueTask.CompletedTask;
    }

    static readonly OperationCanceledException AbortReason = new("The connection was aborted.");

    void Cancel()
    {
        try
        {
            this.aborted.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

}
