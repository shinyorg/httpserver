using Microsoft.Extensions.Logging;

namespace Shiny.Net.HttpServer.Mobile;

/// <summary>
/// Runs a lifecycle-driven start or restart until it takes, and makes giving up loud.
/// </summary>
/// <remarks>
/// <para>
/// Every transition this package drives is provoked by the device rather than by the app — the
/// network changed, the app came back — and a device in the middle of one of those is precisely
/// when a bind is refused: the interface the new route runs over is not up yet, or the port the old
/// listener held is still in TIME_WAIT. A single attempt that loses that race leaves the listener
/// down with nothing scheduled to bring it back, while the app goes on showing a toggle that is
/// already in the position it should be in. That is what "the server shut down randomly" is, in
/// every report of it.
/// </para>
/// <para>
/// So the attempt is repeated on a bounded backoff, and when it finally will not come up the last
/// word is an error rather than a warning. That is not cosmetic: the Microsoft.Extensions.Logging
/// bridges crash reporters ship — Sentry's among them — file an event at <c>Error</c> and leave only
/// a breadcrumb at <c>Warning</c>, so a warning here is a server that stopped and told nobody.
/// </para>
/// <para>
/// A newer request supersedes the one in flight rather than queueing behind it. Two connectivity
/// changes a second apart are one event as far as the listener is concerned, and the later one
/// knows more about the network than the one it interrupts.
/// </para>
/// </remarks>
sealed class ServerTransitionRunner(HttpServerLifecycleOptions options, ILogger logger) : IDisposable
{
    readonly object sync = new();

    CancellationTokenSource? current;
    bool disposed;

    /// <summary>
    /// The last run's task.
    /// <para>
    /// Nothing in the app awaits it — the platform callbacks that start these are synchronous and
    /// have nowhere to await — but a test has to know when the retries have finished.
    /// </para>
    /// </summary>
    internal Task Completion { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Starts <paramref name="transition"/> and retries it on the configured backoff. Returns
    /// immediately. <paramref name="operation"/> is what the logs call it.
    /// </summary>
    public void Run(string operation, Func<CancellationToken, Task> transition)
    {
        CancellationTokenSource cancel;

        lock (this.sync)
        {
            if (this.disposed)
                return;

            // Superseded sources are cancelled and then left to the GC rather than disposed — the
            // same call the core server makes with its own. The loop being cancelled is still
            // holding the token, and disposing the source under it turns an ordinary cancellation
            // into an ObjectDisposedException on a thread with nobody to report it to.
            this.current?.Cancel();
            this.current = cancel = new CancellationTokenSource();
        }

        this.Completion = Task.Run(() => this.RunAsync(operation, transition, cancel.Token));
    }

    /// <summary>
    /// Abandons whatever is in flight.
    /// <para>
    /// For a stop the app asked for: a retry that is still trying to bring the listener up would
    /// otherwise undo it, and bind a socket for an app that has just decided it does not want one.
    /// </para>
    /// </summary>
    public void Cancel()
    {
        lock (this.sync)
        {
            this.current?.Cancel();
            this.current = null;
        }
    }

    public void Dispose()
    {
        lock (this.sync)
        {
            this.disposed = true;
            this.current?.Cancel();
            this.current = null;
        }
    }

    async Task RunAsync(string operation, Func<CancellationToken, Task> transition, CancellationToken cancellationToken)
    {
        try
        {
            var attempts = Math.Max(1, options.RestartAttempts);
            var delay = Clamp(options.RestartRetryDelay);

            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await transition(cancellationToken).ConfigureAwait(false);

                    if (attempt > 1)
                        logger.LogInformation("{Operation} succeeded on attempt {Attempt}", operation, attempt);

                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Superseded by a newer request, or the app is shutting this down. Whatever
                    // cancelled it knows more than the attempt it interrupted, so this is not a
                    // failure and must not be reported as one.
                    logger.LogDebug("{Operation} was abandoned on attempt {Attempt}", operation, attempt);
                    return;
                }
                catch (Exception ex)
                {
                    if (attempt >= attempts)
                    {
                        logger.LogError(
                            ex,
                            "{Operation} failed on all {Attempts} attempt(s). The server is not listening and nothing is scheduled to bring it back — the next connectivity change or foreground transition is the next chance it gets",
                            operation,
                            attempts
                        );
                        return;
                    }

                    logger.LogWarning(
                        ex,
                        "{Operation} failed on attempt {Attempt} of {Attempts}; retrying in {Delay}",
                        operation,
                        attempt,
                        attempts,
                        delay
                    );

                    try
                    {
                        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        logger.LogDebug("{Operation} was abandoned while waiting to retry", operation);
                        return;
                    }

                    // Doubled rather than fixed. The two things that refuse the bind here — an
                    // interface that is not routable yet, and a port the old listener has not
                    // finished letting go of — clear on their own schedule, and neither is helped
                    // by being asked again in 200ms while the phone's radio pays for the attempt.
                    delay = Clamp(TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, options.MaxRestartRetryDelay.Ticks)));
                }
            }
        }
        catch (Exception ex)
        {
            // Nothing awaits this task, so an exception escaping it is an exception reported
            // nowhere — which is the whole failure mode this class exists to close.
            logger.LogError(ex, "The {Operation} retry loop itself failed", operation);
        }
    }

    /// <summary>A misconfigured negative delay is a configuration mistake, not a reason to throw out of a background loop.</summary>
    static TimeSpan Clamp(TimeSpan delay) => delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
}
