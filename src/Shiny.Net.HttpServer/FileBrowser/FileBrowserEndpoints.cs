using Shiny.Net.HttpServer.Files;
using Shiny.Net.HttpServer.Routing;
using Shiny.Net.HttpServer.StaticFiles;

namespace Shiny.Net.HttpServer.FileBrowser;

/// <summary>
/// The routes a file browser registered, so authorization can be applied to them as a group.
/// <para>
/// Handed back rather than swallowed because the write and delete routes usually want a stricter
/// policy than the read ones — locking the whole browser behind one policy is the common case, but
/// it should not be the only one available.
/// </para>
/// </summary>
public sealed class FileBrowserEndpoints
{
    internal FileBrowserEndpoints(
        IReadOnlyList<RouteEndpoint> read,
        IReadOnlyList<RouteEndpoint> write,
        IReadOnlyList<RouteEndpoint> delete
    )
    {
        this.ReadEndpoints = read;
        this.WriteEndpoints = write;
        this.DeleteEndpoints = delete;
    }

    /// <summary>Listing and download.</summary>
    public IReadOnlyList<RouteEndpoint> ReadEndpoints { get; }

    /// <summary>Upload. Empty when writing is off.</summary>
    public IReadOnlyList<RouteEndpoint> WriteEndpoints { get; }

    /// <summary>Delete. Empty when deleting is off.</summary>
    public IReadOnlyList<RouteEndpoint> DeleteEndpoints { get; }

    /// <summary>Every route this browser registered.</summary>
    public IEnumerable<RouteEndpoint> All => this.ReadEndpoints.Concat(this.WriteEndpoints).Concat(this.DeleteEndpoints);

    /// <summary>
    /// Requires authorization for every route.
    /// <code>
    /// app.MapFileBrowser("/files", o => o.RootPath = FileSystem.AppDataDirectory)
    ///    .RequireAuthorization();
    /// </code>
    /// </summary>
    public FileBrowserEndpoints RequireAuthorization(params string[] policies)
        => this.Require(this.All, policies);

    /// <summary>
    /// Requires authorization for the routes that change something, leaving reads open.
    /// <code>
    /// app.MapFileBrowser("/files", …).RequireAuthorizationForChanges("editors");
    /// </code>
    /// </summary>
    public FileBrowserEndpoints RequireAuthorizationForChanges(params string[] policies)
        => this.Require(this.WriteEndpoints.Concat(this.DeleteEndpoints), policies);

    FileBrowserEndpoints Require(IEnumerable<RouteEndpoint> endpoints, string[] policies)
    {
        foreach (var endpoint in endpoints)
        {
            var metadata = endpoint.GetMetadata<Security.AuthorizationMetadata>();

            if (metadata is null)
            {
                metadata = new Security.AuthorizationMetadata();
                endpoint.WithMetadata(metadata);
            }

            metadata.Required = true;

            foreach (var policy in policies)
            {
                if (!metadata.Policies.Contains(policy))
                    metadata.Policies.Add(policy);
            }
        }

        return this;
    }
}

/// <summary>
/// A directory served over HTTP: list it, read from it, write to it, delete from it.
/// <para>
/// Mapped as ordinary routes rather than middleware, so every one of them can carry its own
/// authorization — which is the difference between "the app can browse files" and "anyone who finds
/// the URL can".
/// </para>
/// <code>
/// app.MapFileBrowser("/files", o =>
/// {
///     o.RootPath = FileSystem.AppDataDirectory;   // the app's own storage on a phone
///     o.AllowWrite = true;
/// })
/// .RequireAuthorization();
/// </code>
/// <para>
/// The API is deliberately plain HTTP: <c>GET</c> a directory for JSON, <c>GET</c> a file for its
/// bytes, <c>PUT</c> to write, <c>DELETE</c> to remove. Anything that already speaks HTTP — curl, a
/// browser, a script — can drive it with no client library.
/// </para>
/// </summary>
public static class FileBrowserExtensions
{
    /// <summary>Maps a file browser at <paramref name="prefix"/>.</summary>
    public static FileBrowserEndpoints MapFileBrowser(
        this HttpServer server,
        string prefix,
        Action<FileBrowserOptions> configure
    )
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new FileBrowserOptions();
        configure(options);

        return server.MapFileBrowser(prefix, options);
    }

    /// <summary>Maps a file browser from options built elsewhere.</summary>
    public static FileBrowserEndpoints MapFileBrowser(this HttpServer server, string prefix, FileBrowserOptions options)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        ArgumentNullException.ThrowIfNull(options);

        var root = options.ResolvedRoot;
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"The file browser root '{root}' does not exist.");

        var basePath = "/" + prefix.Trim('/');

        // Mounted at the site root there is no prefix to hang the catch-all off, and gluing one on
        // anyway gives "//{*path}" — which routes correctly, because the template parser trims the
        // leading slashes, but reads like a mistake everywhere the template is printed.
        var childPath = basePath == "/" ? "/{*path}" : $"{basePath}/{{*path}}";
        var browser = new FileBrowserHandler(options, root);

        // Two routes per verb: one for the root of the browser, one for everything under it. A
        // catch-all alone would not match the prefix on its own.
        var read = new List<RouteEndpoint>
        {
            server.MapRoute(HttpMethods.Get, basePath, browser.GetAsync),
            server.MapRoute(HttpMethods.Get, childPath, browser.GetAsync)
        };

        var write = new List<RouteEndpoint>();
        var delete = new List<RouteEndpoint>();

        if (options.AllowWrite)
        {
            write.Add(server.MapRoute(HttpMethods.Put, childPath, browser.PutAsync));
        }

        if (options.AllowDelete)
        {
            delete.Add(server.MapRoute(HttpMethods.Delete, childPath, browser.DeleteAsync));
        }

        return new FileBrowserEndpoints(read, write, delete);
    }
}
