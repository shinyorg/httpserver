using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shiny.Net.HttpServer.Files;

namespace Shiny.Net.HttpServer.OAuthLoopback;

/// <summary>
/// The receiving end of an OAuth 2.0 / OpenID Connect loopback redirect (RFC 8252 §7.3): a
/// one-shot server on an ephemeral <c>127.0.0.1</c> port that waits for the authorization server to
/// send the browser back, hands the app the parameters, and shows the user a page saying they can
/// return to the app.
/// <para>
/// This is only the redirect receiver. Building the authorization URL, PKCE and the token exchange
/// belong to the OAuth client you already use — this gives it a <see cref="RedirectUri"/> and a
/// <see cref="LoopbackCallbackResult"/>.
/// </para>
/// <code>
/// await using var callback = await LoopbackCallbackListener.StartAsync();
/// SystemBrowser.Open(BuildAuthorizeUrl(callback.RedirectUri, callback.State));
/// var result = await callback.WaitAsync();
/// </code>
/// </summary>
public sealed class LoopbackCallbackListener : IAsyncDisposable
{
    const string FormPost = "form_post";
    const string Query = "query";

    readonly HttpServer server;
    readonly LoopbackCallbackOptions options;
    readonly ILogger logger;
    readonly TaskCompletionSource<LoopbackCallbackResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly byte[] expectedState;
    int completed;
    bool disposed;

    LoopbackCallbackListener(LoopbackCallbackOptions options, string state)
    {
        this.options = options;
        this.State = state;
        this.expectedState = Encoding.UTF8.GetBytes(state);

        var loggerFactory = options.LoggerFactory ?? NullLoggerFactory.Instance;
        this.logger = loggerFactory.CreateLogger<LoopbackCallbackListener>();

        this.server = new HttpServer(
            new HttpServerOptions
            {
                Address = options.Address,
                Port = options.Port,

                // A handful of browser connections is all this ever sees. The retry machinery is for
                // servers that must stay up; a sign-in that cannot bind should say so immediately.
                MaxConcurrentConnections = 16,
                StartRetryAttempts = 1,
                ServerHeader = null
            },
            loggerFactory: loggerFactory
        );
        this.server.OnRequest(this.HandleAsync);
    }

    /// <summary>
    /// The redirect URI to register with the provider and put in the authorization request —
    /// <c>http://127.0.0.1:{port}{path}</c>.
    /// </summary>
    public Uri RedirectUri { get; private set; } = null!;

    /// <summary>
    /// The <c>state</c> to send in the authorization request. The callback must echo it back or it
    /// is rejected (unless <see cref="LoopbackCallbackOptions.ValidateState"/> is off).
    /// </summary>
    public string State { get; }

    /// <summary>Binds the listener and returns once <see cref="RedirectUri"/> is known.</summary>
    public static async Task<LoopbackCallbackListener> StartAsync(
        LoopbackCallbackOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        options ??= new LoopbackCallbackOptions();

        if (!IPAddress.IsLoopback(options.Address))
            throw new ArgumentException($"The callback must bind a loopback address; {options.Address} is not one.", nameof(options));

        if (string.IsNullOrEmpty(options.Path) || options.Path[0] != '/')
            throw new ArgumentException("The callback path must start with '/'.", nameof(options));

        if (options.State is { Length: 0 })
            throw new ArgumentException("An empty state protects nothing; leave it null to have one generated.", nameof(options));

        var listener = new LoopbackCallbackListener(options, options.State ?? GenerateState());
        try
        {
            await listener.server.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await listener.server.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        // ListenUrl reports the port actually bound, so an ephemeral port is known from here on.
        listener.RedirectUri = new Uri(listener.server.ListenUrl + options.Path);
        listener.logger.LogDebug("Waiting for the OAuth callback on {RedirectUri}", listener.RedirectUri);
        return listener;
    }

    /// <summary>
    /// Starts a listener, hands its redirect URI and state to <paramref name="buildAuthorizeUri"/>,
    /// opens the result in the system browser and waits for the callback — the whole loopback flow
    /// in one call.
    /// </summary>
    /// <param name="buildAuthorizeUri">Builds the authorization request from the redirect URI and state.</param>
    /// <param name="options">Listener options; the defaults when null.</param>
    /// <param name="openBrowser">Opens the URI; <see cref="SystemBrowser.Open"/> when null.</param>
    /// <param name="cancellationToken">Abandons the sign-in.</param>
    public static async Task<LoopbackCallbackResult> AuthorizeAsync(
        Func<Uri, string, Uri> buildAuthorizeUri,
        LoopbackCallbackOptions? options = null,
        Action<Uri>? openBrowser = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(buildAuthorizeUri);

        await using var listener = await StartAsync(options, cancellationToken).ConfigureAwait(false);
        (openBrowser ?? SystemBrowser.Open)(buildAuthorizeUri(listener.RedirectUri, listener.State));
        return await listener.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for the callback. Completes with the provider's answer — success or an OAuth error —
    /// and throws <see cref="TimeoutException"/> when <see cref="LoopbackCallbackOptions.Timeout"/>
    /// passes first, or <see cref="OperationCanceledException"/> when cancelled.
    /// </summary>
    public async Task<LoopbackCallbackResult> WaitAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (this.options.Timeout is { } limit)
            timeout.CancelAfter(limit);

        try
        {
            return await this.completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"No OAuth callback arrived at {this.RedirectUri} within {this.options.Timeout}.");
        }
    }

    /// <summary>Stops the server. In-flight responses — the completion page — are allowed to finish.</summary>
    public async ValueTask DisposeAsync()
    {
        if (this.disposed)
            return;

        this.disposed = true;

        // Bounded: a browser holding a keep-alive connection open must not hold up the app.
        using var drain = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await this.server.StopAsync(drain.Token).ConfigureAwait(false);
        await this.server.DisposeAsync().ConfigureAwait(false);
    }

    async Task HandleAsync(HttpContext context)
    {
        var request = context.Request;
        var response = context.Response;
        response.Headers.Set(HeaderNames.CacheControl, "no-store");

        if (!string.Equals(request.Path, this.options.Path, StringComparison.Ordinal))
        {
            // favicon.ico, a stray probe — nothing but the redirect path is served.
            response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        Dictionary<string, string> parameters;
        string responseMode;

        if (HttpMethods.IsGet(request.Method))
        {
            parameters = new(StringComparer.Ordinal);
            foreach (var (key, value) in request.Query)
                parameters.TryAdd(key, value.ToString());

            responseMode = Query;
        }
        else if (this.options.AllowFormPost && HttpMethods.IsPost(request.Method) && request.HasFormContentType())
        {
            var form = await request.ReadFormAsync(cancellationToken: context.RequestAborted).ConfigureAwait(false);
            parameters = new(StringComparer.Ordinal);
            foreach (var key in form.Keys)
                parameters.TryAdd(key, form[key].ToString());

            responseMode = FormPost;
        }
        else
        {
            response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            response.Headers.Set(HeaderNames.Allow, this.options.AllowFormPost ? "GET, POST" : "GET");
            return;
        }

        if (this.options.ValidateState && !this.IsExpectedState(parameters.GetValueOrDefault("state")))
        {
            // Not the callback this sign-in started. Rejected rather than completing with an error,
            // so a forged request cannot end the flow the real redirect is still on its way to.
            this.logger.LogWarning("Rejected an OAuth callback whose state did not match the request");
            response.StatusCode = StatusCodes.Status400BadRequest;
            await response.WriteAsync(RenderMessage("Sign-in failed", "This response does not belong to the sign-in in progress."), "text/html; charset=utf-8").ConfigureAwait(false);
            return;
        }

        var result = new LoopbackCallbackResult(parameters, responseMode, this.BuildCallbackUri(request, parameters, responseMode));

        if (Interlocked.Exchange(ref this.completed, 1) == 1)
        {
            // A refresh of the completion page, or the browser replaying the redirect.
            await response.WriteAsync(RenderMessage("Already signed in", "You can close this window and return to the app."), "text/html; charset=utf-8").ConfigureAwait(false);
            return;
        }

        try
        {
            if (this.options.RedirectTo?.Invoke(result) is { } redirect)
            {
                response.Redirect(redirect.AbsoluteUri);
            }
            else
            {
                var html = this.options.RenderPage?.Invoke(result) ?? RenderDefaultPage(result);
                await response.WriteAsync(html, "text/html; charset=utf-8").ConfigureAwait(false);
            }
        }
        finally
        {
            // Completed after the page is written, so an app that disposes the listener the moment
            // WaitAsync returns still gets the page to the browser. A page that failed to render is
            // not a reason to lose the code.
            this.completion.TrySetResult(result);
        }
    }

    bool IsExpectedState(string? state)
        => state is not null
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), this.expectedState);

    Uri BuildCallbackUri(HttpRequest request, Dictionary<string, string> parameters, string responseMode)
    {
        var query = responseMode == Query
            ? request.QueryString
            : "?" + string.Join('&', parameters.Select(x => $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value)}"));

        return new Uri(this.RedirectUri.GetLeftPart(UriPartial.Path) + query);
    }

    static string GenerateState()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    static string RenderDefaultPage(LoopbackCallbackResult result)
        => result.IsError
            ? RenderMessage("Sign-in failed", result.ErrorDescription ?? result.Error!)
            : RenderMessage("Signed in", "You can close this window and return to the app.");

    static string RenderMessage(string title, string message)
    {
        title = WebUtility.HtmlEncode(title);
        message = WebUtility.HtmlEncode(message);

        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{title}}</title>
            <style>
            body { font-family: system-ui, sans-serif; display: grid; place-items: center; min-height: 100vh; margin: 0; background: #f6f7f9; color: #1d1f23; }
            main { text-align: center; padding: 2rem; }
            @media (prefers-color-scheme: dark) { body { background: #16181c; color: #e7e9ec; } }
            </style>
            </head>
            <body><main><h1>{{title}}</h1><p>{{message}}</p></main></body>
            </html>
            """;
    }
}
