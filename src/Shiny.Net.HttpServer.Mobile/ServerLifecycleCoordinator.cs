using Microsoft.Extensions.Logging;

namespace Shiny.Net.HttpServer.Mobile;

/// <summary>
/// Applies the app's foreground and background transitions to the server, one at a time, and keeps
/// a connectivity rebind from undoing a background stop.
/// </summary>
/// <remarks>
/// <para>
/// The platform raises the lifecycle callbacks synchronously on the main thread and the server's
/// operations are asynchronous, so each transition is handed to the thread pool. Left unordered,
/// those transitions overlap: a user who backgrounds the app and comes straight back delivers a
/// foreground while the background stop is still draining requests, and the foreground - asking
/// "did the lifecycle stop this?" before the stop has finished saying so - does nothing. The app is
/// then in the foreground with the server off and nothing scheduled to turn it back on.
/// </para>
/// <para>
/// So every transition, and every rebind, runs inside one gate, and a transition acts on where the
/// app is <em>when it runs</em>, not on the callback that queued it. Whichever transition runs last
/// therefore runs after the last callback, and sees the state the app actually settled in - so the
/// order the thread pool happens to pick them up in does not matter.
/// </para>
/// <para>
/// Outside the <c>PLATFORM</c> guard so it compiles, and is tested, on the base target framework.
/// The platform's part arrives as delegates.
/// </para>
/// </remarks>
sealed class ServerLifecycleCoordinator(
    HttpServer server,
    HttpServerLifecycleOptions options,
    ServerTransitionRunner transitions,
    ILogger logger,
    Func<bool?> isInForeground,
    bool supportsBackgroundExecution,
    Action startBackgroundExecution,
    Action stopBackgroundExecution
) : IDisposable
{
    // Never disposed: nothing ever reads its wait handle, so there is nothing to release, and a
    // transition still queued on it when the task is disposed would otherwise throw on the way in.
    readonly SemaphoreSlim gate = new(1, 1);

    // Only read and written inside the gate.
    bool stoppedByLifecycle;

    // Written from the server's state machine too (see TrackForRestore), which cannot wait on the
    // gate - it runs inside StopAsync, and the gate's holder may be the one awaiting that stop.
    int restoreOnForeground;

    volatile bool disposed;

    /// <summary>
    /// Applies whichever transition the app is in now. Queued behind any transition or rebind
    /// already running.
    /// </summary>
    public async Task ApplyAsync()
    {
        await this.gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (this.disposed)
                return;

            // Null is "no transition yet": nothing has happened for the server to follow.
            switch (isInForeground())
            {
                case false:
                    await this.OnBackgroundingAsync().ConfigureAwait(false);
                    break;

                case true:
                    this.OnForegrounding();
                    break;
            }
        }
        finally
        {
            this.gate.Release();
        }
    }

    /// <summary>
    /// Rebinds after a connectivity change if the server is still meant to be serving and
    /// <paramref name="shouldRebind"/> - the address diff, which only the caller can take - agrees.
    /// Returns whether a rebind was started.
    /// </summary>
    /// <remarks>
    /// Inside the gate, so the check and the restart are one step as far as a background stop is
    /// concerned. Outside it a settle that had already looked could restart the server just after
    /// the stop, bringing the listener back up in the background - the one thing the app asked not
    /// to happen.
    /// </remarks>
    public async Task<bool> TryRebindAsync(Func<bool> shouldRebind)
    {
        await this.gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (this.disposed || !server.IsRunning)
                return false;

            // Backgrounded with the stop still queued behind this: it is about to stop the server,
            // and a rebind now would only make it wait for the restart before it can.
            if (isInForeground() == false && options.BackgroundMode == BackgroundServerMode.Stop)
                return false;

            if (!shouldRebind())
                return false;

            // The single most likely way this package leaves a phone with a dead server, and the
            // reason the retry runner exists. RestartAsync unbinds first and then binds, so a bind
            // refused on a half-up network leaves the server Stopped, and nothing else is watching
            // for that. It is retried as the network settles, and if it still will not come up the
            // give-up is an Error carrying the bind exception rather than a warning nobody collects.
            transitions.Run("Rebind after a connectivity change", server.RestartAsync);
            return true;
        }
        finally
        {
            this.gate.Release();
        }
    }

    /// <summary>
    /// Remembers, on a platform that cannot keep a listener answering in the background, whether the
    /// server was running when the app went away - so the resume can put back what the suspension
    /// took. See <c>HttpServerLifecycleTask.OnServerStateChanged</c> for when this is called.
    /// </summary>
    public void TrackForRestore(bool running)
    {
        if (supportsBackgroundExecution)
            return;

        Volatile.Write(ref this.restoreOnForeground, running ? 1 : 0);
    }

    async Task OnBackgroundingAsync()
    {
        switch (options.BackgroundMode)
        {
            case BackgroundServerMode.Stop:
                // Anything still retrying its way back up is abandoned first, and waited for: it is
                // trying to bind a listener for an app that has just decided it does not want one,
                // and a restart that lands after this stop leaves a socket open in the background.
                //
                // Before the IsRunning check, not after it. A restart caught between its unbind and
                // its bind reports not running, so checking first skipped the stop - and the restart
                // then finished and left the server listening in the background.
                //
                // A start or restart that was still in flight means the server was meant to be up,
                // even if cancelling it leaves it down, so the resume puts it back either way.
                var interrupted = !transitions.Completion.IsCompleted;
                await transitions.CancelAsync().ConfigureAwait(false);

                if (!server.IsRunning)
                {
                    this.stoppedByLifecycle |= interrupted;
                    break;
                }

                logger.LogInformation("Stopping the server for the background");

                // Recorded before the stop rather than after it. The foreground that undoes this
                // waits for the gate either way, and a stop that throws part way has still taken the
                // server down as far as the app is concerned - the resume should put it back.
                this.stoppedByLifecycle = true;
                await server.StopAsync().ConfigureAwait(false);
                break;

            case BackgroundServerMode.KeepAlive when server.IsRunning:
                startBackgroundExecution();
                this.TrackForRestore(true);
                break;
        }
    }

    void OnForegrounding()
    {
        stopBackgroundExecution();

        // Read and cleared as one step: the server's state machine can set it from another thread.
        if (Interlocked.Exchange(ref this.restoreOnForeground, 0) == 1)
        {
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
            transitions.Run("Restart after the background suspension", server.RestartAsync);
            return;
        }

        if (!this.stoppedByLifecycle && !options.AlwaysStartOnForeground)
            return;

        if (server.IsRunning)
        {
            this.stoppedByLifecycle = false;
            return;
        }

        logger.LogInformation("Starting the server for the foreground");

        // Cleared before the start rather than after it. The retry owns getting the listener up from
        // here, and a flag left set would have the next foreground transition queue a second start
        // behind the one already trying.
        this.stoppedByLifecycle = false;
        transitions.Run("Start for the foreground", server.StartAsync);
    }

    public void Dispose() => this.disposed = true;
}
