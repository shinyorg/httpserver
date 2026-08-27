using Shiny.Hosting;

namespace Shiny.Net.HttpServer.Mobile;

public partial class HttpServerLifecycleTask
{
    /// <summary>A foreground service keeps the process and its listener alive, so nothing needs restoring on resume.</summary>
    static partial bool SupportsBackgroundExecution => true;

    /// <summary>Set by the service itself as Android brings it up and tears it down, so this is the fact rather than the intent.</summary>
    static partial bool BackgroundExecutionIsActive => HttpServerForegroundService.IsStarted;

    partial void StartBackgroundExecutionPlatform()
    {
        if (HttpServerForegroundService.IsStarted)
            return;

        Host.Platform.StartService(typeof(HttpServerForegroundService));
    }

    partial void StopBackgroundExecutionPlatform()
    {
        if (!HttpServerForegroundService.IsStarted)
            return;

        Host.Platform.StopService(typeof(HttpServerForegroundService));
    }
}
