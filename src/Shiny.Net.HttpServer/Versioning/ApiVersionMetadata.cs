namespace Shiny.Net.HttpServer.Versioning;

/// <summary>
/// The API versions an endpoint takes part in, attached to it as metadata.
/// <para>
/// Three lists, as in Asp.Versioning. <see cref="SupportedVersions"/> and
/// <see cref="DeprecatedVersions"/> are what the API <em>declares</em> — every version of the
/// resource, which is what gets reported to clients. <see cref="MappedVersions"/> narrows that to the
/// versions <em>this</em> handler serves, for the common case of one class declaring 1.0 and 2.0 and
/// each method serving one of them. With nothing mapped, the endpoint serves everything declared.
/// </para>
/// <para>
/// Endpoints without this metadata are not versioned: they answer whatever version is asked for,
/// including none, and nothing about them changes when versioning is added to an app.
/// </para>
/// </summary>
public sealed class ApiVersionMetadata
{
    /// <summary>Declared versions that are current.</summary>
    public IList<ApiVersion> SupportedVersions { get; } = new List<ApiVersion>();

    /// <summary>Declared versions that still work but are on their way out.</summary>
    public IList<ApiVersion> DeprecatedVersions { get; } = new List<ApiVersion>();

    /// <summary>The declared versions this endpoint actually serves. Empty means all of them.</summary>
    public IList<ApiVersion> MappedVersions { get; } = new List<ApiVersion>();

    /// <summary>
    /// True for an endpoint that serves every version identically — a health check under a versioned
    /// prefix, say. It never answers 400 for a version, and cannot share its route with versioned
    /// endpoints.
    /// </summary>
    public bool IsApiVersionNeutral { get; set; }

    /// <summary>True when the endpoint takes part in version selection at all.</summary>
    public bool IsVersioned
        => !this.IsApiVersionNeutral
            && (this.SupportedVersions.Count > 0 || this.DeprecatedVersions.Count > 0 || this.MappedVersions.Count > 0);

    /// <summary>The versions this endpoint serves, ascending and without duplicates.</summary>
    public IReadOnlyList<ApiVersion> ImplementedVersions
    {
        get
        {
            var set = new SortedSet<ApiVersion>();

            if (this.MappedVersions.Count > 0)
            {
                set.UnionWith(this.MappedVersions);
            }
            else
            {
                set.UnionWith(this.SupportedVersions);
                set.UnionWith(this.DeprecatedVersions);
            }

            return [.. set];
        }
    }

    /// <summary>True when this endpoint serves <paramref name="version"/>.</summary>
    public bool IsMappedTo(ApiVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);

        if (this.MappedVersions.Count > 0)
            return this.MappedVersions.Contains(version);

        return this.SupportedVersions.Contains(version) || this.DeprecatedVersions.Contains(version);
    }

    /// <summary>
    /// True when this endpoint names <paramref name="version"/> in <see cref="MappedVersions"/>,
    /// rather than serving it because it was declared. Where two endpoints of a route serve the same
    /// version, the explicit one is chosen.
    /// </summary>
    public bool IsExplicitlyMappedTo(ApiVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return this.MappedVersions.Contains(version);
    }

    /// <summary>
    /// True when <paramref name="version"/> is declared deprecated here and not also declared
    /// supported — a version one declaration retires and another still offers is not deprecated.
    /// </summary>
    public bool IsDeprecated(ApiVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return this.DeprecatedVersions.Contains(version) && !this.SupportedVersions.Contains(version);
    }

    /// <summary>
    /// A copy, so a version set or a route group can hand each endpoint its own — narrowing one route
    /// with <c>MapToApiVersion</c> must not narrow every other route in the group.
    /// </summary>
    public ApiVersionMetadata Clone()
    {
        var copy = new ApiVersionMetadata { IsApiVersionNeutral = this.IsApiVersionNeutral };

        foreach (var version in this.SupportedVersions)
            copy.SupportedVersions.Add(version);

        foreach (var version in this.DeprecatedVersions)
            copy.DeprecatedVersions.Add(version);

        foreach (var version in this.MappedVersions)
            copy.MappedVersions.Add(version);

        return copy;
    }

    public override string ToString()
    {
        if (this.IsApiVersionNeutral)
            return "version-neutral";

        return string.Join(", ", this.ImplementedVersions);
    }
}

/// <summary>
/// The versions an API declares, defined once and shared by every endpoint of it.
/// <code>
/// var users = app.NewApiVersionSet()
///     .HasDeprecatedApiVersion(0.9)
///     .HasApiVersion(1.0)
///     .HasApiVersion(2.0)
///     .Build();
///
/// app.MapGet("/users", V1).WithApiVersionSet(users).MapToApiVersion(1.0);
/// app.MapGet("/users", V2).WithApiVersionSet(users).MapToApiVersion(2.0);
/// </code>
/// <para>
/// The point of a set is reporting: every endpoint of the API advertises all of its versions, so a
/// client on 1.0 learns that 2.0 exists from any response.
/// </para>
/// </summary>
public sealed class ApiVersionSet
{
    readonly ApiVersionMetadata declared;

    internal ApiVersionSet(ApiVersionMetadata declared, string? name)
    {
        this.declared = declared;
        this.Name = name;
    }

    /// <summary>Optional name, for diagnostics.</summary>
    public string? Name { get; }

    public IReadOnlyList<ApiVersion> SupportedVersions => [.. this.declared.SupportedVersions];

    public IReadOnlyList<ApiVersion> DeprecatedVersions => [.. this.declared.DeprecatedVersions];

    public bool IsApiVersionNeutral => this.declared.IsApiVersionNeutral;

    /// <summary>Fresh metadata carrying this set's declarations, for one endpoint.</summary>
    internal ApiVersionMetadata CreateMetadata() => this.declared.Clone();
}

/// <summary>Builds an <see cref="ApiVersionSet"/>.</summary>
public sealed class ApiVersionSetBuilder(string? name = null)
{
    readonly ApiVersionMetadata declared = new();

    /// <summary>Declares a current version.</summary>
    public ApiVersionSetBuilder HasApiVersion(ApiVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);

        this.declared.SupportedVersions.Add(version);
        return this;
    }

    /// <inheritdoc cref="HasApiVersion(ApiVersion)"/>
    public ApiVersionSetBuilder HasApiVersion(double version) => this.HasApiVersion(new ApiVersion(version));

    /// <inheritdoc cref="HasApiVersion(ApiVersion)"/>
    public ApiVersionSetBuilder HasApiVersion(string version) => this.HasApiVersion(ApiVersion.Parse(version));

    /// <summary>Declares a deprecated version.</summary>
    public ApiVersionSetBuilder HasDeprecatedApiVersion(ApiVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);

        this.declared.DeprecatedVersions.Add(version);
        return this;
    }

    /// <inheritdoc cref="HasDeprecatedApiVersion(ApiVersion)"/>
    public ApiVersionSetBuilder HasDeprecatedApiVersion(double version) => this.HasDeprecatedApiVersion(new ApiVersion(version));

    /// <inheritdoc cref="HasDeprecatedApiVersion(ApiVersion)"/>
    public ApiVersionSetBuilder HasDeprecatedApiVersion(string version) => this.HasDeprecatedApiVersion(ApiVersion.Parse(version));

    /// <summary>Makes every endpoint using the set version-neutral.</summary>
    public ApiVersionSetBuilder IsApiVersionNeutral()
    {
        this.declared.IsApiVersionNeutral = true;
        return this;
    }

    public ApiVersionSet Build() => new(this.declared.Clone(), name);
}
