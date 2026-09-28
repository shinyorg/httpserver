using System.Globalization;

namespace Shiny.Net.HttpServer.Localization;

/// <summary>
/// Which cultures the app speaks, and how a request says which one it wants. Shaped after ASP.NET
/// Core's options of the same name.
/// <code>
/// builder.AddRequestLocalization(o => o
///     .SetDefaultCulture("en")
///     .AddSupportedCultures("en", "fr", "de")
///     .AddSupportedUICultures("en", "fr", "de"));
///
/// var app = builder.Build();
/// app.UseRequestLocalization();
/// </code>
/// <para>
/// <b>InvariantGlobalization.</b> Native AOT and trimmed mobile builds often set
/// <c>&lt;InvariantGlobalization&gt;true&lt;/InvariantGlobalization&gt;</c>, and in that mode the
/// runtime has no culture data and, by default, refuses to create any culture but the invariant one.
/// Nothing here throws because of it: the <c>string</c> overloads skip a name the runtime will not
/// create, the middleware logs once that it has nothing to switch between, and every request runs
/// invariant. Add <c>&lt;PredefinedCulturesOnly&gt;false&lt;/PredefinedCulturesOnly&gt;</c> and
/// negotiation works again by name — <see cref="CultureInfo.CurrentUICulture"/> is <c>fr</c>, so a
/// <c>ResourceManager</c> finds the French satellite assembly — though dates and numbers still format
/// invariantly, because the data to do otherwise is not there. <see cref="IsGlobalizationInvariant"/>
/// says which mode the process is in.
/// </para>
/// </summary>
public sealed class RequestLocalizationOptions
{
    static readonly Lazy<bool> invariant = new(DetectInvariant);
    readonly List<string> unavailable = [];

    /// <summary>
    /// True when the process runs with <c>InvariantGlobalization</c> (or its environment-variable
    /// equivalent), so named cultures carry no data and may not be creatable at all.
    /// </summary>
    public static bool IsGlobalizationInvariant => invariant.Value;

    /// <summary>
    /// What a request gets when no provider names a supported culture. The process's current
    /// culture when the options are created, as in ASP.NET Core; set it with
    /// <see cref="SetDefaultCulture"/>.
    /// </summary>
    public RequestCulture DefaultRequestCulture { get; set; } = new(CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);

    /// <summary>
    /// Cultures a request may pick for formatting. Just the current culture by default. Null accepts
    /// any culture the runtime can create — rarely what you want, since ICU will create one for
    /// almost any well-formed tag a client sends.
    /// </summary>
    public IList<CultureInfo>? SupportedCultures { get; set; } = new List<CultureInfo> { CultureInfo.CurrentCulture };

    /// <summary>Cultures a request may pick for text. Just the current UI culture by default.</summary>
    public IList<CultureInfo>? SupportedUICultures { get; set; } = new List<CultureInfo> { CultureInfo.CurrentUICulture };

    /// <summary>
    /// Whether an unsupported culture falls back through its parents: a request for <c>fr-CA</c> gets
    /// <c>fr</c> when only <c>fr</c> is supported. True by default.
    /// </summary>
    public bool FallBackToParentCultures { get; set; } = true;

    /// <summary>As <see cref="FallBackToParentCultures"/>, for <see cref="SupportedUICultures"/>.</summary>
    public bool FallBackToParentUICultures { get; set; } = true;

    /// <summary>
    /// Sends the chosen UI culture back as <c>Content-Language</c>. False by default, as in ASP.NET
    /// Core. Turning it on says what language the response is in; it does not tell a cache that
    /// the response depends on <c>Accept-Language</c> — add <c>Vary</c> for that if a shared cache
    /// sits in front.
    /// </summary>
    public bool ApplyCurrentCultureToResponseHeaders { get; set; }

    /// <summary>
    /// Asked in order; the first to name a supported culture wins. Query string, then cookie, then
    /// <c>Accept-Language</c> — from the most deliberate choice to the least.
    /// </summary>
    public IList<IRequestCultureProvider> RequestCultureProviders { get; set; } = new List<IRequestCultureProvider>
    {
        new QueryStringRequestCultureProvider(),
        new CookieRequestCultureProvider(),
        new AcceptLanguageHeaderRequestCultureProvider()
    };

    /// <summary>
    /// Sets <see cref="DefaultRequestCulture"/> by name. A name the runtime will not create — every
    /// name, under <c>InvariantGlobalization</c> — leaves the default as it was.
    /// </summary>
    public RequestLocalizationOptions SetDefaultCulture(string culture)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(culture);

        if (this.TryCreate(culture, out var info))
            this.DefaultRequestCulture = new RequestCulture(info);

        return this;
    }

    /// <summary>
    /// Replaces <see cref="SupportedCultures"/> — replaces, as ASP.NET Core does, so the default
    /// entry for the current culture does not linger. Names the runtime will not create are skipped.
    /// </summary>
    public RequestLocalizationOptions AddSupportedCultures(params string[] cultures)
    {
        ArgumentNullException.ThrowIfNull(cultures);

        this.SupportedCultures = this.CreateAll(cultures);
        return this;
    }

    /// <summary>Replaces <see cref="SupportedUICultures"/>, as <see cref="AddSupportedCultures"/> does.</summary>
    public RequestLocalizationOptions AddSupportedUICultures(params string[] uiCultures)
    {
        ArgumentNullException.ThrowIfNull(uiCultures);

        this.SupportedUICultures = this.CreateAll(uiCultures);
        return this;
    }

    /// <summary>Puts a provider ahead of the built-in ones.</summary>
    public RequestLocalizationOptions AddInitialRequestCultureProvider(IRequestCultureProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        this.RequestCultureProviders.Insert(0, provider);
        return this;
    }

    /// <summary>Names passed to the <c>string</c> overloads that the runtime would not create.</summary>
    internal IReadOnlyList<string> UnavailableCultures => this.unavailable;

    List<CultureInfo> CreateAll(string[] names)
    {
        var list = new List<CultureInfo>(names.Length);

        foreach (var name in names)
        {
            if (this.TryCreate(name, out var culture) && !list.Contains(culture))
                list.Add(culture);
        }

        return list;
    }

    bool TryCreate(string name, out CultureInfo culture)
    {
        if (TryGetCulture(name, out culture))
            return true;

        this.unavailable.Add(name);
        return false;
    }

    /// <summary>
    /// A cached, read-only culture by name, or false when the runtime will not produce one — which
    /// under <c>InvariantGlobalization</c> is every name but the invariant one.
    /// </summary>
    internal static bool TryGetCulture(string? name, out CultureInfo culture)
    {
        culture = CultureInfo.InvariantCulture;

        if (string.IsNullOrWhiteSpace(name))
            return false;

        try
        {
            culture = CultureInfo.GetCultureInfo(name.Trim());
            return true;
        }
        catch (ArgumentException)
        {
            // CultureNotFoundException, and the plain ArgumentException a malformed name produces.
            return false;
        }
    }

    static bool DetectInvariant()
    {
        if (AppContext.TryGetSwitch("System.Globalization.Invariant", out var enabled))
            return enabled;

        var variable = Environment.GetEnvironmentVariable("DOTNET_SYSTEM_GLOBALIZATION_INVARIANT");
        if (variable is { Length: > 0 })
            return variable == "1" || variable.Equals("true", StringComparison.OrdinalIgnoreCase);

        // Neither said so, but a runtime with no culture data behaves the same way regardless of
        // how it got there.
        try
        {
            return CultureInfo.GetCultureInfo("en-US").NumberFormat.CurrencySymbol == "¤";
        }
        catch (CultureNotFoundException)
        {
            return true;
        }
    }
}
