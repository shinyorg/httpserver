using System.Security.Claims;
using Shiny.Net.HttpServer.Security;

namespace Shiny.Net.HttpServer.Tests;

public class AntiforgeryHardeningTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    static readonly byte[] Key = Enumerable.Repeat((byte)7, 64).ToArray();

    static HttpContext Context(string? user = null, bool https = false, string? cookie = null)
    {
        var context = new HttpContext();
        context.Request.Scheme = https ? "https" : "http";

        if (user is not null)
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, user)], "Test"));

        if (cookie is not null)
            context.Request.Cookies.SetRaw(cookie);

        return context;
    }

    static (string Cookie, string RequestToken) Issue(Antiforgery antiforgery, string? user = null, bool https = false)
    {
        var context = Context(user, https);
        var tokens = antiforgery.GetTokens(context);
        var setCookie = context.Response.Headers.GetFirst(HeaderNames.SetCookie)!;

        return (setCookie.Split(';')[0], tokens.RequestToken);
    }

    // The token used to be stamped with a fixed window (now / lifetime), so it died at the next window
    // boundary — with a 2s lifetime, often within a second of being issued.
    [Fact]
    public async Task A_token_lives_its_whole_lifetime_from_when_it_was_issued()
    {
        var antiforgery = new Antiforgery(new AntiforgeryOptions { Key = Key, Lifetime = TimeSpan.FromSeconds(3) });

        for (var i = 0; i < 3; i++)
        {
            var (cookie, token) = Issue(antiforgery);
            await Task.Delay(1100, Token);

            Assert.True(antiforgery.ValidateToken(Context(cookie: cookie), token));
        }
    }

    [Fact]
    public async Task A_token_expires_after_its_lifetime()
    {
        var antiforgery = new Antiforgery(new AntiforgeryOptions { Key = Key, Lifetime = TimeSpan.FromSeconds(1) });
        var (cookie, token) = Issue(antiforgery);

        await Task.Delay(2200, Token);

        Assert.False(antiforgery.ValidateToken(Context(cookie: cookie), token));
    }

    [Fact]
    public void An_edited_issue_time_does_not_validate()
    {
        var antiforgery = new Antiforgery(new AntiforgeryOptions { Key = Key });
        var (cookie, token) = Issue(antiforgery);

        var dot = token.IndexOf('.');
        var edited = (long.Parse(token[..dot], System.Globalization.CultureInfo.InvariantCulture) + 1) + token[dot..];

        Assert.False(antiforgery.ValidateToken(Context(cookie: cookie), edited));
    }

    [Fact]
    public void A_token_is_bound_to_the_user_it_was_issued_to()
    {
        var antiforgery = new Antiforgery(new AntiforgeryOptions { Key = Key });
        var (cookie, token) = Issue(antiforgery, user: "alice");

        Assert.True(antiforgery.ValidateToken(Context("alice", cookie: cookie), token));
        Assert.False(antiforgery.ValidateToken(Context("bob", cookie: cookie), token));
        Assert.False(antiforgery.ValidateToken(Context(cookie: cookie), token));

        // And an anonymous visitor's token is no good once someone is signed in.
        var (anonCookie, anonToken) = Issue(antiforgery);
        Assert.False(antiforgery.ValidateToken(Context("alice", cookie: anonCookie), anonToken));
    }

    [Fact]
    public void Over_https_the_cookie_is_secure_and_host_prefixed()
    {
        var antiforgery = new Antiforgery(new AntiforgeryOptions { Key = Key });
        var context = Context(https: true);
        antiforgery.GetTokens(context);

        var setCookie = context.Response.Headers.GetFirst(HeaderNames.SetCookie)!;
        Assert.StartsWith("__Host-shiny.antiforgery=", setCookie, StringComparison.Ordinal);
        Assert.Contains("secure", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Over_plain_http_the_cookie_keeps_its_name_and_is_not_secure()
    {
        var antiforgery = new Antiforgery(new AntiforgeryOptions { Key = Key });
        var (cookie, token) = Issue(antiforgery);

        Assert.StartsWith("shiny.antiforgery=", cookie, StringComparison.Ordinal);
        Assert.True(antiforgery.ValidateToken(Context(cookie: cookie), token));
    }

    [Fact]
    public void A_cookie_without_the_prefix_does_not_count_over_https()
    {
        var antiforgery = new Antiforgery(new AntiforgeryOptions { Key = Key });

        // Planted from a plain-HTTP page or a sibling subdomain: no __Host- prefix.
        var (cookie, token) = Issue(antiforgery);

        Assert.False(antiforgery.ValidateToken(Context(https: true, cookie: cookie), token));
    }
}
