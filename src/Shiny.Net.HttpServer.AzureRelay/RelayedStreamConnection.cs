using System.IO.Pipelines;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Azure.Relay;
using Shiny.Net.HttpServer.Transports;

namespace Shiny.Net.HttpServer.AzureRelay;

/// <summary>
/// An <see cref="IConnection"/> over a relayed byte stream.
/// <para>
/// Azure Relay hands back a plain duplex <see cref="Stream"/>, which is exactly the shape the HTTP
/// core already consumes — so this adapter is the whole of the integration. Keep-alive, chunked
/// bodies and WebSocket upgrades all work, because nothing in between is interpreting the bytes.
/// </para>
/// </summary>
sealed class RelayedStreamConnection : IConnection
{
    readonly HybridConnectionStream stream;
    int disposed;

    public RelayedStreamConnection(string connectionId, HybridConnectionStream stream, EndPoint? remoteEndPoint)
    {
        this.ConnectionId = connectionId;
        this.stream = stream;
        this.RemoteEndPoint = remoteEndPoint;

        this.Input = PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true));
        this.Output = PipeWriter.Create(stream, new StreamPipeWriterOptions(leaveOpen: true));
    }

    public string ConnectionId { get; }

    public PipeReader Input { get; }

    public PipeWriter Output { get; }

    public EndPoint? RemoteEndPoint { get; }

    public EndPoint? LocalEndPoint => null;

    /// <summary>
    /// True: the relay leg is TLS to Azure. It says nothing about the caller's own leg, which is
    /// why <c>UseForwardedHeaders</c> stays opt-in.
    /// </summary>
    public bool IsEncrypted => true;

    public X509Certificate2? ClientCertificate => null;

    public bool IsTunneled => true;

    /// <summary>Null: the relay negotiates no ALPN on the device's behalf, so this stays HTTP/1.1.</summary>
    public string? ApplicationProtocol => null;

    public void Abort()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) != 0)
            return;

        try
        {
            this.stream.Shutdown();
        }
        catch
        {
            // The relay has already dropped it; there is nothing to tear down.
        }

        this.stream.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) != 0)
            return;

        try
        {
            await this.Input.CompleteAsync().ConfigureAwait(false);
            await this.Output.CompleteAsync().ConfigureAwait(false);

            // A graceful half-close, so the caller sees the end of the response rather than a reset.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await this.stream.ShutdownAsync(timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            // Shutting down a stream the relay has already closed is expected to fail.
        }

        await this.stream.DisposeAsync().ConfigureAwait(false);
    }
}
