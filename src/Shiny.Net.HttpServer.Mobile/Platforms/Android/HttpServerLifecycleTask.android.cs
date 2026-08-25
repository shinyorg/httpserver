using Shiny.Hosting;

namespace Shiny.Net.HttpServer.Mobile;

public partial class HttpServerLifecycleTask
{
    /// <summary>A foreground service keeps the process and its listener alive, so nothing needs restoring on resume.</summary>
    static partial bool SupportsBackgroundExecution => true;

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
