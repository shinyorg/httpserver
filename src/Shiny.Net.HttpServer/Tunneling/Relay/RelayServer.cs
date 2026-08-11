using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shiny.Net.HttpServer.Transports;

namespace Shiny.Net.HttpServer.Tunneling;

/// <summary>
/// The public end of a tunnel.
/// <para>
/// Two listeners. On the control port, clients dial out and register a host. On the public port,
/// ordinary HTTP arrives; the relay reads just far enough to see the Host header, finds the tunnel
/// that claimed it, and from then on only moves bytes. It is deliberately not an HTTP server —
/// interpreting the request is the tunnelled app's job, and anything the relay parsed it would also
/// have to get exactly right.
/// </para>
/// </summary>
public sealed class RelayServer : IAsyncDisposable
{
    readonly RelayServerOptions options;
    readonly ILoggerFactory loggerFactory;
    readonly ILogger<RelayServer> logger;
    readonly ConcurrentDictionary<string, TunnelSession> sessions = new(StringComparer.OrdinalIgnoreCase);
    readonly CancellationTokenSource shutdown = new();

    SocketConnectionListener? controlListener;
    SocketConnectionListener? publicListener;
    Task? controlLoop;
    Task? publicLoop;
    int disposed;

    public RelayServer(RelayServerOptions? options = null, ILoggerFactory? loggerFactory = null)
    {
        this.options = options ?? new RelayServerOptions();
        this.loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        this.logger = this.loggerFactory.CreateLogger<RelayServer>();
    }

    /// <summary>Hosts currently registered, for diagnostics and admin endpoints.</summary>
    public IReadOnlyCollection<string> RegisteredHosts => (IReadOnlyCollection<string>)this.sessions.Keys;

    /// <summary>Where tunnel clients should connect. Reflects the real port when 0 was requested.</summary>
    public string? ControlUrl => this.controlListener?.ListenDescription;

    /// <summary>Where public traffic arrives.</summary>
    public string? PublicUrl => this.publicListener?.ListenDescription;

    /// <summary>The bound public port, useful when the options asked for an ephemeral one.</summary>
    public int PublicPort => this.publicListener?.BoundEndPoint?.Port ?? this.options.PublicPort;

    /// <summary>The bound control port.</summary>
    public int ControlPort => this.controlListener?.BoundEndPoint?.Port ?? this.options.ControlPort;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed != 0, this);

        if (this.controlLoop is not null)
            throw new InvalidOperationException("The relay is already running.");

        this.controlListener = new SocketConnectionListener(
            new HttpServerOptions(),
            new HttpServerEndpoint(this.options.Address, this.options.ControlPort) { Https = this.options.ControlHttps },
            this.loggerFactory.CreateLogger<SocketConnectionListener>()
        );

        this.publicListener = new SocketConnectionListener(
            new HttpServerOptions(),
            new HttpServerEndpoint(this.options.Address, this.options.PublicPort) { Https = this.options.PublicHttps },
            this.loggerFactory.CreateLogger<SocketConnectionListener>(),
            listenerIndex: 1
        );

        await this.controlListener.BindAsync(cancellationToken).ConfigureAwait(false);
        await this.publicListener.BindAsync(cancellationToken).ConfigureAwait(false);

        this.controlLoop = Task.Run(
            () => this.AcceptLoopAsync(this.controlListener, this.HandleControlAsync, "control"),
            CancellationToken.None
        );
        this.publicLoop = Task.Run(
            () => this.AcceptLoopAsync(this.publicListener, this.HandlePublicAsync, "public"),
            CancellationToken.None
        );

        this.logger.LogInformation(
            "Relay listening — control {Control}, public {Public}",
            this.ControlUrl,
            this.PublicUrl
        );
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (this.controlLoop is null)
            return;

        await this.shutdown.CancelAsync().ConfigureAwait(false);

        if (this.controlListener is not null)
            await this.controlListener.UnbindAsync(cancellationToken).ConfigureAwait(false);

        if (this.publicListener is not null)
            await this.publicListener.UnbindAsync(cancellationToken).ConfigureAwait(false);

        foreach (var host in this.sessions.Keys)
        {
            if (this.sessions.TryRemove(host, out var session))
                await session.DisposeAsync().ConfigureAwait(false);
        }

        this.controlLoop = null;
        this.publicLoop = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) != 0)
            return;

        await this.StopAsync(CancellationToken.None).ConfigureAwait(false);
        this.shutdown.Dispose();
    }

    // ---- Accept loops ----

    async Task AcceptLoopAsync(
        SocketConnectionListener listener,
        Func<IConnection, Task> handler,
        string which
    )
    {
        var token = this.shutdown.Token;

        while (!token.IsCancellationRequested)
        {
            IConnection? connection;
            try
            {
                connection = await listener.AcceptAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (connection is null)
                return;

            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        // Same as the server's accept path: TLS is completed here, on the
                        // connection's own task, so a stalled handshake cannot block accepting.
                        if (connection is IConnectionInitializer initializer)
                            await initializer.InitializeAsync(token).ConfigureAwait(false);

                        await handler(connection).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (TunnelSession.IsExpectedDisconnect(ex))
                    {
                    }
                    catch (Exception ex)
                    {
                        this.logger.LogError(ex, "Unhandled error on a {Which} connection", which);
                    }
                },
                CancellationToken.None
            );
        }
    }

    // ---- Control side: registration ----

    async Task HandleControlAsync(IConnection connection)
    {
        var channel = new TunnelChannel(connection.Input, connection.Output);
        var token = this.shutdown.Token;

        var hello = await ReadHelloAsync(connection.Input, token).ConfigureAwait(false);
        if (hello is null)
        {
            await channel.SendAsync(TunnelFrameType.HelloReject, 0, "Expected a Hello frame.", token)
                .ConfigureAwait(false);
            await connection.DisposeAsync().ConfigureAwait(false);
            return;
        }

        var (token_, requestedSubdomain) = hello.Value;

        if (this.sessions.Count >= this.options.MaxTunnels)
        {
            await channel.SendAsync(TunnelFrameType.HelloReject, 0, "The relay is at capacity.", token)
                .ConfigureAwait(false);
            await connection.DisposeAsync().ConfigureAwait(false);
            return;
        }

        var request = new TunnelRegistrationRequest(token_, requestedSubdomain, connection.RemoteEndPoint);
        var subdomain = (this.options.Authorize ?? this.DefaultAuthorize)(request);

        if (subdomain is null)
        {
            this.logger.LogWarning("Rejected a tunnel registration from {Remote}", connection.RemoteEndPoint);
            await channel.SendAsync(TunnelFrameType.HelloReject, 0, "Registration was refused.", token)
                .ConfigureAwait(false);
            await connection.DisposeAsync().ConfigureAwait(false);
            return;
        }

        var host = $"{subdomain}.{this.options.Domain}".ToLowerInvariant();
        var session = new TunnelSession(host, connection, channel);

        if (!this.sessions.TryAdd(host, session))
        {
            await channel.SendAsync(TunnelFrameType.HelloReject, 0, $"'{host}' is already registered.", token)
                .ConfigureAwait(false);
            await connection.DisposeAsync().ConfigureAwait(false);
            return;
        }

        await channel.SendAsync(TunnelFrameType.HelloAck, 0, this.BuildPublicUrl(host), token).ConfigureAwait(false);
        this.logger.LogInformation("Tunnel registered for {Host} from {Remote}", host, connection.RemoteEndPoint);

        try
        {
            await session.RunAsync(token).ConfigureAwait(false);
        }
        finally
        {
            this.sessions.TryRemove(host, out _);
            await session.DisposeAsync().ConfigureAwait(false);
            this.logger.LogInformation("Tunnel for {Host} closed", host);
        }
    }

    string? DefaultAuthorize(TunnelRegistrationRequest request)
    {
        if (this.options.Token is { } expected &&
            !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(request.Token ?? string.Empty),
                Encoding.UTF8.GetBytes(expected)
            ))
            return null;

        var requested = request.RequestedSubdomain;
        if (!string.IsNullOrWhiteSpace(requested) && IsValidSubdomain(requested))
        {
            var host = $"{requested}.{this.options.Domain}".ToLowerInvariant();
            return this.sessions.ContainsKey(host) ? null : requested.ToLowerInvariant();
        }

        // Unguessable rather than sequential: a public URL is the only thing standing between the
        // internet and an app that expected to be on a phone.
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
    }

    static bool IsValidSubdomain(string value)
    {
        if (value.Length is 0 or > 63)
            return false;

        foreach (var c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '-')
                return false;
        }

        return value[0] != '-' && value[^1] != '-';
    }

    string BuildPublicUrl(string host)
    {
        var port = this.PublicPort;
        var isDefaultPort = (this.options.PublicScheme == "https" && port == 443)
            || (this.options.PublicScheme == "http" && port == 80);

        return this.options.IncludePortInPublicUrl && !isDefaultPort
            ? $"{this.options.PublicScheme}://{host}:{port}"
            : $"{this.options.PublicScheme}://{host}";
    }

    static async ValueTask<(string? Token, string? Subdomain)?> ReadHelloAsync(
        PipeReader reader,
        CancellationToken cancellationToken
    )
    {
        while (true)
        {
            var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;

            if (TunnelProtocol.TryRead(ref buffer, out var type, out _, out var payload))
            {
                var text = payload.IsEmpty ? string.Empty : Encoding.UTF8.GetString(payload.ToArray());
                reader.AdvanceTo(buffer.Start, buffer.End);

                if (type != TunnelFrameType.Hello)
                    return null;

                var newline = text.IndexOf('\n');
                return newline < 0
                    ? (text, null)
                    : (text[..newline], text[(newline + 1)..]);
            }

            reader.AdvanceTo(buffer.Start, buffer.End);

            if (result.IsCompleted || result.IsCanceled)
                return null;
        }
    }

    // ---- Public side: routing ----

    async Task HandlePublicAsync(IConnection connection)
    {
        var token = this.shutdown.Token;

        var head = await this.ReadHeadAsync(connection, this.options.RequestHeadTimeout, token).ConfigureAwait(false);
        if (head is not { Complete: true, Host: { } host })
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            return;
        }

        if (!this.sessions.TryGetValue(host, out var session))
        {
            this.logger.LogDebug("No tunnel registered for host '{Host}'", host);
            await WriteErrorAsync(connection, 404, "Not Found", $"No tunnel is registered for '{host}'.")
                .ConfigureAwait(false);
            await connection.DisposeAsync().ConfigureAwait(false);
            return;
        }

        var streamId = await session.OpenStreamAsync(connection, token).ConfigureAwait(false);
        var clientIp = (connection.RemoteEndPoint as IPEndPoint)?.Address.ToString();

        try
        {
            var current = head.Value;

            while (true)
            {
                // A reused connection can carry a different Host on its next request. Forwarding it
                // to the tunnel this connection started with would deliver it to the wrong app, so
                // the connection is pinned and a switch is refused rather than silently misrouted.
                if (!string.Equals(current.Host, host, StringComparison.OrdinalIgnoreCase))
                {
                    await WriteErrorAsync(
                        connection,
                        421,
                        "Misdirected Request",
                        "This connection is bound to a different tunnel. Open a new connection."
                    ).ConfigureAwait(false);
                    return;
                }

                var bytes = this.options.AddForwardedHeaders
                    ? RequestHead.WithForwardedHeaders(current.Bytes, clientIp, this.options.PublicScheme, host)
                    : current.Bytes;

                await session.SendAsync(streamId, bytes, token).ConfigureAwait(false);

                var forwarded = await RequestBodyForwarder.ForwardAsync(
                    connection.Input,
                    current.Framing,
                    (payload, ct) => session.SendAsync(streamId, payload, ct),
                    token
                ).ConfigureAwait(false);

                if (!forwarded)
                    return;

                // No head means the client finished with the connection, which is the normal end of
                // a keep-alive exchange rather than an error.
                var next = await this
                    .ReadHeadAsync(connection, this.options.KeepAliveTimeout, token, writeErrors: false)
                    .ConfigureAwait(false);

                if (next is not { Complete: true })
                    return;

                current = next.Value;
            }
        }
        catch (Exception ex) when (TunnelSession.IsExpectedDisconnect(ex))
        {
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Stream {StreamId} on {Host} faulted", streamId, host);
        }
        finally
        {
            await session.CloseStreamAsync(streamId).ConfigureAwait(false);
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    async ValueTask<RequestHead.Result?> ReadHeadAsync(
        IConnection connection,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        bool writeErrors = true
    )
    {
        using var deadline = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);

        RequestHead.Result head;
        try
        {
            head = await RequestHead
                .ReadAsync(connection.Input, this.options.MaxRequestHeadSize, linked.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException || TunnelSession.IsExpectedDisconnect(ex))
        {
            if (writeErrors && deadline.IsCancellationRequested)
                await WriteErrorAsync(connection, 408, "Request Timeout", "Timed out reading the request head.")
                    .ConfigureAwait(false);

            return null;
        }

        if (head.Complete && head.Host is not null)
            return head;

        if (!writeErrors)
            return null;

        await WriteErrorAsync(
            connection,
            400,
            "Bad Request",
            head.Complete
                ? "The request is missing a Host header, so it cannot be routed to a tunnel."
                : "The request head was malformed or too large."
        ).ConfigureAwait(false);

        return null;
    }

    static async Task WriteErrorAsync(IConnection connection, int statusCode, string reason, string message)
    {
        try
        {
            var body = Encoding.UTF8.GetBytes(message);
            var head = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {statusCode} {reason}\r\n" +
                "Content-Type: text/plain; charset=utf-8\r\n" +
                $"Content-Length: {body.Length}\r\n" +
                "Connection: close\r\n" +
                "\r\n"
            );

            connection.Output.Write(head);
            connection.Output.Write(body);
            await connection.Output.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (TunnelSession.IsExpectedDisconnect(ex))
        {
        }
    }
}
