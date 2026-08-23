using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;
using Microsoft.Extensions.DependencyInjection;
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

    protected override ForegroundService StartForegroundServiceType => ForegroundService.TypeDataSync;

    protected override void OnStart(Intent? intent) => IsStarted = true;

    protected override void OnStop() => IsStarted = false;

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
