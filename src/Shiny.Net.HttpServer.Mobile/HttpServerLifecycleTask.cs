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
    bool restoreOnForeground;
    bool subscribed;

    public override void Start()
    {
        base.Start();

        if (options.RestartOnConnectivityChange)
        {
            connectivity.Changed += this.OnConnectivityChanged;
            this.subscribed = true;
        }

        // The foreground service follows the server, not only the app's lifecycle transitions -
        // see OnServerStateChanged for why that difference matters.
        server.StateChanged += this.OnServerStateChanged;
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
                this.TrackForRestore(true);
                break;
        }
    }

    async Task OnForegroundingAsync()
    {
        this.StopBackgroundExecution();

        if (this.restoreOnForeground)
        {
            this.restoreOnForeground = false;

            // Restarted, not started. The server object was never told anything happened, so it
            // still reports Running - and StartAsync, being idempotent, would agree with it and do
            // nothing, leaving the dead listener dead. RestartAsync unbinds and binds again, which
            // is also the right call if the suspension took the socket but the server survived, and
            // is harmless if it was in fact stopped underneath us.
            logger.LogInformation("Restarting the server after the background suspension");

            await server.RestartAsync().ConfigureAwait(false);
            return;
        }

        if (!this.stoppedByLifecycle && !options.AlwaysStartOnForeground)
            return;

        if (server.IsRunning)
            return;

        logger.LogInformation("Starting the server for the foreground");

        await server.StartAsync().ConfigureAwait(false);
        this.stoppedByLifecycle = false;
    }

    /// <summary>
    /// Remembers, on a platform that cannot keep a listener answering in the background, whether the
    /// server was running when the app went away - so the resume can put back what the suspension
    /// took.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the Apple half of <see cref="BackgroundServerMode.KeepAlive"/>. Android has a
    /// foreground service and needs none of it; iOS has nothing that will hold a socket open, so the
    /// process is suspended, the listener stops answering, and the <see cref="HttpServer"/> object
    /// goes on reporting <see cref="HttpServer.IsRunning"/> because nothing on the platform tells it
    /// otherwise. Left alone, the user comes back to an app that says it is serving and a client
    /// that cannot connect - and no amount of pressing the toggle fixes it, because the toggle is
    /// already in the position it should be in.
    /// </para>
    /// <para>
    /// Recorded rather than inferred at resume: <see cref="HttpServer.IsRunning"/> is exactly the
    /// thing that has gone stale, so it cannot answer "was it on?". Only an explicit stop while
    /// backgrounded clears this - see <see cref="OnServerStateChanged"/> - so a server the app or
    /// the user switched off stays off. That restraint is exactly what
    /// <see cref="HttpServerLifecycleOptions.AlwaysStartOnForeground"/> exists to lift, and lifting
    /// it is a separate and more opinionated choice than restoring what was already on.
    /// </para>
    /// </remarks>
    void TrackForRestore(bool running)
    {
        if (SupportsBackgroundExecution)
            return;

        this.restoreOnForeground = running;
    }

    /// <summary>
    /// Keeps the background execution in step with the server itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without this the foreground service is decided once, at the moment the app is backgrounded,
    /// and never revisited - so it answers "was the server running when the user left?" rather than
    /// "is the server running now?". Those come apart in both directions, and both are wrong in a
    /// way the user sees:
    /// </para>
    /// <list type="bullet">
    /// <item>A server stopped while the app is in the background - by a toggle in a notification
    /// action, by the app's own code, by a rebind that failed - leaves the ongoing notification up,
    /// holding the process alive and telling the user something is being served when nothing is.</item>
    /// <item>A server started while the app is in the background gets no service at all, so Android
    /// reclaims the process within minutes and the listener dies with it. The app did everything
    /// right and the server simply stops.</item>
    /// </list>
    /// <para>
    /// Only the settled states act. <see cref="HttpServerState.Starting"/> and
    /// <see cref="HttpServerState.Stopping"/> deliberately leave it as it is: a bind that fails goes
    /// Starting then Stopped, and acting on the first would flash a notification for a server that
    /// never came up, while a stop waits in Stopping for in-flight requests - which is precisely
    /// when the process still needs holding up.
    /// </para>
    /// </remarks>
    void OnServerStateChanged(object? sender, HttpServerState state)
    {
        if (options.BackgroundMode != BackgroundServerMode.KeepAlive)
            return;

        // Only while backgrounded. Null is "no transition yet", which is treated as the foreground:
        // of the two ways to be wrong before the app has ever been left, a missing notification is
        // recoverable at the next transition and an unasked-for one is just wrong.
        if (this.IsInForeground != false)
            return;

        try
        {
            switch (state)
            {
                case HttpServerState.Running:
                    this.StartBackgroundExecution();
                    this.TrackForRestore(true);
                    break;

                case HttpServerState.Stopped:
                    this.StopBackgroundExecution();
                    this.TrackForRestore(false);
                    break;
            }
        }
        catch (Exception ex)
        {
            // This runs inside the server's own state machine, on the thread that called Start or
            // Stop - so an exception here would surface out of StartAsync as though the server had
            // failed. The service is a platform concern and its failure is not the server's.
            logger.LogWarning(ex, "Failed to update background execution for the {State} server", state);
        }
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

    /// <summary>
    /// Whether the platform can actually keep the listener answering with the app in the background.
    /// Android can, through a foreground service; Apple cannot, at any price - so the Apple build
    /// restores the server on resume instead. Everything that differs between the two hangs off this.
    /// </summary>
    static partial bool SupportsBackgroundExecution { get; }

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

        server.StateChanged -= this.OnServerStateChanged;

        GC.SuppressFinalize(this);
    }
}
#endif
