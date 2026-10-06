using NuGet.Versioning;

namespace Shiny.Net.HttpServer.NuGet;

/// <summary>
/// One version of one package, as the feed knows it: what its <c>.nuspec</c> says, plus the
/// facts only the feed has - when it was pushed, whether it is listed, and the hash of its bytes.
/// </summary>
public sealed record NuGetPackage
{
    /// <summary>The id as the author cased it. Ids compare case-insensitively.</summary>
    public required string Id { get; init; }

    public required NuGetVersion Version { get; init; }

    /// <summary>
    /// False once the package has been unlisted. An unlisted version still restores for anyone who
    /// already depends on it; it just stops appearing in search and as a new install.
    /// </summary>
    public bool Listed { get; init; } = true;

    public DateTimeOffset Published { get; init; }

    /// <summary>The <c>.nupkg</c>'s size in bytes.</summary>
    public long Size { get; init; }

    /// <summary>Base64 SHA-512 of the <c>.nupkg</c>, as NuGet records it.</summary>
    public string? Sha512 { get; init; }

    public string? Title { get; init; }

    public string? Description { get; init; }

    public string? Summary { get; init; }

    /// <summary>As written in the nuspec: a comma-separated list.</summary>
    public string? Authors { get; init; }

    public string? Owners { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    public string? ProjectUrl { get; init; }

    public string? IconUrl { get; init; }

    public string? LicenseUrl { get; init; }

    /// <summary>An SPDX expression from <c>&lt;license type="expression"&gt;</c>.</summary>
    public string? LicenseExpression { get; init; }

    public bool RequireLicenseAcceptance { get; init; }

    public bool DevelopmentDependency { get; init; }

    public string? ReleaseNotes { get; init; }

    public string? Copyright { get; init; }

    public string? Language { get; init; }

    public string? MinClientVersion { get; init; }

    public string? RepositoryUrl { get; init; }

    /// <summary>Empty for an ordinary package, which NuGet treats as <c>Dependency</c>.</summary>
    public IReadOnlyList<NuGetPackageType> PackageTypes { get; init; } = [];

    public IReadOnlyList<NuGetDependencyGroup> DependencyGroups { get; init; } = [];

    /// <summary>
    /// True when a SemVer 1.0 client could not understand this package: a version with dotted
    /// prerelease labels or build metadata, or a dependency range that names one.
    /// </summary>
    public bool IsSemVer2 => this.Version.IsSemVer2 || this.DependencyGroups.Any(g => g.Dependencies.Any(d =>
        d.Range.MinVersion?.IsSemVer2 == true || d.Range.MaxVersion?.IsSemVer2 == true
    ));
}

/// <summary>A <c>&lt;packageType&gt;</c> - <c>Dependency</c>, <c>DotnetTool</c>, <c>Template</c>, ...</summary>
public sealed record NuGetPackageType(string Name, string? Version = null);

/// <summary>
/// The dependencies for one target framework. A null framework is the group that applies to every
/// framework - and also what a nuspec with no groups at all is read as.
/// </summary>
public sealed record NuGetDependencyGroup(string? TargetFramework, IReadOnlyList<NuGetDependency> Dependencies);

public sealed record NuGetDependency(string Id, VersionRange Range);
