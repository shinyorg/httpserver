using System.Globalization;

namespace Shiny.Net.HttpServer.Localization;

/// <summary>
/// Reads a culture preference from a request. Returns null when the request says nothing, so the
/// next provider gets a turn.
/// </summary>
public interface IRequestCultureProvider
{
    ValueTask<ProviderCultureResult?> DetermineProviderCultureResult(HttpContext context);
}

/// <summary>
/// <c>?culture=fr-CA&amp;ui-culture=fr</c>. Either key alone sets both. First in the default
/// order, because a culture in the URL is someone choosing on purpose — a language switcher's link,
/// or a developer checking a translation.
/// </summary>
public sealed class QueryStringRequestCultureProvider : IRequestCultureProvider
{
    /// <summary>The formatting culture's key. <c>culture</c> by default.</summary>
    public string QueryStringKey { get; set; } = "culture";

    /// <summary>The text culture's key. <c>ui-culture</c> by default.</summary>
    public string UIQueryStringKey { get; set; } = "ui-culture";

    public ValueTask<ProviderCultureResult?> DetermineProviderCultureResult(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var culture = context.Request.Query.GetFirst(this.QueryStringKey);
        var uiCulture = context.Request.Query.GetFirst(this.UIQueryStringKey);

        return ValueTask.FromResult(Pair(culture, uiCulture));
    }

    internal static ProviderCultureResult? Pair(string? culture, string? uiCulture)
    {
        culture = culture is { Length: > 0 } ? culture.Trim() : null;
        uiCulture = uiCulture is { Length: > 0 } ? uiCulture.Trim() : null;

        if (culture is not { Length: > 0 } && uiCulture is not { Length: > 0 })
            return null;

        return new ProviderCultureResult(culture ?? uiCulture!, uiCulture ?? culture!);
    }
}

/// <summary>
/// A cookie holding the visitor's choice, in ASP.NET Core's format — <c>c=fr-CA|uic=fr</c> — so a
/// cookie written by either server reads on the other. The provider only reads it; the app writes
/// it when the user picks a language:
/// <code>
/// ctx.Response.Cookies.Append(
///     CookieRequestCultureProvider.DefaultCookieName,
///     CookieRequestCultureProvider.MakeCookieValue(new RequestCulture("fr-CA")),
///     new CookieOptions { MaxAge = TimeSpan.FromDays(365), SameSite = SameSiteMode.Lax });
/// </code>
/// </summary>
public sealed class CookieRequestCultureProvider : IRequestCultureProvider
{
    const string CulturePrefix = "c=";
    const string UICulturePrefix = "uic=";

    /// <summary><c>.Shiny.Culture</c>.</summary>
    public static readonly string DefaultCookieName = ".Shiny.Culture";

    public string CookieName { get; set; } = DefaultCookieName;

    public ValueTask<ProviderCultureResult?> DetermineProviderCultureResult(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var value = context.Request.Cookies[this.CookieName];
        return ValueTask.FromResult(value is { Length: > 0 } ? ParseCookieValue(value) : null);
    }

    /// <summary>The cookie value for a culture choice.</summary>
    public static string MakeCookieValue(RequestCulture requestCulture)
    {
        ArgumentNullException.ThrowIfNull(requestCulture);
        return $"{CulturePrefix}{requestCulture.Culture.Name}|{UICulturePrefix}{requestCulture.UICulture.Name}";
    }

    /// <summary>Reads a cookie value, or null when it is not one this provider wrote.</summary>
    public static ProviderCultureResult? ParseCookieValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        // Cookie values arrive as sent, and browsers percent-encode '|' and '=' in some paths.
        if (value.Contains('%'))
            value = Uri.UnescapeDataString(value);

        string? culture = null;
        string? uiCulture = null;

        foreach (var part in value.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.StartsWith(CulturePrefix, StringComparison.OrdinalIgnoreCase))
                culture = part[CulturePrefix.Length..];
            else if (part.StartsWith(UICulturePrefix, StringComparison.OrdinalIgnoreCase))
                uiCulture = part[UICulturePrefix.Length..];
        }

        return QueryStringRequestCultureProvider.Pair(culture, uiCulture);
    }
}

/// <summary>
/// The browser's <c>Accept-Language</c>, in quality order: <c>fr-CA, fr;q=0.9, en;q=0.5</c>. Last in
/// the default order — it is the operating system's setting, not a choice made for this site.
/// </summary>
public sealed class AcceptLanguageHeaderRequestCultureProvider : IRequestCultureProvider
{
    /// <summary>
    /// How many of the caller's highest-weighted languages are considered. Three by default, as in
    /// ASP.NET Core: past that the caller is listing everything they can read, and every extra entry
    /// is more matching work on a header an attacker controls.
    /// </summary>
    public int MaximumAcceptLanguageHeaderValuesToTry { get; set; } = 3;

    public ValueTask<ProviderCultureResult?> DetermineProviderCultureResult(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var header = context.Request.Headers["Accept-Language"];
        if (header.Count == 0)
            return ValueTask.FromResult<ProviderCultureResult?>(null);

        var languages = Parse(header.Count == 1 ? header[0] : string.Join(',', (IEnumerable<string?>)header), this.MaximumAcceptLanguageHeaderValuesToTry);

        return ValueTask.FromResult(
            languages.Count == 0 ? null : new ProviderCultureResult(languages, languages)
        );
    }

    /// <summary>
    /// The language tags in <paramref name="header"/>, highest quality first, ties in the order
    /// written. <c>*</c> and anything weighted <c>q=0</c> — "not this" — are dropped.
    /// </summary>
    internal static List<string> Parse(string? header, int max)
    {
        var entries = new List<(string Tag, double Quality, int Order)>();
        if (string.IsNullOrWhiteSpace(header) || max <= 0)
            return [];

        var order = 0;
        var seen = 0;
        foreach (var item in header.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            // Bounded regardless of max: this is a header anyone can send, and the sort below is not free.
            if (++seen > 32)
                break;

            var semicolon = item.IndexOf(';');
            var tag = (semicolon < 0 ? item : item[..semicolon]).Trim();
            var quality = 1.0;

            if (semicolon >= 0)
            {
                foreach (var parameter in item[(semicolon + 1)..].Split(';', StringSplitOptions.TrimEntries))
                {
                    if (parameter.StartsWith("q=", StringComparison.OrdinalIgnoreCase)
                        && !double.TryParse(parameter.AsSpan(2), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out quality))
                    {
                        quality = 0;
                    }
                }
            }

            if (quality <= 0 || tag == "*" || !IsLanguageTag(tag))
                continue;

            entries.Add((tag, quality, order++));
        }

        entries.Sort(static (a, b) => a.Quality != b.Quality ? b.Quality.CompareTo(a.Quality) : a.Order.CompareTo(b.Order));

        var result = new List<string>(Math.Min(max, entries.Count));
        foreach (var entry in entries)
        {
            if (result.Count == max)
                break;

            result.Add(entry.Tag);
        }

        return result;
    }

    /// <summary>A BCP 47 tag's shape: 1–8 alphanumerics, separated by hyphens.</summary>
    internal static bool IsLanguageTag(ReadOnlySpan<char> tag)
    {
        if (tag.IsEmpty || tag.Length > 64)
            return false;

        var run = 0;
        foreach (var c in tag)
        {
            if (c is '-' or '_')
            {
                if (run == 0)
                    return false;

                run = 0;
                continue;
            }

            if (!char.IsAsciiLetterOrDigit(c) || ++run > 8)
                return false;
        }

        return run > 0;
    }
}

/// <summary>
/// A provider from a delegate — a route value, a claim on the signed-in user, a setting in the app.
/// <code>
/// o.AddInitialRequestCultureProvider(new CustomRequestCultureProvider(ctx =>
///     ValueTask.FromResult&lt;ProviderCultureResult?&gt;(
///         ctx.User.FindFirst("locale")?.Value is { } locale ? new ProviderCultureResult(locale) : null)));
/// </code>
/// </summary>
public sealed class CustomRequestCultureProvider(Func<HttpContext, ValueTask<ProviderCultureResult?>> provider) : IRequestCultureProvider
{
    readonly Func<HttpContext, ValueTask<ProviderCultureResult?>> provider = provider ?? throw new ArgumentNullException(nameof(provider));

    public ValueTask<ProviderCultureResult?> DetermineProviderCultureResult(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return this.provider(context);
    }
}
