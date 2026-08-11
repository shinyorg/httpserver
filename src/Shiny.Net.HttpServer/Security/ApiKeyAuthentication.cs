using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;

namespace Shiny.Net.HttpServer.Security;

/// <summary>
/// A key the server accepts, and who presenting it turns out to be.
/// <para>
/// The key itself is not kept. Only its hash is, so a memory dump or a logged options object does
/// not hand over working credentials — and comparing hashes also equalises length, which is what
/// makes the comparison genuinely fixed-time.
/// </para>
/// </summary>
public sealed class ApiKeyEntry
{
    readonly byte[] hash;

    internal ApiKeyEntry(string key, string name, IEnumerable<string>? roles, IEnumerable<Claim>? claims)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        this.hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        this.Name = name;
        this.Roles = roles is null ? [] : [.. roles];
        this.Claims = claims is null ? [] : [.. claims];
    }

    /// <summary>Who this key represents. Becomes the principal's name.</summary>
    public string Name { get; }

    public IReadOnlyList<string> Roles { get; }

    public IReadOnlyList<Claim> Claims { get; }

    internal bool Matches(ReadOnlySpan<byte> presentedHash) => CryptographicOperations.FixedTimeEquals(this.hash, presentedHash);
}

/// <summary>How an API key is presented and what it means.</summary>
public sealed class ApiKeyOptions
{
    readonly List<ApiKeyEntry> keys = [];

    /// <summary>The scheme name, reported on <c>WWW-Authenticate</c> and <c>ctx.Authentication</c>.</summary>
    public string Scheme { get; set; } = "ApiKey";

    /// <summary>Header carrying the key. Null disables header extraction.</summary>
    public string? HeaderName { get; set; } = "X-API-Key";

    /// <summary>
    /// Also accepts <c>Authorization: ApiKey {key}</c>, for clients that only know how to set an
    /// authorization header.
    /// </summary>
    public bool AllowAuthorizationHeader { get; set; } = true;

    /// <summary>
    /// Query parameter carrying the key. Null by default, and worth leaving that way: a query
    /// string ends up in server logs, browser history and <c>Referer</c> headers, so a key sent
    /// this way is a key you have to assume is written down somewhere.
    /// </summary>
    public string? QueryParameterName { get; set; }

    /// <summary>The keys this server accepts, for the fixed-list case.</summary>
    public IReadOnlyList<ApiKeyEntry> Keys => this.keys;

    /// <summary>
    /// Validates a key that is not in <see cref="Keys"/> — a database lookup, a remote check.
    /// Return null to reject.
    /// <para>
    /// Consulted after the static list, so a configured key never costs a round trip.
    /// </para>
    /// </summary>
    public Func<string, CancellationToken, ValueTask<ClaimsPrincipal?>>? ValidateAsync { get; set; }

    /// <summary>Adds a key the server accepts.</summary>
    public ApiKeyOptions AddKey(string key, string name, params string[] roles)
    {
        this.keys.Add(new ApiKeyEntry(key, name, roles, claims: null));
        return this;
    }

    /// <summary>Adds a key with claims beyond a name and roles.</summary>
    public ApiKeyOptions AddKey(string key, string name, IEnumerable<string>? roles, IEnumerable<Claim>? claims)
    {
        this.keys.Add(new ApiKeyEntry(key, name, roles, claims));
        return this;
    }
}

/// <summary>
/// Authenticates a caller by a shared secret sent with the request.
/// <para>
/// The right tool for a device, a script or a webhook — anything with no user to log in and no
/// browser to hold a session. It says nothing about *who* beyond what the key was issued to, which
/// is why a key maps to a named principal rather than an anonymous "authenticated" flag.
/// </para>
/// <code>
/// builder.Services.AddAuthentication().AddApiKey(o =>
/// {
///     o.AddKey(configuration["Keys:Ingest"]!, "ingest-service", "writer");
///     o.ValidateAsync = async (key, ct) => await store.FindPrincipalAsync(key, ct);
/// });
/// </code>
/// </summary>
public sealed class ApiKeyAuthenticationHandler(ApiKeyOptions options) : IAuthenticationHandler
{
    readonly ApiKeyOptions options = options ?? throw new ArgumentNullException(nameof(options));

    public string Scheme => this.options.Scheme;

    public async ValueTask<AuthenticateResult> AuthenticateAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (this.TryGetKey(context.Request) is not { Length: > 0 } key)
            return AuthenticateResult.NoResult();

        Span<byte> presented = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(key), presented);

        // Every entry is checked even after a match. Returning early would make the response time
        // depend on the key's position in the list, which is a slow but real way to learn things.
        ApiKeyEntry? matched = null;

        foreach (var entry in this.options.Keys)
        {
            if (entry.Matches(presented))
                matched = entry;
        }

        if (matched is not null)
            return AuthenticateResult.Success(BuildPrincipal(matched, this.Scheme));

        if (this.options.ValidateAsync is { } validate)
        {
            var principal = await validate(key, context.RequestAborted).ConfigureAwait(false);

            if (principal is not null)
                return AuthenticateResult.Success(principal);
        }

        // Attempted and rejected, not anonymous: the caller meant to authenticate and got it wrong,
        // and saying so is what stops a later handler papering over it.
        return AuthenticateResult.Fail("The API key is not recognised.");
    }

    string? TryGetKey(HttpRequest request)
    {
        if (this.options.HeaderName is { Length: > 0 } header
            && request.Headers.GetFirst(header) is { Length: > 0 } fromHeader)
            return fromHeader.Trim();

        if (this.options.AllowAuthorizationHeader
            && request.Headers.GetFirst(HeaderNames.Authorization) is { Length: > 0 } authorization)
        {
            var prefix = this.options.Scheme + " ";

            if (authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return authorization[prefix.Length..].Trim();
        }

        if (this.options.QueryParameterName is { Length: > 0 } parameter
            && request.Query[parameter].ToString() is { Length: > 0 } fromQuery)
            return fromQuery.Trim();

        return null;
    }

    static ClaimsPrincipal BuildPrincipal(ApiKeyEntry entry, string scheme)
    {
        var claims = new List<Claim>(entry.Claims.Count + entry.Roles.Count + 1)
        {
            new(ClaimTypes.Name, entry.Name)
        };

        foreach (var role in entry.Roles)
            claims.Add(new Claim(ClaimTypes.Role, role));

        claims.AddRange(entry.Claims);

        return new ClaimsPrincipal(new ClaimsIdentity(claims, scheme, ClaimTypes.Name, ClaimTypes.Role));
    }
}

/// <summary>Registering API key authentication.</summary>
public static class ApiKeyAuthenticationBuilderExtensions
{
    /// <summary>
    /// Adds the <c>ApiKey</c> scheme.
    /// <code>
    /// builder.Services.AddAuthentication().AddApiKey(o => o.AddKey(secret, "ingest-service", "writer"));
    /// </code>
    /// </summary>
    public static AuthenticationBuilder AddApiKey(this AuthenticationBuilder builder, Action<ApiKeyOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new ApiKeyOptions();
        configure(options);

        if (options.Keys.Count == 0 && options.ValidateAsync is null)
            throw new InvalidOperationException(
                $"No keys and no validator. Call {nameof(ApiKeyOptions.AddKey)} or set " +
                $"{nameof(ApiKeyOptions)}.{nameof(ApiKeyOptions.ValidateAsync)}, or the scheme would reject everything."
            );

        builder.Services.AddSingleton(options);

        return builder.AddScheme(sp => new ApiKeyAuthenticationHandler(sp.GetRequiredService<ApiKeyOptions>()));
    }
}
