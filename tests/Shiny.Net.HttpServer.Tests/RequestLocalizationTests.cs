using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging;
using Shiny.Net.HttpServer.Http2.Hpack;
using Shiny.Net.HttpServer.Http3;
using Shiny.Net.HttpServer.Localization;
using Shiny.Net.HttpServer.Testing;

namespace Shiny.Net.HttpServer.Tests;

public class AcceptLanguageParsingTests
{
    [Fact]
    public void Orders_by_quality_keeping_the_written_order_for_ties()
        => Assert.Equal(
            ["fr-CA", "fr", "de", "en"],
            AcceptLanguageHeaderRequestCultureProvider.Parse("en;q=0.5, fr-CA, de;q=0.8, fr, xx;q=0", 10)
        );

    [Fact]
    public void Drops_the_wildcard_zero_weights_and_junk()
        => Assert.Equal(
            ["en-GB"],
            AcceptLanguageHeaderRequestCultureProvider.Parse("*;q=0.9, fr;q=0, en-GB, <script>, a-verylongsubtag, de;q=nope", 10)
        );

    [Fact]
    public void Tries_only_the_first_few_languages()
        => Assert.Equal(
            ["a", "b", "c"],
            AcceptLanguageHeaderRequestCultureProvider.Parse("a, b, c, d, e", 3)
        );

    [Theory]
    [InlineData("fr-CA", true)]
    [InlineData("zh-Hant-TW", true)]
    [InlineData("en_US", true)]
    [InlineData("-fr", false)]
    [InlineData("fr-", false)]
    [InlineData("fr--CA", false)]
    [InlineData("toolongtag", false)]
    [InlineData("fr CA", false)]
    public void Recognises_the_shape_of_a_language_tag(string tag, bool expected)
        => Assert.Equal(expected, AcceptLanguageHeaderRequestCultureProvider.IsLanguageTag(tag));
}

public class CultureMatchingTests
{
    static readonly IList<CultureInfo> Supported = [CultureInfo.GetCultureInfo("en"), CultureInfo.GetCultureInfo("fr"), CultureInfo.GetCultureInfo("de-DE")];

    [Theory]
    [InlineData("fr", "fr")]
    [InlineData("FR", "fr")]
    [InlineData("fr-CA", "fr")]
    [InlineData("de-DE", "de-DE")]
    [InlineData("en_US", "en")]
    [InlineData("de", null)]            // a parent does not match a more specific supported culture
    [InlineData("es-MX", null)]
    public void Falls_back_through_parents_to_a_supported_culture(string requested, string? expected)
        => Assert.Equal(expected, RequestLocalizationMiddleware.Match([requested], Supported, fallBackToParent: true)?.Name);

    [Fact]
    public void Exhausts_the_first_candidates_parents_before_the_second_candidate()
        => Assert.Equal("fr", RequestLocalizationMiddleware.Match(["fr-CA", "en"], Supported, fallBackToParent: true)?.Name);

    [Fact]
    public void Does_not_fall_back_when_told_not_to()
        => Assert.Equal("en", RequestLocalizationMiddleware.Match(["fr-CA", "en"], Supported, fallBackToParent: false)?.Name);

    [Fact]
    public void Reads_the_aspnetcore_cookie_format()
    {
        var result = CookieRequestCultureProvider.ParseCookieValue("c=fr-CA|uic=fr");

        Assert.NotNull(result);
        Assert.Equal(["fr-CA"], result.Cultures);
        Assert.Equal(["fr"], result.UICultures);

        var escaped = CookieRequestCultureProvider.ParseCookieValue(Uri.EscapeDataString("c=de|uic=de"));
        Assert.Equal(["de"], escaped!.Cultures);

        Assert.Null(CookieRequestCultureProvider.ParseCookieValue("garbage"));
        Assert.Equal(
            "c=fr-CA|uic=fr",
            CookieRequestCultureProvider.MakeCookieValue(new RequestCulture("fr-CA", "fr"))
        );
    }

    [Fact]
    public void Skips_a_culture_the_runtime_will_not_create_rather_than_throwing()
    {
        // The same path an InvariantGlobalization build takes for every name.
        var options = new RequestLocalizationOptions()
            .AddSupportedCultures("en", "!!bad")
            .SetDefaultCulture("!!worse");

        Assert.Equal(["en"], options.SupportedCultures!.Select(c => c.Name));
        Assert.Equal(["!!bad", "!!worse"], options.UnavailableCultures);

        var logger = new RecordingLogger<RequestLocalizationMiddleware>();
        _ = new RequestLocalizationMiddleware(options, logger);

        Assert.Contains(logger.At(LogLevel.Warning), e => e.Message.Contains("!!bad"));
    }
}

public class RequestLocalizationMiddlewareTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    static Task<TestServer> StartAsync(Action<RequestLocalizationOptions>? configure = null)
        => TestServer.StartAsync(
            app =>
            {
                app.UseRequestLocalization();
                app.MapGet("/culture", async ctx =>
                {
                    // Across an await, as a real handler would be.
                    await Task.Yield();

                    var feature = ctx.GetRequestCultureFeature();
                    await ctx.Response.WriteAsync(
                        $"{CultureInfo.CurrentCulture.Name}|{CultureInfo.CurrentUICulture.Name}|{feature?.Provider?.GetType().Name ?? "default"}"
                    );
                });
            },
            builder => builder.AddRequestLocalization(o =>
            {
                o.SetDefaultCulture("en")
                    .AddSupportedCultures("en", "fr", "fr-CA", "de")
                    .AddSupportedUICultures("en", "fr", "de");

                configure?.Invoke(o);
            })
        );

    static async Task<string> GetAsync(HttpClient client, string path = "/culture", string? acceptLanguage = null, string? cookie = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path)
        {
            Version = client.DefaultRequestVersion,
            VersionPolicy = client.DefaultVersionPolicy
        };
        if (acceptLanguage is not null)
            request.Headers.Add("Accept-Language", acceptLanguage);
        if (cookie is not null)
            request.Headers.Add("Cookie", cookie);

        var response = await client.SendAsync(request, Token);
        return await response.Content.ReadAsStringAsync(Token);
    }

    [Fact]
    public async Task Runs_the_request_under_the_accept_language_culture()
    {
        await using var server = await StartAsync();

        Assert.Equal(
            "de|de|AcceptLanguageHeaderRequestCultureProvider",
            await GetAsync(server.Client, acceptLanguage: "es;q=0.9, de;q=0.8")
        );
    }

    [Fact]
    public async Task Falls_back_to_the_parent_culture_for_ui_but_keeps_the_specific_one_for_formatting()
    {
        await using var server = await StartAsync();

        Assert.Equal("fr-CA|fr|AcceptLanguageHeaderRequestCultureProvider", await GetAsync(server.Client, acceptLanguage: "fr-CA"));
        Assert.Equal("fr|fr|AcceptLanguageHeaderRequestCultureProvider", await GetAsync(server.Client, acceptLanguage: "fr-BE"));
    }

    [Fact]
    public async Task Uses_the_default_when_nothing_matches()
    {
        await using var server = await StartAsync();

        Assert.Equal("en|en|default", await GetAsync(server.Client, acceptLanguage: "ja, ko"));
        Assert.Equal("en|en|default", await GetAsync(server.Client));
    }

    [Fact]
    public async Task The_query_string_beats_the_cookie_which_beats_the_header()
    {
        await using var server = await StartAsync();
        var cookie = $"{CookieRequestCultureProvider.DefaultCookieName}={Uri.EscapeDataString("c=de|uic=de")}";

        Assert.Equal(
            "fr|fr|QueryStringRequestCultureProvider",
            await GetAsync(server.Client, "/culture?culture=fr", acceptLanguage: "en", cookie: cookie)
        );
        Assert.Equal(
            "de|de|CookieRequestCultureProvider",
            await GetAsync(server.Client, acceptLanguage: "fr", cookie: cookie)
        );
    }

    [Fact]
    public async Task The_query_string_can_set_formatting_and_text_separately()
    {
        await using var server = await StartAsync();

        Assert.Equal(
            "fr-CA|en|QueryStringRequestCultureProvider",
            await GetAsync(server.Client, "/culture?culture=fr-CA&ui-culture=en")
        );
    }

    [Fact]
    public async Task Writes_content_language_when_asked()
    {
        await using var server = await StartAsync(o => o.ApplyCurrentCultureToResponseHeaders = true);

        var request = new HttpRequestMessage(HttpMethod.Get, "/culture");
        request.Headers.Add("Accept-Language", "de");
        var response = await server.Client.SendAsync(request, Token);

        Assert.Equal(["de"], response.Content.Headers.ContentLanguage);
    }

    [Fact]
    public async Task Does_not_leak_one_requests_culture_into_the_next_on_a_kept_alive_connection()
    {
        await using var server = await StartAsync();

        // One HttpClient, one pooled connection, one pooled HttpContext.
        Assert.Equal("de|de|AcceptLanguageHeaderRequestCultureProvider", await GetAsync(server.Client, acceptLanguage: "de"));
        Assert.Equal("en|en|default", await GetAsync(server.Client));
    }

    [Fact]
    public async Task Formats_in_the_request_culture()
    {
        Assert.SkipWhen(RequestLocalizationOptions.IsGlobalizationInvariant, "No culture data to format with.");

        await using var server = await TestServer.StartAsync(app =>
        {
            app.UseRequestLocalization("en", "de");
            app.MapGet("/price", ctx => ctx.Response.WriteAsync(1234.5.ToString("N1")));
        });

        Assert.Equal("1.234,5", await GetAsync(server.Client, "/price", acceptLanguage: "de-AT, en;q=0.5"));
        Assert.Equal("1,234.5", await GetAsync(server.Client, "/price", acceptLanguage: "en"));
    }

    [Fact]
    public async Task Gives_each_multiplexed_http2_stream_its_own_culture()
    {
        await using var server = await StartAsync();

        using var client = new HttpClient(new SocketsHttpHandler())
        {
            BaseAddress = new Uri($"http://127.0.0.1:{server.Port}"),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };

        var languages = Enumerable.Range(0, 30).Select(i => (i % 3) switch { 0 => "fr", 1 => "de", _ => "en" }).ToArray();
        var results = await Task.WhenAll(languages.Select(language => GetAsync(client, acceptLanguage: language)));

        for (var i = 0; i < languages.Length; i++)
            Assert.StartsWith($"{languages[i]}|{languages[i]}|", results[i]);

        // And it really was HTTP/2, not a fallback that would make this test trivially true.
        var probe = new HttpRequestMessage(HttpMethod.Get, "/culture") { Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
        Assert.Equal(HttpVersion.Version20, (await client.SendAsync(probe, Token)).Version);
    }

    [Fact]
    public async Task Reads_accept_language_from_an_http3_request()
    {
        // Through the real HTTP/3 request mapper; QUIC itself is not available on every machine.
        var context = new HttpContext();
        List<HeaderField> fields =
        [
            new(":method", "GET"),
            new(":scheme", "https"),
            new(":authority", "localhost"),
            new(":path", "/"),
            new("accept-language", "fr-CA,fr;q=0.9")
        ];
        Assert.True(Http3RequestMapper.TryApply(context, fields, [], out var error), error);

        var options = new RequestLocalizationOptions().SetDefaultCulture("en").AddSupportedCultures("en", "fr").AddSupportedUICultures("en", "fr");
        string? seen = null;

        await new RequestLocalizationMiddleware(options).InvokeAsync(context, ctx =>
        {
            seen = CultureInfo.CurrentUICulture.Name;
            return ValueTask.CompletedTask;
        });

        Assert.Equal("fr", seen);
        Assert.Equal("fr", context.GetRequestCulture()?.UICulture.Name);
    }

    [Fact]
    public async Task Restores_the_callers_culture_afterwards()
    {
        var before = CultureInfo.CurrentCulture;
        var context = new HttpContext();
        context.Request.Headers.Set("Accept-Language", "de");

        await new RequestLocalizationMiddleware(new RequestLocalizationOptions().AddSupportedCultures("de").AddSupportedUICultures("de"))
            .InvokeAsync(context, _ => ValueTask.CompletedTask);

        Assert.Same(before, CultureInfo.CurrentCulture);
    }

    [Fact]
    public async Task Reports_nothing_without_the_middleware()
    {
        var server = HttpServer.CreateBuilder().Build();
        server.MapGet("/culture", ctx => ctx.Response.WriteAsync(ctx.GetRequestCulture() is null ? "none" : "some"));

        await using (server)
        {
            using var client = server.CreateInMemoryClient();
            Assert.Equal("none", await client.GetStringAsync("/culture", Token));
        }
    }

    [Fact]
    public async Task A_custom_provider_can_go_first()
    {
        await using var server = await StartAsync(o => o.AddInitialRequestCultureProvider(
            new CustomRequestCultureProvider(ctx => ValueTask.FromResult<ProviderCultureResult?>(
                ctx.Request.Headers.GetFirst("X-Locale") is { } locale ? new ProviderCultureResult(locale) : null
            ))
        ));

        var request = new HttpRequestMessage(HttpMethod.Get, "/culture");
        request.Headers.Add("X-Locale", "de");
        request.Headers.Add("Accept-Language", "fr");

        var response = await server.Client.SendAsync(request, Token);
        Assert.Equal("de|de|CustomRequestCultureProvider", await response.Content.ReadAsStringAsync(Token));
    }
}
