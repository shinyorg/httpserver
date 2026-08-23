using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using Shiny.Net.HttpServer.Transports;

namespace Shiny.Net.HttpServer.Testing;

/// <summary>
/// A <see cref="Stream"/> over the transport side of a <see cref="DuplexPipeConnection"/>, so an
/// HTTP client can be handed one end of a connection whose other end is the server.
/// </summary>
sealed class PipeStream(PipeReader reader, PipeWriter writer, IAsyncDisposable connection) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var source = result.Buffer;

            if (!source.IsEmpty)
            {
                var take = (int)Math.Min(source.Length, buffer.Length);
                source.Slice(0, take).CopyTo(buffer.Span);
                reader.AdvanceTo(source.GetPosition(take));

                return take;
            }

            reader.AdvanceTo(source.Start, source.End);

            // Zero means end of stream to a client. Only true once the writer has completed —
            // an empty read result on its own just means "nothing yet".
            if (result.IsCompleted)
                return 0;

            if (result.IsCanceled)
                throw new OperationCanceledException(cancellationToken);
        }
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => await this.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);

    public override int Read(byte[] buffer, int offset, int count)
        => this.ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        writer.Write(buffer.Span);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => this.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count)
        => this.WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!disposing)
            return;

        // Completing the client's writer is what a socket close looks like to the server: the
        // request side ends, and the connection loop unwinds instead of waiting forever.
        writer.Complete();
        reader.Complete();

        _ = connection.DisposeAsync().AsTask();
    }
}

/// <summary>
/// Connects an <see cref="HttpClient"/> to a <see cref="HttpServer"/> through memory.
/// <para>
/// The point is not speed, though it is faster. It is that a test can exercise an endpoint with no
/// port to allocate, no listener to bind, no firewall prompt and nothing to leak when a test fails
/// half way through — while still going through the real request parser, the real router, the real
/// middleware and the real response framing. The only thing replaced is the socket.
/// </para>
/// <para>
/// The seam is the same one tunnelling uses: the server has never known what its bytes arrive on.
/// </para>
/// </summary>
public sealed class InMemoryConnectionHandler : DelegatingHandler
{
    readonly HttpServer server;

    /// <summary>Wires a handler to a server. The server does not need to be started.</summary>
    public InMemoryConnectionHandler(HttpServer server, bool useHttp2 = false)
    {
        ArgumentNullException.ThrowIfNull(server);

        this.server = server;

        this.InnerHandler = new SocketsHttpHandler
        {
            // Everything about how the connection is made is replaced; everything about how HTTP is
            // spoken over it is not, which is what keeps this faithful.
            ConnectCallback = this.ConnectAsync,
            AllowAutoRedirect = false,
            UseCookies = false,
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30)
        };

        this.UseHttp2 = useHttp2;
    }

    /// <summary>
    /// Speaks HTTP/2 over the in-memory connection, by prior knowledge rather than ALPN.
    /// <para>
    /// The server has to allow cleartext HTTP/2 for this — <c>options.Http2.AllowCleartext</c> —
    /// because there is no TLS here to negotiate with, which is also true of a tunnelled
    /// connection.
    /// </para>
    /// </summary>
    public bool UseHttp2 { get; }

    async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var connection = new DuplexPipeConnection(
            "memory-" + Guid.NewGuid().ToString("n")[..8],
            new IPEndPoint(IPAddress.Loopback, 0),
            new IPEndPoint(IPAddress.Loopback, 0),
            isTunneled: false
        )
        {
            ApplicationProtocol = this.UseHttp2 ? "h2" : null
        };

        // Served on its own task: ServeAsync runs for the life of the connection, and the client is
        // about to start writing into the other end of it.
        _ = Task.Run(() => this.server.ServeAsync(connection, CancellationToken.None), CancellationToken.None);

        return await ValueTask.FromResult<Stream>(
            new PipeStream(connection.TransportReader, connection.TransportWriter, connection)
        ).ConfigureAwait(false);
    }
}

/// <summary>Building an in-memory client for a server.</summary>
public static class InMemoryClientExtensions
{
    /// <summary>
    /// An <see cref="HttpClient"/> that talks to this server through memory.
    /// <code>
    /// var app = HttpServer.CreateBuilder().Build();
    /// app.MapGet("/ping", ctx => ctx.Response.WriteTextAsync("pong"));
    ///
    /// using var client = app.CreateInMemoryClient();
    /// Assert.Equal("pong", await client.GetStringAsync("/ping"));
    /// </code>
    /// <para>
    /// The server does not have to be started — nothing is bound, so there is nothing to start.
    /// The base address is cosmetic: it decides the <c>Host</c> header and lets relative URLs work.
    /// </para>
    /// </summary>
    public static HttpClient CreateInMemoryClient(
        this HttpServer server,
        Uri? baseAddress = null,
        bool useHttp2 = false
    )
    {
        ArgumentNullException.ThrowIfNull(server);

        var client = new HttpClient(new InMemoryConnectionHandler(server, useHttp2))
        {
            BaseAddress = baseAddress ?? new Uri("http://localhost/"),
            Timeout = TimeSpan.FromSeconds(30)
        };

        if (useHttp2)
        {
            client.DefaultRequestVersion = HttpVersion.Version20;
            client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        }

        return client;
    }

    /// <summary>The handler on its own, for a client the test wants to configure itself.</summary>
    public static HttpMessageHandler CreateInMemoryHandler(this HttpServer server, bool useHttp2 = false)
        => new InMemoryConnectionHandler(server, useHttp2);
}
