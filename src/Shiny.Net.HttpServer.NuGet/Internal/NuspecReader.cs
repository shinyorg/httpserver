using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using NuGet.Versioning;

namespace Shiny.Net.HttpServer.NuGet.Internal;

/// <summary>An error the feed answers with a status code instead of a 500.</summary>
sealed class NuGetFeedException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>
/// Reads a <c>.nupkg</c>'s manifest. Done by hand rather than with NuGet.Packaging, which would
/// bring Newtonsoft.Json and the signing stack along and is not trim-safe; the manifest is a small,
/// stable XML format and the parts of it a feed serves are a dozen elements.
/// <para>
/// Elements are matched by local name, so every nuspec schema version reads the same.
/// </para>
/// </summary>
static partial class NuspecReader
{
    /// <summary>
    /// Biggest manifest accepted. Real ones are a few KB; the cap is what stops a zip bomb dressed
    /// as a nuspec from being inflated into memory.
    /// </summary>
    const int MaxManifestBytes = 1024 * 1024;

    // NuGet's own rule (PackageIdValidator): word characters, joined by single dots or dashes.
    [GeneratedRegex(@"^\w+([.-]\w+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    public const int MaxIdLength = 100;

    public static bool IsValidId(string? id)
        => !string.IsNullOrEmpty(id) && id.Length <= MaxIdLength && IdPattern().IsMatch(id);

    /// <summary>The manifest bytes from a package file. The archive must have exactly one, at its root.</summary>
    public static byte[] ExtractManifest(Stream nupkg)
    {
        ZipArchive archive;

        try
        {
            archive = new ZipArchive(nupkg, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException)
        {
            throw new NuGetFeedException(StatusCodes.Status400BadRequest, "The upload is not a valid .nupkg (it is not a zip archive).");
        }

        using (archive)
        {
            ZipArchiveEntry? found = null;

            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.Contains('/') || !entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (found is not null)
                    throw new NuGetFeedException(StatusCodes.Status400BadRequest, "The package has more than one .nuspec at its root.");

                found = entry;
            }

            if (found is null)
                throw new NuGetFeedException(StatusCodes.Status400BadRequest, "The package has no .nuspec at its root.");

            if (found.Length > MaxManifestBytes)
                throw new NuGetFeedException(StatusCodes.Status400BadRequest, "The package's .nuspec is too large.");

            using var source = found.Open();
            using var buffer = new MemoryStream((int)found.Length);
            var chunk = new byte[16 * 1024];
            int read;

            // Counted rather than trusted: the length in the central directory is the archive's
            // claim, and the inflater will happily produce more.
            while ((read = source.Read(chunk)) > 0)
            {
                if (buffer.Length + read > MaxManifestBytes)
                    throw new NuGetFeedException(StatusCodes.Status400BadRequest, "The package's .nuspec is too large.");

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }
    }

    /// <summary>Parses a manifest into a package with its feed facts (size, hash, dates) still to fill in.</summary>
    public static NuGetPackage Parse(ReadOnlySpan<byte> manifest)
    {
        XDocument document;

        try
        {
            using var stream = new MemoryStream(manifest.ToArray(), writable: false);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true
            });

            document = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new NuGetFeedException(StatusCodes.Status400BadRequest, "The package's .nuspec is not valid XML: " + ex.Message);
        }

        var metadata = document.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "metadata")
            ?? throw new NuGetFeedException(StatusCodes.Status400BadRequest, "The package's .nuspec has no <metadata>.");

        var id = Text(metadata, "id");

        if (!IsValidId(id))
            throw new NuGetFeedException(StatusCodes.Status400BadRequest, $"'{id}' is not a valid package id.");

        if (!NuGetVersion.TryParse(Text(metadata, "version"), out var version))
            throw new NuGetFeedException(StatusCodes.Status400BadRequest, $"'{Text(metadata, "version")}' is not a valid package version.");

        var license = Child(metadata, "license");

        return new NuGetPackage
        {
            Id = id!,
            Version = version,
            Title = Text(metadata, "title"),
            Description = Text(metadata, "description"),
            Summary = Text(metadata, "summary"),
            Authors = Text(metadata, "authors"),
            Owners = Text(metadata, "owners"),
            Tags = Text(metadata, "tags")?.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [],
            ProjectUrl = Text(metadata, "projectUrl"),
            IconUrl = Text(metadata, "iconUrl"),
            LicenseUrl = Text(metadata, "licenseUrl"),
            LicenseExpression = license?.Attribute("type")?.Value == "expression" ? Trimmed(license.Value) : null,
            RequireLicenseAcceptance = Flag(metadata, "requireLicenseAcceptance"),
            DevelopmentDependency = Flag(metadata, "developmentDependency"),
            ReleaseNotes = Text(metadata, "releaseNotes"),
            Copyright = Text(metadata, "copyright"),
            Language = Text(metadata, "language"),
            MinClientVersion = Trimmed(metadata.Attribute("minClientVersion")?.Value),
            RepositoryUrl = Trimmed(Child(metadata, "repository")?.Attribute("url")?.Value),
            PackageTypes = ReadPackageTypes(metadata),
            DependencyGroups = ReadDependencies(metadata)
        };
    }

    static List<NuGetPackageType> ReadPackageTypes(XElement metadata)
    {
        var list = new List<NuGetPackageType>();

        if (Child(metadata, "packageTypes") is { } types)
        {
            foreach (var type in types.Elements().Where(e => e.Name.LocalName == "packageType"))
            {
                if (Trimmed(type.Attribute("name")?.Value) is { } name)
                    list.Add(new NuGetPackageType(name, Trimmed(type.Attribute("version")?.Value)));
            }
        }

        return list;
    }

    static List<NuGetDependencyGroup> ReadDependencies(XElement metadata)
    {
        var groups = new List<NuGetDependencyGroup>();

        if (Child(metadata, "dependencies") is not { } dependencies)
            return groups;

        var grouped = dependencies.Elements().Where(e => e.Name.LocalName == "group").ToList();

        if (grouped.Count > 0)
        {
            foreach (var group in grouped)
                groups.Add(new NuGetDependencyGroup(Trimmed(group.Attribute("targetFramework")?.Value), ReadDependencyList(group)));
        }
        else
        {
            // The pre-2.0 form: dependencies straight under <dependencies>, for every framework.
            var flat = ReadDependencyList(dependencies);

            if (flat.Count > 0)
                groups.Add(new NuGetDependencyGroup(null, flat));
        }

        return groups;
    }

    static List<NuGetDependency> ReadDependencyList(XElement parent)
    {
        var list = new List<NuGetDependency>();

        foreach (var dependency in parent.Elements().Where(e => e.Name.LocalName == "dependency"))
        {
            var id = Trimmed(dependency.Attribute("id")?.Value);

            if (id is null)
                continue;

            var raw = Trimmed(dependency.Attribute("version")?.Value);
            var range = raw is null ? VersionRange.All : VersionRange.TryParse(raw, out var parsed) ? parsed : null;

            if (range is null)
                throw new NuGetFeedException(StatusCodes.Status400BadRequest, $"Dependency '{id}' has an invalid version range '{raw}'.");

            list.Add(new NuGetDependency(id, range));
        }

        return list;
    }

    static XElement? Child(XElement parent, string name)
        => parent.Elements().FirstOrDefault(e => e.Name.LocalName == name);

    static string? Text(XElement parent, string name) => Trimmed(Child(parent, name)?.Value);

    static bool Flag(XElement parent, string name)
        => bool.TryParse(Text(parent, name), out var value) && value;

    static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
