using Shiny.Hosting;

namespace Shiny.Net.HttpServer.Mobile;

public partial class HttpServerLifecycleTask
{
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
