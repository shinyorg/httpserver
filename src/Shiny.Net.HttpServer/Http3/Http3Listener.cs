using System.Net;
using System.Net.Quic;
using System.Net.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

#pragma warning disable CA1416 // Support is checked in BindAsync before any QUIC type is used.

namespace Shiny.Net.HttpServer.Http3;

/// <summary>
/// Listens for QUIC connections and serves them as HTTP/3.
/// <para>
/// Not an <c>IConnectionListener</c>, because QUIC does not hand out byte streams: a connection is
/// already a set of multiplexed streams, and flattening that into one pipe would throw away the
/// property HTTP/3 exists for. It plugs into the server at the pipeline instead.
/// </para>
/// </summary>
public sealed class Http3Listener : IAsyncDisposable
{
    readonly Http3Options options;
    readonly HttpServer server;
    readonly ILoggerFactory loggerFactory;
    readonly ILogger logger;
    readonly CancellationTokenSource stopping = new();

    QuicListener? listener;
    Task? acceptLoop;
    int disposed;

    public Http3Listener(HttpServer server, Http3Options options, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(options);

        this.server = server;
        this.options = options;
        this.loggerFactory = loggerFactory
            ?? server.Services?.GetService<ILoggerFactory>()
            ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;

        this.logger = this.loggerFactory.CreateLogger<Http3Listener>();
    }

    /// <summary>
    /// Whether this platform can run QUIC at all.
    /// <para>
    /// It needs msquic, which ships with .NET on Windows and Linux and is absent on macOS. Worth
    /// checking rather than assuming: the failure without it is a type initializer exception from
    /// somewhere unhelpful.
    /// </para>
    /// </summary>
    public static bool IsSupported => QuicListener.IsSupported;

    /// <summary>The endpoint actually bound, once <see cref="BindAsync"/> has run.</summary>
    public IPEndPoint? BoundEndPoint { get; private set; }

    /// <summary>The <c>Alt-Svc</c> value a TCP listener should advertise so clients discover this one.</summary>
    public string AltSvc => this.options.BuildAltSvc();

    public async ValueTask BindAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed != 0, this);

        if (!IsSupported)
            throw new PlatformNotSupportedException(
                "QUIC is not available on this platform. HTTP/3 needs msquic, which ships with .NET on " +
                "Windows and Linux but not on macOS."
            );

        if (this.listener is not null)
            throw new InvalidOperationException("The HTTP/3 listener is already bound.");

        if (this.options.Certificate is null && this.options.CertificateSelector is null)
            throw new InvalidOperationException(
                "HTTP/3 requires a certificate: QUIC has no plaintext mode, so there is no such thing " +
                "as an unencrypted HTTP/3 endpoint."
            );

        var listenerOptions = new QuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(this.options.Address, this.options.Port),

            // "h3" is the only protocol offered. A client negotiating anything else has reached the
            // wrong port.
            ApplicationProtocols = [SslApplicationProtocol.Http3],
            ConnectionOptionsCallback = (connection, hello, token) =>
                ValueTask.FromResult(this.BuildConnectionOptions(hello))
        };

        this.listener = await QuicListener.ListenAsync(listenerOptions, cancellationToken).ConfigureAwait(false);
        this.BoundEndPoint = this.listener.LocalEndPoint;

        this.acceptLoop = Task.Run(() => this.AcceptLoopAsync(this.stopping.Token), CancellationToken.None);

        this.logger.LogInformation("HTTP/3 listening on https://{EndPoint} (QUIC)", this.BoundEndPoint);
    }

    public async ValueTask UnbindAsync(CancellationToken cancellationToken = default)
    {
        if (!this.stopping.IsCancellationRequested)
            await this.stopping.CancelAsync().ConfigureAwait(false);

        var quic = Interlocked.Exchange(ref this.listener, null);

        if (quic is not null)
            await quic.DisposeAsync().ConfigureAwait(false);

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
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) != 0)
            return;

        await this.UnbindAsync(CancellationToken.None).ConfigureAwait(false);
        this.stopping.Dispose();
    }

    QuicServerConnectionOptions BuildConnectionOptions(SslClientHelloInfo hello)
    {
        var certificate = this.options.CertificateSelector?.Invoke(hello.ServerName)
            ?? this.options.Certificate
            ?? throw new InvalidOperationException($"No certificate for '{hello.ServerName}'.");

        return new QuicServerConnectionOptions
        {
            DefaultStreamErrorCode = Http3ErrorCode.RequestCancelled,
            DefaultCloseErrorCode = Http3ErrorCode.NoError,
            IdleTimeout = this.options.IdleTimeout,
            MaxInboundBidirectionalStreams = this.options.MaxBidirectionalStreams,
            MaxInboundUnidirectionalStreams = this.options.MaxUnidirectionalStreams,
            ServerAuthenticationOptions = new SslServerAuthenticationOptions
            {
                ApplicationProtocols = [SslApplicationProtocol.Http3],
                ServerCertificate = certificate
            }
        };
    }

    async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        var quic = this.listener;
        if (quic is null)
            return;

        while (!cancellationToken.IsCancellationRequested)
        {
            QuicConnection connection;

            try
            {
                connection = await quic.AcceptConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (Http3Connection.IsExpectedDisconnect(ex))
            {
                break;
            }
            catch (Exception ex)
            {
                this.logger.LogError(ex, "Accepting a QUIC connection failed");
                continue;
            }

            var http3 = new Http3Connection(
                connection,
                this.options,
                this.server.Options,
                this.server.BuildPipelineForTransport(),
                this.server.Services,
                this.loggerFactory
            );

            _ = Task.Run(() => http3.ProcessAsync(cancellationToken), CancellationToken.None);
        }
    }
}

/// <summary>Wiring an HTTP/3 endpoint onto a server.</summary>
public static class Http3Extensions
{
    /// <summary>
    /// Starts an HTTP/3 endpoint alongside the server's TCP listener, and advertises it with
    /// <c>Alt-Svc</c> so clients know to try QUIC.
    /// <code>
    /// var app = builder.Build();
    /// await app.StartAsync();
    /// await using var h3 = await app.ListenHttp3Async(o =>
    /// {
    ///     o.Port = 5001;
    ///     o.Certificate = certificate;
    /// });
    /// </code>
    /// <para>
    /// A client will not use HTTP/3 without being told it exists. Advertising it from the TCP
    /// endpoint is how that happens — there is no other discovery mechanism.
    /// </para>
    /// </summary>
    public static async Task<Http3Listener> ListenHttp3Async(
        this HttpServer server,
        Action<Http3Options> configure,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new Http3Options();
        configure(options);

        var listener = new Http3Listener(server, options);
        await listener.BindAsync(cancellationToken).ConfigureAwait(false);

        server.Use((context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                if (!context.Response.Headers.ContainsKey(HeaderNames.AltSvc))
                    context.Response.Headers[HeaderNames.AltSvc] = listener.AltSvc;

                return default;
            });

            return next(context);
        });

        return listener;
    }
}
