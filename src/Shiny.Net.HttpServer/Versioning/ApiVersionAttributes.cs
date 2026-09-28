using System.Globalization;

namespace Shiny.Net.HttpServer;

/// <summary>
/// Declares an API version on a generated endpoint class or method.
/// <code>
/// [Route("/users")]
/// [ApiVersion("0.9", Deprecated = true)]
/// [ApiVersion("1.0")]
/// [ApiVersion("2.0")]
/// public class UserEndpoints
/// {
///     [Get("")] public UserV1[] List() => ...;                               // 0.9 and 1.0
///     [Get("")] [MapToApiVersion("2.0")] public UserV2[] ListV2() => ...;     // 2.0 only
/// }
/// </code>
/// <para>
/// On the class it declares versions of the whole API — every one is reported to clients from every
/// endpoint. On a method it also scopes that method to the versions it names, the way
/// <see cref="MapToApiVersionAttribute"/> does. The source generator turns these into
/// <c>ApiVersionMetadata</c> at compile time and rejects a version it cannot parse, so nothing is read
/// by reflection when the app starts.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class ApiVersionAttribute : Attribute
{
    /// <param name="version">The version text: <c>1.0</c>, <c>2</c>, <c>1.0-beta</c>, <c>2026-01-15</c>.</param>
    public ApiVersionAttribute(string version) => this.Version = version;

    /// <param name="version">The version as a number, e.g. <c>1.0</c>.</param>
    public ApiVersionAttribute(double version) => this.Version = version.ToString("0.0###############", CultureInfo.InvariantCulture);

    public string Version { get; }

    /// <summary>
    /// Marks the version deprecated: it keeps working, and is reported in <c>api-deprecated-versions</c>
    /// and flagged in its OpenAPI document.
    /// </summary>
    public bool Deprecated { get; set; }
}

/// <summary>
/// Scopes a method of a versioned class to some of the versions the class declares, so two methods
/// with the same route can serve different versions.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class MapToApiVersionAttribute : Attribute
{
    public MapToApiVersionAttribute(string version) => this.Version = version;

    public MapToApiVersionAttribute(double version) => this.Version = version.ToString("0.0###############", CultureInfo.InvariantCulture);

    public string Version { get; }
}

/// <summary>
/// Marks an endpoint class or method as serving every API version identically. It accepts any
/// requested version, or none, and is never answered 400 for one.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class ApiVersionNeutralAttribute : Attribute;
