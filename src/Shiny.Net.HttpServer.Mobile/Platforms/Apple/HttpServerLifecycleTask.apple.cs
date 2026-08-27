namespace Shiny.Net.HttpServer.Mobile;

public partial class HttpServerLifecycleTask
{
    /// <summary>
    /// No, and there is no version of iOS where the answer changes.
    /// <para>
    /// So <see cref="BackgroundServerMode.KeepAlive"/> means something different here than it does
    /// on Android: not "keep it running", which is not on offer, but "put it back on resume". The
    /// server that was serving when the user switched away is serving again by the time they are
    /// looking at the app, without the app having to notice the suspension or hold the flag itself.
    /// </para>
    /// </summary>
    static partial bool SupportsBackgroundExecution => false;

    /// <summary>Nothing is ever started here, so nothing is ever active. Never consulted — the caller checks <see cref="SupportsBackgroundExecution"/> first.</summary>
    static partial bool BackgroundExecutionIsActive => false;

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
