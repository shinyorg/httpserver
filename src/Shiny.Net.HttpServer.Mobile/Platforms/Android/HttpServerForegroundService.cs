using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shiny.Hosting;

namespace Shiny.Net.HttpServer.Mobile;

/// <summary>
/// Keeps the process alive so the listener keeps answering while the app is in the background.
/// <para>
/// Android has exactly one supported way to hold a socket open with the app backgrounded, and this
/// is it: a foreground service with an ongoing notification. There is no quiet version — the
/// notification is the point, because the user is entitled to know something on their phone is
/// still serving the network. Everything else (a background thread, a wake lock, a job) is killed
/// within minutes.
/// </para>
/// <para>
/// The service holds no server state. The <see cref="HttpServer"/> is a singleton in the app's
/// container and keeps listening on its own; all this does is stop Android from reclaiming the
/// process out from under it.
/// </para>
/// </summary>
[Android.App.Service(
    Enabled = true,
    Exported = false,
    ForegroundServiceType = ForegroundService.TypeDataSync
)]
public class HttpServerForegroundService : ShinyAndroidForegroundService
{
    /// <summary>True while the service is holding the process up.</summary>
    public static bool IsStarted { get; private set; }

    // The last start or stop intent posted that the service has not acted on yet, or null when it
    // has caught up. Starting and stopping only post intents - the service comes up or goes down on
    // the main looper some time later - so IsStarted alone answers "is it up now?", not "will it be
    // up once what was asked for lands?". Deciding on IsStarted skipped the stop for a start still on
    // its way, and the service then came up and held its notification over an app in the foreground.
    // Intents to one service are delivered in the order they were posted, so the last one posted is
    // the state the service will settle in.
    static readonly object requestLock = new();
    static bool? pendingStart;

    /// <summary>Posts a start unless the service is up, or on its way up, with no stop behind it.</summary>
    internal static void RequestStart(Action post)
    {
        lock (requestLock)
        {
            // A start that is still pending is posted again rather than trusted: one Android refused
            // inside the service never reaches OnStart, and would otherwise block every later start.
            // A second start intent to a service that is up just rebuilds the same notification.
            if (IsStarted && pendingStart != false)
                return;

            post();
            pendingStart = true;
        }
    }

    /// <summary>Posts a stop if the service is up or on its way up, and no stop is already on its way.</summary>
    internal static void RequestStop(Action post)
    {
        lock (requestLock)
        {
            // A second stop behind the first is worse than redundant: the first destroys the
            // service, and the second, being a start intent with a stop action, creates it again -
            // promoted to the foreground, notification and all - only to stop it.
            if (pendingStart == false)
                return;

            if (pendingStart is null && !IsStarted)
                return;

            post();
            pendingStart = false;
        }
    }

    protected override ForegroundService StartForegroundServiceType => ForegroundService.TypeDataSync;

    protected override void OnStart(Intent? intent)
    {
        lock (requestLock)
        {
            IsStarted = true;

            // Only the start it answers is cleared: a stop posted behind it is still on its way.
            if (pendingStart == true)
                pendingStart = null;
        }

        Log()?.LogInformation("The foreground service is up; the process will be held for as long as it runs");
    }

    protected override void OnStop()
    {
        lock (requestLock)
        {
            IsStarted = false;

            if (pendingStart == false)
                pendingStart = null;
        }

        // The service can go without anyone in this library asking. Android stops it when the user
        // swipes the task away, when the notification permission is revoked, when the OS decides the
        // app has outstayed its welcome. If the server is still listening at that point the two have
        // come apart, and the listener has minutes to live: nothing is holding the process up any
        // more. Nothing here can prevent that - restarting the service against an OS that just
        // stopped it is fighting the user - but it can leave a record, because otherwise this is
        // precisely a server that "stopped randomly" with nothing in the log at the time it stopped.
        try
        {
            if (Host.GetService<HttpServer>() is { IsRunning: true })
            {
                Log()?.LogError(
                    "The foreground service stopped while the server was still listening. Nothing is holding this process up now, so the OS will reclaim it and the listener will stop with it"
                );
            }
        }
        catch (Exception ex)
        {
            // OnStop also runs while the process is being torn down, when the host may already be
            // gone. A diagnostic that throws during a teardown helps nobody.
            Log()?.LogDebug(ex, "Could not check the server's state as the foreground service stopped");
        }
    }

    /// <summary>
    /// Resolved per call rather than held in a field: Android constructs this service, so there is
    /// no constructor to inject into, and the instance outlives no more than the host does anyway.
    /// </summary>
    static ILogger? Log() => Host.GetService<ILoggerFactory>()?.CreateLogger<HttpServerForegroundService>();

    public override IBinder? OnBind(Intent? intent) => null;

    protected override NotificationCompat.Builder CreateNotificationBuilder()
    {
        var builder = base.CreateNotificationBuilder();

        // Resolved rather than injected: Android constructs the service, not the container, so
        // there is no constructor to hand anything to.
        if (Host.GetService<HttpServerLifecycleOptions>() is { } options)
        {
            // Not chained: the AndroidX builder's setters are annotated as returning a nullable
            // builder, so a fluent chain warns on every link.
            builder.SetContentTitle(options.NotificationTitle);
            builder.SetContentText(options.NotificationMessage);
        }

        return builder;
    }
}
