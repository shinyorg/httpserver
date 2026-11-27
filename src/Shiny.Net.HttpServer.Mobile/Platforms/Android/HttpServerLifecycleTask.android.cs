using Shiny.Hosting;

namespace Shiny.Net.HttpServer.Mobile;

public partial class HttpServerLifecycleTask
{
    /// <summary>A foreground service keeps the process and its listener alive, so nothing needs restoring on resume.</summary>
    static partial bool SupportsBackgroundExecution => true;

    /// <summary>Set by the service itself as Android brings it up and tears it down, so this is the fact rather than the intent.</summary>
    static partial bool BackgroundExecutionIsActive => HttpServerForegroundService.IsStarted;

    // Decided against what has been asked for as well as what is up - see the service for why
    // IsStarted alone left the notification up when a foreground followed a background closely.
    partial void StartBackgroundExecutionPlatform()
        => HttpServerForegroundService.RequestStart(static () => Host.Platform.StartService(typeof(HttpServerForegroundService)));

    partial void StopBackgroundExecutionPlatform()
        => HttpServerForegroundService.RequestStop(static () => Host.Platform.StopService(typeof(HttpServerForegroundService)));
}
