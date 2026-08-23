using System.Buffers;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shiny.Net.HttpServer.Security;

namespace Shiny.Net.HttpServer.Mcp;

/// <summary>
/// What this MCP endpoint publishes about itself as an OAuth 2.0 protected resource (RFC 9728).
/// </summary>
public sealed class McpProtectedResourceOptions
{
    /// <summary>
    /// The canonical identifier of this resource — the URL clients are told to bind their token to.
    /// <para>
    /// Left null it is derived per request from the host and the MCP path, which is right for a
    /// tunnelled server whose public address is decided at runtime and unknown at startup. Set it
    /// when the server sits behind a fixed name, because the identifier ends up inside issued
    /// tokens and it has to match what the authorization server was told.
    /// </para>
    /// </summary>
    public string? Resource { get; set; }

    /// <summary>
    /// The authorization servers a client may get a token from. At least one is required — this
    /// document exists to answer "where do I go to log in", and an empty list answers nothing.
    /// </summary>
    public IList<string> AuthorizationServers { get; } = [];

    /// <summary>Scopes a client should ask for. Published so a client does not have to guess.</summary>
    public IList<string> ScopesSupported { get; } = [];

    /// <summary>
    /// How a token may be presented. Header only, deliberately: a token in a query string ends up
    /// in logs, in history and in referrers.
    /// </summary>
    public IList<string> BearerMethodsSupported { get; } = ["header"];

    /// <summary>A human-readable name for the resource, shown by clients during consent.</summary>
    public string? ResourceName { get; set; }

    /// <summary>A documentation URL for whoever is being asked to authorize this.</summary>
    public string? ResourceDocumentation { get; set; }

    /// <summary>The paths this metadata describes, filled in by <c>MapMcpProtectedResource</c>.</summary>
    internal List<string> ProtectedPaths { get; } = [];

    /// <summary>The path the metadata document is served from, for the challenge to point at.</summary>
    internal string MetadataPath { get; set; } = McpProtectedResourceExtensions.WellKnownPath;
}

/// <summary>
/// The OAuth discovery half of a remote MCP server.
/// <para>
/// An MCP client that meets a 401 does not know where to authenticate. The specification's answer
/// is RFC 9728: the 401 carries <c>WWW-Authenticate: Bearer resource_metadata="…"</c>, the client
/// fetches that document, and the document names the authorization servers and the scopes. Without
/// it, a protected MCP server is one a client can only be pointed at by hand — which is precisely
/// the case for a server on a phone behind a tunnel, where the address is new every time.
/// </para>
/// <para>
/// This publishes the document and issues the challenge. Validating the tokens themselves is the
/// JWT package's job — <c>builder.AddJwtBearer(...)</c> — and the audience it validates should be
/// the same <see cref="McpProtectedResourceOptions.Resource"/> published here.
/// </para>
/// </summary>
public static class McpProtectedResourceExtensions
{
    internal const string WellKnownPath = "/.well-known/oauth-protected-resource";

    /// <summary>
    /// Registers the protected-resource metadata and the challenge that points clients at it.
    /// <code>
    /// builder.Services.AddMcpProtectedResource(o =>
    /// {
    ///     o.AuthorizationServers.Add("https://login.example.com");
    ///     o.ScopesSupported.Add("mcp:tools");
    /// });
    /// </code>
    /// </summary>
    public static ShinyHttpServerBuilder AddMcpProtectedResource(
        this ShinyHttpServerBuilder builder,
        Action<McpProtectedResourceOptions> configure
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        builder.Services.TryAddSingleton(_ =>
        {
            var options = new McpProtectedResourceOptions();
            configure(options);

            return options;
        });

        // Registered as a challenge rather than as middleware: this has to replace the generic 401
        // the authorization middleware would otherwise write, and that is exactly what the
        // challenge seam exists for.
        builder.Services.AddSingleton<IAuthenticationChallenge>(sp =>
            new McpResourceChallenge(sp.GetRequiredService<McpProtectedResourceOptions>()));

        return builder;
    }

    /// <summary>
    /// Serves the metadata document and marks <paramref name="mcpPattern"/> as the resource it
    /// describes.
    /// <code>
    /// app.MapMcp("/mcp").RequireAuthorization();
    /// app.MapMcpProtectedResource("/mcp");
    /// </code>
    /// <para>
    /// Two paths are mounted: the bare well-known path, and the path-suffixed form RFC 9728 §3.1
    /// defines for a resource that is not at the root — a client may ask for either.
    /// </para>
    /// </summary>
    public static HttpServer MapMcpProtectedResource(this HttpServer server, string mcpPattern = "/mcp")
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(mcpPattern);

        var options = server.Services?.GetService<McpProtectedResourceOptions>()
            ?? throw new InvalidOperationException(
                "MapMcpProtectedResource needs its options. Register them with " +
                "builder.AddMcpProtectedResource(o => o.AuthorizationServers.Add(\"https://...\"))."
            );

        if (options.AuthorizationServers.Count == 0)
            throw new InvalidOperationException(
                "A protected resource document with no authorization servers tells a client nothing. " +
                "Add at least one: o.AuthorizationServers.Add(\"https://login.example.com\")."
            );

        var normalized = "/" + mcpPattern.Trim('/');
        options.ProtectedPaths.Add(normalized);

        var suffixed = WellKnownPath + normalized;
        options.MetadataPath = suffixed;

        foreach (var path in new[] { WellKnownPath, suffixed })
        {
            server
                .MapGet(path, ctx => WriteMetadataAsync(ctx, options, normalized))
                .AllowAnonymous();
        }

        return server;
    }

    static async ValueTask WriteMetadataAsync(HttpContext context, McpProtectedResourceOptions options, string mcpPath)
    {
        var buffer = new ArrayBufferWriter<byte>(512);

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("resource", options.Resource ?? AbsoluteUrl(context, mcpPath));

            writer.WriteStartArray("authorization_servers");
            foreach (var server in options.AuthorizationServers)
                writer.WriteStringValue(server);
            writer.WriteEndArray();

            if (options.ScopesSupported.Count > 0)
            {
                writer.WriteStartArray("scopes_supported");
                foreach (var scope in options.ScopesSupported)
                    writer.WriteStringValue(scope);
                writer.WriteEndArray();
            }

            if (options.BearerMethodsSupported.Count > 0)
            {
                writer.WriteStartArray("bearer_methods_supported");
                foreach (var method in options.BearerMethodsSupported)
                    writer.WriteStringValue(method);
                writer.WriteEndArray();
            }

            if (options.ResourceName is { Length: > 0 } name)
                writer.WriteString("resource_name", name);

            if (options.ResourceDocumentation is { Length: > 0 } documentation)
                writer.WriteString("resource_documentation", documentation);

            writer.WriteEndObject();
        }

        // The document is public by definition and browser-based MCP clients fetch it cross-origin
        // before they have any credential to send, so it is served open rather than behind CORS
        // configuration the caller cannot influence.
        context.Response.Headers.Set(HeaderNames.AccessControlAllowOrigin, "*");
        context.Response.Headers.Set(HeaderNames.CacheControl, "public, max-age=3600");

        await context.Response
            .WriteBytesAsync(buffer.WrittenMemory, "application/json; charset=utf-8", context.RequestAborted)
            .ConfigureAwait(false);
    }

    internal static string AbsoluteUrl(HttpContext context, string path)
    {
        var host = context.Request.Host is { Length: > 0 } value ? value : "localhost";

        return $"{context.Request.Scheme}://{host}{path}";
    }
}

/// <summary>
/// Answers a denied MCP request with the challenge RFC 9728 defines, so the client knows where to
/// go rather than only that it was refused.
/// </summary>
sealed class McpResourceChallenge(McpProtectedResourceOptions options) : IAuthenticationChallenge
{
    public async ValueTask<bool> TryChallengeAsync(HttpContext context, bool forbidden)
    {
        // A 403 means the caller is known and still not allowed. Pointing them at the login they
        // already completed would send them round a loop.
        if (forbidden || !this.Applies(context))
            return false;

        var metadata = McpProtectedResourceExtensions.AbsoluteUrl(context, options.MetadataPath);
        var challenge = $"Bearer resource_metadata=\"{metadata}\"";

        if (context.Authentication.Failure is { Length: > 0 })
            challenge += ", error=\"invalid_token\"";

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.Set(HeaderNames.WwwAuthenticate, challenge);
        context.Response.ContentLength = 0;

        await context.Response.StartAsync(context.RequestAborted).ConfigureAwait(false);

        return true;
    }

    bool Applies(HttpContext context)
    {
        foreach (var path in options.ProtectedPaths)
        {
            if (context.Request.Path.StartsWith(path, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
