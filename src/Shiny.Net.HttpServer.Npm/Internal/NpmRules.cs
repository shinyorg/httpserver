using System.Globalization;
using System.Text.RegularExpressions;

namespace Shiny.Net.HttpServer.Npm.Internal;

/// <summary>An error answered with a status code and npm's <c>{"error": "..."}</c> body instead of a 500.</summary>
sealed class NpmException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>npm's rules for names and versions - the ones a new package has to follow.</summary>
static partial class NpmRules
{
    public const int MaxNameLength = 214;

    // validate-npm-package-name for new packages: lower case, URL-safe, optionally scoped. '*' is
    // left out of the scope - npm allows it, but no file system on Windows does, and the name
    // becomes a directory.
    [GeneratedRegex(@"^(?:@[a-z0-9\-~][a-z0-9\-._~]*/)?[a-z0-9\-~][a-z0-9\-._~]*$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    // semver.org's own expression.
    [GeneratedRegex(@"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-((?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*)(?:\.(?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*))*))?(?:\+([0-9a-zA-Z-]+(?:\.[0-9a-zA-Z-]+)*))?$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    public static bool IsValidName(string? name)
        => !string.IsNullOrEmpty(name)
            && name.Length <= MaxNameLength
            && NamePattern().IsMatch(name)
            && name is not ("node_modules" or "favicon.ico");

    /// <summary>The part after the scope: <c>@acme/widgets</c> → <c>widgets</c>. Tarballs are named after it.</summary>
    public static string Unscoped(string name)
        => name.StartsWith('@') ? name[(name.IndexOf('/') + 1)..] : name;

    public static string TarballFile(string name, string version) => $"{Unscoped(name)}-{version}.tgz";

    public static bool IsValidVersion(string? version) => version is not null && version.Length <= 256 && VersionPattern().IsMatch(version);

    public static bool IsPrerelease(string version) => version.Split('+')[0].Contains('-');

    /// <summary>SemVer 2.0.0 precedence: build metadata ignored, a prerelease below its release.</summary>
    public static int Compare(string a, string b)
    {
        var x = VersionPattern().Match(a);
        var y = VersionPattern().Match(b);

        if (!x.Success || !y.Success)
            return string.CompareOrdinal(a, b);

        for (var i = 1; i <= 3; i++)
        {
            var c = CompareNumeric(x.Groups[i].Value, y.Groups[i].Value);

            if (c != 0)
                return c;
        }

        var pa = x.Groups[4].Success ? x.Groups[4].Value : null;
        var pb = y.Groups[4].Success ? y.Groups[4].Value : null;

        if (pa is null || pb is null)
            return pa is null ? (pb is null ? 0 : 1) : -1;

        var ia = pa.Split('.');
        var ib = pb.Split('.');

        for (var i = 0; i < Math.Min(ia.Length, ib.Length); i++)
        {
            var na = ia[i].All(char.IsAsciiDigit);
            var nb = ib[i].All(char.IsAsciiDigit);

            var c = (na, nb) switch
            {
                (true, true) => CompareNumeric(ia[i], ib[i]),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(ia[i], ib[i])
            };

            if (c != 0)
                return c;
        }

        return ia.Length.CompareTo(ib.Length);
    }

    static int CompareNumeric(string a, string b)
        => a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b);

    /// <summary>Sorts version strings by SemVer precedence.</summary>
    public static IComparer<string> VersionComparer { get; } = Comparer<string>.Create(Compare);

    /// <summary>The ISO 8601 form npm writes in <c>time</c>: <c>2026-10-06T14:15:00.000Z</c>.</summary>
    public static string Iso(DateTimeOffset value)
        => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
