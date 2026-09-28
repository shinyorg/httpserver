using System.Net;
using Shiny.Net.HttpServer.OAuthLoopback;

namespace Shiny.Net.HttpServer.Tests;

/// <summary>
/// The loopback redirect receiver, driven the way a browser drives it: a real listener on a real
/// ephemeral port, and an <see cref="HttpClient"/> standing in for the authorization server's
/// redirect.
/// </summary>
public class OAuthLoopbackTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    static HttpClient Browser() => new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };

    [Fact]
    public async Task Binds_an_ephemeral_port_on_the_ip_literal()
    {
        await using var listener = await LoopbackCallbackListener.StartAsync(cancellationToken: Token);

        Assert.Equal("127.0.0.1", listener.RedirectUri.Host);
        Assert.NotEqual(0, listener.RedirectUri.Port);
        Assert.Equal("/callback", listener.RedirectUri.AbsolutePath);
        Assert.False(string.IsNullOrWhiteSpace(listener.State));
    }

    [Fact]
    public async Task Returns_the_code_from_a_query_redirect()
    {
        await using var listener = await LoopbackCallbackListener.StartAsync(cancellationToken: Token);
        using var browser = Browser();

        var wait = listener.WaitAsync(Token);
        var response = await browser.GetAsync($"{listener.RedirectUri}?code=abc123&state={Uri.EscapeDataString(listener.State)}", Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("return to the app", await response.Content.ReadAsStringAsync(Token));

        var result = await wait;
        Assert.False(result.IsError);
        Assert.Equal("abc123", result.Code);
        Assert.Equal(listener.State, result.State);
        Assert.Equal("query", result.ResponseMode);
        Assert.Equal("abc123", System.Web.HttpUtility.ParseQueryString(result.CallbackUri.Query)["code"]);
    }

    [Fact]
    public async Task Accepts_form_post()
    {
        await using var listener = await LoopbackCallbackListener.StartAsync(cancellationToken: Token);
        using var browser = Browser();

        var response = await browser.PostAsync(
            listener.RedirectUri,
            new FormUrlEncodedContent(new Dictionary<string, string> { ["code"] = "xyz", ["state"] = listener.State }),
            Token
        );
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await listener.WaitAsync(Token);
        Assert.Equal("xyz", result.Code);
        Assert.Equal("form_post", result.ResponseMode);
        Assert.Equal("xyz", System.Web.HttpUtility.ParseQueryString(result.CallbackUri.Query)["code"]);
    }

    [Fact]
    public async Task Rejects_form_post_when_disabled()
    {
        await using var listener = await LoopbackCallbackListener.StartAsync(new() { AllowFormPost = false }, Token);
        using var browser = Browser();

        var response = await browser.PostAsync(
            listener.RedirectUri,
            new FormUrlEncodedContent(new Dictionary<string, string> { ["code"] = "xyz", ["state"] = listener.State }),
            Token
        );

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task Provider_errors_are_results_not_exceptions()
    {
        await using var listener = await LoopbackCallbackListener.StartAsync(cancellationToken: Token);
        using var browser = Browser();

        var response = await browser.GetAsync(
            $"{listener.RedirectUri}?error=access_denied&error_description={Uri.EscapeDataString("<b>nope</b>")}&state={Uri.EscapeDataString(listener.State)}",
            Token
        );
        var page = await response.Content.ReadAsStringAsync(Token);
        Assert.Contains("&lt;b&gt;nope&lt;/b&gt;", page);
        Assert.DoesNotContain("<b>nope</b>", page);

        var result = await listener.WaitAsync(Token);
        Assert.True(result.IsError);
        Assert.Equal("access_denied", result.Error);
        Assert.Null(result.Code);
    }

    [Theory]
    [InlineData("?code=abc&state=wrong")]
    [InlineData("?code=abc")]
    public async Task A_mismatched_state_is_rejected_and_the_real_callback_still_completes(string query)
    {
        await using var listener = await LoopbackCallbackListener.StartAsync(cancellationToken: Token);
        using var browser = Browser();

        var wait = listener.WaitAsync(Token);
        var forged = await browser.GetAsync(listener.RedirectUri + query, Token);
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        Assert.False(wait.IsCompleted);

        await browser.GetAsync($"{listener.RedirectUri}?code=real&state={Uri.EscapeDataString(listener.State)}", Token);
        Assert.Equal("real", (await wait).Code);
    }

    [Fact]
    public async Task A_supplied_state_is_used_and_validation_can_be_turned_off()
    {
        await using var listener = await LoopbackCallbackListener.StartAsync(new() { State = "mine", ValidateState = false }, Token);
        using var browser = Browser();

        Assert.Equal("mine", listener.State);
        await browser.GetAsync($"{listener.RedirectUri}?code=abc", Token);
        Assert.Equal("abc", (await listener.WaitAsync(Token)).Code);
    }

    [Fact]
    public async Task Other_paths_are_404_and_do_not_complete()
    {
        await using var listener = await LoopbackCallbackListener.StartAsync(cancellationToken: Token);
        using var browser = Browser();

        var wait = listener.WaitAsync(Token);
        var response = await browser.GetAsync($"http://127.0.0.1:{listener.RedirectUri.Port}/favicon.ico", Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(wait.IsCompleted);
    }

    [Fact]
    public async Task A_second_callback_is_answered_but_does_not_replace_the_first()
    {
        await using var listener = await LoopbackCallbackListener.StartAsync(cancellationToken: Token);
        using var browser = Browser();
        var state = Uri.EscapeDataString(listener.State);

        await browser.GetAsync($"{listener.RedirectUri}?code=first&state={state}", Token);
        var again = await browser.GetAsync($"{listener.RedirectUri}?code=second&state={state}", Token);

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal("first", (await listener.WaitAsync(Token)).Code);
    }

    [Fact]
    public async Task Times_out()
    {
        await using var listener = await LoopbackCallbackListener.StartAsync(new() { Timeout = TimeSpan.FromMilliseconds(100) }, Token);
        await Assert.ThrowsAsync<TimeoutException>(() => listener.WaitAsync(Token));
    }

    [Fact]
    public async Task Cancellation_is_not_reported_as_a_timeout()
    {
        await using var listener = await LoopbackCallbackListener.StartAsync(cancellationToken: Token);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listener.WaitAsync(cts.Token));
    }

    [Fact]
    public async Task Custom_page_and_redirect()
    {
        await using (var listener = await LoopbackCallbackListener.StartAsync(new() { RenderPage = r => $"<p>custom {r.Code}</p>" }, Token))
        {
            using var browser = Browser();
            var response = await browser.GetAsync($"{listener.RedirectUri}?code=c1&state={Uri.EscapeDataString(listener.State)}", Token);
            Assert.Equal("<p>custom c1</p>", await response.Content.ReadAsStringAsync(Token));
        }

        await using (var listener = await LoopbackCallbackListener.StartAsync(new() { RedirectTo = _ => new Uri("https://example.com/signed-in") }, Token))
        {
            using var browser = Browser();
            var response = await browser.GetAsync($"{listener.RedirectUri}?code=c2&state={Uri.EscapeDataString(listener.State)}", Token);
            Assert.Equal(HttpStatusCode.Found, response.StatusCode);
            Assert.Equal("https://example.com/signed-in", response.Headers.Location!.AbsoluteUri);
            Assert.Equal("c2", (await listener.WaitAsync(Token)).Code);
        }
    }

    [Fact]
    public async Task Custom_path_and_ipv6_loopback()
    {
        if (!System.Net.Sockets.Socket.OSSupportsIPv6)
            return;

        await using var listener = await LoopbackCallbackListener.StartAsync(new() { Address = IPAddress.IPv6Loopback, Path = "/auth/done" }, Token);

        Assert.Equal("[::1]", listener.RedirectUri.Host);
        Assert.Equal("/auth/done", listener.RedirectUri.AbsolutePath);

        using var browser = Browser();
        await browser.GetAsync($"{listener.RedirectUri}?code=v6&state={Uri.EscapeDataString(listener.State)}", Token);
        Assert.Equal("v6", (await listener.WaitAsync(Token)).Code);
    }

    [Fact]
    public async Task Refuses_a_non_loopback_address()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => LoopbackCallbackListener.StartAsync(new() { Address = IPAddress.Any }, Token));
    }

    [Fact]
    public async Task The_listener_stops_once_disposed()
    {
        Uri redirect;
        await using (var listener = await LoopbackCallbackListener.StartAsync(cancellationToken: Token))
            redirect = listener.RedirectUri;

        using var browser = Browser();
        await Assert.ThrowsAsync<HttpRequestException>(() => browser.GetAsync(redirect, Token));
    }

    [Fact]
    public async Task AuthorizeAsync_runs_the_whole_flow()
    {
        using var browser = Browser();
        Uri? opened = null;

        var result = await LoopbackCallbackListener.AuthorizeAsync(
            (redirectUri, state) => new Uri($"https://idp.example/authorize?redirect_uri={Uri.EscapeDataString(redirectUri.AbsoluteUri)}&state={Uri.EscapeDataString(state)}"),
            openBrowser: uri =>
            {
                opened = uri;

                // Plays the part of the identity provider: send the browser back to the redirect URI.
                var q = System.Web.HttpUtility.ParseQueryString(uri.Query);
                _ = browser.GetAsync($"{q["redirect_uri"]}?code=flow&state={Uri.EscapeDataString(q["state"]!)}", Token);
            },
            cancellationToken: Token
        );

        Assert.NotNull(opened);
        Assert.Equal("flow", result.Code);
    }
}
