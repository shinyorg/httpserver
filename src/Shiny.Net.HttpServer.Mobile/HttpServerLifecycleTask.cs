#if PLATFORM
using Microsoft.Extensions.Logging;
using Shiny.Net;

namespace Shiny.Net.HttpServer.Mobile;

/// <summary>
/// Ties the server to the app's lifecycle and to the device's network.
/// <para>
/// This is the part of "run a server on a phone" that nothing else can do for you. A server
/// object does not know the app was backgrounded, and a socket does not know the phone left the
/// Wi-Fi network it was bound to. Both are ordinary events on a device and neither exists on a
/// machine in a rack, which is why no server framework handles them and why this package does.
/// </para>
/// </summary>
public partial class HttpServerLifecycleTask(
    HttpServer server,
    HttpServerLifecycleOptions options,
    IConnectivity connectivity,
    ILogger<HttpServerLifecycleTask> logger
) : ShinyLifecycleTask, IDisposable
{
    bool stoppedByLifecycle;
    bool subscribed;

    public override void Start()
    {
        base.Start();

        if (options.RestartOnConnectivityChange)
        {
            connectivity.Changed += this.OnConnectivityChanged;
            this.subscribed = true;
        }
    }

    protected override void OnStateChanged(bool backgrounding)
    {
        // The lifecycle callbacks are synchronous and run on the platform's main thread; the
        // server's are not. Anything that faults here has nobody to report to, so it is caught.
        _ = Task.Run(async () =>
        {
            try
            {
                if (backgrounding)
                    await this.OnBackgroundingAsync().ConfigureAwait(false);
                else
                    await this.OnForegroundingAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to apply the {State} transition to the server", backgrounding ? "background" : "foreground");
            }
        });
    }

    async Task OnBackgroundingAsync()
    {
        switch (options.BackgroundMode)
        {
            case BackgroundServerMode.Stop when server.IsRunning:
                logger.LogInformation("Stopping the server for the background");

                await server.StopAsync().ConfigureAwait(false);
                this.stoppedByLifecycle = true;
                break;

            case BackgroundServerMode.KeepAlive when server.IsRunning:
                this.StartBackgroundExecution();
                break;
        }
    }

    async Task OnForegroundingAsync()
    {
        this.StopBackgroundExecution();

        if (!this.stoppedByLifecycle && !options.AlwaysStartOnForeground)
            return;

        if (server.IsRunning)
            return;

        logger.LogInformation("Starting the server for the foreground");

        await server.StartAsync().ConfigureAwait(false);
        this.stoppedByLifecycle = false;
    }

    void OnConnectivityChanged(object? sender, EventArgs e) => _ = Task.Run(async () =>
    {
        try
        {
            if (!server.IsRunning)
                return;

            // Restarted rather than left alone, because the listener is bound to an address the
            // device may no longer hold. A restart on a device that kept its address costs a
            // dropped keep-alive connection; not restarting on one that did not costs everything.
            logger.LogInformation("Connectivity changed to {Access} ({Types}); rebinding", connectivity.Access, connectivity.ConnectionTypes);

            await server.RestartAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A network in transition often refuses the bind. The next change tries again.
            logger.LogWarning(ex, "Failed to rebind the server after a connectivity change");
        }
    });

    /// <summary>Android starts a foreground service here; the Apple build has nothing to start.</summary>
    partial void StartBackgroundExecutionPlatform();

    partial void StopBackgroundExecutionPlatform();

    void StartBackgroundExecution() => this.StartBackgroundExecutionPlatform();

    void StopBackgroundExecution() => this.StopBackgroundExecutionPlatform();

    public void Dispose()
    {
        if (this.subscribed)
        {
            connectivity.Changed -= this.OnConnectivityChanged;
            this.subscribed = false;
        }

        GC.SuppressFinalize(this);
    }
}
#endif
