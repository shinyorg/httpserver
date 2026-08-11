using System.Buffers;
using System.Globalization;
using System.Net;
using System.Text;
using System.Threading.Channels;
using Microsoft.Azure.Relay;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shiny.Net.HttpServer.Transports;
using Shiny.Net.HttpServer.Tunneling;

namespace Shiny.Net.HttpServer.AzureRelay;

/// <summary>
/// Serves an embedded <see cref="HttpServer"/> through an Azure Relay hybrid connection.
/// <para>
/// The device dials out and never accepts an inbound connection, which is what makes this work from
/// behind carrier-grade NAT — the case a phone is always in. Azure holds the public endpoint and
/// forwards to whichever listener is currently attached.
/// </para>
/// <code>
/// var provider = new AzureRelayTunnelProvider(new AzureRelayOptions
/// {
///     ConnectionString = "Endpoint=sb://my-ns.servicebus.windows.net/;SharedAccessKeyName=listen;SharedAccessKey=...",
///     HybridConnectionName = "my-device"
/// });
///
/// await app.RunTunnelAsync(provider, cancellationToken: token);
/// </code>
/// </summary>
public sealed class AzureRelayTunnelProvider : ITunnelProvider
{
    readonly AzureRelayOptions options;
    readonly ILogger logger;
    readonly Channel<IConnection> accepted = Channel.CreateUnbounded<IConnection>(
        new UnboundedChannelOptions { SingleReader = true }
    );

    readonly CancellationTokenSource stopping = new();

    HybridConnectionListener? listener;
    Task? acceptLoop;
    long connectionCounter;
    int disposed;

    public AzureRelayTunnelProvider(AzureRelayOptions options, ILogger<AzureRelayTunnelProvider>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        this.options = options;
        this.logger = logger ?? NullLogger<AzureRelayTunnelProvider>.Instance;

        var (host, name) = options.Resolve();
        this.PublicUrl = options.Mode == AzureRelayMode.Http
            ? $"{options.PublicScheme}://{host}/{name}"
            : $"sb://{host}/{name}";

        this.HybridConnectionName = name;
    }

    public string Name => "azure-relay";

    /// <summary>
    /// In <see cref="AzureRelayMode.Http"/>, the address any HTTP client can reach the device at.
    /// In <see cref="AzureRelayMode.RelayedStream"/>, the <c>sb://</c> address callers pass to
    /// <c>HybridConnectionClient</c>.
    /// </summary>
    public string? PublicUrl { get; }

    public string ListenDescription => this.PublicUrl ?? "azure-relay (not yet open)";

    /// <summary>The hybrid connection name this listener is attached to.</summary>
    public string HybridConnectionName { get; }

    /// <summary>True while the relay reports the listener as connected.</summary>
    public bool IsOnline => this.listener?.IsOnline ?? false;

    /// <summary>Raised when the relay connection drops or comes back, for surfacing status in a UI.</summary>
    public event EventHandler<bool>? ConnectivityChanged;

    public async ValueTask BindAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed != 0, this);

        if (this.listener is not null)
            throw new InvalidOperationException("The relay listener is already open.");

        var hybridConnection = await this.options.CreateListenerAsync(cancellationToken).ConfigureAwait(false);

        hybridConnection.Connecting += (_, _) => this.OnConnectivity(false, "connecting");
        hybridConnection.Online += (_, _) => this.OnConnectivity(true, "online");
        hybridConnection.Offline += (_, _) => this.OnConnectivity(false, "offline");

        if (this.options.Mode == AzureRelayMode.Http)
        {
            // The SDK's handler is synchronous, so the work is started and the callback returns
            // immediately. Blocking here would stall the relay's dispatch loop for every request.
            hybridConnection.RequestHandler = context => _ = Task.Run(
                () => this.HandleRequestAsync(context),
                CancellationToken.None
            );
        }

        await hybridConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
        this.listener = hybridConnection;

        if (this.options.Mode == AzureRelayMode.RelayedStream)
            this.acceptLoop = Task.Run(() => this.AcceptLoopAsync(hybridConnection), CancellationToken.None);

        this.logger.LogInformation("Azure Relay listener open at {Url}", this.PublicUrl);
    }

    public async ValueTask<IConnection?> AcceptAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await this.accepted.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    public async ValueTask UnbindAsync(CancellationToken cancellationToken = default)
    {
        var hybridConnection = Interlocked.Exchange(ref this.listener, null);
        if (hybridConnection is null)
            return;

        await this.stopping.CancelAsync().ConfigureAwait(false);

        try
        {
            await hybridConnection.CloseAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsExpectedDisconnect(ex))
        {
        }

        if (this.acceptLoop is { } loop)
        {
            try
            {
                await loop.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
            }

            this.acceptLoop = null;
        }

        this.accepted.Writer.TryComplete();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) != 0)
            return;

        await this.UnbindAsync(CancellationToken.None).ConfigureAwait(false);
        this.stopping.Dispose();
    }

    // ---- Relayed stream mode ----

    async Task AcceptLoopAsync(HybridConnectionListener hybridConnection)
    {
        try
        {
            while (!this.stopping.IsCancellationRequested)
            {
                // Null is how the SDK signals the listener has closed.
                var stream = await hybridConnection.AcceptConnectionAsync().ConfigureAwait(false);
                if (stream is null)
                    break;

                var id = $"relay-{Interlocked.Increment(ref this.connectionCounter)}";
                this.accepted.Writer.TryWrite(new RelayedStreamConnection(id, stream, remoteEndPoint: null));
            }
        }
        catch (Exception ex) when (IsExpectedDisconnect(ex))
        {
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "The Azure Relay accept loop faulted");
        }
        finally
        {
            this.accepted.Writer.TryComplete();
        }
    }

    // ---- HTTP mode ----

    /// <summary>
    /// Bridges one relayed HTTP request through the server.
    /// <para>
    /// Azure Relay's HTTP mode hands over a parsed request and expects a status code, headers and a
    /// body stream back — not a byte pipe. Rather than give the server a second output shape, the
    /// request is written onto an in-memory connection as ordinary HTTP/1.1, served by the whole
    /// existing pipeline, and the response read back off the wire. Every result type, every piece
    /// of middleware and every route works unchanged.
    /// </para>
    /// </summary>
    async Task HandleRequestAsync(RelayedHttpListenerContext context)
    {
        var id = $"relay-http-{Interlocked.Increment(ref this.connectionCounter)}";
        var connection = new DuplexPipeConnection(id, context.Request.RemoteEndPoint, isTunneled: true);

        try
        {
            // Queued before the request is written, so the server is already draining the pipe by
            // the time a large body arrives and cannot deadlock against the pipe's pause threshold.
            if (!this.accepted.Writer.TryWrite(connection))
            {
                await RespondWithErrorAsync(context, HttpStatusCode.ServiceUnavailable, "The tunnel is closing.")
                    .ConfigureAwait(false);

                return;
            }

            await this.WriteRequestAsync(context, connection).ConfigureAwait(false);

            var response = await Http1ResponseReader
                .ReadAsync(connection.TransportReader, this.options.MaxResponseHeadSize, this.stopping.Token)
                .ConfigureAwait(false);

            await CopyResponseAsync(response, context).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsExpectedDisconnect(ex))
        {
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "Failed to relay a request for {Url}", context.Request.Url);

            try
            {
                await RespondWithErrorAsync(context, HttpStatusCode.BadGateway, "The tunnelled server failed.")
                    .ConfigureAwait(false);
            }
            catch
            {
                // The caller has already gone; nothing left to tell.
            }
        }
    }

    Task WriteRequestAsync(RelayedHttpListenerContext context, DuplexPipeConnection connection)
    {
        var request = context.Request;
        var headers = new List<KeyValuePair<string, string>>();

        foreach (var name in request.Headers.AllKeys)
        {
            if (name is not null)
                headers.Add(new KeyValuePair<string, string>(name, request.Headers[name] ?? string.Empty));
        }

        return this.WriteRequestAsync(
            request.HttpMethod,
            request.Url,
            headers,
            request.HasEntityBody ? request.InputStream : null,
            connection.TransportWriter,
            this.stopping.Token
        );
    }

    /// <summary>
    /// Writes one relayed request onto a connection as ordinary HTTP/1.1.
    /// <para>
    /// Split from the relay context so it can be exercised against a real server without a live
    /// namespace — this half is where the framing decisions live, and framing is what breaks.
    /// </para>
    /// </summary>
    internal async Task WriteRequestAsync(
        string method,
        Uri url,
        IReadOnlyList<KeyValuePair<string, string>> requestHeaders,
        Stream? body,
        System.IO.Pipelines.PipeWriter writer,
        CancellationToken cancellationToken
    )
    {
        var head = new StringBuilder(256);
        head.Append(method).Append(' ').Append(this.BuildTarget(url)).Append(" HTTP/1.1\r\n");

        string? contentLength = null;
        var hasHost = false;

        foreach (var (name, value) in requestHeaders)
        {
            // The relay frames the request for us; passing its framing headers through would give
            // the server two contradictory descriptions of the same body.
            if (name.Equals(HeaderNames.TransferEncoding, StringComparison.OrdinalIgnoreCase) ||
                name.Equals(HeaderNames.Connection, StringComparison.OrdinalIgnoreCase) ||
                name.Equals(HeaderNames.KeepAlive, StringComparison.OrdinalIgnoreCase) ||
                name.Equals(HeaderNames.Upgrade, StringComparison.OrdinalIgnoreCase))
                continue;

            if (name.Equals(HeaderNames.ContentLength, StringComparison.OrdinalIgnoreCase))
            {
                contentLength = value;
                continue;
            }

            hasHost |= name.Equals(HeaderNames.Host, StringComparison.OrdinalIgnoreCase);
            head.Append(name).Append(": ").Append(value).Append("\r\n");
        }

        if (!hasHost)
            head.Append(HeaderNames.Host).Append(": ").Append(url.Host).Append("\r\n");

        // The body has to be framed one way or the other. A declared length is passed through;
        // otherwise it is chunked, because its size is not known up front.
        var declared = contentLength is not null
            && long.TryParse(contentLength, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            && parsed > 0;

        if (declared)
            head.Append(HeaderNames.ContentLength).Append(": ").Append(contentLength).Append("\r\n");
        else if (body is not null)
            head.Append(HeaderNames.TransferEncoding).Append(": chunked\r\n");

        head.Append("\r\n");

        Write(writer, head.ToString());
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);

        if (body is not null)
            await CopyRequestBodyAsync(body, writer, chunked: !declared, cancellationToken).ConfigureAwait(false);

        // No more request bytes. The server sees a clean end of stream, answers, and closes —
        // which is exactly the one-request-per-context shape the relay's HTTP mode has.
        await writer.CompleteAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// The request target to give the server.
    /// <para>
    /// Azure addresses a device as <c>https://{namespace}/{name}/{path}</c>. Left alone, every route
    /// would have to include the hybrid connection name, so by default it is stripped and the same
    /// routes serve relayed and local traffic.
    /// </para>
    /// </summary>
    internal string BuildTarget(Uri url)
    {
        var path = url.AbsolutePath;

        if (this.options.StripHybridConnectionNameFromPath)
        {
            var prefix = "/" + this.HybridConnectionName;

            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                path = path[prefix.Length..];
        }

        if (path.Length == 0)
            path = "/";

        return path + url.Query;
    }

    static async Task CopyRequestBodyAsync(
        Stream body,
        System.IO.Pipelines.PipeWriter writer,
        bool chunked,
        CancellationToken cancellationToken
    )
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            int read;
            while ((read = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (chunked)
                    Write(writer, read.ToString("x", CultureInfo.InvariantCulture) + "\r\n");

                writer.Write(buffer.AsSpan(0, read));

                if (chunked)
                    Write(writer, "\r\n");

                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (chunked)
            {
                Write(writer, "0\r\n\r\n");
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    static async Task CopyResponseAsync(Http1Response response, RelayedHttpListenerContext context)
    {
        context.Response.StatusCode = (HttpStatusCode)response.StatusCode;
        context.Response.StatusDescription = response.ReasonPhrase;

        foreach (var (name, value) in response.Headers)
        {
            // Hop-by-hop headers describe the leg that just ended, and the framing headers are the
            // relay's to decide — it re-frames the response for the caller.
            if (name.Equals(HeaderNames.Connection, StringComparison.OrdinalIgnoreCase) ||
                name.Equals(HeaderNames.TransferEncoding, StringComparison.OrdinalIgnoreCase) ||
                name.Equals(HeaderNames.ContentLength, StringComparison.OrdinalIgnoreCase) ||
                name.Equals(HeaderNames.KeepAlive, StringComparison.OrdinalIgnoreCase) ||
                name.Equals(HeaderNames.Upgrade, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                context.Response.Headers.Add(name, value);
            }
            catch (ArgumentException)
            {
                // WebHeaderCollection reserves a handful of names. Losing one is better than
                // failing the whole response over it.
            }
        }

        if (response.Body is { Length: > 0 } body)
            await context.Response.OutputStream.WriteAsync(body).ConfigureAwait(false);

        await context.Response.CloseAsync().ConfigureAwait(false);
    }

    static async Task RespondWithErrorAsync(RelayedHttpListenerContext context, HttpStatusCode status, string message)
    {
        context.Response.StatusCode = status;
        context.Response.StatusDescription = message;

        await context.Response.CloseAsync().ConfigureAwait(false);
    }

    static void Write(System.IO.Pipelines.PipeWriter writer, string text)
    {
        var byteCount = Encoding.Latin1.GetByteCount(text);
        var span = writer.GetSpan(byteCount);

        Encoding.Latin1.GetBytes(text, span);
        writer.Advance(byteCount);
    }

    void OnConnectivity(bool online, string reason)
    {
        this.logger.LogInformation("Azure Relay listener is {Reason}", reason);
        this.ConnectivityChanged?.Invoke(this, online);
    }

    static bool IsExpectedDisconnect(Exception ex) => ex
        is OperationCanceledException
        or ObjectDisposedException
        or IOException
        or RelayException
        or System.Net.Sockets.SocketException;
}
