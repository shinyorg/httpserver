namespace Shiny.Net.HttpServer;

/// <summary>
/// Where the server is in its lifecycle.
/// <para>
/// The transitional states exist because starting and stopping are not instant — binding a port can
/// fail, and stopping waits for in-flight requests. An app with a toggle needs somewhere to put
/// "working on it" other than a guess.
/// </para>
/// </summary>
public enum HttpServerState
{
    /// <summary>Not listening. Routes can still be registered, and tunnelled connections still served.</summary>
    Stopped,

    /// <summary>Binding the listener. Moves to <see cref="Running"/>, or back to <see cref="Stopped"/> if the bind fails.</summary>
    Starting,

    /// <summary>Listening and accepting.</summary>
    Running,

    /// <summary>Listener unbound; waiting for in-flight requests to finish.</summary>
    Stopping
}
