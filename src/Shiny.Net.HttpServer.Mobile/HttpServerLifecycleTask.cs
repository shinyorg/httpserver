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
    // Android has no callback for "the foreground service never started", so the only way to know is
    // to look afterwards. Five seconds is long enough for the start to have been dispatched on a
    // main looper that is busy resuming an app, and short enough that the log lands near its cause.
    static readonly TimeSpan BackgroundExecutionStartTimeout = TimeSpan.FromSeconds(5);

    // Every start and restart this class drives goes through here rather than being awaited once and
    // hoped over. See ServerTransitionRunner for why a single attempt is the bug being fixed.
    readonly ServerTransitionRunner transitions = new(options, logger);

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

                // Anything still retrying its way back up is abandoned first. It is trying to bind
                // a listener for an app that has just decided it does not want one, and a rebind
                // that lands after this stop leaves a socket open with nothing watching it.
                this.transitions.Cancel();

                await server.StopAsync().ConfigureAwait(false);
                this.stoppedByLifecycle = true;
                break;

            case BackgroundServerMode.KeepAlive when server.IsRunning:
                this.StartBackgroundExecution();
                this.TrackForRestore(true);
                break;
        }
    }

    // Not async: everything it does either completes synchronously or is handed to the retry runner,
    // which owns the outcome and the logging from there. Kept returning a Task so the two lifecycle
    // halves read the same way at the call site.
    Task OnForegroundingAsync()
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

            // Through the runner, because this resume is the same hole as a connectivity change: the
            // first thing an app does on resume is race the OS for a network that is still coming
            // back, and a single refused bind here strands the user on a screen that says the server
            // is on. The flag is already cleared, so nothing else will try again if this does not.
            this.transitions.Run("Restart after the background suspension", server.RestartAsync);
            return Task.CompletedTask;
        }

        if (!this.stoppedByLifecycle && !options.AlwaysStartOnForeground)
            return Task.CompletedTask;

        if (server.IsRunning)
            return Task.CompletedTask;

        logger.LogInformation("Starting the server for the foreground");

        // Cleared before the start rather than after it. The retry owns getting the listener up from
        // here, and a flag left set would have the next foreground transition queue a second start
        // behind the one already trying.
        this.stoppedByLifecycle = false;
        this.transitions.Run("Start for the foreground", server.StartAsync);

        return Task.CompletedTask;
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
            //
            // Error rather than warning, though, because of what the failure means: with no
            // foreground service holding it up, Android reclaims this process within minutes and
            // takes the listener with it. From inside the app that is indistinguishable from the
            // server stopping on its own, which is exactly the report this is here to answer.
            logger.LogError(ex, "Failed to update background execution for the {State} server; on Android the process will be reclaimed and the listener will stop with it", state);
        }
    }

    void OnConnectivityChanged(object? sender, EventArgs e)
    {
        if (!server.IsRunning)
            return;

        // Restarted rather than left alone, because the listener is bound to an address the
        // device may no longer hold. A restart on a device that kept its address costs a
        // dropped keep-alive connection; not restarting on one that did not costs everything.
        logger.LogInformation("Connectivity changed to {Access} ({Types}); rebinding", connectivity.Access, connectivity.ConnectionTypes);

        // The single most likely way this package leaves a phone with a dead server, and the reason
        // the retry runner exists. RestartAsync unbinds first and then binds, so a bind refused on a
        // half-up network - the new interface not routable yet, the old port not released yet -
        // leaves the server Stopped. Nothing else here is watching for that, so waiting for "the
        // next connectivity change" is waiting for an event that may not come until the user walks
        // somewhere else. It is retried as the network settles, and if it still will not come up the
        // give-up is an Error carrying the bind exception rather than a warning nobody collects.
        this.transitions.Run("Rebind after a connectivity change", server.RestartAsync);
    }

    /// <summary>
    /// Whether the platform can actually keep the listener answering with the app in the background.
    /// Android can, through a foreground service; Apple cannot, at any price - so the Apple build
    /// restores the server on resume instead. Everything that differs between the two hangs off this.
    /// </summary>
    static partial bool SupportsBackgroundExecution { get; }

    /// <summary>
    /// Whether the platform's background execution is up <em>right now</em>, as opposed to having
    /// been asked for. On Android that is the foreground service actually running; on Apple there is
    /// nothing to be up, which is what <see cref="SupportsBackgroundExecution"/> already says.
    /// </summary>
    static partial bool BackgroundExecutionIsActive { get; }

    /// <summary>Android starts a foreground service here; the Apple build has nothing to start.</summary>
    partial void StartBackgroundExecutionPlatform();

    partial void StopBackgroundExecutionPlatform();

    void StartBackgroundExecution()
    {
        this.StartBackgroundExecutionPlatform();

        if (SupportsBackgroundExecution)
            _ = this.VerifyBackgroundExecutionAsync();
    }

    void StopBackgroundExecution() => this.StopBackgroundExecutionPlatform();

    /// <summary>
    /// Checks that the background execution asked for above actually came up, and says so loudly
    /// when it did not.
    /// </summary>
    /// <remarks>
    /// Starting it is fire-and-forget on Android by construction: <c>StartService</c> posts an
    /// intent and returns, and the service comes up — or is refused — on the main looper afterwards.
    /// Android refuses it outright when the app is not entitled to a foreground service at that
    /// moment (a background start without an exemption on API 31+, a missing
    /// <c>FOREGROUND_SERVICE_DATA_SYNC</c>, a revoked notification permission), and the refusal is
    /// thrown inside the service, where this caller cannot see it. Unchecked, the first anyone knows
    /// is the process being reclaimed some minutes later with the listener inside it — a server that
    /// "stopped randomly" whose cause was five seconds after the app was backgrounded.
    /// </remarks>
    async Task VerifyBackgroundExecutionAsync()
    {
        try
        {
            await Task.Delay(BackgroundExecutionStartTimeout).ConfigureAwait(false);

            // Not a failure if the server has since stopped: OnServerStateChanged will have taken
            // the service down on purpose, and that is the arrangement working.
            if (BackgroundExecutionIsActive || !server.IsRunning)
                return;

            logger.LogError(
                "The background service did not start within {Timeout}. The server is still listening but nothing is holding this process up, so the OS will reclaim it and the listener will stop with it. Check FOREGROUND_SERVICE, FOREGROUND_SERVICE_DATA_SYNC and POST_NOTIFICATIONS - LocalNetworkAccess.Check() reports which are missing",
                BackgroundExecutionStartTimeout
            );
        }
        catch (Exception ex)
        {
            // Nothing awaits this, so an escaping exception is one reported nowhere.
            logger.LogError(ex, "Failed to confirm that the background service started");
        }
    }

    public void Dispose()
    {
        if (this.subscribed)
        {
            connectivity.Changed -= this.OnConnectivityChanged;
            this.subscribed = false;
        }

        server.StateChanged -= this.OnServerStateChanged;
        this.transitions.Dispose();

        GC.SuppressFinalize(this);
    }
}
#endif
