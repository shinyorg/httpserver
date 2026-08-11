using System.Net;
using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Shiny.Net.HttpServer.Security;

namespace Shiny.Net.HttpServer.Tests;

public class TicketProtectorTests
{
    static readonly byte[] Key = TicketProtector.CreateKey();

    static AuthenticationTicket Ticket(string name = "ada", params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, name) };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));

        var now = DateTimeOffset.UtcNow;

        return new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity(claims, "Cookies", ClaimTypes.Name, ClaimTypes.Role)),
            now,
            now.AddHours(1)
        );
    }

    [Fact]
    public void Round_trips_a_principal()
    {
        var protector = new TicketProtector(Key);
        var restored = protector.Unprotect(protector.Protect(Ticket("ada", "admin", "editor")));

        Assert.NotNull(restored);
        Assert.Equal("ada", restored.Principal.Identity?.Name);
        Assert.True(restored.Principal.IsInRole("admin"));
        Assert.True(restored.Principal.IsInRole("editor"));
        Assert.True(restored.Principal.Identity?.IsAuthenticated);
    }

    [Fact]
    public void Preserves_claim_type_and_issuer()
    {
        var protector = new TicketProtector(Key);
        var now = DateTimeOffset.UtcNow;

        var ticket = new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("tenant", "acme", ClaimValueTypes.String, "https://issuer.example")],
                "Cookies"
            )),
            now,
            now.AddHours(1)
        );

        var claim = protector.Unprotect(protector.Protect(ticket))!.Principal.FindFirst("tenant");

        Assert.NotNull(claim);
        Assert.Equal("acme", claim.Value);
        Assert.Equal("https://issuer.example", claim.Issuer);
    }

    /// <summary>The whole point: the client holds it, so the client must not be able to edit it.</summary>
    [Fact]
    public void Rejects_a_tampered_ticket()
    {
        var protector = new TicketProtector(Key);
        var value = protector.Protect(Ticket());

        // Flip one character of the payload, past the version and key id.
        var chars = value.ToCharArray();
        chars[^1] = chars[^1] == 'A' ? 'B' : 'A';

        Assert.Null(protector.Unprotect(new string(chars)));
    }

    [Fact]
    public void Rejects_a_ticket_from_another_key()
    {
        var mine = new TicketProtector(Key);
        var theirs = new TicketProtector(TicketProtector.CreateKey());

        Assert.Null(mine.Unprotect(theirs.Protect(Ticket())));
    }

    /// <summary>
    /// A rotation issues under the new key while old cookies keep working — otherwise every user is
    /// signed out the moment the key changes.
    /// </summary>
    [Fact]
    public void Still_reads_a_ticket_issued_under_a_retired_key()
    {
        var old = TicketProtector.CreateKey();
        var issued = new TicketProtector(old).Protect(Ticket("ada"));

        var rotated = new TicketProtector(TicketProtector.CreateKey(), old);
        var restored = rotated.Unprotect(issued);

        Assert.NotNull(restored);
        Assert.Equal("ada", restored.Principal.Identity?.Name);
    }

    [Fact]
    public void Does_not_read_a_ticket_from_a_key_that_was_dropped()
    {
        var dropped = TicketProtector.CreateKey();
        var issued = new TicketProtector(dropped).Protect(Ticket());

        Assert.Null(new TicketProtector(TicketProtector.CreateKey()).Unprotect(issued));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-base64!!")]
    [InlineData("aGVsbG8")]
    public void Returns_null_for_anything_that_is_not_a_ticket(string value)
        => Assert.Null(new TicketProtector(Key).Unprotect(value));

    /// <summary>The ciphertext must not be the claims in a thin disguise.</summary>
    [Fact]
    public void Does_not_leave_claims_readable()
    {
        var value = new TicketProtector(Key).Protect(Ticket("ada-lovelace"));

        Assert.DoesNotContain("ada-lovelace", value, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Derives_a_usable_protector_from_a_passphrase()
    {
        var protector = TicketProtector.FromSecret("correct horse battery staple");

        Assert.Equal("ada", protector.Unprotect(protector.Protect(Ticket()))!.Principal.Identity?.Name);
    }
}

public class CookieAuthenticationTests
{
    static readonly byte[] Key = TicketProtector.CreateKey();

    static Task<TestServer> StartAsync(Action<CookieAuthenticationOptions>? extra = null) => TestServer.StartAsync(
        app =>
        {
            app.UseAuthentication();
            app.UseAuthorization();

            app.MapPost("/login", async ctx =>
            {
                var user = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.Name, "ada"), new Claim(ClaimTypes.Role, "admin")],
                    "Cookies",
                    ClaimTypes.Name,
                    ClaimTypes.Role
                ));

                await ctx.SignInAsync(user);
                await ctx.Response.WriteTextAsync("signed in");
            });

            app.MapPost("/logout", async ctx =>
            {
                await ctx.SignOutAsync();
                await ctx.Response.WriteTextAsync("signed out");
            });

            app.MapGet("/me", ctx => ctx.Response.WriteTextAsync(ctx.User.Identity?.Name ?? "anonymous"))
                .RequireAuthorization();
        },
        builder =>
        {
            builder.Services.AddAuthentication().AddCookie(o =>
            {
                o.Protector = new TicketProtector(Key);
                extra?.Invoke(o);
            });

            builder.Services.AddAuthorization();
        }
    );

    /// <summary>
    /// A client that does not keep a cookie jar. The default one does, and it would quietly re-add
    /// the valid cookie to a request meant to carry a tampered one — making the test pass for the
    /// wrong reason.
    /// </summary>
    static HttpClient RawClient(TestServer server)
    {
        var handler = new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false };

        return new HttpClient(handler) { BaseAddress = server.Client.BaseAddress };
    }

    static string? CookieFrom(HttpResponseMessage response)
        => response.Headers.TryGetValues("Set-Cookie", out var values) ? values.FirstOrDefault() : null;

    static string ValueOf(string setCookie) => setCookie.Split(';')[0];

    [Fact]
    public async Task Signs_in_and_reads_the_session_back()
    {
        await using var server = await StartAsync();
        using var client = RawClient(server);

        var login = await client.PostAsync("/login", content: null, TestContext.Current.CancellationToken);
        var cookie = CookieFrom(login);

        Assert.NotNull(cookie);
        Assert.StartsWith(".shiny.auth=", cookie, StringComparison.Ordinal);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/me");
        request.Headers.Add("Cookie", ValueOf(cookie));

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ada", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The defaults that matter: a script must not be able to read it, and it must not ride
    /// cross-site requests.
    /// </summary>
    [Fact]
    public async Task Sets_httponly_and_samesite_by_default()
    {
        await using var server = await StartAsync();

        using var client = RawClient(server);

        var cookie = CookieFrom(await client.PostAsync("/login", null, TestContext.Current.CancellationToken))!;

        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Refuses_a_request_with_no_cookie()
    {
        await using var server = await StartAsync();

        var response = await server.Client.GetAsync("/me", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Signing_out_clears_the_cookie()
    {
        await using var server = await StartAsync();

        using var client = RawClient(server);

        var cookie = ValueOf(CookieFrom(await client.PostAsync("/login", null, TestContext.Current.CancellationToken))!);

        using var logout = new HttpRequestMessage(HttpMethod.Post, "/logout");
        logout.Headers.Add("Cookie", cookie);

        var response = await client.SendAsync(logout, TestContext.Current.CancellationToken);
        var cleared = CookieFrom(response)!;

        Assert.Contains("expires=", cleared, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("max-age=0", cleared, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A rewritten cookie must not become a different user — it must become no user.</summary>
    [Fact]
    public async Task Rejects_a_tampered_cookie()
    {
        await using var server = await StartAsync();

        using var client = RawClient(server);

        var cookie = ValueOf(CookieFrom(await client.PostAsync("/login", null, TestContext.Current.CancellationToken))!);

        // Flipped in the middle of the ciphertext. The last base64 character carries only part of a
        // byte, so changing it can decode to the same bytes and prove nothing.
        var middle = cookie.Length / 2;
        var tampered = string.Concat(
            cookie.AsSpan(0, middle),
            cookie[middle] == 'A' ? "B" : "A",
            cookie.AsSpan(middle + 1)
        );

        using var request = new HttpRequestMessage(HttpMethod.Get, "/me");
        request.Headers.Add("Cookie", tampered);

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        // And it is cleared, so it stops being sent on every subsequent request.
        Assert.Contains("max-age=0", CookieFrom(response) ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rejects_an_expired_ticket()
    {
        await using var server = await StartAsync(o => o.ExpireTimeSpan = TimeSpan.FromMilliseconds(1));

        using var client = RawClient(server);

        var cookie = ValueOf(CookieFrom(await client.PostAsync("/login", null, TestContext.Current.CancellationToken))!);

        await Task.Delay(50, TestContext.Current.CancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/me");
        request.Headers.Add("Cookie", cookie);

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.SendAsync(request, TestContext.Current.CancellationToken)).StatusCode
        );
    }

    /// <summary>
    /// The escape hatch for a cookie that is cryptographically fine and no longer true — a deleted
    /// user, a changed password.
    /// </summary>
    [Fact]
    public async Task Honours_a_ticket_validator()
    {
        await using var server = await StartAsync(o => o.ValidateTicketAsync = (_, _) => new ValueTask<bool>(false));

        using var client = RawClient(server);

        var cookie = ValueOf(CookieFrom(await client.PostAsync("/login", null, TestContext.Current.CancellationToken))!);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/me");
        request.Headers.Add("Cookie", cookie);

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.SendAsync(request, TestContext.Current.CancellationToken)).StatusCode
        );
    }

    /// <summary>An active session should not be signed out mid-use.</summary>
    [Fact]
    public async Task Reissues_a_cookie_that_is_more_than_half_spent()
    {
        await using var server = await StartAsync(o => o.ExpireTimeSpan = TimeSpan.FromSeconds(4));

        using var client = RawClient(server);

        var cookie = ValueOf(CookieFrom(await client.PostAsync("/login", null, TestContext.Current.CancellationToken))!);

        await Task.Delay(2_400, TestContext.Current.CancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/me");
        request.Headers.Add("Cookie", cookie);

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(CookieFrom(response));
    }

    /// <summary>Rewriting the cookie on every request would break shared caching and waste bytes.</summary>
    [Fact]
    public async Task Does_not_reissue_a_fresh_cookie()
    {
        await using var server = await StartAsync(o => o.ExpireTimeSpan = TimeSpan.FromHours(1));

        using var client = RawClient(server);

        var cookie = ValueOf(CookieFrom(await client.PostAsync("/login", null, TestContext.Current.CancellationToken))!);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/me");
        request.Headers.Add("Cookie", cookie);

        Assert.Null(CookieFrom(await client.SendAsync(request, TestContext.Current.CancellationToken)));
    }

    /// <summary>A browser cannot act on a 401; a login page is the useful answer.</summary>
    [Fact]
    public async Task Redirects_a_browser_navigation_to_the_login_page()
    {
        await using var server = await StartAsync(o => o.LoginPath = "/login-page");

        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { BaseAddress = server.Client.BaseAddress };

        using var request = new HttpRequestMessage(HttpMethod.Get, "/me");
        request.Headers.Add("Sec-Fetch-Mode", "navigate");

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/login-page?returnUrl=%2Fme", response.Headers.Location?.OriginalString);
    }

    /// <summary>An API client asked for JSON; sending it an HTML login form helps nobody.</summary>
    [Fact]
    public async Task Answers_an_api_client_with_401_rather_than_a_redirect()
    {
        await using var server = await StartAsync(o => o.LoginPath = "/login-page");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/me");
        request.Headers.Accept.ParseAdd("application/json");

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await server.Client.SendAsync(request, TestContext.Current.CancellationToken)).StatusCode
        );
    }

    [Fact]
    public void Refuses_a_configuration_with_no_protector()
    {
        Assert.Throws<InvalidOperationException>(
            () => new CookieAuthenticationHandler(new CookieAuthenticationOptions())
        );
    }

    /// <summary>Browsers reject SameSite=None without Secure outright, so the combination is a bug.</summary>
    [Fact]
    public void Refuses_samesite_none_without_a_secure_cookie()
    {
        Assert.Throws<InvalidOperationException>(() => new CookieAuthenticationHandler(new CookieAuthenticationOptions
        {
            Protector = new TicketProtector(Key),
            SameSite = SameSiteMode.None,
            SecurePolicy = CookieSecurePolicy.Never
        }));
    }
}
