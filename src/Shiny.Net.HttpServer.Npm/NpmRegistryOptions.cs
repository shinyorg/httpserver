namespace Shiny.Net.HttpServer.Npm;

/// <summary>How an npm registry stores packages and who may change them.</summary>
public sealed class NpmRegistryOptions
{
    /// <summary>Where packages are kept. Required - normally a <see cref="DiskNpmPackageStore"/>.</summary>
    public INpmPackageStore Store { get; set; } = null!;

    /// <summary>
    /// Tokens accepted as <c>Authorization: Bearer</c>, mapped to the user they belong to - what goes
    /// in <c>.npmrc</c> as <c>//host/prefix/:_authToken=…</c>. Compared in constant time.
    /// <code>
    /// o.Tokens["npm_k3y…"] = "allan";
    /// </code>
    /// </summary>
    public IDictionary<string, string> Tokens { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Checks a token not in <see cref="Tokens"/> and returns its user, or null to refuse it - to look
    /// tokens up somewhere, or allow one only certain packages via <see cref="NpmTokenContext.PackageName"/>.
    /// </summary>
    public Func<NpmTokenContext, ValueTask<string?>>? ValidateTokenAsync { get; set; }

    /// <summary>
    /// Handles <c>npm login</c> (and <c>npm adduser</c>): checks a user name and password and returns
    /// a token for <c>.npmrc</c>, or null to refuse. Null - the default - leaves login unsupported, and
    /// tokens are handed out some other way.
    /// <code>
    /// o.LoginAsync = async ctx => await users.CheckAsync(ctx.UserName, ctx.Password) ? await tokens.IssueAsync(ctx.UserName) : null;
    /// </code>
    /// A token returned here is accepted afterwards only if <see cref="Tokens"/> or
    /// <see cref="ValidateTokenAsync"/> knows it.
    /// </summary>
    public Func<NpmLoginContext, ValueTask<string?>>? LoginAsync { get; set; }

    /// <summary>
    /// Requires a valid token for installs too, not only for publishing - a registry for a team's
    /// private code. npm sends the <c>.npmrc</c> token on every request to the registry.
    /// </summary>
    public bool RequireTokenForReads { get; set; }

    /// <summary>Serves the store without publish, unpublish, deprecate or dist-tag changes.</summary>
    public bool ReadOnly { get; set; }

    /// <summary>
    /// Allows <c>npm unpublish</c>. On by default. A version that is unpublished can be republished
    /// only under a new number - an install that pinned the old bytes must never get different ones.
    /// </summary>
    public bool AllowUnpublish { get; set; } = true;

    /// <summary>
    /// Largest tarball a publish accepts. Default 100 MB. npm sends it base64-encoded inside JSON,
    /// a third larger, and the server's <see cref="HttpServerLimits.MaxRequestBodySize"/> (30 MB by
    /// default) is checked first - raise it for the registry's write routes for big packages:
    /// <c>.ForWrites(r =&gt; r.WithRequestSizeLimit(...))</c> on what <c>MapNpmRegistry</c> returns.
    /// </summary>
    public long MaxTarballSize { get; set; } = 100L * 1024 * 1024;

    /// <summary>
    /// The registry's public URL, prefix included (<c>https://npm.example.com/npm</c>), for the
    /// tarball links in package metadata. Null builds them from each request's scheme and host.
    /// </summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>
    /// Another registry to answer from when a package is not here - normally
    /// <c>https://registry.npmjs.org</c> - so one registry URL serves both private and public
    /// packages. A package published here always wins over one of the same name upstream; tarballs
    /// of upstream packages are fetched by the client from upstream directly. Null - the default -
    /// answers 404 for anything not here, and clients use a scoped registry
    /// (<c>@acme:registry=…</c>) instead.
    /// </summary>
    public Uri? Upstream { get; set; }

    /// <summary>Runs before a publish is stored. Call <see cref="NpmPublishContext.Reject"/> to refuse it.</summary>
    public Func<NpmPublishContext, ValueTask>? OnBeforePublishAsync { get; set; }

    /// <summary>Runs after a version has been stored, before the client hears back.</summary>
    public Func<NpmPublishContext, ValueTask>? OnPublishedAsync { get; set; }

    /// <summary>The clock used for publish times. Replace it in tests.</summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    internal INpmPackageStore ResolveStore()
        => this.Store ?? throw new InvalidOperationException($"{nameof(NpmRegistryOptions)}.{nameof(this.Store)} is required.");
}

/// <summary>A token to check, for <see cref="NpmRegistryOptions.ValidateTokenAsync"/>.</summary>
public sealed class NpmTokenContext
{
    internal NpmTokenContext(HttpContext httpContext, string token, string? packageName)
    {
        this.HttpContext = httpContext;
        this.Token = token;
        this.PackageName = packageName;
    }

    public HttpContext HttpContext { get; }

    public string Token { get; }

    /// <summary>The package being read or changed, when the request is about one.</summary>
    public string? PackageName { get; }
}

/// <summary>An <c>npm login</c>, for <see cref="NpmRegistryOptions.LoginAsync"/>.</summary>
public sealed class NpmLoginContext
{
    internal NpmLoginContext(HttpContext httpContext, string userName, string password, string? email)
    {
        this.HttpContext = httpContext;
        this.UserName = userName;
        this.Password = password;
        this.Email = email;
    }

    public HttpContext HttpContext { get; }

    public string UserName { get; }

    public string Password { get; }

    public string? Email { get; }
}

/// <summary>A version being published, for <see cref="NpmRegistryOptions.OnBeforePublishAsync"/> and <see cref="NpmRegistryOptions.OnPublishedAsync"/>.</summary>
public sealed class NpmPublishContext
{
    internal NpmPublishContext(HttpContext httpContext, string packageName, NpmPackageVersion version, string userName)
    {
        this.HttpContext = httpContext;
        this.PackageName = packageName;
        this.Version = version;
        this.UserName = userName;
    }

    public HttpContext HttpContext { get; }

    public string PackageName { get; }

    public NpmPackageVersion Version { get; }

    /// <summary>The user the publishing token belongs to.</summary>
    public string UserName { get; }

    public bool IsRejected { get; private set; }

    internal int RejectStatusCode { get; private set; }

    internal string? RejectDetail { get; private set; }

    /// <summary>Refuses the publish. Only meaningful before it is stored.</summary>
    public void Reject(int statusCode = StatusCodes.Status403Forbidden, string? detail = null)
    {
        this.IsRejected = true;
        this.RejectStatusCode = statusCode;
        this.RejectDetail = detail;
    }
}
