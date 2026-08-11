using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shiny.Net.HttpServer.Transports;

namespace Shiny.Net.HttpServer.Tunneling;

/// <summary>
/// A source of connections that arrived from somewhere other than a local socket.
/// <para>
/// It is an <see cref="IConnectionListener"/> on purpose. From the server's point of view a tunnel
/// is just another way for connections to show up, so nothing above the transport has to learn
/// about tunnels at all — and swapping the reference relay for Azure Relay or SSH is a
/// matter of writing one more implementation of this interface.
/// </para>
/// </summary>
public interface ITunnelProvider : IConnectionListener
{
    /// <summary>Short name for logs, e.g. <c>shiny-relay</c>.</summary>
    string Name { get; }

    /// <summary>
    /// The public URL this tunnel is reachable at, available once
    /// <see cref="IConnectionListener.BindAsync"/> has completed.
    /// </summary>
    string? PublicUrl { get; }
}

/// <summary>Runs a <see cref="HttpServer"/> behind a tunnel.</summary>
public static class HttpServerTunnelExtensions
{
    /// <summary>
    /// Opens the tunnel and serves everything arriving through it until cancelled.
    /// <para>
    /// Composes with <see cref="HttpServer.StartAsync"/> rather than replacing it: a server can
    /// listen locally and answer tunnelled requests at the same time, from the same routes. Used on
    /// its own, no local port is bound at all — which is what an embedded server on a phone wants.
    /// </para>
    /// </summary>
    public static async Task RunTunnelAsync(
        this HttpServer server,
        ITunnelProvider provider,
        ILogger? logger = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(provider);

        logger ??= NullLogger.Instance;

        await provider.BindAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Tunnel {Name} open at {Url}", provider.Name, provider.PublicUrl);

        var inFlight = new HashSet<Task>();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var connection = await provider.AcceptAsync(cancellationToken).ConfigureAwait(false);
                if (connection is null)
                    break;

                var task = Task.Run(
                    () => server.ServeAsync(connection, cancellationToken),
                    CancellationToken.None
                );

                lock (inFlight)
                    inFlight.Add(task);

                _ = task.ContinueWith(
                    completed =>
                    {
                        lock (inFlight)
                            inFlight.Remove(completed);

                        if (completed.IsFaulted)
                            logger.LogError(completed.Exception, "A tunnelled connection faulted");
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default
                );
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        finally
        {
            await provider.UnbindAsync(CancellationToken.None).ConfigureAwait(false);

            Task[] remaining;
            lock (inFlight)
                remaining = [.. inFlight];

            if (remaining.Length > 0)
            {
                try
                {
                    // Drain, but do not hang on a peer that has stopped reading.
                    await Task.WhenAll(remaining).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
                {
                    logger.LogWarning("{Count} tunnelled connection(s) still active at shutdown", remaining.Length);
                }
            }
        }
    }
}
