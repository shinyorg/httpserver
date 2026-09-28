namespace Shiny.Net.HttpServer.OAuthLoopback;

/// <summary>
/// What the authorization server sent back to the redirect URI. A provider that refuses — the user
/// pressed Cancel, the client is misconfigured — is a result with <see cref="IsError"/> set, not an
/// exception: it is an answer, and the app decides what to tell the user.
/// </summary>
public sealed class LoopbackCallbackResult
{
    internal LoopbackCallbackResult(IReadOnlyDictionary<string, string> parameters, string responseMode, Uri callbackUri)
    {
        this.Parameters = parameters;
        this.ResponseMode = responseMode;
        this.CallbackUri = callbackUri;
    }

    /// <summary>Every parameter received, from the query string or the form body.</summary>
    public IReadOnlyDictionary<string, string> Parameters { get; }

    /// <summary><c>query</c> for a GET redirect, <c>form_post</c> for a POSTed form.</summary>
    public string ResponseMode { get; }

    /// <summary>
    /// The URI the browser requested, query string included — what an OIDC client that parses the
    /// response itself (Duende's <c>BrowserResult.Response</c>, for one) wants handed to it. For a
    /// form_post the parameters are appended as a query string so it is still one parseable URI.
    /// </summary>
    public Uri CallbackUri { get; }

    /// <summary>The authorization code, when the flow succeeded.</summary>
    public string? Code => this.Get("code");

    /// <summary>The <c>state</c> echoed back.</summary>
    public string? State => this.Get("state");

    /// <summary>The OAuth error code (<c>access_denied</c>, …), when the flow failed.</summary>
    public string? Error => this.Get("error");

    public string? ErrorDescription => this.Get("error_description");

    public string? ErrorUri => this.Get("error_uri");

    /// <summary>True when the provider returned <c>error</c> rather than a code.</summary>
    public bool IsError => this.Error is not null;

    /// <summary>A parameter by name, or null.</summary>
    public string? Get(string name) => this.Parameters.TryGetValue(name, out var value) ? value : null;
}
