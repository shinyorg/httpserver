using System.Net;
using Microsoft.Extensions.Logging;

namespace Shiny.Net.HttpServer.OAuthLoopback;

/// <summary>Configures a <see cref="LoopbackCallbackListener"/>.</summary>
public sealed class LoopbackCallbackOptions
{
    /// <summary>
    /// The address to bind. <see cref="IPAddress.Loopback"/> by default, or
    /// <see cref="IPAddress.IPv6Loopback"/>. Only loopback addresses are accepted: RFC 8252 §8.3
    /// asks for the IP literal rather than <c>localhost</c>, which can resolve to the other family
    /// or be captured by a firewall, and a callback bound to a routable address is one any machine
    /// on the network can race.
    /// </summary>
    public IPAddress Address { get; set; } = IPAddress.Loopback;

    /// <summary>
    /// The port to bind. 0 (the default) takes an ephemeral port, which is what RFC 8252 §7.3 asks
    /// of the client and every provider that follows it must accept. Set a fixed port only for a
    /// provider that insists on an exact redirect URI.
    /// </summary>
    public int Port { get; set; }

    /// <summary>The path of the redirect URI. Anything else is answered with 404.</summary>
    public string Path { get; set; } = "/callback";

    /// <summary>
    /// How long <see cref="LoopbackCallbackListener.WaitAsync"/> waits before giving up with a
    /// <see cref="TimeoutException"/>. The user closing the browser tab is indistinguishable from
    /// the user still typing a password, so this is the only way that case ends. Null waits forever.
    /// </summary>
    public TimeSpan? Timeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The <c>state</c> value to put in the authorization request. Null (the default) generates a
    /// random one, exposed as <see cref="LoopbackCallbackListener.State"/>.
    /// </summary>
    public string? State { get; set; }

    /// <summary>
    /// When true (the default), a callback whose <c>state</c> is missing or does not match is
    /// answered with 400 and ignored, and the listener keeps waiting for the real one. This is the
    /// CSRF protection of RFC 6749 §10.12; turn it off only for a provider that does not echo
    /// <c>state</c>.
    /// </summary>
    public bool ValidateState { get; set; } = true;

    /// <summary>
    /// When true (the default), a POST to the callback path with a form body is accepted as well as
    /// a GET — the <c>response_mode=form_post</c> shape, which keeps the code out of the browser's
    /// history.
    /// </summary>
    public bool AllowFormPost { get; set; } = true;

    /// <summary>
    /// Renders the page the browser shows once the callback arrives. Null uses a small built-in
    /// page that tells the user to return to the app. The string is sent as <c>text/html</c>, so
    /// anything taken from the result must be HTML-encoded.
    /// </summary>
    public Func<LoopbackCallbackResult, string>? RenderPage { get; set; }

    /// <summary>
    /// When set and returning a URI, the browser is redirected there instead of being shown
    /// <see cref="RenderPage"/> — for an app that has a hosted "you are signed in" page.
    /// </summary>
    public Func<LoopbackCallbackResult, Uri?>? RedirectTo { get; set; }

    /// <summary>Logging for the listener and its server. Null logs nothing.</summary>
    public ILoggerFactory? LoggerFactory { get; set; }
}
