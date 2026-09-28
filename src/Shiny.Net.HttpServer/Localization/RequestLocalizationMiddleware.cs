using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Shiny.Net.HttpServer.Localization;

/// <summary>
/// Runs each request under the culture it asked for.
/// <para>
/// It sets <see cref="CultureInfo.CurrentCulture"/> and <see cref="CultureInfo.CurrentUICulture"/>,
/// which are async-local: they flow through every <c>await</c> of this request's handler and
/// nowhere else, so two requests multiplexed on one HTTP/2 connection each format in their own
/// culture. The previous values are restored when the pipeline returns. Nothing in the handler
/// needs to know localization is on — <c>ToString("C")</c> and a <c>ResourceManager</c> just give
/// the right answer.
/// </para>
/// <para>
/// Put it after anything that establishes who the caller is (a provider may read a claim) and
/// before anything that renders text, including the exception handler if its messages are
/// localized.
/// </para>
/// </summary>
public sealed class RequestLocalizationMiddleware : IHttpMiddleware
{
    static readonly object FeatureKey = new();

    readonly RequestLocalizationOptions options;

    public RequestLocalizationMiddleware(RequestLocalizationOptions options, ILogger<RequestLocalizationMiddleware>? logger = null)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));

        if (options.UnavailableCultures.Count > 0)
        {
            ILogger log = logger ?? NullLogger<RequestLocalizationMiddleware>.Instance;
            log.LogWarning(
                RequestLocalizationOptions.IsGlobalizationInvariant
                    ? "Request localization cannot create {Cultures}: the app runs with InvariantGlobalization. Requests will run as {Default}. Set <PredefinedCulturesOnly>false</PredefinedCulturesOnly> to negotiate by name, or turn InvariantGlobalization off for culture data."
                    : "Request localization skipped {Cultures}: the runtime does not know them. Requests asking for them will run as {Default}.",
                string.Join(", ", options.UnavailableCultures),
                options.DefaultRequestCulture
            );
        }
    }

    public async ValueTask InvokeAsync(HttpContext context, RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var fallback = this.options.DefaultRequestCulture;
        var requestCulture = fallback;
        IRequestCultureProvider? winner = null;

        foreach (var provider in this.options.RequestCultureProviders)
        {
            var result = await provider.DetermineProviderCultureResult(context).ConfigureAwait(false);
            if (result is null)
                continue;

            var culture = Match(result.Cultures, this.options.SupportedCultures, this.options.FallBackToParentCultures);
            var uiCulture = Match(result.UICultures, this.options.SupportedUICultures, this.options.FallBackToParentUICultures);

            // A provider that named only unsupported cultures has said nothing useful; ask the next.
            if (culture is null && uiCulture is null)
                continue;

            requestCulture = new RequestCulture(culture ?? fallback.Culture, uiCulture ?? fallback.UICulture);
            winner = provider;
            break;
        }

        context.Items[FeatureKey] = new RequestCultureFeature(requestCulture, winner);

        if (this.options.ApplyCurrentCultureToResponseHeaders && requestCulture.UICulture.Name is { Length: > 0 } language)
            context.Response.Headers.Set("Content-Language", language);

        var previousCulture = CultureInfo.CurrentCulture;
        var previousUICulture = CultureInfo.CurrentUICulture;

        CultureInfo.CurrentCulture = requestCulture.Culture;
        CultureInfo.CurrentUICulture = requestCulture.UICulture;

        try
        {
            await next(context).ConfigureAwait(false);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUICulture;
        }
    }

    internal static IRequestCultureFeature? GetFeature(HttpContext context)
        => context.Items.TryGetValue(FeatureKey, out var value) ? value as IRequestCultureFeature : null;

    /// <summary>
    /// The first candidate the app supports — each tried exactly, then (when allowed) through its
    /// parents, before the next candidate is looked at. <c>fr-CA, en</c> against <c>en, fr</c> is
    /// <c>fr</c>: the caller's first language, less specific, beats their second.
    /// </summary>
    internal static CultureInfo? Match(IList<string> candidates, IList<CultureInfo>? supported, bool fallBackToParent)
    {
        foreach (var candidate in candidates)
        {
            var name = candidate?.Trim();
            if (name is not { Length: > 0 } || !AcceptLanguageHeaderRequestCultureProvider.IsLanguageTag(name))
                continue;

            while (true)
            {
                if (Find(name, supported) is { } found)
                    return found;

                if (!fallBackToParent)
                    break;

                // Parents by name rather than CultureInfo.Parent: identical for real cultures, and it
                // still works under InvariantGlobalization, where there is no data to ask.
                var dash = name.LastIndexOfAny(['-', '_']);
                if (dash <= 0)
                    break;

                name = name[..dash];
            }
        }

        return null;
    }

    static CultureInfo? Find(string name, IList<CultureInfo>? supported)
    {
        if (supported is null)
            return RequestLocalizationOptions.TryGetCulture(name, out var any) ? any : null;

        foreach (var culture in supported)
        {
            if (culture.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                || (name.Contains('_') && culture.Name.Equals(name.Replace('_', '-'), StringComparison.OrdinalIgnoreCase)))
                return culture;
        }

        return null;
    }
}

/// <summary>Registering, installing and reading request localization.</summary>
public static class RequestLocalizationExtensions
{
    /// <summary>
    /// Registers <see cref="RequestLocalizationOptions"/>. Every call's <paramref name="configure"/>
    /// runs, in order — though <see cref="RequestLocalizationOptions.AddSupportedCultures"/> replaces
    /// the list, so the last call to set it wins.
    /// </summary>
    public static ShinyHttpServerBuilder AddRequestLocalization(
        this ShinyHttpServerBuilder builder,
        Action<RequestLocalizationOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(builder);

        OptionsRegistration.Configure<RequestLocalizationOptions>(builder.Services, configure);
        return builder;
    }

    /// <summary>
    /// Runs each request under the culture it asks for. Uses the registered options when there are
    /// some, adjusted by <paramref name="configure"/>; fresh defaults otherwise.
    /// </summary>
    public static HttpServer UseRequestLocalization(this HttpServer server, Action<RequestLocalizationOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(server);

        var options = server.Services?.GetService<RequestLocalizationOptions>() ?? new RequestLocalizationOptions();
        configure?.Invoke(options);

        var logger = server.Services
            ?.GetService<ILoggerFactory>()
            ?.CreateLogger<RequestLocalizationMiddleware>();

        return server.Use(new RequestLocalizationMiddleware(options, logger));
    }

    /// <summary>
    /// The one-liner: these cultures, for formatting and for text, the first as the default.
    /// <code>
    /// app.UseRequestLocalization("en", "fr", "de");
    /// </code>
    /// </summary>
    public static HttpServer UseRequestLocalization(this HttpServer server, params string[] cultures)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(cultures);

        if (cultures.Length == 0)
            throw new ArgumentException("Name at least one culture.", nameof(cultures));

        return server.UseRequestLocalization(o => o
            .AddSupportedCultures(cultures)
            .AddSupportedUICultures(cultures)
            .SetDefaultCulture(cultures[0])
        );
    }

    /// <summary>
    /// What the localization middleware chose for this request and which provider chose it, or null
    /// when <see cref="UseRequestLocalization(HttpServer, Action{RequestLocalizationOptions}?)"/> is
    /// not in the pipeline ahead of the caller.
    /// </summary>
    public static IRequestCultureFeature? GetRequestCultureFeature(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return RequestLocalizationMiddleware.GetFeature(context);
    }

    /// <summary>The cultures this request runs under, or null without the localization middleware.</summary>
    public static RequestCulture? GetRequestCulture(this HttpContext context)
        => context.GetRequestCultureFeature()?.RequestCulture;
}
