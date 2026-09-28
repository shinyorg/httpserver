using System.Globalization;

namespace Shiny.Net.HttpServer.Localization;

/// <summary>
/// The pair of cultures a request runs under. Shaped after ASP.NET Core's type of the same name.
/// <para>
/// Two, because they answer different questions. <see cref="Culture"/> formats — dates, numbers,
/// currency, sort order. <see cref="UICulture"/> picks text — which <c>.resx</c> a
/// <c>ResourceManager</c> reads. A Canadian user reading English with French-Canadian number
/// formatting is ordinary, and one culture cannot say it.
/// </para>
/// </summary>
public sealed class RequestCulture
{
    /// <summary>The same culture for formatting and for text.</summary>
    public RequestCulture(CultureInfo culture)
        : this(culture, culture)
    {
    }

    /// <summary>Separate cultures for formatting and for text.</summary>
    public RequestCulture(CultureInfo culture, CultureInfo uiCulture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        ArgumentNullException.ThrowIfNull(uiCulture);

        this.Culture = culture;
        this.UICulture = uiCulture;
    }

    /// <summary>
    /// The same culture for formatting and for text, by name. Throws under
    /// <c>InvariantGlobalization</c>, where no named culture exists — use
    /// <see cref="RequestLocalizationOptions.SetDefaultCulture"/> to be tolerant of that.
    /// </summary>
    public RequestCulture(string culture)
        : this(CultureInfo.GetCultureInfo(culture))
    {
    }

    /// <summary>Separate cultures for formatting and for text, by name.</summary>
    public RequestCulture(string culture, string uiCulture)
        : this(CultureInfo.GetCultureInfo(culture), CultureInfo.GetCultureInfo(uiCulture))
    {
    }

    /// <summary>Formats dates, numbers and currency; becomes <see cref="CultureInfo.CurrentCulture"/>.</summary>
    public CultureInfo Culture { get; }

    /// <summary>Selects localized text; becomes <see cref="CultureInfo.CurrentUICulture"/>.</summary>
    public CultureInfo UICulture { get; }

    public override string ToString()
        => this.Culture.Equals(this.UICulture)
            ? this.Culture.Name
            : $"{this.Culture.Name} (UI {this.UICulture.Name})";
}

/// <summary>
/// What one <see cref="IRequestCultureProvider"/> read from a request: culture names in the
/// caller's order of preference, not yet checked against what the app supports.
/// </summary>
public sealed class ProviderCultureResult
{
    /// <summary>The same name for formatting and for text.</summary>
    public ProviderCultureResult(string culture)
        : this([culture], [culture])
    {
    }

    /// <summary>Separate names for formatting and for text.</summary>
    public ProviderCultureResult(string culture, string uiCulture)
        : this([culture], [uiCulture])
    {
    }

    /// <summary>Candidates in preference order — what an <c>Accept-Language</c> header produces.</summary>
    public ProviderCultureResult(IList<string> cultures, IList<string> uiCultures)
    {
        ArgumentNullException.ThrowIfNull(cultures);
        ArgumentNullException.ThrowIfNull(uiCultures);

        this.Cultures = cultures;
        this.UICultures = uiCultures;
    }

    public IList<string> Cultures { get; }

    public IList<string> UICultures { get; }
}

/// <summary>What the localization middleware settled on for this request, and who said so.</summary>
public interface IRequestCultureFeature
{
    /// <summary>The cultures the request is running under.</summary>
    RequestCulture RequestCulture { get; }

    /// <summary>
    /// The provider whose answer was used, or null when none matched and the request fell back to
    /// <see cref="RequestLocalizationOptions.DefaultRequestCulture"/>.
    /// </summary>
    IRequestCultureProvider? Provider { get; }
}

/// <summary>The default <see cref="IRequestCultureFeature"/>.</summary>
public sealed class RequestCultureFeature(RequestCulture requestCulture, IRequestCultureProvider? provider) : IRequestCultureFeature
{
    public RequestCulture RequestCulture { get; } = requestCulture ?? throw new ArgumentNullException(nameof(requestCulture));

    public IRequestCultureProvider? Provider { get; } = provider;
}
