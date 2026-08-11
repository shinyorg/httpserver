using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Shiny.Net.HttpServer.Security;

/// <summary>
/// A username and password the server accepts.
/// <para>
/// The password is not kept — only a hash of <c>username:password</c>, so a memory dump or a logged
/// options object hands over nothing usable, and comparing hashes equalises length, which is what
/// makes the comparison genuinely fixed-time.
/// </para>
/// </summary>
public sealed class BasicCredential
{
    readonly byte[] hash;

    internal BasicCredential(string username, string password, IEnumerable<string>? roles, IEnumerable<Claim>? claims)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentNullException.ThrowIfNull(password);

        this.Username = username;
        this.hash = Hash(username, password);
        this.Roles = roles is null ? [] : [.. roles];
        this.Claims = claims is null ? [] : [.. claims];
    }

    public string Username { get; }

    public IReadOnlyList<string> Roles { get; }

    public IReadOnlyList<Claim> Claims { get; }

    internal bool Matches(ReadOnlySpan<byte> presented) => CryptographicOperations.FixedTimeEquals(this.hash, presented);

    /// <summary>
    /// Hashes the pair rather than the password alone, so an entry cannot be matched by presenting
    /// the same password under a different name.
    /// </summary>
    internal static byte[] Hash(string username, string password)
        => SHA256.HashData(Encoding.UTF8.GetBytes(username + ":" + password));
}

/// <summary>
/// Checks a username and password against whatever holds the accounts.
/// <para>
/// An interface rather than only a delegate, because a credential store is usually a type with
/// dependencies of its own — a database, a keychain, a settings screen — and it is resolved from the
/// container so it can be one. Return null to reject; the caller cannot tell a wrong password from
/// an unknown user, and should not be able to.
/// </para>
/// <code>
/// public sealed class UserStore(AppDb db) : IBasicCredentialValidator
/// {
///     public async ValueTask&lt;ClaimsPrincipal?&gt; ValidateAsync(string username, string password, CancellationToken ct)
///     {
///         var user = await db.FindAsync(username, ct);
///
///         return user is not null &amp;&amp; PasswordHasher.Verify(password, user.PasswordHash)
///             ? new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, user.Name)], "Basic"))
///             : null;
///     }
/// }
///
/// builder.Services.AddAuthentication().AddBasic&lt;UserStore&gt;(o => o.Realm = "Device");
/// </code>
/// </summary>
public interface IBasicCredentialValidator
{
    ValueTask<ClaimsPrincipal?> ValidateAsync(string username, string password, CancellationToken cancellationToken);
}

/// <summary>How Basic authentication is configured.</summary>
public sealed class BasicAuthenticationOptions
{
    readonly List<BasicCredential> credentials = [];

    /// <summary>The scheme name, reported on <c>WWW-Authenticate</c> and <c>ctx.Authentication</c>.</summary>
    public string Scheme { get; set; } = "Basic";

    /// <summary>
    /// The realm a browser shows in its password prompt. Keep it short and recognisable — it is the
    /// only context the person typing gets.
    /// </summary>
    public string Realm { get; set; } = "Restricted";

    /// <summary>The accounts this server accepts, for the fixed-list case.</summary>
    public IReadOnlyList<BasicCredential> Credentials => this.credentials;

    /// <summary>
    /// Validates a username and password that are not in <see cref="Credentials"/>. Return null to
    /// reject.
    /// <para>
    /// This is where a real user database belongs, along with whatever password hashing it uses.
    /// The static list is for the handful of accounts that live in configuration.
    /// </para>
    /// </summary>
    public Func<string, string, CancellationToken, ValueTask<ClaimsPrincipal?>>? ValidateAsync { get; set; }

    /// <summary>
    /// Accepts credentials over a connection that is not encrypted.
    /// <para>
    /// Off, and worth leaving off. Basic sends the password on <em>every request</em>, base64-encoded
    /// — which is not encryption, it is spelling. Over plain HTTP that is the account, in the clear,
    /// repeatedly. Loopback and tunnelled connections are already allowed without this, so the only
    /// thing it turns on is sending passwords across a network in the open.
    /// </para>
    /// </summary>
    public bool AllowInsecureTransport { get; set; }

    /// <summary>Adds an account.</summary>
    public BasicAuthenticationOptions AddUser(string username, string password, params string[] roles)
    {
        this.credentials.Add(new BasicCredential(username, password, roles, claims: null));
        return this;
    }

    /// <summary>Adds an account with claims beyond a name and roles.</summary>
    public BasicAuthenticationOptions AddUser(
        string username,
        string password,
        IEnumerable<string>? roles,
        IEnumerable<Claim>? claims
    )
    {
        this.credentials.Add(new BasicCredential(username, password, roles, claims));
        return this;
    }
}

/// <summary>
/// HTTP Basic authentication (RFC 7617).
/// <para>
/// The scheme every browser and every HTTP client already understands, with no token endpoint and no
/// session to manage — which makes it the shortest path to putting a password in front of something.
/// The cost is that the password travels on every single request, so this belongs behind TLS or a
/// tunnel and nowhere else; the transport check enforces that rather than trusting it.
/// </para>
/// <code>
/// builder.Services.AddAuthentication().AddBasic(o =>
/// {
///     o.Realm = "Device";
///     o.AddUser("ada", configuration["Admin:Password"]!, "admin");
/// });
/// </code>
/// </summary>
public sealed class BasicAuthenticationHandler(
    BasicAuthenticationOptions options,
    IBasicCredentialValidator? validator = null
) : IAuthenticationHandler, IAuthenticationChallenge
{
    const string Prefix = "Basic ";

    readonly BasicAuthenticationOptions options = options ?? throw new ArgumentNullException(nameof(options));

    public string Scheme => this.options.Scheme;

    public async ValueTask<AuthenticateResult> AuthenticateAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var header = context.Request.Headers.GetFirst(HeaderNames.Authorization);
        if (header is null || !header.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        // Checked before the credentials are even decoded: if the transport is wrong, the password
        // has already been exposed and the only useful thing left is to say so.
        if (!this.options.AllowInsecureTransport && !IsSecureEnough(context))
            return AuthenticateResult.Fail("Basic credentials require an encrypted connection.");

        if (!TryDecode(header[Prefix.Length..], out var username, out var password))
            return AuthenticateResult.Fail("The Basic credentials are malformed.");

        var presented = BasicCredential.Hash(username, password);

        // Every entry is checked even after a match, so the time taken says nothing about where in
        // the list an account sits — or whether the username existed at all.
        BasicCredential? matched = null;

        foreach (var credential in this.options.Credentials)
        {
            if (credential.Matches(presented))
                matched = credential;
        }

        if (matched is not null)
            return AuthenticateResult.Success(BuildPrincipal(matched, this.Scheme));

        // The configured list first, so an account in configuration never costs a round trip to
        // whatever the validator talks to.
        if (validator is not null)
        {
            var principal = await validator
                .ValidateAsync(username, password, context.RequestAborted)
                .ConfigureAwait(false);

            if (principal is not null)
                return AuthenticateResult.Success(principal);
        }

        if (this.options.ValidateAsync is { } validate)
        {
            var principal = await validate(username, password, context.RequestAborted).ConfigureAwait(false);

            if (principal is not null)
                return AuthenticateResult.Success(principal);
        }

        // One message for a bad username and a bad password alike: telling them apart is how an
        // attacker enumerates accounts.
        return AuthenticateResult.Fail("The username or password is incorrect.");
    }

    /// <summary>
    /// Writes the 401 with a challenge a browser will act on.
    /// <para>
    /// Without this the pipeline's generic challenge names whatever scheme last ran, and a browser
    /// given anything other than <c>Basic</c> shows the user nothing — no prompt, no way in.
    /// </para>
    /// </summary>
    public ValueTask<bool> TryChallengeAsync(HttpContext context, bool forbidden)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Authenticated and still refused: a password prompt cannot help, and showing one would
        // invite the user to try the same credentials forever.
        if (forbidden || context.Response.HasStarted)
            return new ValueTask<bool>(false);

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;

        // charset is the one parameter RFC 7617 defines, and it is what tells a browser to send
        // non-ASCII passwords as UTF-8 rather than guessing.
        context.Response.Headers[HeaderNames.WwwAuthenticate] =
            $"{this.Scheme} realm=\"{Escape(this.options.Realm)}\", charset=\"UTF-8\"";

        context.Response.ContentLength = 0;

        return new ValueTask<bool>(true);
    }

    /// <summary>
    /// Whether the connection is private enough to carry a password.
    /// <para>
    /// Encrypted, tunnelled, or loopback. A tunnel counts because its public leg is TLS to the
    /// relay — the plaintext hop is inside this device. Loopback counts because there is no network
    /// to intercept.
    /// </para>
    /// </summary>
    internal static bool IsSecureEnough(HttpContext context)
    {
        if (context.Request.IsHttps || context.Connection.IsEncrypted || context.Connection.IsTunneled)
            return true;

        // An unknown remote address is treated as untrusted rather than assumed local.
        return context.Connection.RemoteIpAddress is { } address && IPAddress.IsLoopback(address);
    }

    /// <summary>Decodes <c>base64(user:password)</c>. A password may contain colons; a username may not.</summary>
    internal static bool TryDecode(string encoded, out string username, out string password)
    {
        username = string.Empty;
        password = string.Empty;

        var trimmed = encoded.Trim();
        if (trimmed.Length == 0)
            return false;

        byte[] decoded;

        try
        {
            decoded = Convert.FromBase64String(trimmed);
        }
        catch (FormatException)
        {
            return false;
        }

        // RFC 7617 says UTF-8; older clients sent Latin-1. UTF-8 decoding of Latin-1 bytes would
        // produce replacement characters rather than throwing, which is a wrong password either
        // way — so the modern reading is the one used.
        var pair = Encoding.UTF8.GetString(decoded);

        var separator = pair.IndexOf(':');
        if (separator < 0)
            return false;

        username = pair[..separator];
        password = pair[(separator + 1)..];

        return username.Length > 0;
    }

    static ClaimsPrincipal BuildPrincipal(BasicCredential credential, string scheme)
    {
        var claims = new List<Claim>(credential.Claims.Count + credential.Roles.Count + 1)
        {
            new(ClaimTypes.Name, credential.Username)
        };

        foreach (var role in credential.Roles)
            claims.Add(new Claim(ClaimTypes.Role, role));

        claims.AddRange(credential.Claims);

        return new ClaimsPrincipal(new ClaimsIdentity(claims, scheme, ClaimTypes.Name, ClaimTypes.Role));
    }

    /// <summary>Keeps a realm from breaking out of the quoted header parameter.</summary>
    static string Escape(string realm) => realm.Replace("\\", string.Empty).Replace("\"", string.Empty);
}

/// <summary>Registering Basic authentication.</summary>
public static class BasicAuthenticationBuilderExtensions
{
    /// <summary>
    /// Adds the <c>Basic</c> scheme.
    /// <code>
    /// builder.Services.AddAuthentication().AddBasic(o => o.AddUser("ada", secret, "admin"));
    /// </code>
    /// </summary>
    public static AuthenticationBuilder AddBasic(
        this AuthenticationBuilder builder,
        Action<BasicAuthenticationOptions> configure
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new BasicAuthenticationOptions();
        configure(options);

        return builder.AddBasicCore(options, requireCredentials: true);
    }

    /// <summary>
    /// Adds the <c>Basic</c> scheme with a validator resolved from the container — the overload to
    /// use when accounts live somewhere other than configuration.
    /// <code>
    /// builder.Services.AddSingleton&lt;UserStore&gt;();
    /// builder.Services.AddAuthentication().AddBasic&lt;UserStore&gt;(o => o.Realm = "Device");
    /// </code>
    /// <para>
    /// No static accounts are required here: the validator is the account list.
    /// </para>
    /// </summary>
    public static AuthenticationBuilder AddBasic<
        [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
            System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors
        )] TValidator
    >(
        this AuthenticationBuilder builder,
        Action<BasicAuthenticationOptions>? configure = null
    ) where TValidator : class, IBasicCredentialValidator
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new BasicAuthenticationOptions();
        configure?.Invoke(options);

        // The container activates TValidator by reflection, so its constructors are annotated as
        // preserved. Registered against the interface as well as itself, so an app can inject its
        // own store directly — a settings screen that changes the password needs the same instance.
        // Use the factory overload to avoid reflective activation entirely.
        builder.Services.TryAddSingleton<TValidator>();
        builder.Services.AddSingleton<IBasicCredentialValidator>(sp => sp.GetRequiredService<TValidator>());

        return builder.AddBasicCore(options, requireCredentials: false);
    }

    /// <summary>Adds the <c>Basic</c> scheme with a validator built by a factory.</summary>
    public static AuthenticationBuilder AddBasic(
        this AuthenticationBuilder builder,
        Func<IServiceProvider, IBasicCredentialValidator> validatorFactory,
        Action<BasicAuthenticationOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(validatorFactory);

        var options = new BasicAuthenticationOptions();
        configure?.Invoke(options);

        builder.Services.AddSingleton(validatorFactory);

        return builder.AddBasicCore(options, requireCredentials: false);
    }

    static AuthenticationBuilder AddBasicCore(
        this AuthenticationBuilder builder,
        BasicAuthenticationOptions options,
        bool requireCredentials
    )
    {
        if (requireCredentials && options.Credentials.Count == 0 && options.ValidateAsync is null)
            throw new InvalidOperationException(
                $"No accounts and no validator. Call {nameof(BasicAuthenticationOptions.AddUser)}, set " +
                $"{nameof(BasicAuthenticationOptions)}.{nameof(BasicAuthenticationOptions.ValidateAsync)}, " +
                $"or use the {nameof(AddBasic)}<TValidator> overload — otherwise the scheme would reject " +
                "everything."
            );

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(sp => new BasicAuthenticationHandler(
            sp.GetRequiredService<BasicAuthenticationOptions>(),
            sp.GetService<IBasicCredentialValidator>()
        ));

        // Registered as a challenge too, so an unauthenticated browser gets a password prompt
        // instead of a bare 401 it cannot act on.
        builder.Services.AddSingleton<IAuthenticationChallenge>(
            sp => sp.GetRequiredService<BasicAuthenticationHandler>()
        );

        return builder.AddScheme(sp => sp.GetRequiredService<BasicAuthenticationHandler>());
    }
}
