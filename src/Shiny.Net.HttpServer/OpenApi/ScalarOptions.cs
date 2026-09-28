using System.Text.Json.Nodes;

namespace Shiny.Net.HttpServer.OpenApi;

/// <summary>
/// How <see cref="HttpServerScalarExtensions.MapScalarApiReference"/> renders the
/// <see href="https://scalar.com">Scalar</see> API reference. Names follow Scalar's own configuration
/// keys, so its documentation applies directly.
/// <para>
/// Every setting left null is left out of the configuration, which means Scalar's own default applies
/// — this page does not second-guess them, and a later Scalar release that changes one is picked up.
/// </para>
/// </summary>
public sealed class ScalarOptions
{
    /// <summary>
    /// Where Scalar loads the reference script from. The CDN's latest standalone build by default,
    /// which is what Scalar recommends.
    /// <para>
    /// The script is loaded by the <em>browser</em>, not by the server, so a page viewed on a network
    /// with no internet — a phone and a laptop on the same Wi-Fi, a lab LAN — needs a copy served
    /// from here: download the standalone browser build of <c>@scalar/api-reference</c>
    /// (<c>dist/browser/standalone.js</c>), serve it as a static file, and point this at it. Pin a
    /// version by adding it to the CDN URL: <c>…/@scalar/api-reference@&lt;version&gt;</c>.
    /// </para>
    /// </summary>
    public string ScriptUrl { get; set; } = DefaultScriptUrl;

    /// <summary>The CDN script <see cref="ScriptUrl"/> starts as.</summary>
    public const string DefaultScriptUrl = "https://cdn.jsdelivr.net/npm/@scalar/api-reference";

    /// <summary>The page's <c>&lt;title&gt;</c>.</summary>
    public string Title { get; set; } = "API Reference";

    /// <summary>
    /// The OpenAPI document to show — the pattern given to <c>MapOpenApi</c>. A pattern containing
    /// <c>{documentName}</c> lists one document per API version, newest selected first, the same
    /// documents <c>MapOpenApi("/openapi/{documentName}.json")</c> serves. Ignored when
    /// <see cref="Documents"/> has entries.
    /// <para>
    /// Written into the page as it is, so a root-relative path (the default) resolves against the
    /// host the browser used — through a tunnel as well as on the LAN. Behind a proxy that mounts the
    /// server under a path prefix, give the prefixed path.
    /// </para>
    /// </summary>
    public string DocumentUrl { get; set; } = "/openapi.json";

    /// <summary>
    /// Explicit documents, for anything <see cref="DocumentUrl"/> cannot describe — several
    /// unrelated APIs, or a document hosted elsewhere. Scalar shows a picker when there is more
    /// than one.
    /// </summary>
    public IList<ScalarDocument> Documents { get; } = new List<ScalarDocument>();

    /// <summary>Adds an entry to <see cref="Documents"/>.</summary>
    public ScalarOptions AddDocument(string url, string? title = null, string? slug = null, bool isDefault = false)
    {
        this.Documents.Add(new ScalarDocument { Url = url, Title = title, Slug = slug, IsDefault = isDefault });
        return this;
    }

    /// <summary><c>theme</c>. Null is Scalar's default.</summary>
    public ScalarTheme? Theme { get; set; }

    /// <summary><c>layout</c>. Null is Scalar's default (modern).</summary>
    public ScalarLayout? Layout { get; set; }

    /// <summary><c>darkMode</c> — the initial state of the toggle.</summary>
    public bool? DarkMode { get; set; }

    /// <summary><c>forceDarkModeState</c> — pins one mode regardless of the toggle.</summary>
    public ScalarColorMode? ForceColorMode { get; set; }

    /// <summary><c>hideDarkModeToggle</c>.</summary>
    public bool? HideDarkModeToggle { get; set; }

    /// <summary><c>hideModels</c> — hides the schemas section.</summary>
    public bool? HideModels { get; set; }

    /// <summary><c>hideClientButton</c>.</summary>
    public bool? HideClientButton { get; set; }

    /// <summary><c>showSidebar</c>.</summary>
    public bool? ShowSidebar { get; set; }

    /// <summary><c>defaultOpenAllTags</c> — expands every tag on load.</summary>
    public bool? DefaultOpenAllTags { get; set; }

    /// <summary><c>withDefaultFonts</c>. False stops the page fetching Scalar's fonts.</summary>
    public bool? WithDefaultFonts { get; set; }

    /// <summary>
    /// <c>defaultHttpClient</c> — the code sample shown first, as Scalar's target and client keys.
    /// <c>("csharp", "httpclient")</c> suits a .NET audience; Scalar's own default is curl.
    /// </summary>
    public (string TargetKey, string ClientKey)? DefaultHttpClient { get; set; }

    /// <summary><c>authentication.preferredSecurityScheme</c> — a scheme name from the document.</summary>
    public string? PreferredSecurityScheme { get; set; }

    /// <summary><c>customCss</c>.</summary>
    public string? CustomCss { get; set; }

    /// <summary><c>favicon</c>.</summary>
    public string? Favicon { get; set; }

    /// <summary><c>searchHotKey</c> — the key that, with Ctrl/⌘, opens search.</summary>
    public string? SearchHotKey { get; set; }

    /// <summary>
    /// <c>proxyUrl</c>. Requests from the "try it" client are made by the browser, so they are
    /// subject to CORS; a proxy such as Scalar's avoids that for an API on another origin. Not
    /// needed when the reference is served by the same server as the API.
    /// </summary>
    public string? ProxyUrl { get; set; }

    /// <summary><c>baseServerURL</c> — resolves relative <c>servers</c> entries in the document.</summary>
    public string? BaseServerUrl { get; set; }

    /// <summary>
    /// Any other configuration key, written as given — for options this class does not name, or
    /// ones a newer Scalar adds. Takes precedence over the properties above.
    /// <code>
    /// o.AdditionalConfiguration["hideSearch"] = true;
    /// o.AdditionalConfiguration["hiddenClients"] = new JsonArray("unirest");
    /// </code>
    /// Never put a secret here: the configuration is part of a page anyone who can load it can read.
    /// </summary>
    public IDictionary<string, JsonNode?> AdditionalConfiguration { get; } = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
}

/// <summary>One entry in Scalar's <c>sources</c>.</summary>
public sealed class ScalarDocument
{
    /// <summary>Where the OpenAPI document is.</summary>
    public required string Url { get; init; }

    /// <summary>The name shown in the picker.</summary>
    public string? Title { get; init; }

    /// <summary>The name in the page's URL when this document is selected.</summary>
    public string? Slug { get; init; }

    /// <summary>Selected when the page opens.</summary>
    public bool IsDefault { get; init; }
}

/// <summary>Scalar's built-in themes.</summary>
public enum ScalarTheme
{
    Default,
    Alternate,
    Moon,
    Purple,
    Solarized,
    BluePlanet,
    Saturn,
    Kepler,
    Mars,
    DeepSpace,
    Laserwave,

    /// <summary>No theme — for a page styled entirely by <see cref="ScalarOptions.CustomCss"/>.</summary>
    None
}

/// <summary>Scalar's page layouts.</summary>
public enum ScalarLayout
{
    Modern,
    Classic
}

/// <summary>A colour mode to force.</summary>
public enum ScalarColorMode
{
    Dark,
    Light
}
