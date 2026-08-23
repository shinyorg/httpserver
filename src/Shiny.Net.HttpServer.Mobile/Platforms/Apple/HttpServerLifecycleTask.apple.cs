namespace Shiny.Net.HttpServer.Mobile;

public partial class HttpServerLifecycleTask
{
    /// <summary>
    /// Nothing to start.
    /// <para>
    /// iOS has no equivalent of a foreground service. The background modes that exist — audio,
    /// location, VoIP, external accessory — are for apps that do those things, and using one to
    /// hold a listener open is both a rejection at review and a lie to the user about what the app
    /// is doing. <c>beginBackgroundTask</c> buys seconds, not minutes, and the request in flight
    /// has usually finished inside them anyway.
    /// </para>
    /// <para>
    /// So on Apple platforms a backgrounded server stops answering, and the useful thing this
    /// package does is make that visible and recover from it cleanly on resume.
    /// </para>
    /// </summary>
    partial void StartBackgroundExecutionPlatform()
    {
    }

    partial void StopBackgroundExecutionPlatform()
    {
    }
}
