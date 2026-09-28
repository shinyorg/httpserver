using System.Net;
using System.Text;
using System.Text.Json;
using Shiny.Net.HttpServer.Versioning;

namespace Shiny.Net.HttpServer.OpenApi;

/// <summary>Serving the <see href="https://scalar.com">Scalar</see> API reference.</summary>
public static class HttpServerScalarExtensions
{
    /// <summary>
    /// Serves an interactive API reference for the OpenAPI document — browse every operation, read
    /// its schemas, and send requests from the page.
    /// <code>
    /// app.MapOpenApi();
    /// app.MapScalarApiReference();                       // → /scalar
    ///
    /// // one document per API version: Scalar lists them in a picker, newest first
    /// app.MapOpenApi("/openapi/{documentName}.json");
    /// app.MapScalarApiReference(configure: o => o.DocumentUrl = "/openapi/{documentName}.json");
    /// </code>
    /// <para>
    /// The page is one small HTML response. Scalar itself is a script the browser loads from
    /// <see cref="ScalarOptions.ScriptUrl"/> — the CDN unless you serve a copy — so nothing is added
    /// to the app, and the reference costs the server nothing until someone opens it. The page is
    /// built on the first request, once the route table is final, and excludes itself from the
    /// document.
    /// </para>
    /// <para>
    /// It is an ordinary route, so <c>.RequireAuthorization()</c> straight after this call protects
    /// it. Protect the document route as well: the page only points at the document, and anyone who
    /// can fetch the document can read it.
    /// </para>
    /// </summary>
    public static HttpServer MapScalarApiReference(
        this HttpServer server,
        string pattern = "/scalar",
        Action<ScalarOptions>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

        var options = new ScalarOptions();
        configure?.Invoke(options);

        byte[]? page = null;

        server.MapGet(pattern, ctx =>
        {
            // Same reasoning as MapOpenApi: the route table is frozen by now, and a race only builds
            // the page twice.
            page ??= BuildPage(server, options);

            ctx.Response.Headers["Cache-Control"] = "no-cache";
            return ctx.Response.WriteBytesAsync(page, "text/html; charset=utf-8", ctx.RequestAborted);
        });

        return server.Describe(o => o.Exclude = true);
    }

    /// <summary>The page, as it would be served. Public for tests and for writing it out statically.</summary>
    public static string BuildScalarPage(HttpServer server, ScalarOptions? options = null)
        => Encoding.UTF8.GetString(BuildPage(server, options ?? new ScalarOptions()));

    static byte[] BuildPage(HttpServer server, ScalarOptions options)
    {
        var configuration = BuildConfiguration(server, options);

        // The title and script URL go through the HTML encoder. The configuration is JSON from
        // Utf8JsonWriter, whose default encoder escapes <, > and &, so nothing in it (a CSS string,
        // a title) can close the script element it sits in.
        var html = $"""
            <!doctype html>
            <html>
              <head>
                <title>{WebUtility.HtmlEncode(options.Title)}</title>
                <meta charset="utf-8" />
                <meta name="viewport" content="width=device-width, initial-scale=1" />
              </head>
              <body>
                <div id="app"></div>
                <script src="{WebUtility.HtmlEncode(options.ScriptUrl)}"></script>
                <script>Scalar.createApiReference('#app', {configuration});</script>
              </body>
            </html>
            """;

        return Encoding.UTF8.GetBytes(html);
    }

    static string BuildConfiguration(HttpServer server, ScalarOptions options)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();

            var documents = ResolveDocuments(server, options);
            if (documents.Count == 1 && documents[0].Title is null && documents[0].Slug is null)
            {
                writer.WriteString("url", documents[0].Url);
            }
            else
            {
                writer.WriteStartArray("sources");
                foreach (var document in documents)
                {
                    writer.WriteStartObject();
                    writer.WriteString("url", document.Url);
                    if (document.Title is not null)
                        writer.WriteString("title", document.Title);
                    if (document.Slug is not null)
                        writer.WriteString("slug", document.Slug);
                    if (document.IsDefault)
                        writer.WriteBoolean("default", true);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }

            var extra = options.AdditionalConfiguration;

            WriteString(writer, extra, "theme", options.Theme is { } theme ? ThemeName(theme) : null);
            WriteString(writer, extra, "layout", options.Layout is { } layout ? layout == ScalarLayout.Classic ? "classic" : "modern" : null);
            WriteBoolean(writer, extra, "darkMode", options.DarkMode);
            WriteString(writer, extra, "forceDarkModeState", options.ForceColorMode is { } mode ? mode == ScalarColorMode.Dark ? "dark" : "light" : null);
            WriteBoolean(writer, extra, "hideDarkModeToggle", options.HideDarkModeToggle);
            WriteBoolean(writer, extra, "hideModels", options.HideModels);
            WriteBoolean(writer, extra, "hideClientButton", options.HideClientButton);
            WriteBoolean(writer, extra, "showSidebar", options.ShowSidebar);
            WriteBoolean(writer, extra, "defaultOpenAllTags", options.DefaultOpenAllTags);
            WriteBoolean(writer, extra, "withDefaultFonts", options.WithDefaultFonts);
            WriteString(writer, extra, "customCss", options.CustomCss);
            WriteString(writer, extra, "favicon", options.Favicon);
            WriteString(writer, extra, "searchHotKey", options.SearchHotKey);
            WriteString(writer, extra, "proxyUrl", options.ProxyUrl);
            WriteString(writer, extra, "baseServerURL", options.BaseServerUrl);

            if (options.DefaultHttpClient is { } client && !extra.ContainsKey("defaultHttpClient"))
            {
                writer.WriteStartObject("defaultHttpClient");
                writer.WriteString("targetKey", client.TargetKey);
                writer.WriteString("clientKey", client.ClientKey);
                writer.WriteEndObject();
            }

            if (options.PreferredSecurityScheme is { } scheme && !extra.ContainsKey("authentication"))
            {
                writer.WriteStartObject("authentication");
                writer.WriteString("preferredSecurityScheme", scheme);
                writer.WriteEndObject();
            }

            foreach (var (key, value) in extra)
            {
                writer.WritePropertyName(key);
                if (value is null)
                    writer.WriteNullValue();
                else
                    value.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    const string DocumentNameToken = "{documentName}";

    static IReadOnlyList<ScalarDocument> ResolveDocuments(HttpServer server, ScalarOptions options)
    {
        if (options.Documents.Count > 0)
            return [.. options.Documents];

        var url = options.DocumentUrl;
        if (!url.Contains(DocumentNameToken, StringComparison.OrdinalIgnoreCase))
            return [new ScalarDocument { Url = url }];

        // One source per version the route table serves — exactly the names the versioned
        // MapOpenApi resolves — with the newest opened first, since that is the one most people
        // are writing against.
        var versioning = OpenApiDocumentBuilder.VersioningOptions(server);
        var versions = OpenApiDocumentBuilder.GetApiVersions(server);
        if (versions.Count == 0)
            throw new InvalidOperationException(
                $"{nameof(ScalarOptions.DocumentUrl)} '{url}' names a document per API version, but no mapped endpoint declares an API version."
            );

        var documents = new List<ScalarDocument>(versions.Count);
        for (var i = versions.Count - 1; i >= 0; i--)
        {
            var name = versioning.FormatGroupName(versions[i]);
            documents.Add(new ScalarDocument
            {
                Url = url.Replace(DocumentNameToken, name, StringComparison.OrdinalIgnoreCase),
                Title = name,
                Slug = name,
                IsDefault = i == versions.Count - 1
            });
        }

        return documents;
    }

    static void WriteString(Utf8JsonWriter writer, IDictionary<string, System.Text.Json.Nodes.JsonNode?> extra, string name, string? value)
    {
        if (value is not null && !extra.ContainsKey(name))
            writer.WriteString(name, value);
    }

    static void WriteBoolean(Utf8JsonWriter writer, IDictionary<string, System.Text.Json.Nodes.JsonNode?> extra, string name, bool? value)
    {
        if (value is { } flag && !extra.ContainsKey(name))
            writer.WriteBoolean(name, flag);
    }

    static string ThemeName(ScalarTheme theme) => theme switch
    {
        ScalarTheme.Default => "default",
        ScalarTheme.Alternate => "alternate",
        ScalarTheme.Moon => "moon",
        ScalarTheme.Purple => "purple",
        ScalarTheme.Solarized => "solarized",
        ScalarTheme.BluePlanet => "bluePlanet",
        ScalarTheme.Saturn => "saturn",
        ScalarTheme.Kepler => "kepler",
        ScalarTheme.Mars => "mars",
        ScalarTheme.DeepSpace => "deepSpace",
        ScalarTheme.Laserwave => "laserwave",
        ScalarTheme.None => "none",
        _ => throw new ArgumentOutOfRangeException(nameof(theme), theme, null)
    };
}
