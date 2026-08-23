namespace Shiny.Net.HttpServer;

/// <summary>
/// Requires a valid antiforgery token on a generated endpoint, whether or not the default rule
/// would have asked for one.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class ValidateAntiforgeryAttribute : Attribute;

/// <summary>
/// Exempts a generated endpoint from the antiforgery check — a webhook receiver, or an upload from
/// a native client that has no cookie to abuse in the first place.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class DisableAntiforgeryAttribute : Attribute;
