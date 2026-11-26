using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Shiny.Net.HttpServer.Security;
using Shiny.Net.HttpServer.Sessions;

namespace Shiny.Net.HttpServer.Tests;

// Cookie authentication and sessions both carry an AuthenticationTicket sealed by a TicketProtector.
// These cover what went wrong between them: one protector serving both let a session cookie sign a
// visitor in; a non-persistent sign-in became persistent on renewal; a renewal could only ever copy the
// claims it already had; and a session cookie expired under an active visitor.
public class TicketHardeningTests
{
    static readonly byte[] Key = TicketProtector.CreateKey();

    const string CookiePurpose = "Shiny.Net.HttpServer.Cookies:Cookies";

    static CancellationToken Token => TestContext.Current.CancellationToken;

    static Task<TestServer> StartAsync(
        Action<CookieAuthenticationOptions>? cookies = null,
        Action<SessionOptions>? sessions = null
    ) => TestServer.StartAsync(
        app =>
        {
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseSessions();

            app.MapPost("/visit", async ctx =>
            {
                var count = ctx.Session!.GetInt32("visits") ?? 0;
                ctx.Session!.SetInt32("visits", count + 1);
                await ctx.Response.WriteTextAsync((count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
            });

            app.MapGet("/visits", ctx => ctx.Response.WriteTextAsync(
                (ctx.Session!.GetInt32("visits") ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture)
            ));

            app.MapPost("/login-once", async ctx =>
            {
                await ctx.SignInAsync(Principal("ada"), persistent: false);
                await ctx.Response.WriteTextAsync("signed in");
            });

            app.MapGet("/me", ctx => ctx.Response.WriteTextAsync(ctx.User.Identity?.Name ?? "anonymous"))
                .RequireAuthorization();
        },
        builder =>
        {
            var protector = new TicketProtector(Key);

            builder.AddAuthentication().AddCookie(o =>
            {
                o.Protector = protector;
                cookies?.Invoke(o);
            });

            builder.AddAuthorization();
            builder.AddSessions(o =>
            {
                o.Protector = protector;
                sessions?.Invoke(o);
            });
        }
    );

    static ClaimsPrincipal Principal(string name, params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, name) };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Cookies", ClaimTypes.Name, ClaimTypes.Role));
    }

    static HttpClient RawClient(TestServer server)
        => new(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false }) { BaseAddress = server.Client.BaseAddress };

    static string? SetCookie(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault(v => v.StartsWith(name + "=", StringComparison.Ordinal))
            : null;

    static string ValueOf(string setCookie) => setCookie.Split(';')[0].Split('=', 2)[1];

    static async Task<HttpResponseMessage> GetWithCookieAsync(HttpClient client, string path, string cookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", cookie);
        return await client.SendAsync(request, Token);
    }

    // ---- Finding: a session cookie accepted as an authentication cookie ---------------------------

    [Fact]
    public async Task A_session_cookie_presented_as_the_auth_cookie_does_not_sign_in()
    {
        await using var server = await StartAsync();
        using var client = RawClient(server);

        var visit = await client.PostAsync("/visit", null, Token);
        var session = SetCookie(visit, ".shiny.session");
        Assert.NotNull(session);

        using var response = await GetWithCookieAsync(client, "/me", ".shiny.auth=" + ValueOf(session));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_session_cookie_from_before_purposes_does_not_sign_in_either()
    {
        await using var server = await StartAsync();
        using var client = RawClient(server);

        var now = DateTimeOffset.UtcNow;
        var legacy = LegacyProtect(
            Key,
            new ClaimsIdentity([new Claim("sid", "abc")], "Session"),
            now,
            now.AddHours(1)
        );

        using var response = await GetWithCookieAsync(client, "/me", ".shiny.auth=" + legacy);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_auth_cookie_from_before_purposes_still_signs_in()
    {
        await using var server = await StartAsync();
        using var client = RawClient(server);

        var now = DateTimeOffset.UtcNow;
        var legacy = LegacyProtect(
            Key,
            new ClaimsIdentity([new Claim(ClaimTypes.Name, "ada")], "Cookies", ClaimTypes.Name, ClaimTypes.Role),
            now,
            now.AddHours(1)
        );

        using var response = await GetWithCookieAsync(client, "/me", ".shiny.auth=" + legacy);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ada", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task An_auth_cookie_presented_as_the_session_cookie_is_not_a_session()
    {
        await using var server = await StartAsync();
        using var client = RawClient(server);

        // An OpenID Connect principal carries a "sid" claim; it must not become a session id.
        var now = DateTimeOffset.UtcNow;
        var protector = new TicketProtector(Key);
        var auth = protector.Protect(
            new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim("sid", "victim")], "Cookies")), now, now.AddHours(1)),
            CookiePurpose
        );

        using var response = await GetWithCookieAsync(client, "/visits", ".shiny.session=" + auth);
        Assert.Equal("0", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public void A_ticket_protected_for_one_purpose_does_not_open_under_another()
    {
        var protector = new TicketProtector(Key);
        var now = DateTimeOffset.UtcNow;
        var sealedTicket = protector.Protect(new AuthenticationTicket(Principal("ada"), now, now.AddHours(1)), "one");

        Assert.NotNull(protector.Unprotect(sealedTicket, "one"));
        Assert.Null(protector.Unprotect(sealedTicket, "two"));
        Assert.Null(protector.Unprotect(sealedTicket));
    }

    // ---- Finding: a non-persistent sign-in becoming persistent on renewal -------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_sliding_renewal_keeps_the_cookie_as_persistent_as_it_was(bool persistent)
    {
        await using var server = await StartAsync();
        using var client = RawClient(server);

        // Issued three hours ago with an hour left: well past halfway, so this request renews it.
        var now = DateTimeOffset.UtcNow;
        var old = new TicketProtector(Key).Protect(
            new AuthenticationTicket(Principal("ada"), now.AddHours(-3), now.AddHours(1), persistent),
            CookiePurpose
        );

        using var response = await GetWithCookieAsync(client, "/me", ".shiny.auth=" + old);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var renewed = SetCookie(response, ".shiny.auth");
        Assert.NotNull(renewed);
        Assert.Equal(persistent, renewed.Contains("expires=", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_non_persistent_sign_in_issues_no_expires_and_records_it_in_the_ticket()
    {
        await using var server = await StartAsync();
        using var client = RawClient(server);

        var login = await client.PostAsync("/login-once", null, Token);
        var cookie = SetCookie(login, ".shiny.auth");

        Assert.NotNull(cookie);
        Assert.DoesNotContain("expires=", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.False(new TicketProtector(Key).Unprotect(ValueOf(cookie), CookiePurpose)!.IsPersistent);
    }

    // ---- Finding: renewal could only copy stale claims --------------------------------------------

    [Fact]
    public async Task OnValidatePrincipal_can_replace_the_principal_and_the_cookie_carries_it()
    {
        await using var server = await StartAsync(cookies: o => o.OnValidatePrincipal = ctx =>
        {
            if (ctx.Principal.Identity?.Name == "ada")
                ctx.ReplacePrincipal(Principal("ada-v2"));

            return default;
        });
        using var client = RawClient(server);

        var now = DateTimeOffset.UtcNow;
        var old = new TicketProtector(Key).Protect(new AuthenticationTicket(Principal("ada"), now, now.AddHours(1)), CookiePurpose);

        using var response = await GetWithCookieAsync(client, "/me", ".shiny.auth=" + old);
        Assert.Equal("ada-v2", await response.Content.ReadAsStringAsync(Token));

        var reissued = SetCookie(response, ".shiny.auth");
        Assert.NotNull(reissued);

        var ticket = new TicketProtector(Key).Unprotect(ValueOf(reissued), CookiePurpose)!;
        Assert.Equal("ada-v2", ticket.Principal.Identity?.Name);

        // Refreshing claims does not extend the sign-in.
        Assert.Equal(now.ToUnixTimeSeconds() + 3600, ticket.ExpiresUtc.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task OnValidatePrincipal_can_reject_the_cookie()
    {
        await using var server = await StartAsync(cookies: o => o.OnValidatePrincipal = ctx =>
        {
            ctx.RejectPrincipal();
            return default;
        });
        using var client = RawClient(server);

        var now = DateTimeOffset.UtcNow;
        var cookie = new TicketProtector(Key).Protect(new AuthenticationTicket(Principal("ada"), now, now.AddHours(1)), CookiePurpose);

        using var response = await GetWithCookieAsync(client, "/me", ".shiny.auth=" + cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task OnValidatePrincipal_ShouldRenew_reissues_with_a_fresh_expiry()
    {
        await using var server = await StartAsync(cookies: o =>
        {
            o.SlidingExpiration = false;
            o.OnValidatePrincipal = ctx =>
            {
                ctx.ShouldRenew = true;
                return default;
            };
        });
        using var client = RawClient(server);

        var now = DateTimeOffset.UtcNow;
        var cookie = new TicketProtector(Key).Protect(new AuthenticationTicket(Principal("ada"), now, now.AddMinutes(5)), CookiePurpose);

        using var response = await GetWithCookieAsync(client, "/me", ".shiny.auth=" + cookie);
        var reissued = SetCookie(response, ".shiny.auth");

        Assert.NotNull(reissued);
        Assert.True(new TicketProtector(Key).Unprotect(ValueOf(reissued), CookiePurpose)!.ExpiresUtc > now.AddDays(1));
    }

    // ---- Finding: an active session lost after IdleTimeout ----------------------------------------

    [Fact]
    public async Task An_active_session_outlives_its_idle_timeout()
    {
        await using var server = await StartAsync(sessions: o => o.IdleTimeout = TimeSpan.FromSeconds(2));
        using var client = new HttpClient(new HttpClientHandler { UseCookies = true }) { BaseAddress = server.Client.BaseAddress };

        await client.PostAsync("/visit", null, Token);

        // Read every 700ms for well past the 2s idle timeout. Each read keeps the session alive in the
        // store, and the cookie has to keep up with it.
        for (var i = 0; i < 5; i++)
        {
            await Task.Delay(700, Token);
            Assert.Equal("1", await client.GetStringAsync("/visits", Token));
        }
    }

    /// <summary>
    /// Seals a ticket the way the protector did before purposes and the persistence flag existed: a
    /// version 1 envelope (associated data = key id only) around a version 1 ticket.
    /// </summary>
    static string LegacyProtect(byte[] key, ClaimsIdentity identity, DateTimeOffset issued, DateTimeOffset expires)
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((byte)1);
            writer.Write(issued.ToUnixTimeSeconds());
            writer.Write(expires.ToUnixTimeSeconds());
            writer.Write(1);
            writer.Write(identity.AuthenticationType ?? string.Empty);
            writer.Write(identity.NameClaimType);
            writer.Write(identity.RoleClaimType);

            var claims = identity.Claims.ToArray();
            writer.Write(claims.Length);

            foreach (var claim in claims)
            {
                writer.Write(claim.Type);
                writer.Write(claim.Value);
                writer.Write(claim.ValueType);
                writer.Write(claim.Issuer);
            }
        }

        var plaintext = buffer.ToArray();
        var id = SHA256.HashData(key).AsSpan(0, 4).ToArray();
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var ciphertext = new byte[plaintext.Length];

        using (var aes = new AesGcm(key, 16))
            aes.Encrypt(nonce, plaintext, ciphertext, tag, id);

        return Base64Url.Encode([1, .. id, .. nonce, .. tag, .. ciphertext]);
    }
}
