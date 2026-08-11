using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Shiny.Net.HttpServer.Security;

namespace Shiny.Net.HttpServer.Tests;

public class BasicCredentialDecodingTests
{
    static string Encode(string pair) => Convert.ToBase64String(Encoding.UTF8.GetBytes(pair));

    [Fact]
    public void Decodes_a_username_and_password()
    {
        Assert.True(BasicAuthenticationHandler.TryDecode(Encode("ada:hunter2"), out var user, out var password));

        Assert.Equal("ada", user);
        Assert.Equal("hunter2", password);
    }

    /// <summary>A password may contain colons; only the first one separates.</summary>
    [Fact]
    public void Splits_on_the_first_colon_only()
    {
        Assert.True(BasicAuthenticationHandler.TryDecode(Encode("ada:a:b:c"), out var user, out var password));

        Assert.Equal("ada", user);
        Assert.Equal("a:b:c", password);
    }

    /// <summary>RFC 7617 says UTF-8, which is why the challenge advertises it.</summary>
    [Fact]
    public void Decodes_non_ascii_credentials()
    {
        Assert.True(BasicAuthenticationHandler.TryDecode(Encode("adá:pässwörd✓"), out var user, out var password));

        Assert.Equal("adá", user);
        Assert.Equal("pässwörd✓", password);
    }

    [Fact]
    public void Accepts_an_empty_password()
    {
        Assert.True(BasicAuthenticationHandler.TryDecode(Encode("ada:"), out var user, out var password));

        Assert.Equal("ada", user);
        Assert.Equal(string.Empty, password);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not base64 at all!!")]
    public void Rejects_anything_that_is_not_base64(string value)
        => Assert.False(BasicAuthenticationHandler.TryDecode(value, out _, out _));

    [Fact]
    public void Rejects_a_pair_with_no_separator()
        => Assert.False(BasicAuthenticationHandler.TryDecode(Encode("nocolonhere"), out _, out _));

    [Fact]
    public void Rejects_an_empty_username()
        => Assert.False(BasicAuthenticationHandler.TryDecode(Encode(":onlyapassword"), out _, out _));

    /// <summary>Matching the pair, not the password, so the same password under another name misses.</summary>
    [Fact]
    public void Hashes_the_username_and_password_together()
    {
        var ada = BasicCredential.Hash("ada", "hunter2");
        var bob = BasicCredential.Hash("bob", "hunter2");

        Assert.NotEqual(ada, bob);
        Assert.Equal(ada, BasicCredential.Hash("ada", "hunter2"));
    }
}

public class BasicTransportGuardTests
{
    static HttpContext Context(bool https = false, bool encrypted = false, bool tunneled = false, string? remoteIp = null)
    {
        var context = new HttpContext();

        context.Request.Scheme = https ? "https" : "http";
        context.Connection.IsEncrypted = encrypted;
        context.Connection.IsTunneled = tunneled;

        if (remoteIp is not null)
            context.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);

        return context;
    }

    [Fact]
    public void Accepts_an_encrypted_connection()
    {
        Assert.True(BasicAuthenticationHandler.IsSecureEnough(Context(https: true, remoteIp: "203.0.113.7")));
        Assert.True(BasicAuthenticationHandler.IsSecureEnough(Context(encrypted: true, remoteIp: "203.0.113.7")));
    }

    /// <summary>The plaintext hop is inside the device; the public leg is TLS to the relay.</summary>
    [Fact]
    public void Accepts_a_tunnelled_connection()
        => Assert.True(BasicAuthenticationHandler.IsSecureEnough(Context(tunneled: true, remoteIp: "127.0.0.1")));

    /// <summary>Nothing to intercept, and it is where every developer starts.</summary>
    [Fact]
    public void Accepts_loopback()
    {
        Assert.True(BasicAuthenticationHandler.IsSecureEnough(Context(remoteIp: "127.0.0.1")));
        Assert.True(BasicAuthenticationHandler.IsSecureEnough(Context(remoteIp: "::1")));
    }

    /// <summary>The case the guard exists for: a password in the clear across a real network.</summary>
    [Fact]
    public void Refuses_plain_http_from_the_network()
    {
        Assert.False(BasicAuthenticationHandler.IsSecureEnough(Context(remoteIp: "192.168.1.50")));
        Assert.False(BasicAuthenticationHandler.IsSecureEnough(Context(remoteIp: "203.0.113.7")));
    }
}

public class BasicAuthenticationTests
{
    static Task<TestServer> StartAsync(Action<BasicAuthenticationOptions> configure) => TestServer.StartAsync(
        app =>
        {
            app.UseAuthentication();
            app.UseAuthorization();

            app.MapGet("/who", ctx => ctx.Response.WriteTextAsync(ctx.User.Identity?.Name ?? "anonymous"))
                .RequireAuthorization();

            app.MapGet("/open", ctx => ctx.Response.WriteTextAsync(ctx.User.Identity?.Name ?? "anonymous"));

            app.MapGet("/admin", ctx => ctx.Response.WriteTextAsync("admin area"))
                .RequireAuthorization("admins");
        },
        builder =>
        {
            builder.Services.AddAuthentication().AddBasic(configure);
            builder.Services.AddAuthorization(o => o.AddPolicy("admins", p => p.RequireRole("admin")));
        }
    );

    static HttpRequestMessage Request(string path, string username, string password)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);

        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"))
        );

        return request;
    }

    [Fact]
    public async Task Identifies_a_caller_with_the_right_password()
    {
        await using var server = await StartAsync(o => o.AddUser("ada", "hunter2", "admin"));

        using var request = Request("/who", "ada", "hunter2");
        var response = await server.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ada", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Refuses_the_wrong_password()
    {
        await using var server = await StartAsync(o => o.AddUser("ada", "hunter2"));

        using var request = Request("/who", "ada", "not-the-password");
        var response = await server.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>Telling a bad username from a bad password is how an attacker enumerates accounts.</summary>
    [Fact]
    public async Task Says_the_same_thing_for_an_unknown_user_and_a_wrong_password()
    {
        await using var server = await StartAsync(o => o.AddUser("ada", "hunter2"));

        using var unknownUser = Request("/who", "nobody", "hunter2");
        using var wrongPassword = Request("/who", "ada", "wrong");

        var first = await server.Client.SendAsync(unknownUser, TestContext.Current.CancellationToken);
        var second = await server.Client.SendAsync(wrongPassword, TestContext.Current.CancellationToken);

        Assert.Equal(first.StatusCode, second.StatusCode);
        Assert.Equal(
            first.Headers.WwwAuthenticate.ToString(),
            second.Headers.WwwAuthenticate.ToString()
        );
    }

    /// <summary>
    /// The challenge is the whole reason a browser shows a password box. Named wrongly, the user
    /// gets a blank 401 and no way in.
    /// </summary>
    [Fact]
    public async Task Challenges_with_a_realm_a_browser_will_prompt_for()
    {
        await using var server = await StartAsync(o =>
        {
            o.Realm = "Device settings";
            o.AddUser("ada", "hunter2");
        });

        var response = await server.Client.GetAsync("/who", TestContext.Current.CancellationToken);
        var challenge = response.Headers.WwwAuthenticate.ToString();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.StartsWith("Basic", challenge, StringComparison.Ordinal);
        Assert.Contains("realm=\"Device settings\"", challenge, StringComparison.Ordinal);
        Assert.Contains("charset=\"UTF-8\"", challenge, StringComparison.Ordinal);
    }

    /// <summary>A quote in the realm would end the parameter early and corrupt the header.</summary>
    [Fact]
    public async Task Keeps_a_realm_from_breaking_the_header()
    {
        await using var server = await StartAsync(o =>
        {
            o.Realm = "Bad \"realm\" here";
            o.AddUser("ada", "hunter2");
        });

        var response = await server.Client.GetAsync("/who", TestContext.Current.CancellationToken);

        Assert.Contains("realm=\"Bad realm here\"", response.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Leaves_a_request_with_no_header_anonymous()
    {
        await using var server = await StartAsync(o => o.AddUser("ada", "hunter2"));

        Assert.Equal("anonymous", await server.Client.GetStringAsync("/open", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Rejects_a_malformed_header()
    {
        await using var server = await StartAsync(o => o.AddUser("ada", "hunter2"));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/who");
        request.Headers.TryAddWithoutValidation("Authorization", "Basic not-base64!!");

        var response = await server.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Carries_roles_into_authorization()
    {
        await using var server = await StartAsync(o =>
        {
            o.AddUser("ada", "hunter2", "admin");
            o.AddUser("bob", "hunter2", "reader");
        });

        using var admin = Request("/admin", "ada", "hunter2");
        using var reader = Request("/admin", "bob", "hunter2");

        Assert.Equal(
            HttpStatusCode.OK,
            (await server.Client.SendAsync(admin, TestContext.Current.CancellationToken)).StatusCode
        );

        // Known caller, still not allowed: 403, and no password prompt — one would not help.
        var forbidden = await server.Client.SendAsync(reader, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Empty(forbidden.Headers.WwwAuthenticate);
    }

    [Fact]
    public async Task Handles_a_password_containing_colons()
    {
        await using var server = await StartAsync(o => o.AddUser("ada", "a:b:c"));

        using var request = Request("/who", "ada", "a:b:c");

        Assert.Equal(
            "ada",
            await (await server.Client.SendAsync(request, TestContext.Current.CancellationToken))
                .Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task Handles_non_ascii_credentials()
    {
        await using var server = await StartAsync(o => o.AddUser("adá", "pässwörd"));

        using var request = Request("/who", "adá", "pässwörd");

        Assert.Equal(
            "adá",
            await (await server.Client.SendAsync(request, TestContext.Current.CancellationToken))
                .Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task Falls_back_to_the_validator_for_an_account_that_is_not_configured()
    {
        await using var server = await StartAsync(o => o.ValidateAsync = (user, password, _) =>
            new ValueTask<ClaimsPrincipal?>(
                user == "db-user" && password == "from-the-database"
                    ? new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "db-user")], "Basic"))
                    : null
            )
        );

        using var good = Request("/who", "db-user", "from-the-database");
        using var bad = Request("/who", "db-user", "wrong");

        Assert.Equal(
            "db-user",
            await (await server.Client.SendAsync(good, TestContext.Current.CancellationToken))
                .Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
        );

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await server.Client.SendAsync(bad, TestContext.Current.CancellationToken)).StatusCode
        );
    }

    /// <summary>A scheme with nothing to accept would silently reject every request.</summary>
    [Fact]
    public void Refuses_to_register_with_no_accounts_and_no_validator()
    {
        var services = new ServiceCollection();

        Assert.Throws<InvalidOperationException>(() => services.AddAuthentication().AddBasic(_ => { }));
    }

    /// <summary>
    /// The password is not kept, so a dumped options object hands over nothing usable — and length
    /// no longer varies, which is what makes the comparison fixed-time.
    /// </summary>
    [Fact]
    public void Does_not_keep_the_password()
    {
        var options = new BasicAuthenticationOptions();
        options.AddUser("ada", "hunter2");

        var credential = options.Credentials.Single();

        Assert.Equal("ada", credential.Username);
        Assert.DoesNotContain(
            "hunter2",
            System.Text.Json.JsonSerializer.Serialize(new { credential.Username, credential.Roles }),
            StringComparison.Ordinal
        );
    }
}

/// <summary>
/// The validator interface — the path an app takes when accounts live somewhere other than
/// configuration, and the one a settings screen changes at runtime.
/// </summary>
public class BasicCredentialValidatorTests
{
    sealed class MutableStore : IBasicCredentialValidator
    {
        public string Username { get; set; } = "ada";

        public string Password { get; set; } = "first-password";

        public int Calls { get; private set; }

        public ValueTask<ClaimsPrincipal?> ValidateAsync(string username, string password, CancellationToken cancellationToken)
        {
            this.Calls++;

            return new ValueTask<ClaimsPrincipal?>(
                username == this.Username && password == this.Password
                    ? new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, username)], "Basic"))
                    : null
            );
        }
    }

    static Task<TestServer> StartAsync(Action<IServiceCollection> configureServices) => TestServer.StartAsync(
        app =>
        {
            app.UseAuthentication();
            app.UseAuthorization();

            app.MapGet("/who", ctx => ctx.Response.WriteTextAsync(ctx.User.Identity?.Name ?? "anonymous"))
                .RequireAuthorization();
        },
        builder => configureServices(builder.Services)
    );

    static HttpRequestMessage Request(string username, string password)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/who");

        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"))
        );

        return request;
    }

    [Fact]
    public async Task Authenticates_through_a_validator_resolved_from_the_container()
    {
        await using var server = await StartAsync(services =>
        {
            services.AddAuthentication().AddBasic<MutableStore>(o => o.Realm = "Device");
            services.AddAuthorization();
        });

        using var request = Request("ada", "first-password");
        var response = await server.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ada", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The point of resolving the validator rather than copying its contents at startup: a settings
    /// screen changes the password and the very next request uses it, with nothing restarted.
    /// </summary>
    [Fact]
    public async Task Picks_up_a_password_changed_at_runtime()
    {
        MutableStore? store = null;

        await using var server = await StartAsync(services =>
        {
            services.AddAuthentication().AddBasic<MutableStore>();
            services.AddAuthorization();
        });

        store = server.Server.Services!.GetRequiredService<MutableStore>();

        using var beforeChange = Request("ada", "first-password");
        Assert.Equal(
            HttpStatusCode.OK,
            (await server.Client.SendAsync(beforeChange, TestContext.Current.CancellationToken)).StatusCode
        );

        store.Password = "second-password";

        using var stale = Request("ada", "first-password");
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await server.Client.SendAsync(stale, TestContext.Current.CancellationToken)).StatusCode
        );

        using var updated = Request("ada", "second-password");
        Assert.Equal(
            HttpStatusCode.OK,
            (await server.Client.SendAsync(updated, TestContext.Current.CancellationToken)).StatusCode
        );
    }

    /// <summary>An account in configuration must not cost a call to whatever the validator talks to.</summary>
    [Fact]
    public async Task Prefers_a_configured_account_over_the_validator()
    {
        await using var server = await StartAsync(services =>
        {
            services.AddAuthentication().AddBasic<MutableStore>(o => o.AddUser("local", "local-password"));
            services.AddAuthorization();
        });

        var store = server.Server.Services!.GetRequiredService<MutableStore>();

        using var request = Request("local", "local-password");
        await server.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task Accepts_a_validator_from_a_factory()
    {
        var store = new MutableStore { Username = "made-by-factory", Password = "pw" };

        await using var server = await StartAsync(services =>
        {
            services.AddAuthentication().AddBasic(_ => store);
            services.AddAuthorization();
        });

        using var request = Request("made-by-factory", "pw");

        Assert.Equal(
            HttpStatusCode.OK,
            (await server.Client.SendAsync(request, TestContext.Current.CancellationToken)).StatusCode
        );
    }

    /// <summary>
    /// A validator is an account list, so the "you configured nothing" guard must not fire for it.
    /// </summary>
    [Fact]
    public void Does_not_require_static_accounts_when_a_validator_is_registered()
    {
        var services = new ServiceCollection();

        services.AddAuthentication().AddBasic<MutableStore>();

        Assert.Contains(services, d => d.ServiceType == typeof(IBasicCredentialValidator));
    }
}
