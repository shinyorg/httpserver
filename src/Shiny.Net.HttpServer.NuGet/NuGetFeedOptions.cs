namespace Shiny.Net.HttpServer.NuGet;

/// <summary>What <c>DELETE</c> does to a package version.</summary>
public enum NuGetDeleteBehavior
{
    /// <summary>
    /// Hides the version from search and new installs but keeps it restorable - what nuget.org does,
    /// and the safe default, since anything already depending on that version keeps building.
    /// </summary>
    Unlist,

    /// <summary>Removes the version and its files for good.</summary>
    Delete
}

/// <summary>How a NuGet feed stores packages and who may change them.</summary>
public sealed class NuGetFeedOptions
{
    /// <summary>
    /// Where packages are kept. Required - normally a <see cref="DiskNuGetPackageStore"/>.
    /// </summary>
    public INuGetPackageStore Store { get; set; } = null!;

    /// <summary>
    /// Keys accepted in <c>X-NuGet-ApiKey</c> for push, delete and relist - what
    /// <c>dotnet nuget push --api-key</c> sends. Compared in constant time.
    /// </summary>
    public ICollection<string> ApiKeys { get; } = new List<string>();

    /// <summary>
    /// Checks an API key in place of (or as well as) <see cref="ApiKeys"/> - to look keys up
    /// somewhere, or scope one to certain package ids via <see cref="NuGetApiKeyContext.PackageId"/>.
    /// A key in <see cref="ApiKeys"/> is accepted without calling this.
    /// </summary>
    public Func<NuGetApiKeyContext, ValueTask<bool>>? ValidateApiKeyAsync { get; set; }

    /// <summary>
    /// Whether push, delete and relist need an API key. On by default, and mapping the feed fails
    /// when it is on and no key is configured. Turn it off only when the write routes are protected
    /// some other way - <c>.RequireAuthorization()</c> on <see cref="NuGetFeedMountBuilder.WriteRoutes"/>
    /// with Basic credentials in <c>nuget.config</c>, for example.
    /// </summary>
    public bool RequireApiKey { get; set; } = true;

    /// <summary>
    /// Serves the packages in <see cref="Store"/> without mapping push, delete or relist at all - a
    /// mirror, or a folder of packages filled by something else.
    /// </summary>
    public bool ReadOnly { get; set; }

    /// <summary>What <c>dotnet nuget delete</c> does. <see cref="NuGetDeleteBehavior.Unlist"/> by default.</summary>
    public NuGetDeleteBehavior DeleteBehavior { get; set; } = NuGetDeleteBehavior.Unlist;

    /// <summary>
    /// Lets a push replace a version that already exists instead of answering 409. Off by default,
    /// and best left off: a version that means different bytes on different days is a restore that
    /// cannot be trusted, and every machine that already cached the old bytes keeps them.
    /// </summary>
    public bool AllowOverwrite { get; set; }

    /// <summary>
    /// Largest <c>.nupkg</c> a push accepts. Default 250 MB, nuget.org's limit. The server's own
    /// <see cref="HttpServerLimits.MaxRequestBodySize"/> (30 MB by default) applies first, so raise
    /// that too if packages are bigger.
    /// </summary>
    public long MaxPackageSize { get; set; } = 250L * 1024 * 1024;

    /// <summary>
    /// The feed's public URL, up to and including its prefix (<c>https://pkgs.example.com/nuget</c>),
    /// for the absolute links the protocol is made of. Null builds them from each request's scheme
    /// and host, which is right unless a proxy in front rewrites them and forwarded headers are not
    /// being honoured.
    /// </summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>
    /// Runs before a pushed package is stored - to enforce naming rules, required metadata, or which
    /// key may push which id. Call <see cref="NuGetPushContext.Reject"/> to refuse it.
    /// </summary>
    public Func<NuGetPushContext, ValueTask>? OnBeforePushAsync { get; set; }

    /// <summary>Runs after a pushed package has been stored, before the client hears back.</summary>
    public Func<NuGetPushContext, ValueTask>? OnPackagePushedAsync { get; set; }

    /// <summary>The clock used for publish times. Replace it in tests.</summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    internal INuGetPackageStore ResolveStore()
        => this.Store ?? throw new InvalidOperationException($"{nameof(NuGetFeedOptions)}.{nameof(this.Store)} is required.");
}

/// <summary>An API key to check, for <see cref="NuGetFeedOptions.ValidateApiKeyAsync"/>.</summary>
public sealed class NuGetApiKeyContext
{
    internal NuGetApiKeyContext(HttpContext httpContext, string apiKey, string? packageId)
    {
        this.HttpContext = httpContext;
        this.ApiKey = apiKey;
        this.PackageId = packageId;
    }

    public HttpContext HttpContext { get; }

    public string ApiKey { get; }

    /// <summary>
    /// The package being deleted or relisted. Null for a push, where the id is not known until the
    /// package has been read - check it in <see cref="NuGetFeedOptions.OnBeforePushAsync"/>, which
    /// has the key as <see cref="NuGetPushContext.ApiKey"/>.
    /// </summary>
    public string? PackageId { get; }
}

/// <summary>A pushed package, for <see cref="NuGetFeedOptions.OnBeforePushAsync"/> and <see cref="NuGetFeedOptions.OnPackagePushedAsync"/>.</summary>
public sealed class NuGetPushContext
{
    internal NuGetPushContext(HttpContext httpContext, NuGetPackage package, string? apiKey)
    {
        this.HttpContext = httpContext;
        this.Package = package;
        this.ApiKey = apiKey;
    }

    public HttpContext HttpContext { get; }

    public NuGetPackage Package { get; }

    /// <summary>The key the client pushed with, already validated. Null when keys are not required.</summary>
    public string? ApiKey { get; }

    /// <summary>True once <see cref="Reject"/> has been called.</summary>
    public bool IsRejected { get; private set; }

    internal int RejectStatusCode { get; private set; }

    internal string? RejectDetail { get; private set; }

    /// <summary>Refuses the push with <paramref name="statusCode"/>. Only meaningful before it is stored.</summary>
    public void Reject(int statusCode = StatusCodes.Status403Forbidden, string? detail = null)
    {
        this.IsRejected = true;
        this.RejectStatusCode = statusCode;
        this.RejectDetail = detail;
    }
}
