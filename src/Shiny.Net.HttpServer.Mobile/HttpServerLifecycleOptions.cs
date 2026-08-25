namespace Shiny.Net.HttpServer.Mobile;

/// <summary>What happens to the server when the app leaves the foreground.</summary>
public enum BackgroundServerMode
{
    /// <summary>
    /// Stop the server on background and start it again on resume.
    /// <para>
    /// The honest default. On iOS the process is suspended within seconds of backgrounding and the
    /// listener stops answering whatever you do — so a server that is left "running" is one that
    /// looks fine in your code and refuses connections in reality. Stopping it makes the state
    /// visible: <see cref="HttpServer.IsRunning"/> is false, the UI can say so, and clients get a
    /// refused connection instead of a hang.
    /// </para>
    /// </summary>
    Stop,

    /// <summary>
    /// Keep serving in the background where the platform allows it.
    /// <para>
    /// On Android this starts a foreground service, which is the only supported way to keep a
    /// socket answering with the app in the background — and it means a permanent notification,
    /// which is the deal Android offers.
    /// </para>
    /// <para>
    /// On iOS nothing keeps the listener open, so this means the other half of the promise: the
    /// server is left running as the app goes away — a few seconds of background execution is
    /// sometimes exactly enough to finish the request in flight — and it is <b>restarted when the
    /// app comes back</b>, because the suspension took the socket while leaving
    /// <see cref="HttpServer.IsRunning"/> saying otherwise. A server that was serving when the user
    /// switched away is serving again by the time they are looking at the app. One that was
    /// switched off before they left stays off; this is not
    /// <see cref="HttpServerLifecycleOptions.AlwaysStartOnForeground"/>.
    /// </para>
    /// <para>
    /// Both platforms follow the server, not only the app's transitions: on Android, stop the server
    /// while the app is backgrounded and the notification goes with it rather than claiming to serve
    /// nothing, and start it while backgrounded and the service comes up rather than leaving the
    /// process to be reclaimed; on iOS the same transitions decide whether the resume restores it.
    /// Nothing to call for either.
    /// </para>
    /// </summary>
    KeepAlive,

    /// <summary>
    /// Do nothing. For an app that manages the server itself and only wants the network-change
    /// handling.
    /// </summary>
    Leave
}

/// <summary>How the server should follow the app's lifecycle.</summary>
public sealed class HttpServerLifecycleOptions
{
    /// <summary>What to do when the app is backgrounded. <see cref="BackgroundServerMode.Stop"/> by default.</summary>
    public BackgroundServerMode BackgroundMode { get; set; } = BackgroundServerMode.Stop;

    /// <summary>
    /// Starts the server when the app comes to the foreground, even if it was not the lifecycle
    /// that stopped it.
    /// <para>
    /// Off by default: a server the user switched off with a toggle should stay off, and an app
    /// that turns it back on every time the user opens the app is an app that ignores them.
    /// </para>
    /// </summary>
    public bool AlwaysStartOnForeground { get; set; }

    /// <summary>
    /// Restarts the server when the device's connectivity changes — Wi-Fi to cellular, one network
    /// to another, a hotspot coming up.
    /// <para>
    /// On by default here, unlike the core option it complements. A phone's addresses change as a
    /// matter of course, and a listener bound to the address it had at breakfast is the single most
    /// common way an embedded server "stops working for no reason".
    /// </para>
    /// </summary>
    public bool RestartOnConnectivityChange { get; set; } = true;

    /// <summary>
    /// The title on the Android foreground-service notification, when
    /// <see cref="BackgroundMode"/> is <see cref="BackgroundServerMode.KeepAlive"/>.
    /// </summary>
    public string NotificationTitle { get; set; } = "Server running";

    /// <summary>The body on that notification. Say what it is for — the user is going to see it all day.</summary>
    public string NotificationMessage { get; set; } = "Serving on the local network";
}
